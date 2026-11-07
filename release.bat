@echo off
rem Publishes a GitHub release of RemindMe (github.com/inhahe/RemindMe): the Windows
rem installer and exe and the Android APK, tagged v<VERSION>, once the tests pass. Nothing
rem is published unless this is main as GitHub has it and VERSION is new: raise VERSION for
rem every release.
rem
rem   release.bat            test, build and publish
rem   release.bat --dry-run  test, build and show the release; publish nothing
rem
rem The work is in release.ps1. In a batch file, every path through this folder (whose
rem name has an "&", a command separator to cmd) would need careful quoting, and the
rem build scripts it shares VERSION's rules with are PowerShell already.
setlocal
set "ARGS="
if /i "%~1"=="--dry-run" (
    set "ARGS=-DryRun"
) else if not "%~1"=="" (
    goto :usage
)
if not "%~2"=="" goto :usage
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %ARGS%
exit /b %errorlevel%

:usage
echo Usage: release.bat [--dry-run]
echo Tests, builds and publishes the version in VERSION as a GitHub release; with --dry-run,
echo it publishes nothing.
exit /b 1
