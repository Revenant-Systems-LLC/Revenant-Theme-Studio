# Revenant Theme Studio

A no-bullshit Windows icon and theme manager for people who care what their desktop looks like.

Windows makes changing system icons painful. The official path is buried in obscure dialogs, partial across icon types, and one wrong move in `regedit` gets you a broken shell. The community workaround is Resource Hacker, which has looked the same since 2002 and still requires you to manually copy protected system files, take ownership, edit binary resources by hand, and pray nothing breaks.

RTS does the work for you, with a clean UI, automatic backups, and full undo on every change. No admin elevation required for normal use. The protected-file edits are handled by a separate helper executable that elevates only when actually needed, so the main app stays sandboxed and friendly.

**Revenant Systems LLC**

[Download on the Microsoft Store](<STORE_URL>) · Built by [@DΛVΣӨFŦΉΣDΣΛD](#)

---

## Free vs Pro

| Feature | Free | Pro |
|---|:---:|:---:|
| Folder icons via `desktop.ini` (one at a time) | ✓ | ✓ |
| Bundled icon library (30 free icons) | ✓ | ✓ |
| Curated artist-made Pro icon pack (140+ icons) |  | ✓ |
| **Auto Match** (scan directory tree, bulk apply by name) |  | ✓ |
| **Drive icons** (per-drive custom icons via HKCU) |  | ✓ |
| **Shell location icons** (Desktop, Downloads, Documents, This PC, Recycle Bin, and more) |  | ✓ |
| **Imageres MUN editor** (replace system icons inside `imageres.dll.mun` safely) |  | ✓ |
| Automatic registry snapshots and MUN file backups |  | ✓ |
| Full change history with one-click undo |  | ✓ |
| Wallpaper system (v2.0, see below) |  | ✓ |

Pro unlock is a one-time $10 license key.

## What it does

### Folder icons (Free and Pro)
Pick any folder, pick an icon, click apply. Explorer refreshes immediately. Works via standard `desktop.ini` writes, no system modification, no admin needed.

### Auto Match (Pro)
Point it at a directory tree and it scans every folder name against the icon library, applying icons only where it has a strong name match. Folders with no confident match are skipped. Nothing gets applied silently.

### Drive icons (Pro)
Set a custom icon for any drive letter. Per-user HKCU registry only, no admin required, does not affect other accounts on the same machine.

### Shell location icons (Pro)
Override the icons for well-known shell locations (Desktop, Downloads, Documents, Music, Pictures, Videos, This PC, Recycle Bin, and more) via per-user HKCU CLSID overrides. Same safety properties as drive icons.

### Imageres MUN editor (Pro)
For the system icons that live inside `imageres.dll.mun` (the ones the registry overrides cannot touch), RTS ships a dedicated MUN editor that replaces icons inside the resource file directly. Before any change, RTS makes a backup of the original MUN. The actual write is done by `RTS.MunWriter`, a separate helper executable that elevates only for the duration of the edit. This is the part that replaces Resource Hacker, with a UI built for humans instead of for resource engineers.

### Change history and undo (Pro)
Every modification is logged with full before-state. One-click undo reverses any change and restores the previous icon reference. Registry snapshots and MUN backups are kept automatically so nothing is ever destructive.

## Coming in 2.0: the wallpaper system

The 2.0 update adds a multi-monitor wallpaper engine designed for people whose current solution is "right-click, set as desktop background, repeat every two weeks."

- **Multi-monitor management for up to 4 displays** with a draggable UI that renders each monitor at its actual resolution.
- **Two display modes:** one image stretched across all screens, or independent images per screen.
- **Image board sourcing:** point it at any image board you trust and let RTS pull, rotate, and apply wallpapers automatically. Set a schedule, set a tag, walk away.
- **On-device ML upscaling** via ONNX RealESRGAN x4plus. Pulled images get upscaled before being applied so a 1080p source does not look like pixelated bullshit on a 4K monitor.

Set it once and you never have to change your wallpaper again.

2.0 is in active development. Pro license carries forward, no upgrade fee.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- .NET 8 (Windows)
- No admin elevation required for Free or for most Pro features. The MUN editor briefly elevates the `RTS.MunWriter` helper for the duration of the protected-file write, then drops elevation. The main app never runs elevated.

## Why it is safe

- All registry edits go to HKCU only, never HKLM.
- Every modification is logged with a full before-state in the change history.
- MUN file edits are preceded by an automatic backup of the original file.
- The helper that performs protected-file writes (`RTS.MunWriter`) runs as a separate, signed executable with a minimal scope. The main UI never touches `imageres.dll.mun` directly.
- Auto Match never applies an icon without a confident name match.
- Nothing is ever applied silently. Every action shows in the change log and can be undone.

## Bundled icon library

The `IconEngine/` directory ships with the binary and contains:
- `folder-icons/` for app, brand, and theme folder icons
- `drive-icons/` for custom drive icons
- `gunmetal-theme/` for the Revenant gunmetal aesthetic

Free includes a 30-icon starter set. Pro unlocks the full 140+ icon curated pack, designed by Revenant Systems. You can also point the app at any folder of `.ico` files on your system, library-bundled or not.

## Building from source

```
dotnet build
```

Target framework: `net8.0-windows`. The `RTS.MunWriter` helper builds as a separate project alongside the main app.

## License

Copyright © Revenant Systems LLC. All rights reserved. Pro features require a valid license key.
