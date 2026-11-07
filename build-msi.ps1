<#
  Builds the Windows release files in release\ (S9):
    RemindMe-<version>.msi   the installer, with .NET included
    RemindMe.exe             the program on its own, self-contained: .NET included
    RemindMeFD.exe           the same, framework-dependent (FD): under 1 MB, but it
                             needs the .NET 10 Desktop Runtime installed
  The two exes are named as in the user's other projects (NotepadRedo.exe and
  NotepadRedoFD.exe): plain names, the version being the release's.

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
$publish = Join-Path $root 'installer\publish'
$publishFd = Join-Path $root 'installer\publish-fd'
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "=== RemindMe ${version}: Windows installer and exes ==="

Write-Host '[1/4] Publishing the self-contained single-file exe...'
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish (self-contained) failed.' }

Write-Host '[2/4] Publishing the framework-dependent single-file exe...'
dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:Version=$version -o $publishFd
if ($LASTEXITCODE) { throw 'dotnet publish (framework-dependent) failed.' }

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
$msi = Join-Path $out "RemindMe-$version.msi"
wix build (Join-Path $root 'installer\RemindMe.wxs') -arch x64 `
    -d "Version=$version" -d "PublishDir=$publish" `
    -b (Join-Path $root 'installer') -b (Join-Path $root 'RemindMe.Desktop') `
    -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext -pdbtype none -o $msi
if ($LASTEXITCODE) { throw 'wix build failed (if it mentions the EULA: wix eula accept wix7).' }
Copy-Item (Join-Path $publish 'RemindMe.exe') (Join-Path $out 'RemindMe.exe') -Force
Copy-Item (Join-Path $publishFd 'RemindMe.exe') (Join-Path $out 'RemindMeFD.exe') -Force

Write-Host "=== Done: $msi, RemindMe.exe and RemindMeFD.exe ==="
