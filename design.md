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
| `WeekInterval`, `WeeksFrom`, `FirstDayOfWeek` | Weekly, every N weeks (S7): N (1 is every week), a date in the first week that rings, and the day weeks start on. That day comes from the culture of the device that first saved the schedule, and editing elsewhere keeps it, so every device agrees which weeks ring. |
| `At` | Once: a wall-clock date and time |
| `Duration` | Countdown: the length |
| `ReminderMinutes` | How long after a dismissal it reminds again, while not done. 0 means it doesn't. |
| `WindowsSound`, `AndroidSound` | A `SoundRef` per platform, since a sound picked on one means nothing on the other. `null` means the device's default applies (R11). |
| `SoundMode` | `Once` or `Repeat`. `null` means the device's default applies. |
| `Volume`, `FadeInSeconds` | S10: percent of the device's alarm volume, and how long the sound takes to grow to it. `null` means the device's default applies; the choices both apps offer are `SoundLevels.Volumes` and `SoundLevels.FadeIns`. |
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
| `PausedAtUtc` | When the user paused the task; `null` while it isn't paused |

Per-device `AppSettings` (not synced): defaults for the sound, sound mode, volume and fade-in,
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
- **Weekly**: every enabled weekday at `TimeOfDay`. Every N weeks: only in the
  weeks numbered 0, N, 2N… counting from the week of `WeeksFrom` (weeks
  starting on `FirstDayOfWeek`), and none before it. The previous and next
  ringing days are found by scanning day by day across one cycle (7·N + 8 days;
  N is at most 99), with week numbers taken from day numbers rather than
  `DateTime` arithmetic, which a record from elsewhere with no first week would
  push before year 1.
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
  or "never" when the reminder interval is 0. A **snooze preset** (S5: 5, 10 or
  30 minutes or an hour, `AlarmEngine.SnoozePresets`, on both alarm screens)
  sets `SnoozeUntil = now + preset` instead, leaving the task's reminder
  interval as it is.
- **Saving a schedule change**: `ScheduleRevision++`, a fresh state with
  `DoneThrough = save time`, and `Anchor` cleared. Only occurrences after the save
  fire. A start of "now", the editor's default, therefore means the first alarm
  comes one interval from now, not immediately. A paused task stays paused,
  except a countdown restarted on purpose, which runs.
- **Pause (S4, S6)**: `PausedAt = now`. A paused task is never triggered, so
  pausing one that rings stops it, and it has no wake-up. **Resume**:
  `DoneThrough = now`, so whatever came up while paused (and before) is passed
  over rather than ringing all at once. A countdown's end moves on by the time it
  was paused, so it takes up where it stopped, with the time it had left. Both
  are state changes, synced like a mark-done.
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

## Calendar (`CalendarMonth.cs`, S2)

`CalendarMonth.Build` lays out a month for both apps: six weeks from the
culture's first day of the week, each day with its tasks' times. It is built
from the schedules alone, like everything else, and nothing records a history.
So it shows what is still to come or still waiting (`Waiting`: came up, not
marked done), and nothing at or before a task's `DoneThrough`: times already
dealt with, and times before the schedule was saved, which the schedule would
otherwise also give.

- **Paused tasks** don't show.
- **Several times a day** (a fixed-length interval under a day) shows as one
  line a day ("every 15 min"), at its first time.
- **Restarts when done:** only the next time is known; the later ones assume
  the task is marked done on time and are marked `Estimated` ("~9:00 AM").
- **The desktop window** (`CalendarWindow`) has a grid of up to four lines a
  day, plus "+N more", and the chosen day's full list beside it, which opens
  the editor. It rebuilds on every store change and when the date changes.
- **Android** (`CalendarActivity`, from the ⋮ menu) shows each day's first
  time and "+N", with the chosen day's list below it.

## Persistence (`TaskStore.cs`)

