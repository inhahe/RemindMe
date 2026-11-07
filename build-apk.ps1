<#
  Builds the Android APK, release\<version>\RemindMe-<version>.apk (S9).

  The version comes from the VERSION file next to this script. Its four parts
  a.b.c.d become Android's versionName, and the versionCode Android compares for
  upgrades is a*1000000 + b*10000 + c*100 + d.

  Signing: an update installs over an earlier APK only if both are signed with the
  same key. Set these to sign with your own release key:
    REMINDME_KEYSTORE    the keystore file
    REMINDME_KEY_ALIAS   the key's alias in it
    REMINDME_STORE_PASS  the keystore's password
    REMINDME_KEY_PASS    the key's password (often the same)
  (Make one with: keytool -genkeypair -v -keystore remindme.keystore -alias
  remindme -keyalg RSA -keysize 4096 -validity 36500, then keep it, and its
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
$project = Join-Path $root 'RemindMe.Android\RemindMe.Android.csproj'
$out = Join-Path $root "release\$version"
$dest = Join-Path $out "RemindMe-$version.apk"
New-Item -ItemType Directory -Force $out | Out-Null
# An earlier run's APK of this version goes first, so a failed build can't leave it behind.
if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }

$signing = @()
if ($env:REMINDME_KEYSTORE) {
    foreach ($v in 'REMINDME_KEY_ALIAS', 'REMINDME_STORE_PASS', 'REMINDME_KEY_PASS') {
        if (-not (Get-Item "env:$v" -ErrorAction SilentlyContinue)) { throw "REMINDME_KEYSTORE is set, so $v must be too." }
    }
    # "env:NAME" makes the build read the passwords from the environment, so they
    # appear on no command line.
    $signing = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$env:REMINDME_KEYSTORE",
        "-p:AndroidSigningKeyAlias=$env:REMINDME_KEY_ALIAS",
        '-p:AndroidSigningStorePass=env:REMINDME_STORE_PASS', '-p:AndroidSigningKeyPass=env:REMINDME_KEY_PASS')
    Write-Host "Signing with the key '$env:REMINDME_KEY_ALIAS' in $env:REMINDME_KEYSTORE."
}
else {
    Write-Warning "REMINDME_KEYSTORE isn't set: signing with this machine's debug key (see the top of this script)."
}

Write-Host "=== RemindMe ${version}: Android APK (versionCode $code) ==="
dotnet publish $project -c Release -p:ApplicationDisplayVersion=$version -p:ApplicationVersion=$code @signing
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

$apk = Get-ChildItem (Join-Path $root 'RemindMe.Android\bin\Release') -Recurse -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw 'The build succeeded but no signed APK was found.' }
Copy-Item -LiteralPath $apk.FullName -Destination $dest
Write-Host "=== Done: $dest ==="
