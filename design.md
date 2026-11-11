# RemindMe — design

Reminders and alarms on Windows and Android, kept in sync between your devices.
Progress and the full requirement list are in `ROADMAP.md`. This file covers how
it is built.

## Projects

| Project | Framework | Holds |
|---|---|---|
| `RemindMe.Core` | `net10.0` | Everything that isn't UI: the model, schedule math, alarm evaluation, persistence, merging, and (Phase 3) the sync protocol and networking |
| `RemindMe.Core.Tests` | `net10.0`, xUnit | Tests for Core |
| `RemindMe.Desktop` | `net10.0-windows`, WPF, with WinForms interop for the tray icon | The Windows app |
| `RemindMe.Android` | `net10.0-android`, .NET for Android with AndroidX views. Not MAUI. | The Android app |

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
| `WeekInterval`, `WeeksFrom`, `FirstDayOfWeek` | Weekly, every N weeks (S7): N (1 is every week), a date in the first week that rings, and the day weeks start on. That day comes from the culture of the device that first saved the schedule, and editing elsewhere keeps it, so every device agrees which weeks ring. Both editors save through `TaskDefinition.AsWeekly`, which keeps that rule. |
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
  except a countdown restarted on purpose, which runs. The editor's "start the
  countdown again" box starts ticked only for a countdown that has finished, not
  for one running or paused (`TextFormat.RestartCountdownByDefault`).
- **Pause (S4, S6)**: `PausedAt = now`. A paused task is never triggered, so
  pausing one that rings stops it, and it has no wake-up. **Resume** is a
  mark-done at the moment of resuming (`AlarmEngine.MarkDone`): `DoneThrough =
  now`, so whatever came up while paused (and before) is passed over rather than
  ringing all at once, and a task that restarts when done counts its next time
  from now. A countdown's end first moves on by the time it was paused, so it
  takes up where it stopped, with the time it had left. Both are state changes,
  synced like a mark-done.
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
  Once it has come up, its timer has stopped: the time it came up is the one
  `Waiting` entry, and the estimates count from now, as if it were marked done
  now (`AlarmEngine.MarkDone`).
  For one several times a day, the line of the day its next time falls on is
  exact; the others are estimates.
- **Keeping up**: `CalendarMonth.NextChange` gives the first exact time shown
  still to come. Estimates don't count: a waiting task's are reckoned from now,
  so they would always be about to change. They are reckoned afresh on every
  rebuild. Both screens rebuild at that time, on every store change, and when
  the date changes (`CalendarMonth.Stale`, checked as `CalendarMonth.TickDelay`
  says: just after the next change, and at least once a minute). So a time
  turns red as it comes up.
- **The desktop window** (`CalendarWindow`) has a grid of up to four lines a
  day, plus "+N more", and the chosen day's full list beside it, which opens
  the editor. Each day is a button, named for a screen reader by its date and
  task count. Only the chosen day is a tab stop, so Tab enters the grid there
  and goes on to the day's list; the arrow keys (the grid's own handler: WPF's
  directional navigation, tried first, didn't move between these buttons) move
  a day or a week, choosing as they go, and turn the month past the six weeks
  shown.
  The 42 buttons are made once and filled anew on each update (a task
  changing, a time coming up, the month turning), and the list's rows
  (`DayEntry`, kept by their `DayKey`: the task and the time, or for an
  estimate, whose time moves, which of the task's estimates on the day it is,
  counted from the last, since marking a time done takes the first away) are
  updated in place: the focus and the selection stay where they are, and a
  screen reader isn't sent back to the start. Store changes arriving together
  rebuild once. Turning the month with the focus in the grid moves it to the
  chosen day; a focused button whose name changes (its date, as the month
  turns) raises a name change for screen readers, which drop a repeated focus
  event. A list row with the focus that goes passes it to the row now in its
  place, or to the chosen day, and Enter opens the focused row's task.
- **Android** (`CalendarActivity`, from the ⋮ menu) shows each day's first
  time, in short ("9:00a", "~6:30p", or "18:30" with a 24-hour clock), and
  "+N", with the chosen day's list below it.

## Persistence (`TaskStore.cs`)

