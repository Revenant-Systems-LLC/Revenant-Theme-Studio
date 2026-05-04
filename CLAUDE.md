# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is Revenant Theme Studio, a Windows 11 theme manager that allows users to customize folder, drive, and system icons using their own `.ico` files or a bundled icon library. The application uses Windows desktop.ini files and registry modifications to apply icons, with a consent-based system for safety.

## Common Development Tasks

### Building
```
dotnet build
```

Target framework: `net8.0-windows`

### Running Tests
Tests are located in the standard test directory structure. Run all tests with:
```
dotnet test
```

### Running the Application
```
dotnet run
```

## High-Level Code Architecture

### Core Components

1. **MainWindow.xaml** - Main UI with three tabs:
   - Manual tab: Direct icon assignment to folders, drives, and system locations
   - Drive tab: Drive-specific icon management
   - Auto Match tab: Automated folder icon matching system

2. **Services** - Core functionality is implemented in service classes:
   - `FolderIconService` - Manages folder icon application via desktop.ini
   - `DriveIconService` - Manages drive icon registry modifications
   - `ShellIconService` - Manages system shell location icon overrides
   - `IconMatchingService` - Auto-matching algorithm for folder names to icons
   - `ChangeHistoryService` - Tracks and manages undo history
   - `ManagedStorageService` - Manages persistent storage of icons
   - `LicenseService` - Pro license validation system
   - `ConsentService` - User consent management

3. **Key Patterns**:
   - All icon changes are reversible via the undo system
   - Icons are stored in managed storage to prevent broken references
   - Pro features are gated behind HMAC-SHA256 license validation
   - Registry changes are made per-user via HKCU to avoid requiring admin rights
   - All changes are consent-gated to prevent unwanted system modifications

### Data Flow

1. User selects target and icon
2. User clicks Apply
3. Service applies change to system (desktop.ini or registry)
4. Change is recorded in history for undo
5. App shutdown triggers archiving of critical icons to ProgramData

### Architecture Notes

- WPF/.NET 8 application with MVVM pattern
- Services are designed to be stateless where possible
- All file system and registry operations are wrapped in services
- Async operations for long-running tasks (Auto Match scanning)
- Pro features: Auto Match, System Icons, Undo, and advanced icon management
- Free features: Basic folder/drive icon application

### Build Configuration

The project targets `net8.0-windows` and uses WPF. The project file excludes worktree directories to prevent build conflicts.

## Wallpaper Feature — Implementation Plan

### Overview
Set-and-forget wallpaper system. User provides an image board URL with their filters → app extracts top image → upscales if needed → previews → applies to monitors. User never thinks about wallpaper again.

**Phase 1: MVP** — URL source → image extraction → upscale → preview → apply
**Phase 2: Monitor Placement UI** — drag up to 4 monitor boxes (proportional to real resolutions) over a wallpaper image, each monitor shows its cropped section without stretching

### Phase 1 Implementation Order

1. **DisplayDetectionService** (`Features/Wallpaper/Services/DisplayDetectionService.cs`)
   - P/Invoke `EnumDisplayMonitors` + `GetMonitorInfo` to enumerate monitors
   - Returns `List<MonitorProfile>` with real resolution, position, device name
   - Foundation: wallpaper engine and preview both need this

2. **WebImageSource** (update `Features/Wallpaper/Models/WallpaperSource.cs`)
   - HTTP fetch the user's URL → parse HTML → extract image URLs
   - Per-site generalized extraction rules: position-based (first, last, random, nth)
   - User provides URL with their preferred filters already applied
   - NuGet: `HtmlAgilityPack` for HTML parsing

3. **UpscalingService** (`Features/Wallpaper/Services/UpscalingService.cs`)
   - Real-ESRGAN via ONNX Runtime — local inference, no API costs
   - Ships with app (~100-200MB model files in assets)
   - Takes image + target resolution → returns upscaled image
   - NuGet: `Microsoft.ML.OnnxRuntime` (CPU) or `Microsoft.ML.OnnxRuntime.DirectML` (GPU)

4. **WallpaperEngineService** (`Features/Wallpaper/Services/WallpaperEngineService.cs`)
   - `IDesktopWallpaper` COM interface for per-monitor wallpaper (Windows 8+)
   - Apply wallpaper to specific monitors by device path
   - Implements `IWallpaperEngineService` interface from WallpaperModels.cs

5. **Preview + Config**
   - WPF dialog showing the wallpaper before applying
   - Simple JSON config for saved sources and extraction rules
   - Ties all services together into the end-to-end flow

### Phase 2 — Monitor Placement UI (after MVP)
- Up to 4 monitor boxes, sized proportionally to real monitor resolutions
- Drag monitors over a wallpaper image to position them
- Each monitor shows its cropped section at native resolution — no stretching
- Solves mismatched-monitor stretching (e.g., 1080p TV + 2K monitor)

### Key Dependencies
- `HtmlAgilityPack` — HTML parsing for image extraction
- `Microsoft.ML.OnnxRuntime` or `Microsoft.ML.OnnxRuntime.DirectML` — ONNX inference
- Real-ESRGAN ONNX model file (realesrgan-x4plus.onnx or similar)
- No external APIs — everything runs locally

### Models & Interfaces (already exist)
- `Features/Wallpaper/Models/WallpaperModels.cs` — MonitorProfile, MonitorLayout, WallpaperAssignment, IWallpaperEngineService, IDisplayDetectionService, LayoutMode
- `Features/Wallpaper/Models/WallpaperSource.cs` — WallpaperSource, LocalFolderSource, WebImageSource (stubbed), WallpaperImage, SourceType

## Key Design Decisions

1. **No Admin Required** - All registry operations use HKCU, not HKLM
2. **Reversible Changes** - Every modification can be cleanly undone
3. **Consent Gating** - User must explicitly consent to changes
4. **Per-User Operations** - All changes are in user space, not system-wide
5. **Pro Licensing** - Advanced features require license validation
6. **Safety First** - Changes are validated and can be rolled back