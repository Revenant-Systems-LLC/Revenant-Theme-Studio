// Theme Studio sandbox tests.
//
//   RtsSandboxTests probe <parsing-name>          Print the icon location the shell resolves for one item. Read-only.
//   RtsSandboxTests selfcheck <dir> [set-dir...]  Host-safe: build the generated test icon into <dir>, probe stock
//                                                 icons, and scan whole icon sets read-only (decode at 16/32/48/256,
//                                                 and run Theme Studio's own import rules on every file).
//   RtsSandboxTests run <results-dir> [icons-dir] Run every test. Refuses to run anywhere but Windows Sandbox,
//                                                 because tests write the registry and patch imageres.dll.mun.
//
// Test 1:  which registry key actually controls the Documents icon.
// Test 1b: Theme Studio's ShellIconService on all 7 library targets, using Dave's black-suede-gold set.
// Test 4:  the elevated imageres.dll.mun write, using Dave's jade-gold set:
//          round 1 through the app's import pipeline, round 2 with the authored icon as-is.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Revenant_Theme_Studio.Services;

if (args.Length >= 2 && args[0] == "probe")
{
    Console.WriteLine(Shell.ProbeOnSta(args[1]));
    return 0;
}

if (args.Length >= 2 && args[0] == "selfcheck")
{
    Directory.CreateDirectory(args[1]);
    var report = new Report(Path.Combine(args[1], "selfcheck.txt"));
    var ico = Tests.BuildTestIco(args[1]);
    report.L($"test icon frames: {string.Join(",", Tests.IcoSizes(ico))}");
    foreach (var item in Tests.DocumentsItems)
        report.L($"[{item.Label}] stock icon: {Tests.Probe(item.ParsingName)}");
    foreach (var setDir in args.Skip(2))
        Tests.ScanIconSet(report, setDir);
    report.L("DONE");
    return 0;
}

