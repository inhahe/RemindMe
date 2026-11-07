# Known issues and technical debt

Open items only; remove an entry when it is fixed.

## Not yet verified

### LAN discovery between two real machines
Discovery is LanLink's design, adapted, and the sessions behind it are tested
(in-process engines over loopback, and two desktop instances plus the Android
emulator linked by address). But no two machines on one LAN have discovered each
other yet. That needs the app to listen on the network, which raises a Windows
Firewall prompt for the debug build (`REMINDME_BIND=127.0.0.1` avoids that in
tests). **To verify:** run the app on two PCs on one LAN, or a PC and the phone;
each should list the other in Devices within a few seconds.

### Waking the computer from sleep (S3)
Checked only as far as setting the timer: app.log says "Wake timer set for …",
and the timer is set again as the next alarm changes. The computer was never
put to sleep for it. **To verify:**
1. Turn on Power Options → Change advanced power settings → Sleep → Allow wake
   timers (for the power source in use; many laptops turn it off on battery).
2. Make a task due in a few minutes, then put the computer to sleep (Sleep, not
   hibernate or shut down). `powercfg /waketimers`, in an administrator
   prompt, should list the app's timer.
3. The computer should wake about 20 seconds before the task, and ring on time.

What to watch:
- **The screen.** After a timer wake, Windows leaves the display off. While the
  sound plays, the app requests `ES_DISPLAY_REQUIRED`. Microsoft documents that
  as "forces the display to be on by resetting the display idle timer", but
  doesn't say outright that it lights a display a timer wake left off. If the
  alarm sounds while the screen stays dark, the fix is to turn the display on
  when an alert begins after an automatic wake (`IsSystemResumeAutomatic`):
  - A zero-distance `SendInput` mouse move. Windows also takes it as the user
    being present, so the computer then sleeps on its usual idle timeout
    rather than within two minutes.
  - Or `SC_MONITORPOWER` −1, which recent Windows versions are said to ignore.
- **Modern Standby** computers (S0 low-power idle, most recent laptops) don't
  sleep in S3. Microsoft's page on timer wakes covers S3 and hibernation only,
  and Modern Standby honours wake timers far less.

### The Windows exes on a PC without .NET
Both exes were started only on this PC, which has the .NET SDK and therefore
the Desktop Runtime. Not seen:
- what `RemindMeFD.exe` does where the .NET 10 Desktop Runtime is missing.
  .NET's own launcher should show a dialog naming the runtime and linking to
  its download, but the app starts itself at every login (start with Windows),
  and it isn't known whether that start shows the dialog each time or fails
  silently;
- that `RemindMe.exe` and the MSI need nothing installed.

**To verify:** on a PC or VM without .NET, run each exe once, and log in again
after running `RemindMeFD.exe`.

### The tray menu, opened by a real right-click
The tray icon's menu is a WPF menu now, so that it takes the theme. Tests can't
right-click the icon without moving the mouse on the desktop. Posting the
icon's right-click message instead opens the menu, which closes at once, because
the app can't then take the foreground the way a real click on the tray lets it.
On a real click, the menu makes its own window the foreground, as other WPF tray
menus do, so that a click elsewhere closes it. **To verify:** right-click the
icon (in the taskbar's hidden icons, if it's there); the menu should open at the
mouse, light or dark like the windows, run what you choose, and close when you
click elsewhere or press Esc.

### Dark mode, set in Windows
The windows were checked dark by building with the theme pinned to dark
(`Fluent.Dark.xaml` in `App.xaml`), not by switching Windows to dark mode, and
the switch while the app runs wasn't seen. **To verify:** with the app open,
switch Settings → Personalization → Colors → "Choose your mode" to Dark and back.

### Android on a real phone
Checked only on the emulator (API 36). Worth checking on the phone, a Samsung
running One UI: that the alarm screen opens over the lock screen (One UI can
restrict full-screen intents), that background sync survives with the screen off
(battery optimization), and that the ringtone picker's choices play. Samsung's
"Deep sleeping apps" list can stop an app altogether; the app should be on
"Never sleeping apps".

### Paths the emulator couldn't reach
- **An alarm firing from deep Doze.** Android refuses to enter deep Doze while
  an alarm clock is due within about 30 minutes, which is the guarantee, and the
  shell can't lower that threshold on API 36. The alarm is registered with
  `FLAG_WAKE_FROM_IDLE`.
- **A cold start under Restricted battery use.** The process couldn't be killed
  without root, which the Play Store system image lacks. The alarm rang on time
  under Restricted with the process alive.
- **A sticky restart refused the foreground** (`RingingService.OnStartCommand`
  falling back to the safety net): Android does this only to a service it
  restarts in the background after killing the process mid-alarm.
- **A notification button pressed after its task was answered**: the app
  withdraws such notifications itself, so none was left to press; the check
  that ignores one is there in case another path leaves one.

Checked on the emulator:
- **Direct boot**: with a PIN set and the emulator rebooted, the app's alarm was
  registered while the user was still `RUNNING_LOCKED`, and every task was there
  after unlocking. With no data yet moved to device storage (cleared, as on the
  first start after the update), the locked process loaded nothing and wrote
  nothing, then loaded the data the moment the PIN was entered.
- **The safety net**: with the `START_FOREGROUND` app op denied, the alarm rang
  as an insistent notification on the fallback channel and opened the alarm
  screen over the lock screen; Silence posted it again quietly, and Close,
  remind me later withdrew it. With the process killed while it rang, Mark done
  on its notification marked the task done and withdrew the notification. With
  sync on and the app op denied, opening the app logged the refusal instead of
  crashing.
- **Silence, again**: a task ringing every minute, silenced on the alarm screen,
  sounded again at its next occurrence, and the button was usable again. With
  the screen turned off after the first alert, the next one turned it on.
- **Alerts across a process's death** (the safety net, app op denied, process
  killed in the background): a silenced alarm stayed silent when a new process
  started, and nothing was posted again; Silence worked in a process that never
  posted the alarm; Mark done from the shade withdrew one this process only
  knew from the notification.

## Decisions waiting on the user

### Releasing (these are yours to settle)
- **The APK's signing key.** `build-apk.ps1` signs with your key when the
  `REMINDME_*` variables point at one, and with this machine's debug key
  otherwise, as LanLink's APKs are. Whatever key a published APK is signed with,
  an update installs over it only if it is signed with the same one: build
  releases on this machine, or keep a copy of
  `%LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore`. Moving to a real
  release key later (the script's header says how to make one) means users
  uninstall first. Their tasks then come back from the automatic backups in
  `Download/RemindMe backups`, which survive the uninstall.
- **Not tried: an actual install.** The MSI was built and its payload unpacked
  (`msiexec /a`), and the exe it contains starts and exits; installing it for
  real would change this machine (Program Files, firewall rules), so that, and
  an upgrade over it, are yours to try.

### F-Droid can't build .NET apps
Choosing .NET for Android (so one Core serves both apps) rules out F-Droid. The
APK is sideloaded, as LanLink's is.
