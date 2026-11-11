# RemindMe: project notes

- **Start every task by reading `ROADMAP.md`, then `design.md`.** The roadmap
  maps each requirement from `todo.txt` (R1–R17, D1–D5) to a phase, and ticks
  off what is done. Tick items in the same commit that finishes them.
- The user-facing document is `README.md`.
- Bugs and technical debt go in `known-issues.md`.
- The project was called Alarm & Calendar until its first release, and the
  folder keeps that name, `&` included: `cmd` treats it as a command separator.
  Quote every path in `.bat` files, and prefer PowerShell or Git Bash for build
  commands.
- Build: `dotnet build RemindMe.slnx`. Test: `dotnet test RemindMe.Core.Tests`.
- The repository is public, `github.com/inhahe/RemindMe`. `todo.txt` stays out
  of it (it is in `.gitignore`), as in the user's other projects.
