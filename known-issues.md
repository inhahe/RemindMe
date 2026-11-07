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
- **The safety net** (`RingingService.NotifyWithoutService`), which runs only
  when Android refuses to start the ringing service.
- **Direct boot**: the locked-boot path needs a device with a screen lock.

## Decisions waiting on the user

### No installer, no release script
The desktop app is a plain build output, and the APK is signed with the debug
key. LanLink's pattern (a `VERSION` file, an MSI that adds the firewall rules,
`release-github.bat`) would fit; see ROADMAP S9.

### F-Droid can't build .NET apps
Choosing .NET for Android (so one Core serves both apps) rules out F-Droid. The
APK is sideloaded, as LanLink's is.
