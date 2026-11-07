<#
  Publishes a GitHub release of the version in VERSION (S9): builds the Windows
  installer and exe and the Android APK, then creates the release v<version> with
  them attached. Nothing is published if that release already exists: bump VERSION
  first.

  -Repo owner/name   the GitHub repository; by default, the one "origin" points at
  -DryRun            build everything and show the release command, without running it

  Needs the GitHub CLI (gh), signed in with repo access. Creating the release also
  creates its tag on GitHub, at the commit GitHub has for the default branch: push
  first.
#>
param([string]$Repo, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$tag = "v$version"

if (-not $Repo) {
    $url = git -C $root remote get-url origin 2>$null
    if ($url -match 'github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?$') { $Repo = $Matches.repo }
    else { throw 'No GitHub repository: add a remote called origin, or pass -Repo owner/name.' }
}
Write-Host "=== Alarm & Calendar $tag -> $Repo ==="

if (-not $DryRun) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is missing: https://cli.github.com/' }
    gh auth status *> $null
    if ($LASTEXITCODE) { throw 'gh is not signed in: run gh auth login.' }
    gh release view $tag --repo $Repo *> $null
    if (-not $LASTEXITCODE) {
        Write-Host "Release $tag already exists on ${Repo}: nothing to do. Bump VERSION to publish a new one."
        return
    }
}

& (Join-Path $root 'build-msi.ps1')
$assets = @((Join-Path $root "release\AlarmCalendar-$version.exe"), (Join-Path $root "release\AlarmCalendar-$version.msi"))
$notes = "Alarm & Calendar $version. Windows: the installer (AlarmCalendar-$version.msi), or the program on its own (AlarmCalendar-$version.exe)."
try {
    & (Join-Path $root 'build-apk.ps1')
    $assets += Join-Path $root "release\AlarmCalendar-$version.apk"
    $notes += " Android: AlarmCalendar-$version.apk, to install by hand (Android 10 or later)."
}
catch {
    Write-Warning "The APK didn't build ($_); releasing without it."
}

$command = @('release', 'create', $tag) + $assets + @('--repo', $Repo, '--title', "Alarm & Calendar $version", '--notes', $notes)
if ($DryRun) {
    Write-Host "Would run: gh $($command -join ' ')"
    return
}
gh @command
if ($LASTEXITCODE) { throw 'gh release create failed.' }
Write-Host "=== Released: https://github.com/$Repo/releases/tag/$tag ==="
