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
  from 5 s up to 60 s. A remote link doesn't redial while a session to its node
  exists by another path, so a LAN session and a remote link to the same node
  don't keep replacing each other.
- **Discovery port vs session port**: discovery always uses UDP 37660 with
  `ReuseAddress`, and the TCP port each node listens on travels in the
  announcement. So several instances on one machine, or a node with a custom
  port, still find each other.
- **Startup settling (desktop)**: for 6 s after startup, an alarm missed while
  the app was closed waits before it rings, so a linked device that already
  marked it done can say so first. Without this, the phone and every other
  device would ring for something already dealt with.
- **Code layout** (`AlarmCalendar.Core/Sync/`): `Protocol.cs` (messages,
  framing, `SyncCrypto`), `Discovery.cs`, `PeerSession.cs` (one connection, its
  keys and its pairing state), `SyncEngine.cs` (listener, dialling, sessions,
  replication, pairing). The engine raises its events on pool threads; the apps
  marshal them to their UI thread.
- **Identity**: the node ID, device name, group ID and group secret live in
  `AppSettings`. Pairing raises `IdentityChanged` for the app to persist.
  Settings windows change only their own fields, through `UpdateSettings(edit)`
  (`AppHost` on desktop, `AppState` on Android), on the settings as they are at
  save time. A window that saved the copy it took on opening would silently
  undo a pairing made while it was open.
- **Merge across revisions**: `DoneThroughUtc` takes the maximum only between
  states of the same schedule revision. A newer revision replaces it outright,
  which is the one way a `DoneThrough` left in the future by a wrong clock
  gets lowered: saving the schedule again. A new revision is
  `max(definition's, state's) + 1`, since the two merge separately and the
  state can be ahead.

## Testing

- `AlarmCalendar.Core.Tests`: schedule math, the engine, merging (including a
  randomized three-device gossip test), persistence, and sync between real
  engines over loopback TCP (pairing, bridging, a wrong secret, a rejected
  pairing).
- The desktop app is driven with UI Automation from PowerShell, in a throwaway
  `--data-dir` whose `settings.json` sets `autostartInitialized: true` and
  `startWithWindows: false`, so no `Run` entry is written.
- **`ALARMCAL_BIND=127.0.0.1`** (an environment variable, for development
  only) makes the desktop app listen on loopback with no LAN discovery. Two
  instances on one machine can then be linked through remote addresses
  (`127.0.0.1:<port>`) without raising a Windows Firewall prompt for the debug
  build.

## Desktop app

- `App.xaml.cs`: single instance per data directory, through a named mutex and
  a "show" event (the same as LanLink). Command line: `--minimized` (to the
  tray, which autostart passes) and `--data-dir <dir>`. If startup fails, it
  shows the error and exits rather than holding the mutex invisibly.
- `AppHost`: the long-lived services (settings, store, sounds, alarms, sync),
  plus `UpdateSettings(edit)`. Every settings change goes through
  `UpdateSettings`, which applies a window's edits to the *current* settings.
  So a pairing, or the window placement, changed while the window was open isn't
  reverted.
- `AlarmCoordinator`: the 1-second tick. It evaluates every task and opens,
  re-alerts or quietly closes `AlarmWindow`s. A window opened by "Show alarm" on
  a snoozed task doesn't ring until its reminder comes due.
- `MainWindow`: the task list (rows updated in place, `Move` keeping the
  selection), the toolbar, and the tray icon. The close button hides it to the
  tray.
- `TaskEditorWindow`, `SettingsWindow`, `DevicesWindow` and
  `SoundPickerWindow` are all **non-modal**. WPF's `ShowDialog` disables every
  other window of the thread, ringing alarm windows included, and an alarm must
  stay answerable. In the editor, the description `TextBox` has
  `AcceptsReturn`, so Enter adds a line. There is no default button; Esc is
  `IsCancel`, and Cancel closes the window.
- `AlarmWindow`: one per ringing task, topmost. Closing it any way the user can
  dismisses the task; `CloseQuietly` and exiting the app (`AppHost.Exiting`,
  also set when Windows ends the session) don't. The task stays due and rings
  late on the next start. Of the reminder and restart fields, it writes back
  only the ones the user changed.
- `SoundService`: one sound at a time (D5). The newest alert's sound plays, and
  stopping it resumes the newest remaining looping one. WAV files play through
  `SoundPlayer`, which needs no Media Foundation, so Windows N editions work too;
  other formats play through WPF `MediaPlayer`. A failure falls back to the
  built-in `Alarm01.wav` and reports it in the alarm window, then to the
  system beep.
