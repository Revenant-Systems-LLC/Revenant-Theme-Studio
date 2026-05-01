using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RTS.MunWriter;
using static RTS.MunWriter.NativeMethods;

if (args.Length < 1) { Err("Usage: RTS.MunWriter <job.json>"); return 1; }
if (!File.Exists(args[0])) { Err($"Job file not found: {args[0]}"); return 1; }

MunJob job;
try
{
    job = JsonSerializer.Deserialize<MunJob>(File.ReadAllText(args[0]),
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? throw new InvalidOperationException("Null job.");
}
catch (Exception ex) { Err($"Bad job JSON: {ex.Message}"); return 1; }

if (!File.Exists(job.Target)) { Err($"Target not found: {job.Target}"); return 1; }

// ── 1. Read existing group child IDs before touching the file ────────────────
Dictionary<int, List<ushort>> existingIds;
try   { existingIds = ReadGroupChildIds(job.Target); }
catch (Exception ex) { Err($"Cannot read existing groups: {ex.Message}"); return 1; }

// ── 2. Take ownership + grant write access ───────────────────────────────────
if (!RunCmd("takeown", $"/f \"{job.Target}\"")) { Err("takeown failed."); return 2; }
if (!RunCmd("icacls",  $"\"{job.Target}\" /grant Administrators:F")) { Err("icacls grant failed."); return 2; }

// ── 3. Open resource update handle ──────────────────────────────────────────
IntPtr hUpdate = BeginUpdateResource(job.Target, false);
if (hUpdate == IntPtr.Zero)
{
    Err($"BeginUpdateResource failed: {Marshal.GetLastWin32Error()}");
    RestoreAcl(job.Target);
    return 3;
}

bool committed = false;
try
{
    foreach (var rep in job.Replacements)
    {
        if (!File.Exists(rep.IcoPath)) { Err($"ICO not found: {rep.IcoPath}"); EndUpdateResource(hUpdate, true); return 3; }

        List<(byte w, byte h, ushort bc, byte[] data)> frames;
        try   { frames = ParseIco(rep.IcoPath); }
        catch (Exception ex) { Err($"Bad ICO {rep.IcoPath}: {ex.Message}"); EndUpdateResource(hUpdate, true); return 3; }

        if (frames.Count == 0) { Err($"No frames: {rep.IcoPath}"); EndUpdateResource(hUpdate, true); return 3; }

        if (!existingIds.TryGetValue(rep.GroupId, out var childIds) || childIds.Count == 0)
        {
            Err($"Group {rep.GroupId} not found in target — skipping.");
            continue;
        }

        int   frameCount = Math.Min(frames.Count, childIds.Count);
        byte[] grpBytes  = BuildGrpIconDir(frames, childIds, frameCount);

        if (!UpdateResource(hUpdate, (IntPtr)RT_GROUP_ICON, (IntPtr)rep.GroupId, 0, grpBytes, (uint)grpBytes.Length))
        {
            Err($"UpdateResource group {rep.GroupId}: {Marshal.GetLastWin32Error()}");
            EndUpdateResource(hUpdate, true);
            return 4;
        }

        for (int i = 0; i < frameCount; i++)
        {
            byte[] fd = frames[i].data;
            if (!UpdateResource(hUpdate, (IntPtr)RT_ICON, (IntPtr)childIds[i], 0, fd, (uint)fd.Length))
            {
                Err($"UpdateResource icon {childIds[i]}: {Marshal.GetLastWin32Error()}");
                EndUpdateResource(hUpdate, true);
                return 4;
            }
        }
    }

    if (!EndUpdateResource(hUpdate, false)) { Err($"EndUpdateResource: {Marshal.GetLastWin32Error()}"); return 4; }
    committed = true;
}
finally
{
    if (!committed) EndUpdateResource(hUpdate, true);
    RestoreAcl(job.Target);
}

Console.Error.WriteLine("OK");
return 0;

// ── Local helpers ─────────────────────────────────────────────────────────────

static void Err(string msg) => Console.Error.WriteLine(msg);

static bool RunCmd(string exe, string arguments)
{
    var p = Process.Start(new ProcessStartInfo(exe, arguments)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    })!;
    p.WaitForExit();
    return p.ExitCode == 0;
}

static void RestoreAcl(string target)
{
    RunCmd("icacls", $"\"{target}\" /setowner \"NT SERVICE\\TrustedInstaller\"");
    RunCmd("icacls", $"\"{target}\" /grant:r \"NT SERVICE\\TrustedInstaller\":F");
    RunCmd("icacls", $"\"{target}\" /remove:g Administrators");
}

static Dictionary<int, List<ushort>> ReadGroupChildIds(string path)
{
    IntPtr hMod = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (hMod == IntPtr.Zero) throw new IOException($"LoadLibraryEx: {Marshal.GetLastWin32Error()}");

    var result = new Dictionary<int, List<ushort>>();
    try
    {
        var groupIds = new List<int>();
        EnumResNameProc cb = (h, type, name, param) =>
        {
            long v = name.ToInt64();
            if (v > 0 && v <= ushort.MaxValue) groupIds.Add((int)v);
            return true;
        };
        EnumResourceNames(hMod, (IntPtr)RT_GROUP_ICON, cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        foreach (int gid in groupIds)
        {
            IntPtr hRes  = FindResource(hMod, (IntPtr)gid, (IntPtr)RT_GROUP_ICON);
            if (hRes == IntPtr.Zero) continue;
            IntPtr pData = LockResource(LoadResource(hMod, hRes));
            if (pData == IntPtr.Zero) continue;

            int count = Marshal.ReadInt16(pData + 4);
            var ids   = new List<ushort>(count);
            for (int i = 0; i < count; i++)
                ids.Add((ushort)Marshal.ReadInt16(pData + 6 + i * 14 + 12));
            result[gid] = ids;
        }
    }
    finally { FreeLibrary(hMod); }
    return result;
}

static byte[] BuildGrpIconDir(
    List<(byte w, byte h, ushort bc, byte[] data)> frames,
    List<ushort> childIds, int count)
{
    using var ms = new MemoryStream();
    using var w  = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
    for (int i = 0; i < count; i++)
    {
        var (fw, fh, fbc, fd) = frames[i];
        w.Write(fw); w.Write(fh); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write(fbc); w.Write((uint)fd.Length); w.Write(childIds[i]);
    }
    return ms.ToArray();
}

static List<(byte w, byte h, ushort bc, byte[] data)> ParseIco(string path)
{
    using var fs = File.OpenRead(path);
    using var r  = new BinaryReader(fs);
    r.ReadUInt16();
    if (r.ReadUInt16() != 1) throw new InvalidDataException("Not an ICO file.");
    int count = r.ReadUInt16();

    var entries = new List<(byte w, byte h, ushort bc, uint sz, uint off)>(count);
    for (int i = 0; i < count; i++)
    {
        byte w = r.ReadByte(); byte h = r.ReadByte();
        r.ReadByte(); r.ReadByte(); r.ReadUInt16();
        ushort bc = r.ReadUInt16();
        uint sz = r.ReadUInt32(); uint off = r.ReadUInt32();
        entries.Add((w, h, bc, sz, off));
    }

    var frames = new List<(byte, byte, ushort, byte[])>(count);
    foreach (var (w, h, bc, sz, off) in entries)
    {
        fs.Seek(off, SeekOrigin.Begin);
        frames.Add((w, h, bc, r.ReadBytes((int)sz)));
    }
    return frames;
}
