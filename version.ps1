<#
  Reads the release version from the VERSION file, for the scripts that dot-source this
  file (build-msi.ps1, build-apk.ps1).

  A version is two to four numbers, a.b.c.d. Both platforms put limits on the parts:
  Windows Installer allows at most 255.255.65535 (and ignores a fourth part), and the
  APK's versionCode, a*1000000 + b*10000 + c*100 + d, is unique and grows with the
  version only while b, c and d stay below 100. So a is at most 255, and b, c and d
  are at most 99.
#>

# The version $text ("1.2.3", or a tag's "v1.2.3") as a [version] of four parts, missing
# parts being 0, so two versions compare with -lt and -gt; $null for anything else,
# including parts beyond the limits above.
function ConvertTo-ReleaseVersion([string]$text) {
    if ($text -notmatch '^v?(\d{1,3})(\.\d{1,2}){1,3}$') { return $null }
    $parts = @($text.TrimStart('v').Split('.') | ForEach-Object { [int]$_ }) + @(0, 0, 0)
    if ($parts[0] -gt 255) { return $null }
    return [version]::new($parts[0], $parts[1], $parts[2], $parts[3])
}

# Returns the version's text (as written, e.g. "1.2.3"), its four parts (missing ones
# being 0), and the same as a [version]. Throws if VERSION breaks the rules above.
function Get-ReleaseVersion([string]$root) {
    $text = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
    if ($text -notmatch '^\d+(\.\d+){1,3}$') { throw "VERSION must be like 1.2.3.4, not '$text'." }
    $version = ConvertTo-ReleaseVersion $text
    if (-not $version) {
        throw "VERSION $text is out of range: the first part can be at most 255, the others at most 99."
    }
    [pscustomobject]@{
        Text = $text
        Parts = @($version.Major, $version.Minor, $version.Build, $version.Revision)
        Version = $version
    }
}
