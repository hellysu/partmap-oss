# AGENTS.md — PartMap

PartMap is a Windows WPF desktop application for visually organizing 3MF and G-code 3MF files.

## Working principles

- Prefer small, focused changes over broad refactors.
- Reuse the existing hotspot, drag/drop, grouping, archive, portable configuration, and slicing flows.
- File operations must protect user data: never silently overwrite real model files and keep destructive actions explicit.
- Shared-directory changes must preserve locking/revision protections and avoid blocking another application that is saving a 3MF.
- UI and drag/drop changes should be verified in the real Windows client when practical; compilation alone is not an interaction test.

## Repository data rules

- Do not commit real product/customer names, product artwork, model files, G-code, local settings, logs, credentials, or private network paths.
- Use `development-data/` and synthetic names for tests and examples.
- Portable metadata lives under `配置/PartMap.product.json` with its diagram under `配置/包装图.*`.
- `历史/` and `gcode/历史/` are archive directories and are not normal scan targets.

## Verification

For ordinary changes, run the affected smoke tests and a strict build. Before release or packaging changes, run `./verify-development.ps1 -Publish`.