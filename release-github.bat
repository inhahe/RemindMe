@echo off
:: Publishes a GitHub release; see release-github.ps1 (pass -DryRun to try it without publishing).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release-github.ps1" %*
