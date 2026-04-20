# Revenant Theme Studio 1.0 — Ship Handoff

**Status**: Code is ship-ready on `rescue/working-build`. Release binary publishes cleanly (413 KB EXE + 53 MB resource DLL, framework-dependent on .NET 8). Store tile assets generated. MSIX manifest staged. **Two manual steps remain before you can submit to Partner Center.**

Everything in this note assumes you are in the repo root.

---

## What was done this session

1. **Consolidated the 1.0 working state.** The dirty working tree on `rescue/working-build` (consent dialog, license window, Controls, Assets/, gunmetal theme, etc.) was committed in a single "Consolidate 1.0 working state" commit. `Assets/Assets.zip` (27 MB source archive) and `.vs/` were added to `.gitignore` and kept out of the commit.
2. **Baked ship metadata into the assembly.** `Revenant-Theme-Studio.csproj` now emits `Version=1.0.0`, `Product=Revenant Theme Studio`, `Company=Revenant Systems LLC`, `Copyright`, and `Description` via auto-generated assembly attributes. Verified in the DLL's `VersionInfo`.
3. **Generated the full Store tile set.** `Package\generate-store-assets.ps1` resamples `Assets\rts_app-alt.png` (2560²) and `Assets\rts.png` (2.03:1) into the 11 PNGs the manifest references, at both scale-100 and scale-200. Outputs are in `Package\Assets\`.
4. **Staged the MSIX manifest.** `Package\Package.appxmanifest` is the shippable manifest (derived from `Package.appxmanifest.template`). Capabilities, target device family, logo references, and runFullTrust are configured. The only placeholder is the publisher string (see Step 1 below).
5. **Verified the Release publish.** `dotnet publish -c Release -r win-x64` produces a clean `dist\publish\` with no warnings.

---

## What you still have to do

### Step 1 — Update the Publisher identity (~1 min, blocker)

Open `Package\Package.appxmanifest` and replace

```xml
<Identity ... Publisher="CN=Revenant Systems LLC" ... />
```

with the **exact** Publisher ID from Partner Center:
1. Go to [Partner Center](https://partner.microsoft.com/dashboard) > your dashboard.
2. **Account Settings > Legal Info > Publisher display name / Publisher ID**.
3. Copy the Publisher ID value (looks like `CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`).
4. Paste it into the `Publisher=` attribute. Must match byte-for-byte.

Do the same in `Package.appxmanifest.template` if you want the template to stay in sync (optional — the template is documentation now).

### Step 2 — Create the packaging project (~10 min, blocker)

The Microsoft Store ingests MSIX, not raw EXE. To build an MSIX:

**Option A — Visual Studio WAP project (recommended, native tooling):**
1. Open `Revenant-Theme-Studio.sln` in Visual Studio.
2. Add > New Project > **Windows Application Packaging Project** (C#, targets `.wapproj`). Name it `Revenant-Theme-Studio.Package` or similar.
3. In the WAP project, right-click **Applications** > **Add Reference** > check `Revenant-Theme-Studio`.
4. Replace the WAP project's auto-generated `Package.appxmanifest` with our copy: `Package\Package.appxmanifest`.
5. Copy `Package\Assets\*.png` into the WAP project's `Assets\` folder (same filenames).
6. Right-click the WAP project > **Publish** > **Create App Packages** > **Microsoft Store** > sign in, select the app reservation, build.

**Option B — CLI (MakeAppx.exe + SignTool.exe):**
Only do this if you already have a packaging pipeline. Otherwise Option A is less error-prone.

### Step 3 — Reserve the app name in Partner Center (if not done yet)

Partner Center > **Apps and games** > **New product** > **MSIX or PWA app** > reserve **"Revenant Theme Studio"**. The package `Identity Name` in our manifest (`RevenantSystemsLLC.RevenantThemeStudio`) must match the one Partner Center assigns after reservation — **update both values if they differ**.

### Step 4 — Upload and submit

Partner Center walks you through: age ratings, privacy policy URL, screenshots (take from the running app — 5 tabs, each a good candidate), pricing, store listing copy. The **Help** tab inside the app is a good source for the store listing description.

---

## Reference — what's where

| Path | Purpose |
|------|---------|
| `Package\Package.appxmanifest` | Production MSIX manifest — edit Publisher, then use in WAP project |
| `Package\Package.appxmanifest.template` | Same content plus setup-instruction comments — reference only |
| `Package\Assets\*.png` | 11 Store tiles (StoreLogo, Square44, Square150, Square310, Wide310x150, SplashScreen + scale-200 variants) |
| `Package\generate-store-assets.ps1` | Re-run if you update the source art in `Assets\rts_app-alt.png` or `Assets\rts.png` |
| `dist\publish\` | Release publish output (framework-dependent net8.0-windows, win-x64). Gitignored. |
| `Revenant-Theme-Studio.csproj` | Has Version/Product/Company metadata baked in |
| `rescue/working-build` | Current ship branch. `main` (origin) is broken — the cleanup commits there removed csproj settings the build needs. Merge rescue into main when ready. |

---

## Known safe-to-ship notes

- **`.mun Editor` tab button is intentionally disabled** (`IsEnabled="False"`, "coming soon") — this is fine for 1.0, not a blocker.
- **`runFullTrust` capability** in the manifest is required by `DriveIconService` (HKCU writes) and `FolderIconService` (desktop.ini writes). Store reviewers will check this — it's legitimate and well-documented in `Package.appxmanifest.template` comments.
- **53 MB DLL** is expected — it embeds the full icon library from `Assets\` as WPF pack resources. Store download size is acceptable.

---

## Rollback

If you need to undo this session's commits: `git log` on `rescue/working-build` shows the last two are mine (`Consolidate 1.0 working state` and `Ship prep: metadata, Store tiles, packaging manifest`). `git reset --hard HEAD~2` from `rescue/working-build` returns to where you were. Nothing was pushed; nothing was merged into `main`.
