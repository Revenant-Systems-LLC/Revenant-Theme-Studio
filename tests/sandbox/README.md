# Sandbox test venue

Dave picked Windows Sandbox / a VM as the venue for tests on 2026-11-09. On Yggdrasil, `WindowsSandbox.exe` is present and the Hyper-V services (`vmms`, `vmcompute`) are running.

## How it works

- `rts-test.wsb` opens a disposable Windows Sandbox. When the window closes, everything inside it is thrown away.
- The config turns off the virtual GPU (no VRAM used on the host), networking, mic, camera, clipboard and printer, and uses 4 GB of RAM.
- The sandbox sees only two host folders:

  | Folder | Access | What it holds |
  |---|---|---|
  | `kit\` | read-only | the test runner and the self-contained builds it needs |
  | `results\` | writable | the only place a run can write back to the host |

- At logon, `kit\run.ps1` runs the tests, writes a report into `results\`, and shuts the sandbox down. Nobody has to click anything.
- The sandbox runs the same Windows build as the host, 26300. Windows 10 22H2 checks need a separate Hyper-V VM from an ISO. That comes later.
- The sandbox starts with no .NET runtime installed, so test tools go into `kit\` as self-contained builds.

## Launch

Ask Dave before every launch. A run uses CPU and about 4 GB of RAM for a few minutes.

```powershell
WindowsSandbox.exe M:\Projects\revenant-theme-studio\tests\sandbox\rts-test.wsb
```

## Results

**Run 2026-11-09 05:31** ([tests-20261109-053143.txt](results/tests-20261109-053143.txt)): Sandbox build 26100.9711 (24H2 base), elevated.

**Test 1: library icons, settled.**
- What RTS writes today, `HKCU\CLSID\{guid}\DefaultIcon`, is ignored for both Documents GUID sets.
- `HKCU\Software\Classes\CLSID\{guid}\DefaultIcon` takes effect for both.
- RTS's own `ShellIconService.ApplyOverride("Documents")` changed nothing. The library-icon feature is broken as shipped.

**Test 4: elevated MUN write.**
- The commit failed with helper exit 4 ("Resource commit failed") after 479 ms. That means the update was opened, then `UpdateResource` or `EndUpdateResource` failed.
- The likeliest cause is that the file is in use (Explorer has it mapped). That is unconfirmed: the helper reports no Win32 code.
- The in-place write can't work while Windows has the file open, and the "WRP-safe" fallback only triggers when `BeginUpdateResource` fails, so it never ran.
- The failure was clean:
  - the group is unchanged
  - the file still loads
  - the owner is back to TrustedInstaller
  - the ACL matches stock, including `Administrators:(RX)`, which the old restore code would have stripped
- The frame-drop bug could not be observed because nothing was written.

**Run 2026-11-09 05:37** ([tests-20261109-053704.txt](results/tests-20261109-053704.txt)): verification of the library-icon fix.
- **Test 1b passed 23 of 24 checks.**
  - All 6 visible library targets now take a custom icon on every GUID, including the nav-pane set, and restore to their exact stock icons.
  - The old `HKCU\CLSID` writes are removed, and no per-user keys are left behind.
  - The one failed check is 3D Objects. Windows 11 no longer lists it under This PC, so it cannot be probed (`E_INVALIDARG`). Its legacy cleanup and restore still passed.
- **Test 4 is unchanged**: the write still fails cleanly with exit 4, as expected, since the write path has not been changed.

**Runs 2026-11-09 06:10 to 14:41: the elevated MUN write, from broken to working.**

| Run | Test 4 | What it showed |
|---|---|---|
| 06:10 | 15/24 | Windows error 50. `UpdateResource` refuses icon types in a MUI language-neutral file (documented in its remarks). It never worked on Windows 10/11 |
| 14:20 | 26/30 | New direct resource patcher: both writes land, all sizes kept, original preserved. The file and folder permissions drifted |
| 14:41 | **32/32** | Permissions now saved and restored byte-for-byte with SetFileSecurity. The file and folder descriptors are identical, and all 714 other items under SystemResources kept theirs |

The 14:41 run used Dave's jade-gold `documents.ico`, both through the app's import pipeline and as authored. In both cases:
- Every slot kept its size.
- Windows draws it at 16 to 256.
- The Windows original is preserved byte-identical as `imageres.dll.mun.rts-original-<version>-*`.
- The second write set the earlier RTS copy aside as `.rts-old-*`.

Test 1b on the same run: 23/24 with black-suede-gold. The one miss is 3D Objects, which Windows 11 doesn't show.

## Test queue

Each unknown comes from the specs in `docs\specs`.

| # | Test | Spec | Answers |
|---|---|---|---|
| 0 | Smoke: build, admin, imageres present, .NET present | — | Can the harness run at all? |
| 1 | Library icons: write `HKCU\CLSID` vs `HKCU\Software\Classes\CLSID` for Documents, then read the icon the shell resolves | icon v2, README §3.1 | Decision 4 (the library-icon fix) |
| 2 | Desktop icons: precedence of `Explorer\CLSID` vs `Software\Classes\CLSID` for This PC and Recycle Bin | icon v2 SHELL | Which key to write |
| 3 | File-type icons on a scratch extension: the precedence matrix (extension, ProgID, UserChoice) | icon v2 FTYPE | Why IconPackager fails on Win11 |
| 4 | MUN helper end to end: elevated write to imageres.dll.mun, frame sizes, ACL restored to stock | appendix D, icon v2 MUN | The never-tested elevated path |
| 5 | Theme-service overwrite: write HKCU colors, cursors and sounds, trigger theme events, re-read | engine F4 | Whether a managed .theme is needed |
| 6 | Named sound-scheme registration: registry diff | cursors spec | Decision 4 in that spec |
| 7 | Accent palette: write the bundle palette, re-read after a colorization change | colors spec | Whether Windows overwrites it |
