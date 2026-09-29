# Contributing

Please open an issue before a large feature or architectural change. Small fixes can be submitted directly as pull requests.

Use Windows x64 and the .NET 8 SDK. Run the console regression suite before submitting:

```powershell
dotnet run --project tests/Planify.Tests/Planify.Tests.csproj
```

Sync changes should include regression coverage for offline retries and concurrent edits. Preserve unknown iCalendar properties, Apple extensions, alarms and timezone components. Never publish task databases, app passwords or personal server addresses.

Contributions are provided under GPL-3.0-or-later. Keep upstream attribution. AI-assisted changes are welcome in this independent repository when reviewed and tested; this does not override the upstream Planify project's contribution policy.
