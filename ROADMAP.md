# Alarm & Calendar — roadmap

This roadmap is the project's memory. The request (in `todo.txt`) is too long to
keep in mind at once, so every requirement in it has an ID below, and every phase
lists the IDs it delivers. **Before starting work, read this file and
`design.md`. When a phase item is finished, tick it here in the same commit.**

Status key: `[x]` done · `[~]` partly done (see the note) · `[ ]` not started.

## Requirements, from `todo.txt`

| ID | Requirement | Phase |
|---|---|---|
| R1 | The main window lists the tasks. The user can create, edit and delete one. | 2 |
| R2 | In the editor, the description field is multi-line: Enter adds a new line rather than closing. **Save** saves. **Esc** or **Cancel** closes the editor without saving. | 2, 4 |
| R3 | A top-level radio button chooses between **Scheduled** and **Countdown timer**. Each has its own pane in a rounded rectangle. | 2, 4 |
| R4 | Inside the Scheduled pane, a nested radio button chooses **Every N…** or **On days of the week**, again in a nested rounded rectangle. A **Once at…** option is added too (see S1). | 2, 4 |
| R5 | Every N: N, plus a unit picked from a list (years, months, weeks, days, hours, minutes, seconds), and a start date and time that default to today and now. | 1, 2, 4 |
| R6 | Days of the week: a checkbox per weekday, plus a time of day. That time defaults to the **Default time of day** setting, which starts at **09:00** (the answer to "what should it default to?"). | 1, 2, 4 |
| R7 | Every N: choose whether the timer restarts **when the task is marked done**, or keeps to the original schedule, with the next occurrence coming up **on time**. This can also be changed in the alarm window. | 1, 2, 4 |
| R8 | Reminder interval: how long after you close the reminder, without marking the task done, it reminds you again. Hours and minutes only. Also changeable in the alarm window. | 1, 2, 4 |
| R9 | A one-shot countdown timer that sounds an alarm, made in the same dialog under its own radio button and pane. | 1, 2, 4 |
| R10 | Per-task sound: a button that shows the task's current sound and opens a picker for a system sound or a sound file. | 2, 4 |
| R11 | A default sound in Settings. Changing it changes the sound of every task still left on "default". | 1, 2, 4 |
| R12 | Tasks survive reboots, countdowns included. | 1, 2, 4 |
| R13 | A reminder that fell due while the app was closed is shown as soon as the app opens, with a warning that it is late by so many years, months, weeks, days, hours, minutes or seconds. | 1, 2, 4 |
| R14 | Sound mode: play the sound once per popup, or repeat it until the popup is dismissed or the task is marked done. Per task, with a default in Settings. | 1, 2, 4 |
| R15 | Each row of the main window shows a short schedule summary, the start of the description, whether the task is triggered and waiting to be marked done (and if so, when it reminds next, as a time and as h:mm from now), and for a repeating task, when the next occurrence is. | 2, 4 |
| R16 | An Android version with the same features. | 4 |
| R17 | Instances on several computers and the phone link the way `..\lanlink` does: LAN auto-discovery, connecting to a remote address, and bridging between LANs. Task entries **and states** sync between them. | 3, 4 |

Derived requirements (implied by the above, not spelled out in it):

| ID | Requirement | Phase |
|---|---|---|
| D1 | Desktop: single instance, a tray icon, closing the window minimizes it to the tray, and the app starts with Windows (minimized). Without that, R12 and R13 mean little: no alarm can ring after a reboot until the app is opened again. | 2 |
| D2 | Alarms stay correct across sleep and resume, changes to the system clock and DST transitions. | 1, 2, 4 |
| D3 | Sync goes only between **your** devices. Linking them takes an explicit pairing step, and traffic is authenticated and encrypted. LanLink syncs with anything on the LAN, which is fine for one-off file sends but wrong for a list of private tasks that is merged automatically. | 3 |
| D4 | Android: exact alarms that fire in Doze, rescheduling after boot, an alarm screen that shows over the lock screen, and a sound that keeps playing while the app is in the background. | 4 |
| D5 | Several alarms ringing at once don't play over each other. | 2, 4 |

Suggestions, not requested. S1 is built; the rest wait for a yes:

