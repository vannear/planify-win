# Changelog

## 0.5.0

- Replace the list-top search box with quick task entry; press Enter to add to the selected list, or the first list from All Tasks.
- Move task search into the left sidebar.
- Add editable due dates and absolute display reminder date/time.
- Preserve due-date TZID and time when changing only its calendar day; preserve existing relative alarms until the reminder is explicitly changed.
- Retain the existing Nextcloud CalDAV synchronization and conflict-safety behavior.

## 0.4.0 — First public release

- Windows x64 MSI installer and portable distribution.
- Nextcloud/CalDAV task collection discovery and sync across all lists.
- Local SQLite cache and durable offline edits with ETag conflict protection.
- Task creation, notes, completion, reopening and deletion.
- Windows Credential Manager integration and optional automatic connection.
- Planify-inspired sidebar, task views, counts and application icon.
- Public-source cleanup: no personal server URL, credentials, databases or development artifacts.

This is an early community Windows implementation, not an official Planify release. Recurring and collaborative tasks remain read-only. Reminders are CalDAV/Apple Reminders VTODO alarms; Windows background reminder notifications are not implemented.
