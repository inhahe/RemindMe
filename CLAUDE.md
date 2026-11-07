# Alarm & Calendar: project notes

- **Start every task by reading `ROADMAP.md`, then `design.md`.** The roadmap
  maps each requirement from `todo.txt` (R1–R17, D1–D5) to a phase, and ticks
  off what is done. Tick items in the same commit that finishes them.
- The user-facing document is `README.md`.
- Bugs and technical debt go in `known-issues.md`.
- The folder name contains `&`, which `cmd` treats as a command separator.
  Quote every path in `.bat` files, and prefer PowerShell or Git Bash for build
  commands.
- Build: `dotnet build AlarmCalendar.slnx`. Test: `dotnet test AlarmCalendar.Core.Tests`.
