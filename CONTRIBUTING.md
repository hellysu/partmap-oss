# Contributing to PartMap

Thanks for helping improve PartMap.

## Development

- Windows 10 22H2 or Windows 11
- .NET 8 SDK
- PowerShell 5.1 or PowerShell 7

Run the full local verification before opening a pull request:

```powershell
.\verify-development.ps1
```

For changes that affect packaging, also run:

```powershell
.\verify-development.ps1 -Publish
```

Please keep pull requests focused and avoid committing real product files, product images, local settings, logs, credentials, or private network paths. Use the reproducible sample data under `development-data/` for tests and examples.
