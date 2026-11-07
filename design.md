# Alarm & Calendar — design

Reminders and alarms on Windows and Android, kept in sync between your devices.
Progress and the full requirement list are in `ROADMAP.md`. This file covers how
it is built.

## Projects

| Project | Framework | Holds |
|---|---|---|
| `AlarmCalendar.Core` | `net10.0` | Everything that isn't UI: the model, schedule math, alarm evaluation, persistence, merging, and (Phase 3) the sync protocol and networking |
| `AlarmCalendar.Core.Tests` | `net10.0`, xUnit | Tests for Core |
| `AlarmCalendar.Desktop` | `net10.0-windows`, WPF, with WinForms interop for the tray icon | The Windows app |
| `AlarmCalendar.Android` | `net10.0-android`, .NET for Android with AndroidX views. Not MAUI. | The Android app |

**Why a single C# Core.** Every device works out for itself when a task
triggers, from the schedule alone (see *Alarm evaluation*). So both platforms
must compute *identical* occurrences, or a task marked done on the phone would
trigger again on the PC. In LanLink, separate desktop and mobile copies of the
network code drifted and broke interoperability (its `design.md` has the
history). Here there is one implementation, and it is the only one.

**Why .NET for Android rather than MAUI or Kotlin.** Kotlin would mean a second
Core. An alarm app is mostly native integration: `AlarmManager`, broadcast
receivers, foreground services, full-screen intents, activities shown over the
lock screen. MAUI would wrap all of that in a layer with known traps (LanLink's
`design.md` lists several) and still need the native code underneath. Plain .NET
for Android gives the native APIs directly and still references Core.
Drawback: F-Droid cannot build .NET apps, so the APK is sideloaded, as LanLink's
is.

## Time

- **Instants** (when something happened, or will happen at an absolute moment)
  are `DateTimeOffset`, kept in UTC.
- **Wall-clock times** ("09:00 every Monday", the start of an Every-N schedule)
  are `DateTime` with `Kind = Unspecified`, read in the device's current time
  zone. So "every day at 09:00" rings at 09:00 local time through DST changes,
  and after the device moves to another zone.
- `TimeMath.ToInstant` converts wall-clock to instant. **A time that doesn't
  exist** (in the spring-forward gap) moves forward by the length of the gap, so
  02:30 becomes 03:30. **A time that occurs twice** (in the fall-back overlap)
  takes its first occurrence.
- Everything that reads the clock goes through `IClock` (`UtcNow`, `Zone`), so
  tests can control both.

## Data model (`Model/`)

An `AlarmTask` is `Id` (a `Guid`) plus two parts that sync independently, each
with its own version (a hybrid logical clock, `Hlc`):

**`TaskDefinition`** is what the user edits, versioned by `DefVersion`:

| Field | Meaning |
|---|---|
| `Description` | Free text, multi-line |
| `Kind` | `Interval` (every N), `Weekly` (days of the week), `Once`, `Countdown` |
| `IntervalCount`, `Unit` | N and its unit: seconds through years |
| `Start` | Wall-clock start of an Interval schedule: occurrence *k* is `Start + k·N·unit` |
| `Restart` | `FromDone` (the next occurrence is N after you mark it done) or `OnSchedule` (it keeps to the grid set by `Start`). Interval only. |
| `Days`, `TimeOfDay` | Weekly: a bitmask indexed by `DayOfWeek` (bit 0 is Sunday), and a wall-clock time |
| `At` | Once: a wall-clock date and time |
| `Duration` | Countdown: the length |
| `ReminderMinutes` | How long after a dismissal it reminds again, while not done. 0 means it doesn't. |
| `WindowsSound`, `AndroidSound` | A `SoundRef` per platform, since a sound picked on one means nothing on the other. `null` means the device's default applies (R11). |
| `SoundMode` | `Once` or `Repeat`. `null` means the device's default applies. |
| `ScheduleRevision` | Goes up on every change that affects *when* the task fires |
| `Deleted` | Tombstone. A deleted task keeps its ID, so a deletion syncs like any other edit. |

**`TaskState`** is what happens over time, versioned by `StateVersion`:

| Field | Meaning |
|---|---|
| `Revision` | The `ScheduleRevision` this state was written against |
| `Anchor` | Wall-clock time that replaces `Start` after a `FromDone` mark-done |
| `CountdownEndsUtc` | When a running countdown ends. An instant, so it survives reboots (R12). |
| `DoneThroughUtc` | Every occurrence at or before this has been dealt with. It only ever goes up. |
| `SnoozeUntilUtc`, `SnoozeOccurrenceUtc` | After a dismissal: when to remind again, and the occurrence that was dismissed |

