# Known issues and technical debt

Open items only; remove an entry when it is fixed.

## Not yet verified

### LAN discovery between two real machines
Discovery is LanLink's design, adapted, and the sessions behind it are tested
(in-process engines over loopback, and two desktop instances plus the Android
emulator linked by address). But no two machines on one LAN have discovered each
other yet. That needs the app to listen on the network, which raises a Windows
Firewall prompt for the debug build (`ALARMCAL_BIND=127.0.0.1` avoids that in
tests). **To verify:** run the app on two PCs on one LAN, or a PC and the phone;
each should list the other in Devices within a few seconds.

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

### No installer, no release script
The desktop app is a plain build output, and the APK is signed with the debug
key. LanLink's pattern (a `VERSION` file, an MSI that adds the firewall rules,
`release-github.bat`) would fit; see ROADMAP S9.

### F-Droid can't build .NET apps
Choosing .NET for Android (so one Core serves both apps) rules out F-Droid. The
APK is sideloaded, as LanLink's is.
