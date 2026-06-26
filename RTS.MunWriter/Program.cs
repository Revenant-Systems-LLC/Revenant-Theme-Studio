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
try
{
    // Try takeown with error handling
    var takeownResult = RunCmdWithOutput("takeown", $"/f \"{job.Target}\"");
    if (!takeownResult.success)
    {
        Err($"takeown failed: {takeownResult.output}");
        // Try alternative method
        Err("Attempting alternative permission method...");
    }

    // Try icacls with error handling
    var icaclsResult = RunCmdWithOutput("icacls", $"\"{job.Target}\" /grant Administrators:F");
    if (!icaclsResult.success)
    {
        Err($"icacls grant failed: {icaclsResult.output}");
        // Try full control for current user
        var userName = Environment.UserName;
        var userResult = RunCmdWithOutput("icacls", $"\"{job.Target}\" /grant \"{userName}\":F");
        if (!userResult.success)
        {
            Err($"User permission grant failed: {userResult.output}");
            return 2;
        }
    }
}
catch (Exception ex)
{
    Err($"Permission setup failed: {ex.Message}");
    return 2;
}

// ── 3. Open resource update handle ──────────────────────────────────────────
IntPtr hUpdate;
bool useAlternativeMethod = false;

try
{
    hUpdate = BeginUpdateResource(job.Target, false);
    if (hUpdate == IntPtr.Zero)
    {
        int win32Error = Marshal.GetLastWin32Error();
        Err($"BeginUpdateResource failed: {win32Error} (0x{win32Error:X8})");

        // If WRP is blocking, try alternative method
        if (win32Error == 5 || win32Error == 32) // ACCESS_DENIED or SHARING_VIOLATION
        {
            Err("Attempting alternative WRP-safe method...");
            useAlternativeMethod = true;
        }
        else
        {
            RestoreAcl(job.Target);
            return 3;
        }
    }
}
catch (Exception ex)
{
    Err($"BeginUpdateResource exception: {ex.Message}");
    RestoreAcl(job.Target);
    return 3;
}

// Alternative method: copy to temp, modify, replace
if (useAlternativeMethod)
{
    try
    {
        var result = AlternativeUpdateMethod(job);
        if (!result.success)
        {
            Err($"Alternative method failed: {result.message}");
            RestoreAcl(job.Target);
            return 3;
        }
        Console.Error.WriteLine("OK");
        return 0;
    }
    catch (Exception ex)
    {
        Err($"Alternative method exception: {ex.Message}");
        RestoreAcl(job.Target);
        return 3;
    }
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

        try
        {
            if (!UpdateResource(hUpdate, (IntPtr)RT_GROUP_ICON, (IntPtr)rep.GroupId, 0, grpBytes, (uint)grpBytes.Length))
            {
                int win32Error = Marshal.GetLastWin32Error();
                Err($"UpdateResource group {rep.GroupId}: {win32Error} (0x{win32Error:X8})");
                EndUpdateResource(hUpdate, true);
                return 4;
            }

            for (int i = 0; i < frameCount; i++)
            {
                byte[] fd = frames[i].data;
                if (!UpdateResource(hUpdate, (IntPtr)RT_ICON, (IntPtr)childIds[i], 0, fd, (uint)fd.Length))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    Err($"UpdateResource icon {childIds[i]}: {win32Error} (0x{win32Error:X8})");
                    EndUpdateResource(hUpdate, true);
                    return 4;
                }
            }
        }
        catch (Exception ex)
        {
            Err($"UpdateResource exception: {ex.Message}");
            EndUpdateResource(hUpdate, true);
            return 4;
        }
    }

    try
    {
        if (!EndUpdateResource(hUpdate, false))
        {
            int win32Error = Marshal.GetLastWin32Error();
            Err($"EndUpdateResource: {win32Error} (0x{win32Error:X8})");
            return 4;
        }
        committed = true;
    }
    catch (Exception ex)
    {
        Err($"EndUpdateResource exception: {ex.Message}");
        return 4;
    }
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