Per-device `AppSettings` (not synced): defaults for the sound, sound mode,
reminder interval, time of day (09:00) and restart mode; desktop window and
startup options; sync identity and options.

## Schedule math (`Occurrences.cs`)

An occurrence is an instant. For each kind:

- **Interval, calendar units** (days, weeks, months, years): occurrence *k* is
  `ToInstant(anchor.AddMonths(k·N))`, and so on for each unit. It is always
  computed **from the anchor**, never by stepping from the previous occurrence,
  so "monthly from the 31st" gives Feb 28, then Mar 31, without drifting to the
  28th.
- **Interval, fixed-length units** (seconds, minutes, hours):
  `ToInstant(anchor) + k·N·unit`, in absolute time. "Every 8 hours" stays 8
  hours apart across a DST change.
- **Weekly**: every enabled weekday at `TimeOfDay`.
- **Once** and **Countdown**: a single occurrence, or none while the countdown
  isn't running.

The operations are the latest occurrence at or before *t*, the first occurrence
after *t*, and the number of occurrences in a range (for "missed 4 times").
Interval works these out arithmetically through occurrence indices, with no
iteration, so "every second, unattended for a year" stays cheap.

## Alarm evaluation (`AlarmEngine.cs`)

Whether a task is triggered is **derived** from the schedule and `DoneThroughUtc`.
It is never stored. So triggering writes nothing and creates no conflict to
sync: each device reaches the same answer independently.

- **Pending occurrence**: the earliest occurrence *o* with
  `DoneThrough < o ≤ now`. **Triggered** means one exists.
- **Latest occurrence**: with `OnSchedule`, the latest occurrence at or before
  now. With `FromDone` it equals the pending occurrence, because the timer stops
  at the first one until the task is marked done.
- **Snoozed** when `SnoozeOccurrence ≥ latest` and `now < SnoozeUntil`. A
  **popup should show** when the task is triggered and not snoozed. So a new
  `OnSchedule` occurrence during a snooze reminds at once.
- **Mark done**: with `FromDone`, `Anchor = now` and the next occurrence is
  N after that. Otherwise `DoneThrough = now`, which covers every pending
  occurrence.
- **Dismiss**: `SnoozeOccurrence = latest`, and `SnoozeUntil = now + reminder`,
  or "never" when the reminder interval is 0.
- **Saving a schedule change**: `ScheduleRevision++`, a fresh state with
  `DoneThrough = save time`, and `Anchor` cleared. Only occurrences after the save
  fire. A start of "now", the editor's default, therefore means the first alarm
  comes one interval from now, not immediately.
- **Next wake-up**: the earliest of each untriggered task's next occurrence and
  each snoozed task's `SnoozeUntil` (or its next `OnSchedule` occurrence, if that
  is sooner). Android hands this to `AlarmManager`. The desktop polls every
  second, which also gets sleep, resume and clock changes right with no extra
  work.
- **Late warning (R13)**: when a popup first shows for occurrence *o* at time
  *t* with `t − o` over 15 s, the popup says it is late by
  `TextFormat.LateWarning`, for example "2 days, 3 hours, 5 minutes" (the three
  largest non-zero calendar units), and
  why: "the app wasn't running" if *o* came before the process started, or "the
  device was asleep or busy" otherwise. A late re-reminder is measured from
  `SnoozeUntil` instead.

## Persistence (`TaskStore.cs`)

