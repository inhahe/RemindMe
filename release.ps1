<#
  Publishes a GitHub release of RemindMe (github.com/inhahe/RemindMe), as release.bat
  runs it: the Windows installer and exe and the Android APK of the version in VERSION,
  tagged v<version>, once the tests pass. A 0.x version is published as a pre-release.

  What is released is main as GitHub has it, so nothing is published unless:
  - this is main, nothing is uncommitted, and nothing the build uses is left out of git;
  - this commit is the one GitHub's main has (push first);
  - VERSION is new: not released, not tagged, and above the last release.

  -DryRun   check, test and build everything, and show the release; publish nothing.

  Needs the GitHub CLI (gh), signed in with repo access. Runs in Windows PowerShell 5.1,
  which release.bat uses, as well as in PowerShell 7.
#>
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
# A refusal or a failed step ends the script with its message alone, not a PowerShell
# error record, and exit code 1 for release.bat.
trap {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
$root = $PSScriptRoot
$Repo = 'inhahe/RemindMe'
. (Join-Path $root 'version.ps1')
$release = Get-ReleaseVersion $root
$version = $release.Text
$tag = "v$version"

# Runs a command whose failure is an answer (no such release, no such tag), returning
# its output without its errors; $LASTEXITCODE says how it went. Windows PowerShell 5.1
# turns a native command's redirected stderr into errors, which 'Stop' would make fatal.
function Invoke-Probe([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command 2>$null
}

function Stop-Release([string]$why) { throw "$why Nothing was published." }

# The four parts of "v1.2.3" or "v1.2.3.4" (missing ones 0), or $null for another tag.
function Get-TagParts([string]$name) {
    if ($name -notmatch '^v\d+(\.\d+){1,3}$') { return $null }
    $parts = @($name.Substring(1).Split('.') | ForEach-Object { [int]$_ }) + @(0, 0, 0)
    return , $parts[0..3]
}

# Positive when the parts $a order after $b.
function Compare-Parts($a, $b) {
    for ($i = 0; $i -lt 4; $i++) { if ($a[$i] -ne $b[$i]) { return $a[$i] - $b[$i] } }
    return 0
}

Write-Host "=== RemindMe $version -> $Repo ($tag) ==="

# --- The GitHub CLI ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Stop-Release 'The GitHub CLI (gh) is needed: https://cli.github.com/.' }
$null = Invoke-Probe { gh auth status --hostname github.com }
if ($LASTEXITCODE) { Stop-Release 'gh is not signed in to github.com: run gh auth login.' }

# --- What is released is main as GitHub has it ---
$branch = Invoke-Probe { git -C $root symbolic-ref --short HEAD }
if ($branch -ne 'main') { Stop-Release "Releases are made from main, and this is '$branch'." }
$null = Invoke-Probe { git -C $root diff --quiet HEAD -- }
if ($LASTEXITCODE) { Stop-Release 'There are uncommitted changes: commit and push them first.' }
$leftOut = @(Invoke-Probe { git -C $root ls-files --others --exclude-standard -- RemindMe.Core RemindMe.Core.Tests RemindMe.Desktop RemindMe.Android installer VERSION version.ps1 build-msi.ps1 build-apk.ps1 })
if ($leftOut.Count -gt 0) { Stop-Release "$($leftOut[0]) is not in git, but the build may use it: add it, or ignore it." }
$null = Invoke-Probe { git -C $root fetch --quiet --tags origin }
if ($LASTEXITCODE) { Stop-Release 'Could not reach GitHub (git fetch origin failed).' }
$head = Invoke-Probe { git -C $root rev-parse HEAD }
$main = Invoke-Probe { git -C $root rev-parse origin/main }
if ($head -ne $main) { Stop-Release 'This commit is not the main GitHub has: push (or pull) first.' }

# --- Once per version, and each above the one before ---
$null = Invoke-Probe { gh release view $tag --repo $Repo }
if (-not $LASTEXITCODE) { Stop-Release "Version $version is already released (https://github.com/$Repo/releases/tag/$tag): raise VERSION first." }
$null = Invoke-Probe { git -C $root rev-parse -q --verify "refs/tags/$tag" }
if (-not $LASTEXITCODE) { Stop-Release "The tag $tag exists without a release (left by a deleted one?): raise VERSION, or delete the tag on GitHub and here." }
$previous = Invoke-Probe { git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' HEAD }
if ($LASTEXITCODE -or -not (Get-TagParts $previous)) { $previous = $null }
if ($previous -and (Compare-Parts $release.Parts (Get-TagParts $previous)) -le 0) {
    Stop-Release "Version $version is not above the last release, ${previous}: raise VERSION."
}

# --- Test, then build ---
Write-Host '[1/3] Running the tests...'
dotnet test (Join-Path $root 'RemindMe.Core.Tests')
if ($LASTEXITCODE) { Stop-Release 'The tests failed.' }
Write-Host '[2/3] Building...'
& (Join-Path $root 'build-msi.ps1')
& (Join-Path $root 'build-apk.ps1')
$names = "RemindMe-$version.msi", "RemindMe-$version.exe", "RemindMe-$version.apk"
$assets = @($names | ForEach-Object { Join-Path $root "release\$_" })
foreach ($asset in $assets) { if (-not (Test-Path $asset)) { Stop-Release "The build made no $asset." } }

# --- The release notes: what it is, how to install it, and what changed ---
$notes = @(
    "RemindMe ${version}: reminders and alarms that keep ringing until you mark them done, on Windows and Android, synced between your own devices."
    ''
    '> **Alpha:** RemindMe is at an early stage and has not been tested in real use yet. Expect bugs, and don''t rely on it alone for anything that matters.'
    ''
    "**Windows** (10 or 11, x64): run ``RemindMe-$version.msi`` to install it, or use ``RemindMe-$version.exe``, the same program on its own, with nothing to install."
    ''
    "**Android** (10 or later): download ``RemindMe-$version.apk`` on the phone and open it, allowing your browser or file manager to install apps when asked."
    ''
)
if ($previous) {
    $notes += "**Changes since ${previous}:**"
    $notes += ''
    $notes += @(Invoke-Probe { git -C $root log --no-merges --pretty=format:'- %s' "$previous..HEAD" })
}
else { $notes += 'The first release.' }
$work = Join-Path ([IO.Path]::GetTempPath()) "remindme-release-$version"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $notesFile = Join-Path $work 'notes.md'
    [IO.File]::WriteAllText($notesFile, ($notes -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
    $command = @('release', 'create', $tag) + $assets + @('--repo', $Repo, '--target', $head, '--title', "RemindMe $version", '--notes-file', $notesFile)
    if ($release.Parts[0] -eq 0) { $command += '--prerelease' }

    if ($DryRun) {
        Write-Host ''
        Write-Host "Dry run: would publish $tag at $head$(if ($release.Parts[0] -eq 0) { ' as a pre-release' }), with:"
        $assets | ForEach-Object { Write-Host "  $_" }
        Write-Host 'and these notes:'
        Write-Host ''
        Get-Content $notesFile | ForEach-Object { Write-Host $_ }
        return
    }

    Write-Host "[3/3] Publishing $tag..."
    gh @command
    if ($LASTEXITCODE) { Stop-Release 'gh release create failed.' }
    $null = Invoke-Probe { git -C $root fetch --quiet --tags origin }
    Write-Host "=== Released: https://github.com/$Repo/releases/tag/$tag ==="
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
