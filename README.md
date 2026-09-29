# Planify Windows Community

An unofficial Windows task manager inspired by [Planify](https://github.com/alainm23/planify), built with C#, WinUI 3 and SQLite. Nextcloud CalDAV two-way synchronization is its core feature. Independent community project; not an official Planify Windows release.

[中文说明](README.zh-CN.md) · [Download](https://github.com/vannear/planify-win/releases/latest) · [Changelog](CHANGELOG.md)

## Install and connect

Download the x64 MSI from Releases. Windows 10 version 1809 or newer, or Windows 11, is required. Runtime components are included. The installer is currently unsigned.

Installation is per user under `%LOCALAPPDATA%\Programs\Planify Windows Community`, with a Start menu shortcut. Quit an older running copy before installing. Existing task data is reused.

1. Open account settings and enter your Nextcloud URL, username and application password.
2. Enable “remember password” to store it in Windows Credential Manager after successful authentication.
3. Click “sync all lists” to upload and download changes. Synchronization is manual; automatic connection does not run background synchronization.
4. Save task edits before synchronizing. Offline edits remain in a persistent local queue.

For Apple Reminders interoperability, connect both clients to the same Nextcloud CalDAV account. This app does not connect to iCloud. Test a separate list first; not every Apple task type has been validated.

## Features and limitations

- Discover CalDAV task lists and synchronize all lists in one action.
- Create, edit, complete, reopen and delete ordinary tasks; edit titles and notes.
- Local SQLite cache and durable offline upload queue.
- Conditional writes using ETags; preserve local edits when concurrent server changes cause conflicts.
- Preserve untouched iCalendar fields, including Apple extensions, due dates, time zones and alarms.
- Native Windows Credential Manager integration, including password replacement and removal.
- Sidebar task views, list counts and application icon. The current interface is Chinese.

Recurring tasks, recurrence exceptions and tasks with organizers/attendees are read-only. There is no due-date editor, subtask creation, Windows reminder notification, background synchronization, automatic updater, list creation or multi-account interface yet. This is an early community release.

Task content is stored in ordinary SQLite at `%LOCALAPPDATA%\PlanifyWindowsCommunity\tasks.db`; it is not encrypted by this app. Exit the app before backing up that directory. Passwords are stored separately by Windows Credential Manager for the current Windows user on this machine. Uninstalling preserves task data and saved credentials. Use “forget saved password” inside the app before uninstalling if you want to remove its saved credential.

## Build

On Windows x64 with .NET 8 SDK:

```powershell
dotnet restore src/Planify.App/Planify.App.csproj --configfile NuGet.Config
dotnet run --project tests/Planify.Tests/Planify.Tests.csproj
dotnet publish src/Planify.App/Planify.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish
./installer/Copy-Notices.ps1 -PublishDir artifacts/publish
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2
./installer/Build-Installer.ps1 -PublishDir artifacts/publish
```

`Planify.Core` contains synchronization, iCalendar and persistence; `Planify.App` contains the WinUI interface. The 51 automated checks cover protocol handling, offline queues, batch sync and credential policy. Native credential storage and real-server interoperability require separate interactive testing.

## License and attribution

GPL-3.0-or-later; see [LICENSE](LICENSE). Inspired by upstream Planify's design and CalDAV architecture. The Planify icon originates from the upstream project; original authors retain their rights. This implementation was developed with AI assistance and is not endorsed by upstream. Third-party runtime components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