`tasks.json` and `settings.json` live in `%LOCALAPPDATA%\AlarmCalendar\` on
desktop and in the app's files directory on Android. Writes go to a temporary
file, which then replaces the real one, so a crash can't leave half a file.
`tasks.json.bak` keeps the previous good copy. An unreadable file is renamed
aside rather than overwritten. The store owns the node's `Hlc` and raises
`Changed`, with the IDs that changed and whether the change was local or came
from sync.

## Sync merge

Each part of a task merges on its own:

- **Definition**: last writer wins by `DefVersion`.
- **State**: last writer wins by `(Revision, StateVersion)`, except that
  `DoneThroughUtc` is always the **maximum** of the two sides, whichever wins.

That last rule is what makes "done" stick. Suppose the phone was off when a task
fell due, and the PC marked it done meanwhile. The phone triggers it on startup,
but triggering writes nothing, so when the PC's larger `DoneThrough` arrives the
phone's popup simply closes. Nothing can lower `DoneThrough`, so no ordering of
messages can bring a finished occurrence back.

`Hlc` is `(milliseconds, counter, nodeId)`. Each local write ticks it to
`max(wall clock, last)`, and receiving a record advances it past the incoming
version. So a device whose clock runs slow still issues versions newer than
anything it has already seen.

## Sync network (Phase 3)

Derived from LanLink.Core. The design, not the code, is copied, because
LanLink's protocol carries its own message kinds and is being changed
separately (see *Simplification opportunities*).

- **Port 37660**, TCP for sessions and UDP for discovery. Different from
  LanLink's 37656, so both apps can run on one machine.
- **Discovery**: a UDP broadcast every 3 s on each interface's subnet broadcast
  address: `{app, proto, node, name, port, group}`. `group` is the sync
  group's public ID, not its secret.
- **Sessions**: frames of `[4B length][body]`. Each side sends a plaintext
  `hello`: `{node, name, group, nonce, proto}`. With the same group, both
  derive per-direction keys from the group secret and the two nonces (HKDF-SHA256),
  and every later frame is AES-GCM with a counter nonce. A peer with the wrong
  secret fails its first frame and is dropped. As in LanLink, **the lower node ID
  dials**, a fresh `hello` from a known node replaces the old connection, TCP
  keep-alive is on, and inbound connections from outside the LAN are refused
  unless `AcceptExternalConnections` is set.
- **Pairing (linking)**: on a session between different groups, the joining
  device sends `pair_request` with an ephemeral ECDH P-256 key. The other replies
  with its own key. Both show a 6-digit code derived from the shared secret, and
  the user accepts on the receiving device, which sends `pair_accept` carrying its
  group ID and secret, encrypted under the ECDH secret. The joiner adopts that
  group and reconnects, and the two task lists merge. Comparing the codes defeats
  a man in the middle on the LAN.
- **Replication**: when a session starts, both sides send a `digest`:
  `{id, defVersion, stateVersion, doneThrough}` for every task. Each then pushes
  `tasks` for every record where it is newer in any of those three, or that the
  other side lacks. Local changes go to all sessions at once. A record that
  changes local state is **forwarded to every other session** (gossip). Gossip
  is what bridges LANs: a PC with a remote link carries updates between its LAN
  and the far one, with no routing table, and a record that changes nothing is not
  forwarded, so loops end.
- **Remote addresses**: kept in settings, dialled persistently, with a backoff
  from 5 s up to 60 s.

## Desktop app

- `App.xaml.cs`: single instance through a named mutex and a "show" event (the
  same as LanLink). It reads `--minimized`, which autostart passes.
- `MainWindow`: the task list, refreshed every second; the tray icon; the
  1-second tick that drives `AlarmEngine` and opens `AlarmWindow`s.
- `TaskEditorWindow`: the R3/R4 layout, with `GroupBox`es styled as rounded
  rectangles. Esc is `IsCancel`. The description `TextBox` has
  `AcceptsReturn = true`, so Enter adds a line rather than pressing Save.
- `AlarmWindow`: one per ringing task, topmost.
- `SoundPlayer`: WPF `MediaPlayer`, looping on `MediaEnded`. Only one sound
  plays at a time (D5). The newest popup's sound plays, and closing it resumes
  the sound of the newest popup still open.
- `Autostart.cs`: a single per-user `HKCU\...\Run` entry, as in LanLink.

## Android app

- `MainActivity`: the list. `TaskEditActivity`, `SettingsActivity`,
  `DevicesActivity`: the other screens.
- `AlarmScheduler`: puts the engine's next wake-up into
  `AlarmManager.SetAlarmClock`. It needs `USE_EXACT_ALARM` (API 33+), or
  `SCHEDULE_EXACT_ALARM` below that.
- `AlarmReceiver` → evaluation → `AlarmService` (a `mediaPlayback` foreground
  service that plays the sound and posts a full-screen-intent notification) →
  `AlarmActivity`, shown over the lock screen.
- `BootReceiver` (boot, time or time zone change, package replaced):
  reschedules, then evaluates, so missed alarms fire with the late warning.
- `SyncService`: a `connectedDevice` foreground service that keeps sync running
  in the background. It is a `connectedDevice` service rather than `dataSync`,
  because Android 15 caps `dataSync` at 6 hours a day.

## Simplification opportunities

- LanLink.Core and this project's sync layer share a design: discovery, framing,
  lower-ID dialling, hello replacement. A shared networking library would serve
  both, but extracting it means changing LanLink, so it was left alone.