| ID | Suggestion |
|---|---|
| S1 | **Once at a date and time**, as a third nested schedule option. An "alarm & calendar" app without plain one-off reminders seemed incomplete. Built. |
| S2 | A **calendar view**: a month grid showing every task's occurrences. The name promises a calendar, but the request has none. |
| S3 | **Wake the PC from sleep** for an alarm, with a waitable timer (`SetWaitableTimer`, `fResume`). |
| S4 | **Pause** a task without deleting it. |
| S5 | **Snooze presets** in the alarm window (5, 10 or 30 minutes), next to the reminder-interval fields. |
| S6 | Countdown **pause/resume**, and **restart** from the list. |
| S7 | **Every N weeks on chosen weekdays**, for example every other Monday and Thursday. |
| S8 | **Export and import** tasks as a JSON file. |
| S9 | Releases: an MSI and APK, `VERSION` and a release script as in LanLink. |
| S10 | Per-task volume, and a gradual volume ramp-up. |

## Phases

### Phase 0: groundwork
- [x] `ROADMAP.md`, `design.md`, `CLAUDE.md`, git, `.gitignore`
- [x] Solution layout: `AlarmCalendar.Core` (net10.0), `AlarmCalendar.Core.Tests` (xUnit), `AlarmCalendar.Desktop` (WPF), `AlarmCalendar.Android` (.NET for Android, no MAUI)

### Phase 1: Core, shared by both apps (R5–R9, R11–R14, D2)
- [ ] Model: `AlarmTask` = definition + state, each with its own version (see design.md → *Data model*)
- [ ] Schedule engine: occurrences for Every N (calendar units in local wall-clock time, fixed-length units in absolute time), weekly, once and countdown. Latest occurrence at or before a time, and the first after it. DST gaps and overlaps.
- [ ] Alarm evaluation: triggered or not, the pending occurrence, the reminder (snooze) state, the next wake-up time, and which popups should be showing
- [ ] Actions: mark done (each restart mode), dismiss, change the reminder interval or restart mode from the alarm window, start a countdown
- [ ] Delay text: "late by 2 days, 3 hours, 5 minutes", from years down to seconds
- [ ] Sound reference model: default, a system sound or a file, kept per platform, with resolution falling back to the default
- [ ] Persistence: JSON store, atomic writes, tolerant of a corrupt file (keeps a backup)
- [ ] Hybrid logical clock and merge rules (design.md → *Sync merge*)
- [ ] Unit tests for all of the above

### Phase 2: Windows desktop app (R1–R15, D1, D2, D5)
- [ ] Main window: task list (R15), New / Edit / Delete, double-click to edit, Delete key, context menu (Mark done, Show alarm)
- [ ] Task editor dialog: R2–R10 and R14, laid out exactly as R3 and R4 describe
- [ ] Sound picker: the Windows media sounds, a file, or "default"; preview
- [ ] Alarm popup: description, scheduled time, late warning (R13), restart mode (R7), reminder interval (R8), **Mark done**, **Dismiss**, **Silence**; Esc dismisses
- [ ] Sound playback: once or looping, one sound at a time across popups (D5)
- [ ] Settings window: default sound, sound mode, reminder interval and time of day; start with Windows; close to tray
- [ ] Single instance, tray icon, autostart, resume-from-sleep and time-change handling
- [ ] Checked by driving the app on the desktop: screenshots of each window, and an alarm actually ringing

### Phase 3: sync (R17, D3)
- [ ] Networking derived from LanLink.Core: UDP discovery, framed TCP, the lower node ID dials, a fresh hello replaces a stale connection, keep-alive, the external-connection gate, remote addresses
- [ ] Pairing: ECDH key exchange, a 6-digit confirmation code shown on both devices, and the joining device adopting the group key
- [ ] Session authentication and AES-GCM encryption under the group key
- [ ] Replication: digest exchange on connect, change broadcast, gossip forwarding (which gives LAN bridging with no routing table), tombstones
- [ ] Desktop UI: a Devices window listing discovered, linked and connected devices, with Link, Unlink and Add remote address
- [ ] Tests: two or three in-process nodes on loopback converge, including concurrent edits and mark-done against trigger

### Phase 4: Android (R2–R17 on the phone, D4)
- [ ] Project builds to an APK; package `com.inhahe.alarmcalendar`
- [ ] Main list, task editor, settings and sound picker (ringtones, alarms, notifications, a file), devices screen
- [ ] AlarmManager `setAlarmClock` for the next wake-up; receiver → evaluation → alarm service (media playback, full-screen-intent notification) → alarm activity over the lock screen
- [ ] Boot, time-change and package-replaced receivers reschedule; missed alarms show with the late warning
- [ ] Sync foreground service (`connectedDevice`) for staying linked in the background
- [ ] Checked on the emulator (`inkdown_test`): screenshots, an alarm ringing with the screen locked, sync with the desktop app

### Phase 5: polish and docs
- [ ] `README.md` (the user-facing document): features, settings, how to link devices
- [ ] `known-issues.md` kept current
- [ ] Independent code review of each batch (global rules)
