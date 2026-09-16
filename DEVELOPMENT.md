# PartMap Development

PartMap is a Windows WPF desktop application targeting .NET 8.

## Requirements

1. Windows 10 22H2 or Windows 11.
2. .NET 8 SDK.
3. PowerShell 5.1 or PowerShell 7.

Visual Studio 2022 with the .NET desktop development workload is optional.

## Verify the project

```powershell
.\verify-development.ps1
```

To also rebuild and startup-check the self-contained x64/x86 packages:

```powershell
.\verify-development.ps1 -Publish
```

The verification script restores dependencies, runs the smoke tests, performs a strict Release build, and checks application startup.

## Repository layout

- `Controls/`: hotspot controls.
- `Dialogs/`: product and slicing dialogs.
- `Models/`: product, file, hotspot, and portable package models.
- `Services/`: grouping, file operations, configuration, thumbnails, slicing, and portable product data.
- `tests/PartMap.SmokeTests/`: regression tests.
- `development-data/`: reproducible, non-production sample data.
- `publish-portable.ps1`: self-contained Windows publishing.

`bin/`, `obj/`, `dist/`, `artifacts/`, local products, settings, logs, and real model data are not source files and must not be committed.

## Product data

PartMap keeps local product records under `products/`. A model folder can also contain portable product metadata under `配置/PartMap.product.json` and its diagram under `配置/包装图.*` so the mapping can be restored on another computer.

Real `.3mf` / `.gcode.3mf` files remain in the user-selected model directory. The repository's `development-data/` samples are generated specifically for development and testing.

## Development guidelines

Keep changes focused. Protect real files from silent overwrite or destructive operations. Changes that touch file operations, portable configuration, shared-directory behavior, or slicing should include or update regression coverage.
