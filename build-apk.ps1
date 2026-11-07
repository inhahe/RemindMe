<#
  Builds the Android APK, release\AlarmCalendar-<version>.apk (S9).

  The version comes from the VERSION file next to this script. Its four parts
  a.b.c.d become Android's versionName, and the versionCode Android compares for
  upgrades is a*1000000 + b*10000 + c*100 + d.

  Signing: an update installs over an earlier APK only if both are signed with the
  same key. Set these to sign with your own release key:
    ALARMCAL_KEYSTORE    the keystore file
    ALARMCAL_KEY_ALIAS   the key's alias in it
    ALARMCAL_STORE_PASS  the keystore's password
    ALARMCAL_KEY_PASS    the key's password (often the same)
  (Make one with: keytool -genkeypair -v -keystore alarmcalendar.keystore -alias
  alarmcalendar -keyalg RSA -keysize 4096 -validity 36500, then keep it, and its
  passwords, safe and out of git: losing it means no more updates.) Without them,
  the APK is signed with this machine's debug key, as LanLink's is; an APK built on
  another machine then won't install over it.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'version.ps1')
$release = Get-ReleaseVersion $root
$version = $release.Text
$parts = $release.Parts
$code = $parts[0] * 1000000 + $parts[1] * 10000 + $parts[2] * 100 + $parts[3]
$project = Join-Path $root 'AlarmCalendar.Android\AlarmCalendar.Android.csproj'
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null

$signing = @()
if ($env:ALARMCAL_KEYSTORE) {
    foreach ($v in 'ALARMCAL_KEY_ALIAS', 'ALARMCAL_STORE_PASS', 'ALARMCAL_KEY_PASS') {
        if (-not (Get-Item "env:$v" -ErrorAction SilentlyContinue)) { throw "ALARMCAL_KEYSTORE is set, so $v must be too." }
    }
    # "env:NAME" makes the build read the passwords from the environment, so they
    # appear on no command line.
    $signing = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$env:ALARMCAL_KEYSTORE",
        "-p:AndroidSigningKeyAlias=$env:ALARMCAL_KEY_ALIAS",
        '-p:AndroidSigningStorePass=env:ALARMCAL_STORE_PASS', '-p:AndroidSigningKeyPass=env:ALARMCAL_KEY_PASS')
    Write-Host "Signing with the key '$env:ALARMCAL_KEY_ALIAS' in $env:ALARMCAL_KEYSTORE."
}
else {
    Write-Warning "ALARMCAL_KEYSTORE isn't set: signing with this machine's debug key (see the top of this script)."
}

Write-Host "=== Alarm & Calendar ${version}: Android APK (versionCode $code) ==="
dotnet publish $project -c Release -p:ApplicationDisplayVersion=$version -p:ApplicationVersion=$code @signing
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

$apk = Get-ChildItem (Join-Path $root 'AlarmCalendar.Android\bin\Release') -Recurse -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw 'The build succeeded but no signed APK was found.' }
$dest = Join-Path $out "AlarmCalendar-$version.apk"
Copy-Item $apk.FullName $dest -Force
Write-Host "=== Done: $dest ==="
