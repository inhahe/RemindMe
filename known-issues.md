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
(battery optimization), and that the ringtone picker's choices play.

## Decisions waiting on the user

### No installer, no release script
The desktop app is a plain build output, and the APK is signed with the debug
key. LanLink's pattern (a `VERSION` file, an MSI that adds the firewall rules,
`release-github.bat`) would fit; see ROADMAP S9.

### F-Droid can't build .NET apps
Choosing .NET for Android (so one Core serves both apps) rules out F-Droid. The
APK is sideloaded, as LanLink's is.
