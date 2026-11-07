<#
  Builds the Windows installer, release\RemindMe-<version>.msi, and the
  self-contained exe beside it, release\RemindMe-<version>.exe (S9).

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
$publish = Join-Path $root 'installer\publish'
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "=== RemindMe ${version}: Windows installer ==="

Write-Host '[1/3] Publishing the self-contained single-file exe...'
dotnet publish (Join-Path $root 'RemindMe.Desktop\RemindMe.Desktop.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

Write-Host '[2/3] Checking the WiX Toolset...'
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

Write-Host '[3/3] Building the MSI...'
$msi = Join-Path $out "RemindMe-$version.msi"
wix build (Join-Path $root 'installer\RemindMe.wxs') -arch x64 `
    -d "Version=$version" -d "PublishDir=$publish" `
    -b (Join-Path $root 'installer') -b (Join-Path $root 'RemindMe.Desktop') `
    -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext -pdbtype none -o $msi
if ($LASTEXITCODE) { throw 'wix build failed (if it mentions the EULA: wix eula accept wix7).' }
Copy-Item (Join-Path $publish 'RemindMe.exe') (Join-Path $out "RemindMe-$version.exe") -Force

Write-Host "=== Done: $msi ==="