`tasks.json` and `settings.json` live in `%LOCALAPPDATA%\RemindMe\` on
desktop and in the app's files directory on Android. Writes go to a temporary
file, which then replaces the real one, so a crash can't leave half a file.
`tasks.json.bak` keeps the previous good copy. An unreadable file is renamed
aside rather than overwritten. The store owns the node's `Hlc` and raises
`Changed`, with the IDs that changed and whether the change was local or came
from sync.

**Export and import (S8)**: `Export` writes every live task to a JSON file
(`{app, format, exportedUtc, tasks}`). `Import` reads one back:

- Every record keeps the versions and revision it was exported with and merges
  by the sync rules (`MergeRules.Merge`). So an old backup can't undo newer
  edits: not here, and not on the device it came from once the two are linked,
  as fresh versions would.
- Nothing in the past rings: wherever the file's state replaces this device's
  (a task it doesn't have, or the file's state is of a newer revision),
  `DoneThrough` is raised to the import time. Within one revision that is "done"
  moving forward, which merging carries as it is, so the version stays. A
  paused task keeps its own, since resuming passes over the past anyway. One
  consequence: once linked, the raise reaches the device the file came from too,
  passing over an occurrence still waiting there, as marking it done here would.
- A task this device deleted comes back with a fresh `DefVersion`, outranking
  the tombstone, and its state merges as above. It gets a new revision, with a
  fresh `StateVersion` and `DoneThrough` = now, only if the tombstone is of a
  newer revision than the file, in its state or in its definition (the task was
  rescheduled after the export, then deleted). The definition counts because it
  syncs apart from the state: a schedule's definition can arrive before its
  state, which would otherwise outrank the restored one when it comes.
- Records that aren't well formed (`MergeRules.IsWellFormed`: no ID,
  definition or state, a null description or sound name) are skipped, as sync
  skips them. JSON can leave a member null whatever its declared type: an
  explicit `null`, or a record's missing constructor parameter.
- The change is raised as a local one, so it reaches linked devices like an
  edit.
- A file that isn't an export of this app (`tasks` missing, say), or is from a
  newer format, is refused with `InvalidDataException`.

Both apps offer it under *Your tasks* in Settings: file dialogs on the desktop,
the document picker on Android.

**Automatic backups (S11, `Backups.cs`)**: a file in the export format, written
once the tasks have changed and nothing more has changed for 5 s
(`Backups.Settle`). Import is therefore the restore.
- **What counts as a change**: `Backups.Fingerprint`, the live tasks' IDs and
  `DefVersion`s. Adding, editing and deleting change it, whether made here or
  arriving by sync. Marking done, snoozing and pausing don't, so they write
  nothing. A backup is written only when the fingerprint differs from that of
  this device's newest backup, so a restart or an import that changes nothing
  writes nothing.
- **A series, not one file**: overwriting a single file would make it a mirror.
  A task deleted by mistake (or by a linked device) would then be gone from the
  backup within seconds. `Backups.ToDelete` keeps the ten newest backups
  whatever their age, every one from the last day, the newest of each day for
  60 days, and the newest of each month for two years. Ages come from the
  clock, so the ten newest are kept regardless: a clock set days ahead to try
  an alarm would otherwise thin out the day's backups for good.
- **Names**: `remindme-backup-2026-11-07-075300-0500-de193e1e.json`. The
  local time is the one people recognise; its UTC offset makes backups taken
  across a clock change or a flight between time zones order by the moment they
  were taken. Then comes `Backups.DeviceTag` (the start of the sync node ID).
  Several devices can share one folder (a Documents folder in the same cloud
  drive), and each thins out only its own series. `FileName` refuses a tag that
  `Parse` wouldn't recognise.
- **`BackupRunner`** (Core) makes every decision, for both apps, against an
  `IBackupFolder` (list, read, write, delete; failures as `IOException`), so it
  is all tested:
  - Each attempt lists the folder once and works from that list.
  - It reads this device's newest backup the first time, and again whenever
    that backup has gone (deleted by hand, or by a cloud drive freeing space),
    so an emptied folder gets a new backup without waiting for a change.
  - A backup dated more than a minute ahead (written while the clock was) isn't
    taken as the newest: it would stay "newest" for days, and every start
    would write another backup beside it.
  - If the newest backup can't be read (a cloud file left online-only, say), a
    fresh one is written rather than none ever again.
  - A new name never repeats an existing one: names go to the second, and a
    second backup within one second moves on a second. The same name would
    replace the first on Windows, and MediaStore would rename the second past
    `Parse`.
  - The fingerprint counts only well-formed records, as a file's does, so the
    store and its backup agree whatever the store holds.
  - Old backups are thinned after a write, and once per folder, not on every
    attempt.
  - A failure of any kind goes into `Problem` (the Settings line) and is
    logged once per message, never thrown: a backup must neither take the app
    down nor stop without a word. An old backup that can't be deleted is also
    logged once.
  - The apps only decide when to attempt and supply the folder.
  - `TextFormat.BackupStatus` words the Settings line for both: off, the
    failure, the last backup, or none yet.
- **Desktop** (`BackupService`): the folder in Settings, by default
  `Documents\RemindMe backups`, where people look for their files and
  which a cloud drive often keeps. `AppHost.BackupFolderOf` turns the setting
  into the folder for both the service and the Settings window. An instance
  with its own `--data-dir` uses `<data dir>\backups` instead, keeping test data
  out of the real folder, and so does one whose Documents folder Windows can't
  resolve. Attempts run on the thread pool: the folder may be a network share
  that hangs, and the UI thread runs the alarms. An attempt still settling at
  exit runs then, and exit waits for it 5 s at most. Files are written with
  `Json.WriteAtomically`, which flushes to disk and leaves no temp file behind
  when a write fails.
- **Android** (`AutoBackup`): MediaStore's `Download/RemindMe backups`.
  It needs no permission, shows in the Files app and the import's picker, and
  survives an uninstall. The app sees only the files it wrote itself, so after a
  reinstall it starts a new series and leaves the old one alone. Shared storage
  opens only after the first unlock since a restart, but the tasks can change
  before then (they live in device storage, and sync runs). So an attempt while
  locked tries again on unlock (`AppState.WhenUnlocked`, which the data loading
  uses too). Files are written with `IS_PENDING` until complete. The work runs
  off the main thread, since MediaStore calls go to another process, and an
  attempt queries the media provider once, then reads and deletes through that
  listing.
- **Limit**: Import merges, so restoring brings back deleted tasks but doesn't
  undo an edit made since the backup: the newer version wins.

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
  in `hello` must match (`Protocol.Version`, raised whenever a build without a
  change would misread or drop what a build with it sends: 4 added the pause,
  every-N-weeks, volume and fade-in fields, which an older build would drop and
  then erase everywhere with its next write).
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
- **Code layout** (`RemindMe.Core/Sync/`): `Protocol.cs` (messages,
  framing, `SyncCrypto`), `Discovery.cs`, `PeerSession.cs` (one
  connection and its keys, and `PairRound`), `SyncEngine.cs` (listener, dialling, sessions,
  replication, pairing). The engine raises its events on pool threads; the apps
  marshal them to their UI thread.
- **Identity**: the node ID, device name, group ID and group secret live in
  `AppSettings`. Pairing raises `IdentityChanged` for the app to persist. The
  stored `DeviceName` is only a name the user gave; empty, the device goes by
  its own name, read at each start (`NameOr`): the computer's as typed
  (`Dns.GetHostName`, not the capitals of `Environment.MachineName`), or the
  phone's from Settings → About phone (`Settings.Global.DEVICE_NAME`), else its
  maker and model. `SetDeviceName` stores a name blank or equal to the device's
  own (in any case) as empty, so it follows a rename. Up to 0.1.1 the own name
  was stored on first run: `Load` empties one still equal to it; one that
  differs can't be told from a name given.
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

- `RemindMe.Core.Tests`: schedule math, the engine, merging (including a
  randomized three-device gossip test), persistence, and sync between real
  engines over loopback TCP (pairing, bridging, a wrong secret, a rejected
  pairing).
- The desktop app is driven with UI Automation from PowerShell, in a throwaway
  `--data-dir` whose `settings.json` sets `autostartInitialized: true` and
  `startWithWindows: false`, so no `Run` entry is written.
- **`REMINDME_BIND=127.0.0.1`** (an environment variable, for development
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
- `DevicesWindow`'s list is refreshed every 3 seconds and on every sync change.
  Its rows, the task list's and the calendar day list's are `Observable`, kept
  by a key and updated in place by `Rows.Update`, so the selection and the
  keyboard focus aren't thrown off a row at each refresh. It runs Core's
  `ListSync.Reconcile` (unit-tested): rows whose key is gone are removed, the
  longest run of rows already in order stays, and every other row moves once,
  so one task re-ranked is one move, not a row-by-row shuffle a screen reader
  would hear; a key given twice is an error, before anything changes. If the
  row with the window's logical focus goes (which is also where it is while the
  keyboard is in the toolbar, whose Delete keeps it), `Rows.Update` passes the
  focus to the row now in its place (or a fallback), once the list is laid out,
  scrolling it into view if need be; updates in between (one per task deleted)
  move it on. It takes the keyboard focus only in the active window, setting
  the logical focus otherwise. The selection doesn't follow: an action on it
  would then act on a row nobody chose.
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
  `Alarm01.wav`, at the task's volume and with its fade carrying on, and
  reports it in the alarm window, then to the system beep.
  A fade-in (`SoundLevels.FadeLevel`) rises with the square of the time, from 2%
  of the target, in 200 ms steps. It counts from when the alarm began, so a
  sound resumed after another alarm interrupted it doesn't start quiet again,
  and on a monotonic clock (`Environment.TickCount64`; Android's
  `SystemClock.ElapsedRealtime`), so a clock change can't stall or skip it.
- `WakeTimer` (S3): one waitable timer (`CreateWaitableTimerExW`,
  `SetWaitableTimer` with `fResume`), kept set by the coordinator 20 s before
  the next wake-up (`AlarmEngine.NextWakeUp`), so the computer wakes from sleep
  and the app is up when the alarm is due. It is set again when that time
  changes, when it has gone off while the computer was awake (so a computer put
  to sleep in the last 20 s still wakes), and after a failure to set it, which
  is logged once. Closing the app destroys it. Windows must allow wake timers,
  which many laptops don't on battery; a system that can't resume this way is
  logged. While an alarm window's sound plays (`SoundService.IsPlaying`),
  `SetThreadExecutionState` keeps the system and the display on, since after a
  timer wake Windows leaves the display off and goes back to sleep within about
  two minutes. Once it stops (played once, or silenced), the computer may sleep
  again, the alarm window still on screen. The **Wake the computer from sleep
  for alarms** setting (on by default) turns the timer off.
- `Autostart.cs`: a single per-user `HKCU\...\Run` entry, as in LanLink. An
  instance with its own `--data-dir` gets an entry of its own name, and is never
  enabled on its first run.
- `Fields.cs`: the editors' parsing, which lives in Core's `FieldParsing`, so
  Android uses the same rules.

### Look: WPF's Fluent theme

- Every window takes WPF's Fluent theme (.NET 9 and up), following Windows'
  light or dark mode, and switching when it changes. `App.xaml` merges
  `PresentationFramework.Fluent`'s `Themes/Fluent.xaml` itself, so that its own
  styles can be `BasedOn` the theme's named styles (`DefaultButtonStyle`,
  `AccentButtonStyle`, ...) while the file loads. That merge also sets the
  mode: WPF reads it from the file's name (`Fluent.xaml` is "System";
  `Fluent.Light.xaml` or `Fluent.Dark.xaml` would pin one) and replaces the
  dictionary with the theme in Windows' colours, again whenever Windows
  changes. `Application.ThemeMode` is left unset: it would insert a second copy
  of the theme ahead of this one.
- A style without `BasedOn` would bring back the old Aero look, and an implicit
  one `BasedOn="{StaticResource {x:Type Button}}"` in `App.xaml` finds itself (a
  keyed one, such as `PaneRadio`, finds the implicit style).
- Colours are the theme's brushes as `DynamicResource`
  (`TextFillColorSecondaryBrush`, `SystemFillColorCriticalBrush`,
  `CardBackgroundFillColorDefaultBrush`, ...), never fixed values; code sets them
  with `SetResourceReference` (the calendar's cells). Shared styles in
  `App.xaml`: `Pane` and `NestedPane` (cards), `Section`, `Hint`, `Error`
  (collapsed while empty), `Primary` (the window's main button, in the accent
  colour), `ToolButton` (the toolbar's, flat, with its own template), `Small`
  (number boxes), `Icon`.
- Icons are characters of Windows' icon font, Segoe Fluent Icons, named in
  `Icons` (XAML: `{x:Static local:Icons.Edit}`). A button keeps its text as its
  `Content`, so screen readers name it and its access key works; `Icons.Glyph`,
  an attached property, gives the icon, which the `WithIcon` template puts
  before the text.
- A screen reader names a list's row by its item's `ToString()`, unless told
  otherwise: `TaskRow` and `DeviceRow` return the task's description and the
  device's name (which typing in the list also finds rows by), and their
  status goes in `AutomationProperties.ItemStatus`. The sound picker's items,
  Core's `SoundRef` records, are named by `AutomationProperties.Name`. An item
  whose content is a `TextBlock` (the calendar's day list) is named by its text.
  A `ListView` with a `GridView` takes its rows' style `BasedOn`
  `GridView.GridViewItemContainerStyleKey`: the plain list item has no columns.
- The main window's toolbar is a `ToolBar` with a template of its own (`Bar`):
  too narrow for its buttons, it puts the last ones in a menu under a More
  button. The button keeps its room while hidden, since showing it would take
  room from a button and change what fits. Devices, Calendar, Settings and Exit
  are a plain row docked first, so they stay. The toolbar lets Tab go on past it
  (a `ToolBar` keeps it cycling inside), and the main window's
  `PreviewTextInput` gives Alt and the access key to the buttons in the More
  menu, which Windows' access keys skip while the menu is closed.
- Workarounds for the theme: its accent button doesn't show access keys (the
  `AccessKeyText` template does); its text box shows a clear button over the
  text when focused unless it wraps (`Small` sets `WrapWithOverflow`, which never
  breaks a number); its toolbar's overflow chevron is faint and easily missed;
  its buttons are `VerticalAlignment="Center"` and `HorizontalAlignment="Left"`
  by default.
- `ScreenFit` keeps the windows that size themselves to their contents (the
  editor, Settings, Devices and the alarm) on their screen: no taller than its
  work area, their contents scrolling above the buttons and the error line,
  which stay in view. It starts from the screen the window opens on (its
  owner's, or the mouse's for the alarm, as WPF centres them), and moves the
  window back inside when it opens or its contents grow it. Where the user puts
  or sizes it is left alone: nothing moves while it is maximized, during
  Windows' move and size loop, or once the user has sized it (`SizeToContent`
  is then `Manual`); dragged to another screen, it takes that screen's height
  when the drag ends.
- The tray icon is WinForms' `NotifyIcon`, with a WPF `ContextMenu`
  (`MainWindow.xaml`'s `TrayMenu`) so the menu takes the theme too. It is not
  shared: each opening makes a new one, with the theme as it is then. A test app
  showed a closed menu kept in resources, outside every window, keeping its
  light colours after a switch to dark, while a menu set as an element's
  `ContextMenu` (the task list's) followed it.
  Opening it makes its window the foreground, or a click elsewhere wouldn't
  close it.

## Android app

Plain .NET for Android (`net10.0-android`) with AndroidX views and XML layouts;
it runs on Android 10 (API 29) and up. The root namespace is
`RemindMe.Droid`, because inside a namespace called `RemindMe.Android`,
`Android.App` would resolve to `RemindMe.Android.App`.

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

As in LanLink and the user's other projects, `release.bat` publishes. The work
is in PowerShell rather than batch: the project folder's name has an `&`, a
command separator to `cmd`.

- **`VERSION`** (two to four numbers) is the single source of the version.
  It becomes the desktop assembly's version, Android's `versionName`, and its
  `versionCode` (a·10⁶ + b·10⁴ + c·10² + d), which Android compares for
  upgrades. `version.ps1`, which all three scripts dot-source, reads it and
  enforces both platforms' limits. The first part can be at most 255, as MSI
  allows. The others can be at most 99, so the `versionCode` stays unique and
  increasing. MSI ignores the fourth part, so the installer's
  `AllowSameVersionUpgrades` makes a fourth-part bump an upgrade anyway. The
  price: an older MSI that differs only in the fourth part installs over a
  newer one without the downgrade message.
- **`build-msi.ps1`**: two single-file builds of the desktop app (win-x64),
  then `installer/RemindMe.wxs` with WiX 7 and its UI and Firewall extensions.
  Everything goes into `release/<version>/`.
  - The self-contained build, with .NET included (around 70 MB, compressed),
    goes into the MSI and beside it as `RemindMe.exe`.
  - The framework-dependent build (under 1 MB; it needs the .NET 10 Desktop
    Runtime) is `RemindMeFD.exe`.
  - These are the names the author's NotepadRedo uses for its two exes: plain,
    without the version, which is the release's. So each version gets a folder
    of its own: a running copy of an earlier build (being tried) holds its exe,
    and mustn't block the next version.
  - The script deletes its own files from an earlier run of the version first,
    and says what to close when a running copy holds one.
  - Each build publishes with its own `--artifacts-path`, so the two can't mix
    up their runtime settings in a shared `obj`. Both publish folders live in
    one temporary folder, deleted when the script ends.
  - Each publish must have made nothing but `RemindMe.exe` (and pdbs). Only
    the exe is released, so a native library left beside it would be missing
    from the release.
  - The MSI keeps .NET included, so installing needs nothing else.
  - Per machine, into `Program Files\RemindMe`. The install folder is
    remembered for upgrades, and the `UpgradeCode` is fixed forever (it
    predates the rename from Alarm & Calendar, which nothing released had).
  - A Start menu shortcut.
  - Firewall exceptions for the exe on private and domain networks (not
    public), which replace the first-run prompt.
  - A license page: `LICENSE`, which the script turns into the RTF the page
    needs (`Write-LicenseRtf`), so the text is kept in one place.
  - "Launch now" on the last page, run as the installing user.
  - An upgrade first runs the installed exe with `--exit`, which signals the
    running copy (through a named event per data folder, like the existing
    "show" one) and waits until it has exited, so the exe can be replaced.
- **`build-apk.ps1`**: the Release APK with that version. It is signed with the
  key the `REMINDME_*` environment variables name (passwords passed as
  `env:NAME`, so they appear on no command line), or this machine's debug key.
- **`release.bat`** runs **`release.ps1`**, which publishes to
  `inhahe/RemindMe`, modelled on the user's Pitchstone `release.bat`.
  - What is released is `main` as GitHub has it. It refuses another branch,
    uncommitted changes, an untracked file where the build reads, and a commit
    GitHub's `main` doesn't have.
  - It refuses a version already released, or tagged without a release, or not
    above the highest version among the `v*` tags (whatever order they were
    made in; a tag that isn't a version is skipped).
  - Then the tests, `build-msi.ps1` and `build-apk.ps1`. All four files in
    `release/<version>/` are required.
  - Then `gh release create --target <commit>`. The notes give what it is, the
    alpha warning, which Windows file to take (with the exes' measured sizes),
    how to install, and the commits since the last release. A 0.x version is a
    pre-release.
  - `--dry-run` does everything but publish, and shows the notes. The checks
    a real run stops at are only warnings in it, so a release can be tried
    before anything is pushed.
  - `release.bat` runs it in Windows PowerShell 5.1. Under
    `$ErrorActionPreference = 'Stop'`, that ends the script at the first native
    command whose redirected stderr has a line, so the git and gh checks that
    may fail go through `Invoke-Probe`.

## Simplification opportunities

- LanLink.Core and this project's sync layer share a design: discovery, framing,
  lower-ID dialling, hello replacement. A shared networking library would serve
  both, but extracting it means changing LanLink, so it was left alone.
