<#
  Publishes a GitHub release of RemindMe (github.com/inhahe/RemindMe), as release.bat
  runs it: the version in VERSION, tagged v<version>, once the tests pass, with the
  Windows installer, the two Windows exes (RemindMe.exe with .NET included, RemindMeFD.exe
  without; see build-msi.ps1) and the Android APK. A 0.x version is a pre-release.

  What is released is main as GitHub has it, so nothing is published unless:
  - origin is github.com/inhahe/RemindMe;
  - this is main, with nothing uncommitted and no untracked file (ignored ones aside);
  - this commit is the one GitHub's main has (push first);
  - VERSION is new: not released, not tagged, and above every release tagged so far.

  -DryRun   test, build and show the release, publishing nothing. Checks that a real
            run would stop at (not pushed yet, say) are reported as warnings, so a
            release can be tried before anything goes to GitHub.

  Needs the GitHub CLI (gh), signed in with repo access. Runs in Windows PowerShell 5.1,
  which release.bat uses, as well as in PowerShell 7.
#>
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'

# A refusal, raised by Stop-Release: its message says it all.
class ReleaseRefused : System.Exception {
    ReleaseRefused([string]$message) : base($message) {}
}

# A refusal ends the script with its message alone; anything else also says where it
# happened (which build script, which line). Either way, exit code 1 for release.bat.
trap {
    Write-Host ''
    if ($_.Exception -is [ReleaseRefused]) {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    else {
        Write-Host "$($_.Exception.Message)$($_.InvocationInfo.PositionMessage)" -ForegroundColor Red
        Write-Host 'Nothing was published.' -ForegroundColor Red
    }
    exit 1
}

$root = $PSScriptRoot
$Repo = 'inhahe/RemindMe'
# Native programs' output (git log's commit subjects, among it) is UTF-8; Windows
# PowerShell would read it in the console's code page, mangling an em dash.
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
. (Join-Path $root 'version.ps1')
$release = Get-ReleaseVersion $root
$version = $release.Text
$tag = "v$version"
$preRelease = $release.Version.Major -eq 0

# Runs a command whose failure is an answer (no such release, no such tag), returning
# its output without its errors; $LASTEXITCODE says how it went. Windows PowerShell 5.1
# turns a native command's redirected stderr into errors, which 'Stop' would make fatal.
function Invoke-Probe([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command 2>$null
}

function Stop-Release([string]$why) { throw [ReleaseRefused]::new("$why Nothing was published.") }

# A check a real run stops at; a dry run only warns, and carries on.
$warnings = @()
function Test-Gate([bool]$ok, [string]$why) {
    if ($ok) { return }
    if (-not $DryRun) { Stop-Release $why }
    Write-Warning $why
    $script:warnings += $why
}

Write-Host "=== RemindMe $version -> $Repo ($tag)$(if ($DryRun) { ', dry run' }) ==="

# --- The GitHub CLI, and the repository ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Stop-Release 'The GitHub CLI (gh) is needed: https://cli.github.com/.' }
$null = Invoke-Probe { gh auth status --hostname github.com }
if ($LASTEXITCODE) { Stop-Release 'gh is not signed in to github.com: run gh auth login.' }
$origin = Invoke-Probe { git -C $root remote get-url origin }
$originIsRepo = "$origin" -match "github\.com[:/]$([regex]::Escape($Repo))(\.git)?/?$"
Test-Gate $originIsRepo "origin is '$origin', not github.com/${Repo}: what is released is main as $Repo has it."

# --- What is released is main as GitHub has it ---
$branch = Invoke-Probe { git -C $root symbolic-ref --short HEAD }
Test-Gate ($branch -eq 'main') "Releases are made from main, and this is '$branch'."
$dirty = @(Invoke-Probe { git -C $root status --porcelain --untracked-files=all })
$firstDirty = if ($dirty.Count -gt 0) { $dirty[0].Trim() } else { '' }
Test-Gate ($dirty.Count -eq 0) "There are uncommitted changes or untracked files ($firstDirty...): commit them, or ignore them."
$head = Invoke-Probe { git -C $root rev-parse HEAD }
if ($originIsRepo) {
    $null = Invoke-Probe { git -C $root fetch --quiet --tags origin }
    if ($LASTEXITCODE) { Stop-Release "Could not fetch from $Repo (git fetch origin failed): is GitHub reachable, and does a local tag clash with one there?" }
    $main = Invoke-Probe { git -C $root rev-parse origin/main }
    Test-Gate ($head -eq $main) 'This commit is not the main GitHub has: push (or pull) first.'
}

# --- Once per version, and each above the ones before ---
$null = Invoke-Probe { gh release view $tag --repo $Repo }
if (-not $LASTEXITCODE) { Stop-Release "Version $version is already released (https://github.com/$Repo/releases/tag/$tag): raise VERSION first." }
$null = Invoke-Probe { git -C $root rev-parse -q --verify "refs/tags/$tag" }
Test-Gate ($LASTEXITCODE -ne 0) "The tag $tag exists without a release (left by a deleted one?): raise VERSION, or delete the tag on GitHub and here."
# The highest version tagged so far, whatever order the tags were made in.
$tags = @(Invoke-Probe { git -C $root tag --list 'v*' } | Where-Object { ConvertTo-ReleaseVersion $_ })
$previous = $tags | Sort-Object { ConvertTo-ReleaseVersion $_ } | Select-Object -Last 1
if ($previous) {
    Test-Gate ($release.Version -gt (ConvertTo-ReleaseVersion $previous)) "Version $version is not above the last release, ${previous}: raise VERSION."
}

# --- Test, then build ---
Write-Host '[1/3] Running the tests...'
dotnet test (Join-Path $root 'RemindMe.Core.Tests')
if ($LASTEXITCODE) { Stop-Release 'The tests failed.' }
Write-Host '[2/3] Building...'
# The exes' names carry no version, so an older build's could otherwise be attached
# should this one fail to replace it.
$names = "RemindMe-$version.msi", 'RemindMe.exe', 'RemindMeFD.exe', "RemindMe-$version.apk"
$assets = @($names | ForEach-Object { Join-Path $root "release\$_" })
$assets | Where-Object { Test-Path $_ } | Remove-Item -Force
& (Join-Path $root 'build-msi.ps1')
& (Join-Path $root 'build-apk.ps1')
foreach ($asset in $assets) { if (-not (Test-Path $asset)) { Stop-Release "The build made no $asset." } }

# --- The release notes: what it is, how to install it, and what changed ---
$notes = @(
    "RemindMe ${version}: reminders and alarms that keep ringing until you mark them done, on Windows and Android, synced between your own devices."
    ''
    '> **Alpha:** RemindMe is at an early stage and has not been tested in real use yet. Expect bugs, and don''t rely on it alone for anything that matters.'
    ''
    '**Windows** (10 or 11, 64-bit): three files of the same program. Download one:'
    ''
    "- **RemindMe-$version.msi** installs it, with .NET built in: in the Start menu, starting with Windows, and let through Windows Firewall for syncing on private networks. If unsure, take this one."
    '- **RemindMe.exe** (about 75 MB) is the program on its own, with nothing to install: .NET is built in.'
    '- **RemindMeFD.exe** (under 1 MB) is the same program, but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed. FD stands for framework-dependent.'
    ''
    'Either exe can be renamed, to RemindMe.exe say.'
    ''
    "**Android** (10 or later): download ``RemindMe-$version.apk`` on the phone and open it, allowing your browser or file manager to install apps when asked."
    ''
)
$null = if ($previous) { Invoke-Probe { git -C $root merge-base --is-ancestor $previous HEAD } }
if ($previous -and -not $LASTEXITCODE) {
    $notes += "**Changes since ${previous}:**"
    $notes += ''
    $notes += @(Invoke-Probe { git -C $root log --no-merges --pretty=format:'- %s' "$previous..HEAD" })
}
elseif (-not $previous) { $notes += 'The first release.' }
$work = Join-Path ([IO.Path]::GetTempPath()) "remindme-release-$version"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $notesFile = Join-Path $work 'notes.md'
    [IO.File]::WriteAllText($notesFile, ($notes -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
    $command = @('release', 'create', $tag) + $assets + @('--repo', $Repo, '--target', $head, '--title', "RemindMe $version", '--notes-file', $notesFile)
    if ($preRelease) { $command += '--prerelease' }

    if ($DryRun) {
        Write-Host ''
        Write-Host "Dry run: would publish $tag at $head$(if ($preRelease) { ' as a pre-release' }), with:"
        $assets | ForEach-Object { Write-Host "  $_" }
        Write-Host 'and these notes:'
        Write-Host ''
        Get-Content $notesFile -Encoding UTF8 | ForEach-Object { Write-Host $_ }
        if ($warnings.Count -gt 0) {
            Write-Host ''
            Write-Host "A real run would stop first, at: $($warnings[0])" -ForegroundColor Yellow
        }
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