`tasks.json` and `settings.json` live in `%LOCALAPPDATA%\AlarmCalendar\` on
desktop and in the app's files directory on Android. Writes go to a temporary
file, which then replaces the real one, so a crash can't leave half a file.
`tasks.json.bak` keeps the previous good copy. An unreadable file is renamed
aside rather than overwritten. The store owns the node's `Hlc` and raises
`Changed`, with the IDs that changed and whether the change was local or came
from sync.

**Export and import (S8)**: `Export` writes every live task to a JSON file
(`{app, format, exportedUtc, tasks}`). `Import` reads one back:

- A task this device has merges by the sync rules (`MergeRules.Merge`), so an
  old backup can't undo newer edits.
- One it doesn't have, or has deleted, comes back as a new revision that
  outranks the tombstone, with a fresh `DefVersion`. Its definition, anchor, a
  running countdown's end and a pause are kept. `DoneThrough` becomes the
  import time, so nothing in the past rings; a paused task keeps its own, since
  resuming passes over the past anyway.
- The change is raised as a local one, so it reaches linked devices like an
  edit.
- A file that isn't an export of this app, or is from a newer format, is
  refused with `InvalidDataException`.

Both apps offer it under *Your tasks* in Settings: file dialogs on the desktop,
the document picker on Android.

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
  address: `{app, proto, node, name, port, group, at, boot, seq, mac}`. `group`
  is the sync group's public ID, not its secret, so claiming it proves nothing:
  `mac` is an HMAC-SHA256 of the other fields under a key derived from the group
  secret, and an announcement claiming this node's group without a valid one is
  dropped. The rest makes a recording useless (`AdmitMemberAnnouncement`).
  `boot` is a random ID of the sender's process and `seq` its announcement's
  number in it: within one process the number must grow, which no change of
  either clock can upset. An announcement from another process of the sender
  must have a later sending time (`at`) than the last accepted, and every one
  must be within 10 minutes of this device's clock. So a replay, from anywhere,
  is ignored: it can neither keep a device that left the group a member nor
  point dials at the replayer. (A device whose clock is further off goes
  unannounced, and so, until its clock catches up, does one that restarted with
  its clock set back; the log says why, and remote links still work.) Only
  members so proven are shown as linked from announcements alone, and dialled.
- **Sessions**: frames of `[4B length][body]`. Each side sends a plaintext
  `hello`: `{node, name, group, nonce, proto}`. With the same group, both
  derive per-direction keys from the group secret and the two nonces (HKDF-SHA256),
  and every later frame is AES-GCM with a counter nonce. A peer with the wrong
  secret fails its first frame and is dropped. As in LanLink, **the lower node ID
  dials** (but if a same-group peer keeps announcing for 10 s with no session,
  the higher ID dials too, in case a firewall refuses the lower one), TCP
  keep-alive is on with `NoDelay`, and inbound connections from outside the LAN
  are refused unless `AcceptExternalConnections` is set. The LAN is loopback,
  private and link-local addresses (`Protocol.IsPrivateOrLan`, as in LanLink). A
  global IPv6 address can't be told apart (its owner may be down the hall or a
  customer of the same ISP), so **without `AcceptExternalConnections` the
  listener takes IPv4 only** (`SyncEngine.ListensOnIpv6`). A LAN peer dialling a
  host name that resolves to both is then refused on IPv6 at connect time and
  falls back to IPv4; accepting the connection and then dropping it would leave
  it no reason to. With it, the listener is dual-mode (IPv4 and IPv6) when bound
  to `Any`, so IPv6 remote addresses work. Discovery is IPv4 broadcast anyway.
- **Hello proves nothing; the first sealed frame does.** That frame is a tiny
  `ready`, and a session is `Authenticated` once its peer's `ready` opens. The
  digest goes out only after that. It can be any size, while frames from a peer
  that hasn't authenticated are limited to 64 KB (32 MB after), and at most 32
  unproven connections are accepted at a time.
- **Takeover rules.** A fresh `hello` replaces a stale session (as in LanLink),
  but only one that is unsealed, unauthenticated and not pairing. A session
  claiming the group takes over only when it authenticates, and one that hasn't
  authenticated within 15 s is closed. An inbound session whose hello gives the
  node ID of a remembered linked device without the group (`IsLinkedPeer`) is
  kept apart: it never speaks for that device (it isn't installed, so the device
  isn't shown "not linked"), but it may ask to pair, so a device that left the
  group can link again through the usual codes. Otherwise anyone on the LAN could
  knock a linked device off its connection, or make it appear "not linked" and
  invite a re-link. Only authenticated sessions receive broadcasts. Node IDs must
  be 32 hex digits, which makes the key labels unambiguous. The protocol version
  in `hello` must match.
- **Bounded noise.** At most 256 devices are remembered from announcements. When
  the table is full, a proven member still gets in, in place of the stalest
  stranger, and a stranger's announcement can't take over a fresh member's
  entry by giving its node ID. Inbound connections from outside the group are
  closed after 3 minutes idle (with no pairing under way). `PeersChanged` is
  coalesced to about four a second.
- **Pairing (linking)**: commit, then reveal, then **both users accept**. The
  requester sends `pair_request {commit = SHA-256(its ECDH P-256 key, members),
  members}`, the accepter answers `pair_challenge {its key, members}`, and the
  requester reveals its key in `pair_reveal`, which must match the commitment.
  Each side's key is fixed before it sees the other's, so a man in the middle
  can't search for a key that makes the two 6-digit codes agree (without the
  commitment it could, in seconds). The code covers both keys and both member
  counts. Both devices then show the code and ask their user (`PairRequested`
  fires on both), and **each links only on its own user's yes**. The accepter's
  yes sends `pair_accept`. The requester completes only when it has both
  `pair_accept` and its own user's yes, and the accepter adopts a `pair_group`
  only after its own user's yes. So no device can be pulled into a group, or
  hand over its group's key, without the person in front of it comparing the
  codes.
  **The device linked with fewer others joins the other's group** (`members`
  comes from the app's remembered peers; on a tie, the requester joins), so
  linking a new device from an established one brings the new one in. When the
  requester joins, `pair_accept` carries the accepter's group, sealed under the
  ECDH secret with counter 0. Otherwise the requester sends its own group in
  `pair_group` with counter 1. The two never share a nonce, whatever either side
  is told. Both prompts say which way it goes, and warn when the joiner will
  leave devices behind.
  **Rounds.** Each attempt is a `PairRound` (`PeerSession.cs`), and every message
  after the request carries its ID, the commitment (`round`). A message for any
  other round, one already over on this side, is ignored, and an answer acts only
  on the round it was asked about, so however the two users' answers and retries
  interleave, nothing from one try reaches the next. Each side keeps its round
  open until it is decided for both: the accepter, after saying yes to a
  requester that joins, still waits for that user's answer (the requester's
  joining and reconnecting, or its `pair_reject`), so the connection stays
  protected from the idle timeout and its slot stays taken. One round at a time
  on each side, and per connection; further requests get
  `pair_reject {reason: busy}`. When a round ends unanswered (the other side said
  no or went away), its prompt is `Withdrawn` and the screens close it. A round
  that ends without linking leaves the connection free to try again, and the
  requester always hears Accepted, Rejected or Failed. A round unanswered for 5
  minutes ends with `pair_reject {reason: timeout}`. The accepter waits 30 s
  longer, so the requester, which started first, always gives up first: a
  `pair_group` sent just before then still finds the accepter's round open,
  rather than a requester reporting a link that never happened.
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
  don't keep replacing each other. It redials at once, skipping the backoff, when
  a pairing on its session said yes on this side (`LinkedJustNow`: the session
  is closed as one side joins, and the other side may not be able to dial back,
  behind NAT), and when this node's group changes (pairing, leaving), which
  kicks every link, whether it was waiting or dialling at that moment.
- **Discovery port vs session port**: discovery always uses UDP 37660 with
  `ReuseAddress`, and the TCP port each node listens on travels in the
  announcement. So several instances on one machine, or a node with a custom
  port, still find each other.
- **Startup settling**: an alarm missed while the app (or phone) was off waits
  before it rings, until the first complete exchange with a linked device
  (`FirstSyncDone`), and for at most 15 s. A device that remembers no linked
  peers doesn't wait at all. Completion is explicit: after answering a peer's
  digest, a node sends `digest_done`, and frames arrive in order, so receiving it
  means every record that peer had to send has arrived. Without the wait, the
  phone and every other device would ring for something already dealt with.
- **Remote addresses report their state** (`RemoteStates()`: connected,
  can't connect, doesn't have the key, not linked) in the Devices screens. A
  link's backoff resets only after a session that got into sync, or lasted 30 s.
- **Code layout** (`AlarmCalendar.Core/Sync/`): `Protocol.cs` (messages,
  framing, `SyncCrypto`), `Discovery.cs`, `PeerSession.cs` (one
  connection and its keys, and `PairRound`), `SyncEngine.cs` (listener, dialling, sessions,
  replication, pairing). The engine raises its events on pool threads; the apps
  marshal them to their UI thread.
- **Identity**: the node ID, device name, group ID and group secret live in
  `AppSettings`. Pairing raises `IdentityChanged` for the app to persist.
  Settings windows change only their own fields, through `UpdateSettings(edit)`
  (`AppHost` on desktop, `AppState` on Android), on the settings as they are at
  save time. A window that saved the copy it took on opening would silently
  undo a pairing made while it was open. Remembered linked devices
  (`KnownPeers`, which drive the Devices list, `members`, `IsLinkedPeer` and
  startup settling) are updated from `PeerSeen`, which fires only for sessions
  of the current group. So leaving a group, which closes its sessions, doesn't
  bring its devices straight back.
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
  other formats, and a WAV that is to play quieter or fade in (S10), play
  through WPF `MediaPlayer`, the only one of the two with a volume. Where it
  can't play a WAV, the WAV falls back to `SoundPlayer` at full volume, and the
  alarm window says so. Any other failure falls back to the built-in
  `Alarm01.wav` and reports it in the alarm window, then to the system beep.
  A fade-in (`SoundLevels.FadeLevel`) rises with the square of the time, from 2%
  of the target, in 200 ms steps. It counts from when the alarm began, so a
  sound resumed after another alarm interrupted it doesn't start quiet again.
- `WakeTimer` (S3): one waitable timer (`CreateWaitableTimerExW`,
  `SetWaitableTimer` with `fResume`), kept set by the coordinator 20 s before
  the next wake-up (`AlarmEngine.NextWakeUp`), so the computer wakes from sleep
  and the app is up when the alarm is due. It is re-armed only when that time
  changes, and closing the app destroys it. Windows must allow wake timers, which
  many laptops don't on battery; a system that can't resume this way is logged.
  While an alarm window is sounding, `SetThreadExecutionState` keeps the system
  and the display on, since after a timer wake Windows leaves the display off
  and goes back to sleep within about two minutes. The **Wake the computer from
  sleep for alarms** setting (on by default) turns the timer off.
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
  broadcast woke the process (measured: "mAllowStartForeground false"). Only an
  alert not yet posted re-arms that one-second alarm; otherwise a refusal would
  repeat every second.
- `AlarmReceiver` → `RingingService` (a `mediaPlayback` foreground service). It
  re-evaluates every second, posts one notification per ringing task (full-screen
  intent to `AlarmActivity`, with Mark done and Remind me later handled by
  `AlarmActionReceiver`), plays the sound, vibrates, and stops itself when
  nothing rings. So done or dismissed anywhere, another device included, stops
  it. It is sticky, so a ringing alarm survives the process being killed; if
  Android then refuses it the foreground (a restart in the background, Android
  12 and later), the safety net rings instead. The notification rules:
  - **The service's own foreground notification has a fixed ID that is never a
    task's, on its own low-importance channel.** Android won't cancel a running
    foreground service's notification, and removes the old one when the service
    moves to another ID. It must not alert either, or Android groups it with the
    alarm (next rule).
  - **Each alarm notification has an explicit group of its own.** Android
    auto-groups an app's ungrouped alerting notifications, and an auto-grouped
    alarm's full-screen intent doesn't open the alarm screen (measured on API
    36: the screen stayed off).
  - **Alarm notifications alert once**: posted again over themselves (the service
    taking over from the safety net or from a process that died, a silenced
    safety-net alarm), they neither sound nor open the alarm screen a second
    time. A new alert, the first or a later occurrence of a task still showing,
    is cancelled and posted afresh, by the service and the safety net alike, so
    it does both.
  - **Their buttons act only while their task rings.** A leftover notification's
    Mark done would otherwise mark a task done that isn't due, which moves an
    interval task that restarts when done; it is withdrawn instead.
  - **What has been posted outlives the process**, and so does the record of it.
    Each alarm notification carries its alert (task, due time, late warning,
    sound problem, silenced) in its extras. When a process starts,
    `RingingService.RestoreFromShown` rebuilds `Alerts` and `Silenced` from the
    notifications showing. Otherwise a process started after one that died (by a
    notification button, sync, Android) would post every alert again, sounding
    a silenced safety-net alarm and reopening the alarm screen, or leave one up
    once answered.
  - **Answered alarms are withdrawn** (`WithdrawAnswered`, on every evaluation
    and store change): every alert whose task should no longer alert. Its
    notification, silencing and sound go. One merely held back while sync
    settles after a start is kept.
  - The service's notification is re-posted only when the count changes.
  Silence stops a task's vibration as well as its sound; vibration repeats while
  some unsilenced alert repeats. A later alert of the same task sounds again,
  and the alarm screen's Silence works again for it.
- **The safety net**: if Android refuses to start `RingingService` (a battery
  manager, or an inexact alarm on Android 12 without exact-alarm access), the
  receiver posts each alert itself on `alarms_fallback`, a channel that carries
  the phone's alarm sound, insistent (repeating) for a task whose sound repeats.
  Android plays it, so it rings even if the process dies; the task's own sound
  isn't used. Silence posts it again on the silent alarm channel. Since no
  service is running to notice, `RingingService.Poke` (on every store change)
  withdraws each safety-net notification whose alarm no longer rings, answered
  here, by a button, or on another device. When the service is refused the
  foreground after starting, it stops counting as running at once, so nothing
  has it play over the safety net and then stop.
- `AlarmSound`: `MediaPlayer` on the alarm stream, one sound at a time, like the
  desktop's, with the task's volume and fade-in (`SetVolume`, the same curve as
  the desktop's). It tries the chosen sound, then the phone's alarm, ringtone and
  notification defaults, then the first installed alarm sound. An emulator, or
  a phone, can have no default set, and checking whether a default's address
  can be opened is no test: the settings-provider address fails that check yet
  plays. Only a failure of the user's own choice is reported.
- `AlarmActivity`: `showWhenLocked` and `turnScreenOn`, one ringing task at a
  time. It plays nothing itself. When its task stops ringing for any reason
  (answered here, through a notification button, or on another device), it
  moves on to the next ringing task, or closes, in the background too. Opened
  again for the task it shows, it keeps what the user has typed. Opened from
  "Show alarm", it stays up while its task is merely waiting. Back counts as
  Close, remind me later, and only reminder and restart fields the user changed
  are written back.
- **Direct boot**: the app is `directBootAware`, and its files live in
  device-protected storage. The locked-boot receiver can then set alarms before
  the first unlock after a restart, so a phone that rebooted overnight for an
  update still rings at 07:00. That is what Android's own clock app does. The
  cost: those files are protected by the device key only, not the user's PIN.
  Earlier versions kept them in credential-protected storage. On the first start
  while the phone is unlocked they are copied over, overwriting; then a marker
  file (`moved-to-device-storage`) records that device storage holds the data;
  only then are the old files deleted. Until the marker exists the old files are
  the data: a start whose copy failed runs from them and copies again next time,
  so nothing saved since is overwritten. A process started while the phone is
  locked before that can't know the data, so it is
  `Provisional`: nothing is read or written, and sync stays off, until
  `LoadOnceUnlocked`, which the unlock broadcast (a receiver registered at run
  time), the boot-completed broadcast and the first screen opened each call.
- **Pairing questions** (`AppState.PendingPrompt`, a queue): the Devices screen
  asks the first while it is in front, and a notification stands for it
  otherwise, including after the screen goes to the background unanswered. An
  answer or a withdrawal removes the question and its notification, and brings
  up the next.
- **Startup settling** holds only alarms genuinely missed, those due more than a
  minute before the process started (`AlarmEngine.MissedWhileNotRunning`). On
  Android the alarm usually starts the process itself, a moment after it is due,
  and that alarm is on time.
- `BootReceiver` (locked boot, boot, time or time-zone change, package
  replaced): clears the time-zone cache and reschedules, so missed alarms ring
  with the late warning. It also starts `SyncService`, and on boot completed
  loads the data of a process that started while the phone was locked.
- `SyncService`: a `connectedDevice` foreground service that keeps the process,
  and with it the engine in `AppState`, alive in the background. It is
  `connectedDevice` rather than `dataSync` because Android 15 caps `dataSync` at
  6 hours a day; `CHANGE_WIFI_MULTICAST_STATE` qualifies it. It is sticky, so
  Android restarts it, and sync, after killing the process; if Android then
  refuses it the foreground (Android 12 and later, in the background), it stops,
  and sync waits for the app to be opened. A start refused for any reason (the
  background, an app op, a permission) is logged, never a crash.
- `SoundChooser`: Android's ringtone picker (alarms, ringtones, notifications),
  or the document picker for a file, taking a persistable read grant so the file
  still plays after a reboot. Its callback is fixed when it is created, so a
  pick that returns to a recreated activity still lands. The editor and Settings
  save their picked dates, times, sound and weekday boxes across recreation
  (rotation, dark mode).
- `Screen`: the base class of every activity, which holds what each would
  otherwise repeat.
- `Permissions`: notifications (API 33+), exact alarms (API 31–32), full-screen
  intents (API 34+). The list asks once per run; Settings shows each one's state
  with a Fix button, plus a note about battery optimization.
- Release builds trim fully, with trim warnings as errors. Core serializes
  through a source-generated `JsonSerializerContext` (`CoreJson`), so nothing
  depends on reflection.

## Releases (S9)

As in LanLink, with PowerShell instead of batch files: the project folder's `&`
is a command separator to `cmd`.

- **`VERSION`** (four numbers) is the single source of the version. It becomes
  the desktop assembly's version, Android's `versionName`, and its
  `versionCode` (a·10⁶ + b·10⁴ + c·10² + d), which Android compares for
  upgrades.
- **`build-msi.ps1`**: a self-contained single-file `AlarmCalendar.exe`
  (win-x64), then `installer/AlarmCalendar.wxs` with WiX 7 and its UI and
  Firewall extensions.
  - Per machine, into `Program Files\AlarmCalendar`, with no `&` in the folder
    name. The install folder is remembered for upgrades, and the `UpgradeCode`
    is fixed forever.
  - A Start menu shortcut.
  - Firewall exceptions for the exe on private and domain networks (not
    public), which replace the first-run prompt.
  - No license page, since none is chosen.
  - "Launch now" on the last page, run as the installing user.
  - An upgrade first runs the installed exe with `--exit`, which signals the
    running copy (through a named event per data folder, like the existing
    "show" one) and waits until it has exited, so the exe can be replaced.
- **`build-apk.ps1`**: the Release APK with that version. It is signed with the
  key the `ALARMCAL_*` environment variables name (passwords passed as
  `env:NAME`, so they appear on no command line), or this machine's debug key.
- **`release-github.ps1`**: builds both and creates the GitHub release
  `v<version>` with `gh`, unless it exists already. The APK is optional.
  `-DryRun` builds and prints the command; the repository comes from `origin`
  or `-Repo`.

## Simplification opportunities

- LanLink.Core and this project's sync layer share a design: discovery, framing,
  lower-ID dialling, hello replacement. A shared networking library would serve
  both, but extracting it means changing LanLink, so it was left alone.