static (bool success, string output) RunCmdWithOutput(string exe, string arguments)
{
    try
    {
        var p = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;

        // Read both pipes concurrently to prevent deadlock when either buffer fills.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        p.WaitForExit();
        string output = stdoutTask.Result + stderrTask.Result;

        return (p.ExitCode == 0, output);
    }
    catch (Exception ex)
    {
        return (false, $"Exception: {ex.Message}");
    }
}

static void RestoreAcl(string target)
{
    try
    {
        RunCmd("icacls", $"\"{target}\" /setowner \"NT SERVICE\\TrustedInstaller\"");
        RunCmd("icacls", $"\"{target}\" /grant:r \"NT SERVICE\\TrustedInstaller\":F");
        RunCmd("icacls", $"\"{target}\" /remove:g Administrators");
    }
    catch (Exception ex)
    {
        Err($"Warning: ACL restoration failed: {ex.Message}");
        // Non-fatal - the file will still work with incorrect permissions
    }
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

static (bool success, string message) AlternativeUpdateMethod(MunJob job)
{
    try
    {
        // Create temporary copy
        string tempPath = Path.Combine(Path.GetTempPath(), $"rts_mun_temp_{Guid.NewGuid():N}.mun");
        File.Copy(job.Target, tempPath, overwrite: true);

        // Read existing groups from temp copy
        Dictionary<int, List<ushort>> existingIds;
        try { existingIds = ReadGroupChildIds(tempPath); }
        catch (Exception ex) { return (false, $"Cannot read temp file: {ex.Message}"); }

        // Open resource update on temp file
        IntPtr hUpdate = BeginUpdateResource(tempPath, false);
        if (hUpdate == IntPtr.Zero)
        {
            int win32Error = Marshal.GetLastWin32Error();
            return (false, $"BeginUpdateResource on temp file failed: {win32Error}");
        }

        bool committed = false;
        try
        {
            foreach (var rep in job.Replacements)
            {
                if (!File.Exists(rep.IcoPath)) { return (false, $"ICO not found: {rep.IcoPath}"); }

                List<(byte w, byte h, ushort bc, byte[] data)> frames;
                try { frames = ParseIco(rep.IcoPath); }
                catch (Exception ex) { return (false, $"Bad ICO {rep.IcoPath}: {ex.Message}"); }

                if (frames.Count == 0) { return (false, $"No frames: {rep.IcoPath}"); }

                if (!existingIds.TryGetValue(rep.GroupId, out var childIds) || childIds.Count == 0)
                {
                    Console.Error.WriteLine($"Group {rep.GroupId} not found in target — skipping.");
                    continue;
                }

                int frameCount = Math.Min(frames.Count, childIds.Count);
                byte[] grpBytes = BuildGrpIconDir(frames, childIds, frameCount);

                if (!UpdateResource(hUpdate, (IntPtr)RT_GROUP_ICON, (IntPtr)rep.GroupId, 0, grpBytes, (uint)grpBytes.Length))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    return (false, $"UpdateResource group {rep.GroupId}: {win32Error}");
                }

                for (int i = 0; i < frameCount; i++)
                {
                    byte[] fd = frames[i].data;
                    if (!UpdateResource(hUpdate, (IntPtr)RT_ICON, (IntPtr)childIds[i], 0, fd, (uint)fd.Length))
                    {
                        int win32Error = Marshal.GetLastWin32Error();
                        return (false, $"UpdateResource icon {childIds[i]}: {win32Error}");
                    }
                }
            }

            if (!EndUpdateResource(hUpdate, false))
            {
                int win32Error = Marshal.GetLastWin32Error();
                return (false, $"EndUpdateResource: {win32Error}");
            }
            committed = true;
        }
        finally
        {
            if (!committed) EndUpdateResource(hUpdate, true);
        }

        // Replace original file with modified temp file
        try
        {
            // Take ownership of original file again
            RunCmd("takeown", $"/f \"{job.Target}\"");
            RunCmd("icacls", $"\"{job.Target}\" /grant Administrators:F");

            // Delete original and move temp file
            File.Delete(job.Target);
            File.Move(tempPath, job.Target);

            // Restore original permissions
            RestoreAcl(job.Target);

            return (true, "Success");
        }
        catch (Exception ex)
        {
            return (false, $"File replacement failed: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        return (false, $"Alternative method failed: {ex.Message}");
    }
}
