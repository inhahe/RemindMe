<#
  Builds the Windows release files in release\<version>\ (S9):
    RemindMe-<version>.msi   the installer, with .NET included
    RemindMe.exe             the program on its own, self-contained: .NET included
    RemindMeFD.exe           the same, framework-dependent (FD): well under 1 MB, but
                             it needs the .NET 10 Desktop Runtime installed
  The exes have plain names, the version being the release's, as the author's
  NotepadRedo has (NotepadRedo.exe and NotepadRedoFD.exe). So each version builds
  into a folder of its own: a copy of an earlier build that is running, to try it,
  holds its file, and must not stop the next version's build.

  The version comes from the VERSION file next to this script, the single source
  of truth: bump it for every release, so Windows sees an upgrade.

  Needs the WiX Toolset (installed as a .NET tool if missing) with its UI and
  Firewall extensions (added if missing). WiX 7 asks once for its EULA to be
  accepted ("wix eula accept wix7"): that is for you to decide, so this script
  doesn't.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'version.ps1')
$version = (Get-ReleaseVersion $root).Text
$project = Join-Path $root 'RemindMe.Desktop\RemindMe.Desktop.csproj'
$out = Join-Path $root "release\$version"
$msi = Join-Path $out "RemindMe-$version.msi"
$exe = Join-Path $out 'RemindMe.exe'
$exeFd = Join-Path $out 'RemindMeFD.exe'
# Everything in between (both builds' intermediate files and publish folders) lives
# here, and goes when the script ends.
$work = Join-Path ([IO.Path]::GetTempPath()) "remindme-build-$([guid]::NewGuid().ToString('N'))"

Write-Host "=== RemindMe ${version}: Windows installer and exes ==="

# This script's own files from an earlier run of this version go first, so a failed
# run can't leave them beside a new MSI.
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($file in $msi, $exe, $exeFd) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    try { Remove-Item -LiteralPath $file -Force }
    catch { throw "Couldn't replace ${file}: is a copy of it running? Close it, then build again. ($($_.Exception.Message))" }
}

# Publishes one single-file build of the desktop app into $work\$name, and returns its
# exe. Each build has intermediate files of its own (--artifacts-path), so the two can't
# mix up their runtime settings. The exe must be all it made (pdbs aside): only the exe
# is released, so anything beside it would be missing from the release.
function Publish-Exe([string]$name, [string[]]$options) {
    $dir = Join-Path $work "publish-$name"
    # To the screen, not into the function's result, which is the folder alone.
    dotnet publish $project -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$version --artifacts-path (Join-Path $work "artifacts-$name") -o $dir @options | Out-Host
    if ($LASTEXITCODE) { throw "dotnet publish ($name) failed." }
    $others = @(Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Name -ne 'RemindMe.exe' -and $_.Extension -ne '.pdb' })
    if ($others.Count -gt 0) {
        throw "The $name publish made more than RemindMe.exe ($($others[0].Name)...): the release takes only the exe, so it would go without."
    }
    return $dir
}

# Writes the installer's license page to $path, from LICENSE, so the text lives in one
# place: a paragraph of RTF for each of LICENSE's (blank-line separated, their lines
# joined), the first, the license's name, in bold.
function Write-LicenseRtf([string]$path) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
    $paragraphs = @($text.Trim() -split '\r?\n\s*\r?\n' | ForEach-Object { (($_ -split '\r?\n') | ForEach-Object { $_.Trim() }) -join ' ' })
    $rtf = [Text.StringBuilder]::new()
    [void]$rtf.Append('{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}\viewkind4\uc1\pard\sa120\f0\fs20 ')
    for ($i = 0; $i -lt $paragraphs.Count; $i++) {
        $escaped = [Text.StringBuilder]::new()
        foreach ($c in $paragraphs[$i].ToCharArray()) {
            if ($c -eq '\' -or $c -eq '{' -or $c -eq '}') { [void]$escaped.Append('\').Append($c) }
            # \u takes a signed 16-bit number, and ? stands in for readers without Unicode.
            elseif ([int]$c -gt 127) { [void]$escaped.Append("\u$(if ([int]$c -gt 32767) { [int]$c - 65536 } else { [int]$c })?") }
            else { [void]$escaped.Append($c) }
        }
        if ($i -eq 0) { [void]$rtf.Append("\b $escaped\b0\par`n") } else { [void]$rtf.Append("$escaped\par`n") }
    }
    [void]$rtf.Append('}')
    [IO.File]::WriteAllText($path, $rtf.ToString(), [Text.Encoding]::ASCII)
}

try {
    New-Item -ItemType Directory -Force $work | Out-Null
    Write-Host '[1/4] Publishing the self-contained single-file exe...'
    $publish = Publish-Exe 'self-contained' @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true')

    Write-Host '[2/4] Publishing the framework-dependent single-file exe...'
    $publishFd = Publish-Exe 'framework-dependent' @('--self-contained', 'false')

    Write-Host '[3/4] Checking the WiX Toolset...'
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        dotnet tool install --global wix
        if ($LASTEXITCODE) { throw 'Could not install WiX: dotnet tool install --global wix' }
        # The global tools folder reaches the PATH of shells started later, not this one's.
        $tools = Join-Path $env:USERPROFILE '.dotnet\tools'
        if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { $env:Path = "$tools;$env:Path" }
    }
    $extensions = (wix extension list --global) -join "`n"
    foreach ($ext in 'WixToolset.UI.wixext', 'WixToolset.Firewall.wixext') {
        if ($extensions -notmatch [regex]::Escape($ext)) {
            wix extension add --global $ext
            if ($LASTEXITCODE) { throw "Could not add the WiX extension $ext." }
        }
    }

    Write-Host '[4/4] Building the MSI...'
    Write-LicenseRtf (Join-Path $work 'License.rtf')
    wix build (Join-Path $root 'installer\RemindMe.wxs') -arch x64 `
        -d "Version=$version" -d "PublishDir=$publish" `
        -b $work -b (Join-Path $root 'installer') -b (Join-Path $root 'RemindMe.Desktop') `
        -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext -pdbtype none -o $msi
    if ($LASTEXITCODE) { throw 'wix build failed (if it mentions the EULA: wix eula accept wix7).' }
    Copy-Item -LiteralPath (Join-Path $publish 'RemindMe.exe') -Destination $exe
    Copy-Item -LiteralPath (Join-Path $publishFd 'RemindMe.exe') -Destination $exeFd
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "=== Done: $out (RemindMe-$version.msi, RemindMe.exe, RemindMeFD.exe) ==="
