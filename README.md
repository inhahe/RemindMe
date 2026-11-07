# RemindMe

Reminders and alarms that keep nagging until you mark them done, on Windows and
Android, kept in sync between your computers and your phone.

## Tasks

Each task has a description (several lines if you like) and one of these
schedules:

| Schedule | What it does |
|---|---|
| **Every N** years, months, weeks, days, hours, minutes or seconds | Starts at a date and time you choose (today and now by default). |
| **On days of the week** | Tick the days, and choose a time (09:00 unless you change the default in Settings). **Every 2 weeks** (or 3, and so on) rings only every other week, starting the week you choose: "every other Monday and Thursday". |
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
- **Remind me in 5 min, 10 min, 30 min or 1 h**: closes the alarm and brings it
  back after that long, this once, whatever the task's reminder interval is.

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
ringing it), **Pause**, and **Restart countdown**.

**Pause** (also on the toolbar) keeps a task without letting it ring: a ringing
alarm stops, and nothing comes up until you **Resume** it. Whatever would have
come up meanwhile is skipped, not rung all at once. A paused countdown keeps the
time it had left and carries on from there. Editing a paused task keeps it
paused; **Restart countdown** (or ticking "Start the countdown again" in the
editor) starts it running again. A task that restarts when marked done counts
its next time from when you resume it.

## Calendar

**Calendar…** (toolbar or tray menu; on Android, the ⋮ menu) shows a month of
your tasks: each day lists when its tasks come up. Click a day to see its tasks
in full, and double-click one (tap it, on Android) to edit it. It shows what is
still to come, and in red what has come up but isn't marked done yet. A task
that comes up several times a day shows once, as "every 15 min". For a task that
restarts when marked done, only its next time is certain; the later ones,
marked "~", assume you mark it done on time; once it has come up, they count
from now. Paused tasks aren't shown.

## Sounds

The **Sound** button in the task editor shows the task's sound; click it to pick
a Windows sound, a sound file (WAV, MP3, WMA, M4A, AAC, FLAC), or **Default**.
**Play it** chooses between once each time the alarm appears and over and over
until the alarm is closed or marked done. **Volume** sets how loud, as a share of
the system volume (the alarm volume on Android), and can **fade in**: start
quietly and grow to that volume over 10 seconds up to 5 minutes.

Tasks left on **Default** (sound, play mode, volume, fade-in) follow the
default in **Settings**, including when you change it later. If a sound can't be found or played, the built-in alarm sound
plays instead and the alarm window says so.

## Settings

| Setting | What it does |
|---|---|
| Sound / Play the sound / Volume | The default sound, sound mode, volume and fade-in for tasks left on "Default". |
| Remind again after | The reminder interval new tasks start with. |
| Time of day for new days-of-the-week tasks | 09:00 unless changed. |
| For new "every N" tasks | Which restart choice new tasks start with. |
| Start with Windows (in the tray) | On by default: alarms only ring while the app runs. |
| Closing the main window keeps the app running in the tray | On by default. Use **Exit** (toolbar or tray menu) to quit. |
| Wake the computer from sleep for alarms | On by default: a sleeping computer wakes just before an alarm, and the screen stays on while its sound plays. Windows must allow wake timers (Power Options → Change advanced power settings → Sleep → Allow wake timers); many laptops don't on battery. A computer that is shut down or hibernating doesn't wake. |

**Your tasks** (in Settings, on Windows and Android): **Export tasks** saves every
task to a file, as a backup or to move them to a device you haven't linked.
**Import tasks** reads such a file back. It adds the tasks this device doesn't
have, including ones you deleted, and updates any the file has a newer version
of; a backup older than your edits changes nothing. Nothing that came up in the
past rings because of an import.

**Automatic backups** (on unless you untick "Back up automatically after every
change"): a few seconds after a task is added, changed or deleted, on this
device or a linked one, the app writes a backup file. Marking done, snoozing and
pausing don't count. Each backup is a new file, so a mistake can't overwrite the
backup that still has what you lost:
- Every backup from the last day is kept, then the newest of each day for 60
  days, then the newest of each month for two years. The ten newest are always
  kept, whatever the clock says.
- On Windows they go to `Documents\RemindMe backups` (Settings can name
  another folder, and **Open folder** shows it). On Android they go to
  `Download/RemindMe backups`, where they stay even if the app is
  uninstalled.
- To restore, import one (above). It brings back deleted tasks, but doesn't undo
  edits made since, since the newer version of a task wins.
- Each file name ends with the device's ID, so devices sharing a folder (a
  Documents folder in the same cloud drive, say) each thin out only their own.

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
  accepts connections only from private addresses, which home and office
  networks use, and only over IPv4.
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
| `--exit` | Tell the running copy (of that data folder) to exit, and wait until it has. The installer uses this before an upgrade. |

Tasks and settings live in `%LOCALAPPDATA%\RemindMe` (`tasks.json`,
`settings.json`, each with a `.bak` of the previous version) along with `app.log`.

## Android

The phone app has the same tasks, schedules, alarm screen, settings and sync,
laid out for a phone:

- **The list**: tap a task to edit it, long-press for Mark done, Show alarm,
  Restart countdown, Pause or Resume, and Delete, and tap **+** for a new task. The ⋮ menu has
  **Calendar**, **Devices and sync** and **Settings**.
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
dotnet build RemindMe.slnx
dotnet test RemindMe.Core.Tests
dotnet publish RemindMe.Android -c Release -f net10.0-android
```

The APK lands in `RemindMe.Android/bin/Release/net10.0-android/publish/`
(`com.inhahe.remindme-Signed.apk`). It is signed with the debug key, which
is fine for installing by hand.

### Releases

The version is in `VERSION` (four numbers, e.g. `1.0.0.0`; the first at most
255, the others at most 99); bump it for every release. Each script puts its
output in `release/`:

| Script | Builds |
|---|---|
| `build-msi.ps1` | `RemindMe-<version>.msi`, the Windows installer, and `RemindMe-<version>.exe`, the program on its own (self-contained, no .NET needed). Needs the WiX Toolset (`dotnet tool install --global wix`, done for you) with its EULA accepted once (`wix eula accept wix7`). |
| `build-apk.ps1` | `RemindMe-<version>.apk`. Sign it with your own key by setting `REMINDME_KEYSTORE`, `REMINDME_KEY_ALIAS`, `REMINDME_STORE_PASS` and `REMINDME_KEY_PASS` (the script's header says how to make one); otherwise it uses this machine's debug key, and an APK built elsewhere won't install over it. |
| `release-github.ps1` (or `release-github.bat`) | Both of the above, then a GitHub release `v<version>` with them attached, using the GitHub CLI (`gh`). `-DryRun` builds everything and shows the release command without running it; `-Repo owner/name` names the repository if `origin` isn't it. |

The installer puts the program in `Program Files\RemindMe`, adds it to the
Start menu, and lets it through Windows Firewall on private and domain networks,
so sync works without the first-run prompt. It can start it when it finishes. An
upgrade closes the running copy first (`RemindMe.exe --exit`) and installs
over the previous version. Tasks and settings stay where they are, per user.
