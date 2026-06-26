# Revenant Theme Studio — CLAUDE.md

Auto-loaded when Claude Code works in this directory.
Supplements M:\Projects\CLAUDE.md and M:\CLAUDE.md (both always loaded).

---

## Build & Verify

```powershell
cd M:\Projects\Revenant-Theme-Studio
dotnet build      # must be 0 errors, 0 warnings before any commit
dotnet run        # launch app for visual verification
```

**After every code change:** run `dotnet build`. If it fails, read the full error before suggesting a fix.
**Feedback loop:** tell Claude "run dotnet build after each change and iterate until clean."

---

## Current Branches

| Branch | Purpose |
|--------|---------|
| `main` | v1.0 shipped, live at revenantsystems.net |
| `2.0/wallpaper-sourcing` | Active 2.0 wallpaper development |

---

## v1.0 — Stable, Do Not Touch

- Folder icon replacement (desktop.ini)
- Drive icon override (registry)
- Shell/system icon override (HKCU registry)
- Auto Match (Levenshtein)
- Pro license gating (RTSP- key, HMAC-validated, license.key file)
- Registry consent + backup on first Pro action
- Undo/change history

**Known bug:** Auto Match has a logic error requiring a full code sweep — not triaged yet. Do not attempt to fix without explicit instruction.

---

## v2.0 — Wallpaper Feature (In Progress)

### Backend: COMPLETE (builds clean)

| File | Purpose |
|------|---------|
| `Features/Wallpaper/Services/DisplayDetectionService.cs` | P/Invoke EnumDisplayMonitors |
| `Features/Wallpaper/Models/WallpaperSource.cs` | HTML parsing + API-first extraction |
| `Features/Wallpaper/Services/ImageBoardExtractor.cs` | Danbooru, Gelbooru, Konachan, Wallhaven |
| `Features/Wallpaper/Services/ImageDownloadService.cs` | Download to temp |
| `Features/Wallpaper/Services/UpscalingService.cs` | Real-ESRGAN 4x, ONNX Runtime DirectML, tiled |
| `Features/Wallpaper/Services/WallpaperEngineService.cs` | IDesktopWallpaper COM, per-monitor apply |
| `Features/Wallpaper/Services/WallpaperOrchestrator.cs` | Full pipeline: fetch -> download -> upscale -> apply |
| `Features/Wallpaper/Services/WallpaperScheduler.cs` | Recurring timer, 1-min floor |
| `Features/Wallpaper/Services/WallpaperConfigService.cs` | JSON persistence |
| `Features/Wallpaper/Services/TempFileCleanupService.cs` | Daily purge |

**NuGet:** HtmlAgilityPack 1.12.4, Microsoft.ML.OnnxRuntime.DirectML 1.24.4
**ONNX model:** NOT present. Must source `realesrgan-x4plus.onnx` (~64MB) and drop in `Assets/Models/` manually.

### UI: NOT BUILT

Wallpaper tab UI is next. Two top-level flows:
- **One Surface** — monitors as combined canvas, one image, drag monitor windows to crop regions
- **Independent** — each monitor is its own slot with its own source/schedule

Source type (static image, rotating local folder, live board URL) is a slot property in either flow — not a top-level mode.

---

## Known Issues

- Rogue `Program.cs` in project root causes CS0017 — delete if present
- CS8618 non-nullable warnings in wallpaper feature — resolve before merge to main
- Possible duplicate class definitions from prior session (WallpaperSource, WallpaperImage etc.) — verify build is clean

---

## Hardware Context (wallpaper feature)

- GPU: RTX 3070 Ti (8GB VRAM) — DirectML upscaling appropriate
- Monitor count: 4
- Preferred rotation interval: ~5 minutes

---

## Architecture

- WPF / .NET 8, C#, single .csproj
- `Features/Wallpaper/` — all 2.0 code
- `Models/`, `ViewModels/`, `Services/`, `IconEngine/`, `Converters/` — v1.0 structure
- Target: `net8.0-windows`

---

## Multimodal Tip

For UI work: drag and drop a mockup image directly into the Claude Code terminal prompt.
Say "implement this UI" and give it `dotnet build` + `dotnet run` as its feedback loop.
It will iterate until the result matches.
