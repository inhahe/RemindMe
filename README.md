# Alarm & Calendar

Reminders and alarms that keep nagging until you mark them done, on Windows and
Android, kept in sync between your computers and your phone.

## Tasks

Each task has a description (several lines if you like) and one of these
schedules:

| Schedule | What it does |
|---|---|
| **Every N** years, months, weeks, days, hours, minutes or seconds | Starts at a date and time you choose (today and now by default). |
| **On days of the week** | Tick the days, and choose a time (09:00 unless you change the default in Settings). |
| **Once** | A single date and time. |
| **Countdown timer** | A one-shot timer: it rings once when it runs out. |

For an **Every N** task, choose what happens when you mark it done:

- **N after it's marked done**: the timer restarts when you mark it done. "Water the
  plants every 3 days" means 3 days after you actually watered them.
- **On the original schedule**: the next time comes up on time whatever you do. If
  you haven't marked the last one done, the alarm simply rings again.

Times keep to the wall clock through daylight-saving changes: "every day at 9:00"
stays at 9:00. Hours, minutes and seconds count real elapsed time: "every 8
hours" stays 8 hours apart.

## When a task comes up

An alarm window opens on top of everything, shows the description, and plays the
task's sound:

- **Mark done**: the task is dealt with until its next time.
- **Close, remind me later** (or Esc, or the window's ✕): the task stays waiting to
  be marked done, and the alarm comes back after the task's **reminder interval**
  (hours and minutes; 0 h 0 min means not until the next time it comes up).
- **Silence**: stops the sound but keeps the window open.

You can change the reminder interval and the restart choice right there in the
alarm window.

**Missed alarms.** If the app wasn't running when something came up (the
computer was off, say), the alarm appears as soon as the app starts, with a
warning: "This reminder is late by 2 days, 3 hours, 5 minutes: the app wasn't
running at the time." Tasks and running countdowns survive restarts.

## The task list

Each row shows the start of the description, the schedule, the status (ringing,
or waiting to be marked done and when it reminds next, e.g. "reminds today
14:30 (in 1:25)"), and when it next comes up. Ringing tasks are listed first.

Double-click a task (or press Enter) to edit it, and press Delete to delete it.
Right-click for **Mark done**, **Show alarm** (reopens a snoozed alarm without
ringing it), and **Restart countdown**.

## Sounds

The **Sound** button in the task editor shows the task's sound; click it to pick
a Windows sound, a sound file (WAV, MP3, WMA, M4A, AAC, FLAC), or **Default**.
**Play it** chooses between once each time the alarm appears and over and over
until the alarm is closed or marked done.

Tasks left on **Default** follow the default in **Settings**, including when you
change it later. If a sound can't be found or played, the built-in alarm sound
plays instead and the alarm window says so.

## Settings

| Setting | What it does |
|---|---|
| Sound / Play the sound | The default sound and sound mode for tasks left on "Default". |
| Remind again after | The reminder interval new tasks start with. |
| Time of day for new days-of-the-week tasks | 09:00 unless changed. |
| For new "every N" tasks | Which restart choice new tasks start with. |
| Start with Windows (in the tray) | On by default: alarms only ring while the app runs. |
| Closing the main window keeps the app running in the tray | On by default. Use **Exit** (toolbar or tray menu) to quit. |

## Syncing your devices

Open **Devices…** (in the toolbar or the tray menu).

- **On the same network**: other copies of the app appear by themselves. Select
  one and click **Link…**. Both devices then show a six-digit code and ask
  whether to link. Check that the two codes match, then accept on both; neither
  links until its own user accepts. A request nobody answers is dropped after 5
  minutes.
  The two devices then share all their tasks, and keep the tasks and their state
  in sync: mark a task done on one, and its alarm stops on the others. It doesn't
  matter which device you click Link on: the one linked with fewer devices joins
  the other's group, and the prompt warns you if a device would leave others
  behind.
- **Over the internet**: on the device the others will reach, forward the port
  (37660 by default) to it and tick **Accept connections from outside the local
  network**. On the others, add its address (`host` or `host:port`) under
  **Devices elsewhere**, then link as above. With that box unticked, a device
  accepts connections only from its local network: private addresses, and IPv6
  addresses within its own network's prefix.
- **Bridging**: a device linked to both a far device and the ones near it
  carries updates between them, so every linked device stays in sync.
- **Leave the sync group…** stops this device syncing. It keeps its tasks, and the
  others keep theirs.

Only linked devices can read or change your tasks: traffic between them is
encrypted, and a device without the group's key gets nothing. The first time
the app listens on the network, Windows asks whether to allow it. Allow it on
private networks.

## Command line

| Option | Effect |
|---|---|
| `--minimized` | Start in the tray (start with Windows uses this). |
| `--data-dir <folder>` | Keep tasks and settings in another folder; that folder gets its own instance. |

Tasks and settings live in `%LOCALAPPDATA%\AlarmCalendar` (`tasks.json`,
`settings.json`, each with a `.bak` of the previous version) along with `app.log`.

## Android

The phone app has the same tasks, schedules, alarm screen, settings and sync,
laid out for a phone:

- **The list**: tap a task to edit it, long-press for Mark done, Show alarm,
  Restart countdown and Delete, and tap **+** for a new task. The ⋮ menu has
  **Devices and sync** and **Settings**.
- **The editor**: as on Windows. **Back** or **Cancel** leaves without saving.
- **When a task comes up**: with the phone locked, the alarm screen opens over
  the lock screen; otherwise it appears as a notification with **Mark done** and
  **Remind me later** (tap it for the full alarm screen). Back on the alarm screen
  is the same as **Close, remind me later**. Alarms play at the alarm volume, so Do
  Not Disturb lets them through unless it is set to block alarms. They also
  vibrate, and they are set again after a restart. Ones missed while the phone
  was off ring when it starts, marked late. If Android or the phone's battery
  manager won't let the app play its own sound, the alarm still rings, with the
  phone's alarm sound.
- **Sounds**: pick one of the phone's alarm, ringtone or notification sounds, or
  a sound file.
- **Settings** also shows whether notifications, exact alarms and full-screen
  alarms are allowed, with a button to fix each. Background sync is most reliable
  with battery optimization off for the app.
- **Sync**: while sync is on, a quiet "Keeping your devices in sync" notification
  stays up; that is what lets your other devices reach the phone in the
  background. Link the phone in **Devices and sync** as on Windows.

Needs Android 10 or later. The APK is installed by hand (it isn't in an app store).

## Building

Needs the .NET 10 SDK (with the Android workload for the phone app).

```
dotnet build AlarmCalendar.slnx
dotnet test AlarmCalendar.Core.Tests
dotnet publish AlarmCalendar.Android -c Release -f net10.0-android
```

The APK lands in `AlarmCalendar.Android/bin/Release/net10.0-android/publish/`
(`com.inhahe.alarmcalendar-Signed.apk`). It is signed with the debug key, which
is fine for installing by hand.
