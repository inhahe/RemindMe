<#
  Reads the release version from the VERSION file, for build-msi.ps1, build-apk.ps1
  and release.ps1, which dot-source this file.

  A version is two to four numbers, a.b.c.d. Both platforms put limits on the parts:
  Windows Installer allows at most 255.255.65535 (and ignores a fourth part), and the
  APK's versionCode, a*1000000 + b*10000 + c*100 + d, is unique and grows with the
  version only while b, c and d stay below 100. So a is at most 255, and b, c and d
  are at most 99.
#>

# Returns the version's text (as written, e.g. "1.2.3") and its four parts, missing
# parts being 0. Throws if VERSION breaks the rules above.
function Get-ReleaseVersion([string]$root) {
    $text = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
    if ($text -notmatch '^\d+(\.\d+){1,3}$') { throw "VERSION must be like 1.2.3.4, not '$text'." }
    $parts = @($text.Split('.') | ForEach-Object { [int]$_ }) + @(0, 0, 0)
    $parts = $parts[0..3]
    if ($parts[0] -gt 255 -or ($parts[1..3] | Where-Object { $_ -gt 99 })) {
        throw "VERSION $text is out of range: the first part can be at most 255, the others at most 99."
    }
    [pscustomobject]@{ Text = $text; Parts = $parts }
}