- `Autostart.cs`: a single per-user `HKCU\...\Run` entry, as in LanLink. An
  instance with its own `--data-dir` gets an entry of its own name, and is never
  enabled on its first run.
- `Fields.cs`: the editors' parsing, which lives in Core's `FieldParsing`, so
  Android uses the same rules.

## Android app

Plain .NET for Android (`net10.0-android`) with AndroidX views and XML layouts;
it runs on Android 10 (API 29) and up. The root namespace is
`AlarmCalendar.Droid`, because inside a namespace called `AlarmCalendar.Android`,
`Android.App` would resolve to `AlarmCalendar.Android.App`.

- `AppState` (with `App`, the `Application` class, which initializes it before
  any component runs) is the counterpart of the desktop's `AppHost`: settings,
  store, sync engine, `UpdateSettings(edit)`. Any store change reschedules the
  alarm and pokes the ringing service.
- `MainActivity`: the list, refreshed every second. A row is redrawn only when
  its text changes. Redrawing every row every second kept the UI from ever
  being idle, which costs battery, and stops `uiautomator` from dumping the
  screen. `TaskEditActivity`, `SettingsActivity` and `DevicesActivity` are the
  other screens. Inside an activity, the bare name `AlarmService` means
  `Context.AlarmService` (a string), which is why the ringing service is
  called `RingingService`, and why the static intent helpers are named
  `IntentFor`, not `Intent`.
- `AlarmScheduler`: one `AlarmManager.SetAlarmClock` alarm for the engine's
  next wake-up. It needs `USE_EXACT_ALARM` (API 33+, granted at install to alarm
  apps), or `SCHEDULE_EXACT_ALARM` below that, and falls back to an inexact alarm
  without it. **Anything due now goes through an alarm set one second ahead,
  never a direct service start**. Starting a foreground service from an exact
  alarm is always allowed, whereas a boot receiver may not start a
  media-playback service at all (Android 15), and nor may `App.OnCreate` when a
  broadcast woke the process (measured: "mAllowStartForeground false").
- `AlarmReceiver` → `RingingService` (a `mediaPlayback` foreground service). It
  re-evaluates every second, posts one notification per ringing task (full-screen
  intent to `AlarmActivity`, with Mark done and Remind me later handled by
  `AlarmActionReceiver`), plays the sound, vibrates, and stops itself when
  nothing rings. So done or dismissed anywhere, another device included, stops
  it.
- `AlarmSound`: `MediaPlayer` on the alarm stream, one sound at a time, like the
  desktop's. It tries the chosen sound, then the phone's alarm, ringtone and
  notification defaults, then the first installed alarm sound. An emulator, or
  a phone, can have no default set, and checking whether a default's address
  can be opened is no test: the settings-provider address fails that check yet
  plays. Only a failure of the user's own choice is reported.
- `AlarmActivity`: `showWhenLocked` and `turnScreenOn`. Back counts as Close,
  remind me later, and only reminder and restart fields the user changed are
  written back.
- `BootReceiver` (boot, time or time-zone change, package replaced): clears the
  time-zone cache and reschedules, so missed alarms ring with the late warning.
  It also starts `SyncService`.
- `SyncService`: a `connectedDevice` foreground service that keeps the process,
  and with it the engine in `AppState`, alive in the background. It is
  `connectedDevice` rather than `dataSync` because Android 15 caps `dataSync` at
  6 hours a day; `CHANGE_WIFI_MULTICAST_STATE` qualifies it. It is sticky, so
  Android restarts it, and sync, after killing the process.
- `SoundChooser`: Android's ringtone picker (alarms, ringtones, notifications),
  or the document picker for a file, taking a persistable read grant so the file
  still plays after a reboot.
- `Permissions`: notifications (API 33+), exact alarms (API 31–32), full-screen
  intents (API 34+). The list asks once per run; Settings shows each one's state
  with a Fix button, plus a note about battery optimization.
- Release builds trim. Core is rooted (`TrimmerRootAssembly`, `RootMode="All"`)
  and reflection-based System.Text.Json is kept on, because the store, the
  settings and the sync messages are deserialized by reflection.

## Simplification opportunities

- LanLink.Core and this project's sync layer share a design: discovery, framing,
  lower-ID dialling, hello replacement. A shared networking library would serve
  both, but extracting it means changing LanLink, so it was left alone.