if (args.Length >= 2 && args[0] == "run")
{
    if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing to run: this is not Windows Sandbox. These tests write the registry and patch imageres.dll.mun.");
        return 2;
    }

    Directory.CreateDirectory(args[1]);
    var report = new Report(Path.Combine(args[1], $"tests-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
    string iconsDir = args.Length >= 3 ? args[2] : @"C:\rts-icons";
    report.L($"icon sets: {(Directory.Exists(iconsDir) ? iconsDir : "not found, using the generated test icon")}");
    var work = Path.Combine(Path.GetTempPath(), "rts-work");
    Directory.CreateDirectory(work);

    byte[] ico;
    try { ico = Tests.BuildTestIco(work); }
    catch (Exception ex) { report.L($"FATAL: could not build test icon: {ex}"); return 3; }
    var icoRef = Path.Combine(work, "probe.ico") + ",0";

    // Which tests: "1,4" by default, or a list such as "1,5" (Test 5 assumes an unpatched imageres.dll.mun).
    var select = (args.Length >= 4 ? args[3] : "1,4").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    report.L($"tests selected: {string.Join(",", select)}");

    if (select.Contains("1"))
    {
        try { Tests.LibraryIcons(report, icoRef, Path.Combine(iconsDir, "black-suede-gold")); }
        catch (Exception ex) { report.L($"TEST 1 crashed: {ex}"); }
    }
    if (select.Contains("5"))
    {
        try { ThemeTest.Run(report, iconsDir, work); }
        catch (Exception ex) { report.L($"TEST 5 crashed: {ex}"); }
    }
    if (select.Contains("4"))
    {
        try { Tests.MunWrite(report, ico, Path.Combine(iconsDir, "jade-gold", "documents.ico")); }
        catch (Exception ex) { report.L($"TEST 4 crashed: {ex}"); }
    }

    report.L("DONE");
    return 0;
}

Console.Error.WriteLine("usage: RtsSandboxTests probe <name> | selfcheck <dir> [set-dir...] | run <results-dir> [icons-dir]");
return 1;

internal sealed class Report
{
    private readonly string _path;
    public Report(string path) { _path = path; }
    public void H(string title) => L($"\n=== {title} ===");
    public void L(string line)
    {
        Console.WriteLine(line);
        File.AppendAllText(_path, line + Environment.NewLine);   // written as we go, so a crash keeps what ran
    }
}

internal static class Tests
{
    public record Item(string Label, string Guid, string ParsingName);

    // Documents has two GUID sets (spec-phase2-icon-engine-v2.md, SHELL table).
    public static readonly Item[] DocumentsItems =
    {
        new("This PC set", "{D3162B92-9365-467A-956B-92703ACA08AF}",
            @"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\::{D3162B92-9365-467A-956B-92703ACA08AF}"),
        new("Nav-pane set", "{A8CDFF1C-4878-43be-B5FD-F8091C1C60D0}",
            @"::{A8CDFF1C-4878-43be-B5FD-F8091C1C60D0}"),
    };

    /// <summary>Asks a fresh child process, so no in-process shell cache can hide a change.</summary>
    public static string Probe(string parsingName)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"probe \"{parsingName}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15000);
        return output;
    }

    // ── Test 1 / 1b ────────────────────────────────────────────────────────────

    public static void LibraryIcons(Report r, string icoRef, string bsgDir)
    {
        r.H("TEST 1: which registry key controls the Documents icon");
        r.L($"test icon: {icoRef}");

        foreach (var item in DocumentsItems)
        {
            r.L($"[{item.Label}] {item.Guid}");
            r.L($"  baseline: {Probe(item.ParsingName)}");

            foreach (var (label, classKey) in new[]
            {
                ("HKCU\\CLSID  (what RTS wrote before the fix)", $@"CLSID\{item.Guid}"),
                ("HKCU\\Software\\Classes\\CLSID",               $@"Software\Classes\CLSID\{item.Guid}"),
            })
            {
                bool existed = Registry.CurrentUser.OpenSubKey(classKey) != null;
                using (var k = Registry.CurrentUser.CreateSubKey(classKey + @"\DefaultIcon"))
                    k.SetValue(null, icoRef, RegistryValueKind.String);

                string got = Probe(item.ParsingName);
                bool takes = got.Contains("probe.ico", StringComparison.OrdinalIgnoreCase);
                r.L($"  write {label}: {got}  => {(takes ? "TAKES EFFECT" : "ignored")}");

                if (existed) Registry.CurrentUser.DeleteSubKeyTree(classKey + @"\DefaultIcon", false);
                else Registry.CurrentUser.DeleteSubKeyTree(classKey, false);
            }
        }

        // Theme Studio's own service, for every library target and both of its GUIDs,
        // with the matching icon from Dave's black-suede-gold set.
        r.H("TEST 1b: ShellIconService on all 7 library targets (black-suede-gold)");
        var iconFor = new Dictionary<string, string>
        {
            ["3D Objects"] = "folder.ico", ["Desktop"] = "desktop-computer.ico", ["Documents"] = "documents.ico",
            ["Downloads"] = "downloads.ico", ["Music"] = "music.ico", ["Pictures"] = "pictures.ico", ["Videos"] = "videos.ico",
        };
        var svc = new ShellIconService();
        int pass = 0, total = 0;
        foreach (var (name, file) in iconFor)
        {
            var spec = svc.Targets[name];
            string iconPath = Path.Combine(bsgDir, file);
            if (!File.Exists(iconPath)) iconPath = icoRef[..icoRef.LastIndexOf(',')];
            string iconRef = iconPath + ",0";

            var guids = new List<(string label, string guid, string parsing)>
            {
                ("This PC", spec.Clsid, $@"::{{20D04FE0-3AEA-1069-A2D8-08002B30309D}}\::{spec.Clsid}")
            };
            if (spec.SecondaryClsid != null) guids.Add(("nav pane", spec.SecondaryClsid, $"::{spec.SecondaryClsid}"));

            // Seed a write at the old ignored path, as an older build would have left behind.
            using (var legacy = Registry.CurrentUser.CreateSubKey($@"CLSID\{spec.Clsid}\DefaultIcon"))
                legacy.SetValue(null, iconRef);

            svc.ApplyOverride(name, iconRef);
            bool legacyGone = Registry.CurrentUser.OpenSubKey($@"CLSID\{spec.Clsid}") == null;
            foreach (var g in guids)
            {
                string applied = Probe(g.parsing);
                bool takes = applied.Contains(iconPath, StringComparison.OrdinalIgnoreCase);
                total++; if (takes) pass++;
                r.L($"{name,-10} [{g.label}] apply -> {(takes ? Path.GetFileName(iconPath) : applied)}");
            }
            r.L($"{name,-10} legacy HKCU\\CLSID write removed: {legacyGone}");

            svc.RestoreDefault(name);
            foreach (var g in guids)
            {
                string restored = Probe(g.parsing);
                bool stock = !restored.Contains(iconPath, StringComparison.OrdinalIgnoreCase);
                bool leftover = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{g.guid}") != null;
                total++; if (stock && !leftover) pass++;
                r.L($"{name,-10} [{g.label}] restore -> {restored}  leftover key: {leftover}");
            }
        }
        r.L($"TEST 1b: {pass}/{total} checks passed");
    }

    // ── Test 4 ─────────────────────────────────────────────────────────────────

    public static void MunWrite(Report r, byte[] probeIco, string jadePath)
    {
        r.H("TEST 4: elevated write to imageres.dll.mun (jade-gold)");
        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources", "imageres.dll.mun");

        // Round 1: what the app's MUN editor does today (Theme Studio's import rules).
        // Round 2: the authored icon exactly as Dave made it, all its frames intact.
        byte[] round1 = probeIco, round2 = probeIco;
        string source = "generated test icon";
        if (File.Exists(jadePath))
        {
            byte[]? normalized = null;
            Shell.RunSta(() => normalized = IcoFileBuilder.ValidateAndNormalize(jadePath).IcoBytes);
            round1 = normalized!;
            round2 = File.ReadAllBytes(jadePath);
            source = jadePath;
        }
        r.L($"source icon: {source}");
        r.L($"  round 1 (app import pipeline): {string.Join(",", IcoSizes(round1))}, PNG frames {PngFrames(round1)}");
        r.L($"  round 2 (authored as-is):      {string.Join(",", IcoSizes(round2))}, PNG frames {PngFrames(round2)}");

        var reader = new MunResourceReader();
        var before = reader.ReadAllGroups(target);
        r.L($"icon groups in file: {before.Count}");

        // A group with many frames, so a dropped 16/24/32 would show.
        var pick = before.FirstOrDefault(g => IcoSizes(g.icoBytes).Count >= 8);
        if (pick.icoBytes is null) pick = before[0];
        r.L($"group {pick.groupId} frames before:  {string.Join(",", IcoSizes(pick.icoBytes))}");

        string dir = Path.GetDirectoryName(target)!;
        string ownerBefore = Owner(target), dirOwnerBefore = Owner(dir);
        byte[] fileSdBefore = Sd(target), dirSdBefore = Sd(dir);
        var treeBefore = Tree(dir);
        r.L($"file permissions before:   {Sddl(fileSdBefore)}");
        r.L($"folder permissions before: {Sddl(dirSdBefore)}");
        r.L($"items under SystemResources: {treeBefore.Count}");
        string hashBefore = Sha256(target);
        uint linksBefore = LinkCount(target);
        r.L($"file owner before: {ownerBefore}, hard links: {linksBefore}, sha256: {hashBefore[..16]}...");
        r.L($"folder owner before: {dirOwnerBefore}");

        int checks = 0, passed = 0;
        void Check(bool ok, string what) { checks++; if (ok) passed++; r.L($"  {(ok ? "PASS" : "FAIL")}  {what}"); }

        // Apply twice: the first swap keeps the Windows original, the second replaces our own file.
        for (int round = 1; round <= 2; round++)
        {
            var icoBytes = round == 1 ? round1 : round2;
            r.L($"-- commit {round} ({(round == 1 ? "app import pipeline" : "authored as-is")})");
            var sw = Stopwatch.StartNew();
            var (ok, msg) = new MunWriterClient().Commit(
                new MunCommitRequest(target, new List<(int, byte[])> { (pick.groupId, icoBytes) }));
            r.L($"  success={ok} in {sw.ElapsedMilliseconds} ms; {msg.Replace("\n", " ").Trim()}");
            Check(ok, "commit succeeded");

            try
            {
                var after = reader.ReadAllGroups(target);
                var g = after.FirstOrDefault(x => x.groupId == pick.groupId);
                Check(g.icoBytes is not null, $"group {pick.groupId} present ({after.Count} groups load)");
                if (g.icoBytes is not null)
                {
                    var sizesAfter = IcoSizes(g.icoBytes);
                    r.L($"  group {pick.groupId} frames after: {string.Join(",", sizesAfter)}");
                    Check(sizesAfter.SequenceEqual(IcoSizes(pick.icoBytes)), "every original size kept (16/24/32 not dropped)");
                    Check(!g.icoBytes.SequenceEqual(pick.icoBytes), "icon content changed");
                }
            }
            catch (Exception ex) { Check(false, $"re-read after commit: {ex.Message}"); }

            var decoded = DecodeFromModule(target, pick.groupId);
            r.L($"  Windows decodes the new group at: {decoded}");
            Check(!decoded.Contains("FAIL"), "Windows can draw the new icon at 16/24/32/48/256");

            Check(LinkCount(target) == 1, "new live file is not linked into WinSxS");
            Check(Owner(target) == ownerBefore, $"file owner restored ({Owner(target)})");
            byte[] fileSdNow = Sd(target);
            Check(fileSdNow.SequenceEqual(fileSdBefore), "file owner, group and DACL byte-identical to the original");
            if (!fileSdNow.SequenceEqual(fileSdBefore)) r.L($"    file now:   {Sddl(fileSdNow)}");
            Check(Owner(dir) == dirOwnerBefore, $"folder owner restored ({Owner(dir)})");
            byte[] dirSdNow = Sd(dir);
            Check(dirSdNow.SequenceEqual(dirSdBefore), "folder owner, group and DACL byte-identical to the original");
            if (!dirSdNow.SequenceEqual(dirSdBefore)) r.L($"    folder now: {Sddl(dirSdNow)}");
            var treeNow = Tree(dir);
            var drifted = treeBefore.Where(kv => !kv.Key.StartsWith("imageres.dll.mun", StringComparison.OrdinalIgnoreCase)
                                              && (!treeNow.TryGetValue(kv.Key, out var v) || v != kv.Value)).Select(kv => kv.Key).ToList();
            Check(drifted.Count == 0, $"the other {treeBefore.Count - 1} items under SystemResources kept their permissions");
            foreach (var d in drifted.Take(5)) r.L($"    changed: {d}");

            var originals = Directory.GetFiles(dir, "imageres.dll.mun.rts-original-*");
            var olds      = Directory.GetFiles(dir, "imageres.dll.mun.rts-old-*");
            r.L($"  aside files: {originals.Length} original, {olds.Length} old");
            Check(originals.Length == 1, "exactly one preserved Windows original");
            if (originals.Length == 1)
            {
                Check(Sha256(originals[0]) == hashBefore, "preserved original is byte-identical to the file before");
                Check(LinkCount(originals[0]) == linksBefore, $"preserved original still linked into WinSxS ({LinkCount(originals[0])} links)");
                Check(Owner(originals[0]) == ownerBefore, "preserved original still owned by TrustedInstaller");
            }
            Check(olds.Length == (round == 1 ? 0 : 1), $"round {round}: {(round == 1 ? "no" : "one")} replaced RTS copy set aside");
        }
        r.L($"TEST 4: {passed}/{checks} checks passed");
    }

    // ── Icon set scan (selfcheck, read-only) ───────────────────────────────────

    public static void ScanIconSet(Report r, string setDir)
    {
        r.H($"ICON SET SCAN: {setDir}");
        if (!Directory.Exists(setDir)) { r.L("  folder not found"); return; }
        var files = Directory.GetFiles(setDir, "*.ico").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        int multi = 0, single = 0, decodeFail = 0, importOk = 0, importWarn = 0, importFail = 0;
        var problems = new List<string>();

        Shell.RunSta(() =>
        {
            foreach (var f in files)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(f); }
                catch (Exception ex) { problems.Add($"{Path.GetFileName(f)}: unreadable ({ex.Message})"); continue; }

                var sizes = IcoSizes(bytes);
                if (sizes.Count > 1) multi++; else single++;

                var bad = new[] { 16, 32, 48, 256 }.Where(s => !DecodeFromFile(f, s)).ToList();
                if (bad.Count > 0) { decodeFail++; problems.Add($"{Path.GetFileName(f)}: Windows can't draw it at {string.Join(",", bad)}"); }

                try
                {
                    var result = IcoFileBuilder.ValidateAndNormalize(f);
                    if (result.Warning is null) importOk++;
                    else { importWarn++; problems.Add($"{Path.GetFileName(f)}: import warning: {result.Warning}"); }
                }
                catch (Exception ex) { importFail++; problems.Add($"{Path.GetFileName(f)}: import rejected: {ex.Message}"); }
            }
        });

        r.L($"  {files.Count} icons: {multi} with authored small sizes, {single} single-frame");
        r.L($"  Windows decode at 16/32/48/256: {files.Count - decodeFail} ok, {decodeFail} with failures");
        r.L($"  Theme Studio import rules: {importOk} ok, {importWarn} warnings, {importFail} rejected");
        foreach (var p in problems.Take(60)) r.L($"    {p}");
        if (problems.Count > 60) r.L($"    ... {problems.Count - 60} more");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public static List<int> IcoSizes(byte[] ico)
    {
        var sizes = new List<int>();
        if (ico.Length < 6) return sizes;
        int count = BitConverter.ToUInt16(ico, 4);
        for (int i = 0; i < count && 6 + i * 16 < ico.Length; i++)
        {
            int w = ico[6 + i * 16];
            sizes.Add(w == 0 ? 256 : w);
        }
        return sizes;
    }

    private static int PngFrames(byte[] ico)
    {
        int count = ico.Length >= 6 ? BitConverter.ToUInt16(ico, 4) : 0, png = 0;
        for (int i = 0; i < count && 6 + i * 16 + 16 <= ico.Length; i++)
        {
            int off = BitConverter.ToInt32(ico, 6 + i * 16 + 12);
            if (off + 4 <= ico.Length && ico[off] == 0x89 && ico[off + 1] == 0x50 && ico[off + 2] == 0x4E && ico[off + 3] == 0x47) png++;
        }
        return png;
    }

    public static byte[] BuildTestIco(string dir)
    {
        string png = Path.Combine(dir, "probe-256.png");
        byte[]? result = null;
        Shell.RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x0A, 0x0A)), null, new Rect(0, 0, 256, 256));
                dc.DrawEllipse(Brushes.Goldenrod, null, new Point(128, 128), 92, 92);
            }
            var bmp = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(png)) encoder.Save(fs);

            // Theme Studio's own builder, so the test icon matches what the app produces.
            result = IcoFileBuilder.ValidateAndNormalize(png).IcoBytes;
        });
        File.WriteAllBytes(Path.Combine(dir, "probe.ico"), result!);
        return result!;
    }

    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImageById(IntPtr hinst, IntPtr id, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr hFile, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr h);

    private static bool DecodeFromFile(string path, int size)
    {
        IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (h == IntPtr.Zero) return false;
        DestroyIcon(h);
        return true;
    }

    private static string DecodeFromModule(string path, int groupId)
    {
        IntPtr mod = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (mod == IntPtr.Zero) return "FAIL (file did not load)";
        try
        {
            return string.Join(" ", new[] { 16, 24, 32, 48, 256 }.Select(s =>
            {
                IntPtr h = LoadImageById(mod, (IntPtr)groupId, IMAGE_ICON, s, s, 0);
                if (h == IntPtr.Zero) return $"{s}:FAIL";
                DestroyIcon(h);
                return $"{s}:ok";
            }));
        }
        finally { FreeLibrary(mod); }
    }

    private static string Icacls(string path)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"), $"\"{path}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return text.Trim();
    }

    private static string Normalize(string icaclsText, string path) =>
        string.Join("\n", icaclsText.Replace(path, "", StringComparison.OrdinalIgnoreCase)
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Successfully")));

    private static string Owner(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return sec.GetOwner(typeof(NTAccount))?.Value ?? "(none)";
        }
        catch (Exception ex) { return $"(unreadable: {ex.Message})"; }
    }

    internal static byte[] Sd(string path)
    {
        GetFileSecurityW(path, 7, null, 0, out uint need);
        var b = new byte[need];
        return need > 0 && GetFileSecurityW(path, 7, b, need, out _) ? b : Array.Empty<byte>();
    }

    private static string Sddl(byte[] sd) => sd.Length == 0 ? "(unreadable)" :
        new RawSecurityDescriptor(sd, 0).GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);

    /// <summary>Relative path to SDDL for every item under a folder.</summary>
    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Sddl(Sd(p)), StringComparer.OrdinalIgnoreCase);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileSecurityW(string path, uint info, byte[]? sd, uint length, out uint needed);

    internal static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    // Pack = 4: the three FILETIMEs sit at offset 4, with no 8-byte alignment padding.
    // Without it every later field shifts and the link count reads garbage (327680).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes; public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    internal static uint LinkCount(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(fs.SafeFileHandle, out var info) ? info.NumberOfLinks : 0;
    }
}

internal static class Shell
{
    public static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new InvalidOperationException(error.Message, error);
    }

    public static string ProbeOnSta(string parsingName)
    {
        string result = "";
        RunSta(() => result = Probe(parsingName));
        return result;
    }

    private static string Probe(string parsingName)
    {
        int hr = SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return $"ERR parse 0x{hr:X8}";
        try
        {
            var info = new SHFILEINFO();
            var ok = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICONLOCATION);
            if (ok == IntPtr.Zero) return "ERR SHGetFileInfo";
            return $"{info.szDisplayName},{info.iIcon}";
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    private const uint SHGFI_PIDL = 0x000000008;
    private const uint SHGFI_ICONLOCATION = 0x000001000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
