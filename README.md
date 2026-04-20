# Revenant Theme Studio

Custom icon manager for Windows. Replace folder icons, drive icons, and shell location icons (Desktop, Downloads, Documents, etc.) using your own `.ico` files or a bundled icon library.

**Revenant Systems LLC**

---

## What it does

- **Folder Icons** — Pick any folder on your system, select an icon from the library, apply. Explorer refreshes immediately. Works via `desktop.ini`.
- **Drive Icons** — Set a custom icon for any drive letter via HKCU registry. No admin required.
- **Shell Icons** — Override icons for well-known shell locations (Desktop, Downloads, Documents, Music, Pictures, Videos, This PC, Recycle Bin, and more) via per-user HKCU CLSID overrides.
- **Auto Match** — Scan a directory tree and automatically apply icons to folders with strong name matches. Unmatched folders are skipped — nothing is applied without a confident match.
- **Undo** — Every change is logged. Undo reverses the last applied change and restores the previous icon reference.

## Requirements

- Windows 10 (1809+) or Windows 11
- .NET 8 (Windows)
- No admin elevation required

## Icon Library

The bundled `IconEngine/` directory contains a large collection of `.ico` files organized by category:
- `folder-icons/` — app, brand, and theme icons for folders
- `drive-icons/` — custom drive icons
- `gunmetal-theme/` — Revenant gunmetal theme variants

You can also point the app at any folder of `.ico` files on your system.

## Building

```
dotnet build
```

Target framework: `net8.0-windows`

## License

Copyright © Revenant Systems LLC. All rights reserved.
