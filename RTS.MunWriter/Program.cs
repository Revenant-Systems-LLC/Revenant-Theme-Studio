// RTS.MunWriter: the elevated helper that replaces icon groups in imageres.dll.mun.
//
// How it writes (rewritten 2026-11-09). The old code edited the live file in place.
// That fails while Windows has the file mapped, which is always: Explorer loads it.
// And had it worked, it would have changed the WinSxS component-store copy too, since
// the live file is a hard link to it, and SFC could no longer repair it. Now:
//
//   1. Patch a private copy in an admin-only folder under %SystemRoot%\Temp. The patch edits the
//      resource table directly (PeResourcePatcher), because UpdateResource refuses to change
//      icon types in a MUI language-neutral file like imageres.dll.mun (error 50).
//   2. Verify the copy: Windows loads it, every replaced group is there with the same slots,
//      Windows can draw each one at 16, 32 and 256, and the PE checksum is right.
//   3. Briefly take ownership of Windows\SystemResources (the folder only, never its
//      contents) and grant Administrators full control on it, through SetFileSecurity so the
//      change never ripples into the subfolders that inherit from it.
//   4. Rename the live file aside, which Windows allows while it is in use, then move the
//      patched copy in. The first time for a given file version, the live file is Windows'
//      own: it is kept as ".rts-original-<version>-*", untouched, with its TrustedInstaller ACL
//      and its WinSxS link. Once an original for that version exists, the live file is one this
//      helper put there; it becomes ".rts-old-*" and is deleted once nothing uses it. A patch
//      never changes the version resource, so the version identifies the file reliably.
//   5. Give the new file the original's exact owner, group and DACL, put the folder's back
//      exactly as they were, and check both byte for byte against the saved descriptors.
//
// Restore mode ("mode": "restore") undoes it: the live file must be one this helper put in place
// (not linked into WinSxS), and the preserved ".rts-original-<version>-*" of the same version is
// renamed back with its own owner, DACL and WinSxS link. The displaced copy becomes ".rts-old-*".
//
// Running processes keep the old file until they restart: sign out, or restart Explorer.
//
// Exit codes: 0 ok, 1 bad usage or job file, 5 job refused.
// Failures carry the stage and the Win32 error: stage * 100000 + error, where the stages are
//   1 working copy, 2 resource update, 3 verify, 4 folder permissions,
//   5 rename aside, 6 move in (original put back), 7 ACL restore,
//   8 rollback FAILED (the target may be missing), 9 restore not possible.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using RTS.MunWriter;
using static RTS.MunWriter.NativeMethods;

if (args.Length < 1) { Err("Usage: RTS.MunWriter <job.json>"); return 1; }
if (!File.Exists(args[0])) { Err($"Job file not found: {args[0]}"); return 1; }

// The job file sits in a user-writable temp folder, so this elevated process must not
// trust it. It is read through one handle, and the folder that handle really resolves
// to is the only place icons may come from.
MunJob job;
string jobDir;
try
{
    using var jf = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
    jobDir = Path.GetDirectoryName(FinalPath(jf.SafeFileHandle))
             ?? throw new InvalidOperationException("Job folder could not be resolved.");
    job = JsonSerializer.Deserialize<MunJob>(jf,
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? throw new InvalidOperationException("Null job.");
}
catch (Exception ex) { Err($"Bad job file: {ex.Message}"); return 1; }

// ── Restore mode: put the preserved Windows original back ───────────────────
if (string.Equals(job.Mode, "restore", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        ValidateTarget(job);
        if (job.Replacements is { Count: > 0 }) throw new InvalidDataException("a restore job takes no icons");
    }
    catch (Exception ex) { Err($"Refused job: {ex.Message}"); return 5; }
    return Restore(job.Target);
}
if (!string.Equals(job.Mode, "replace", StringComparison.OrdinalIgnoreCase)) { Err("Refused job: unknown mode."); return 5; }

// ── 0. Validate the job and load every icon into memory ──────────────────────
Dictionary<int, List<(byte w, byte h, ushort bc, byte[] data)>> icons;
try   { icons = ValidateAndLoad(job, jobDir); }
catch (Exception ex) { Err($"Refused job: {ex.Message}"); return 5; }

string target   = job.Target;
string dir      = Path.GetDirectoryName(target)!;
string fileName = Path.GetFileName(target);
string work     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                               "Temp", $"rts-mun-{Guid.NewGuid():N}");
string patched  = Path.Combine(work, fileName);

try
{
    // ── 1. Private working copy ───────────────────────────────────────────────
    try
    {
        CreatePrivateDirectory(work);
        File.Copy(target, patched, overwrite: false);
    }
    catch (Exception ex) { Err($"Working copy failed: {ex.Message}"); return Code(1, Win32Of(ex)); }

    // ── 2. Patch the copy ─────────────────────────────────────────────────────
    int patchResult = PatchCopy(patched, job, icons);
    if (patchResult != 0) return patchResult;

    // ── 3. Verify the copy ────────────────────────────────────────────────────
    try
    {
        int verifyResult = VerifyCopy(target, patched, job);
        if (verifyResult != 0) return verifyResult;
    }
    catch (Exception ex) { Err($"Verify: patched copy does not load: {ex.Message}"); return Code(3, Win32Of(ex)); }

    // ── 4-5. Swap it in ───────────────────────────────────────────────────────
    return Swap(target, dir, fileName, patched, restoring: false);
}
finally
{
    try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
}

// ── Swap ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Swaps <paramref name="incoming"/> in for the live file. Replace: the patched copy, which then gets
/// the live file's exact descriptor. Restore: the preserved original, which keeps its own.
/// </summary>
static int Swap(string target, string dir, string fileName, string incoming, bool restoring)
{
    // Owner, group and DACL of the folder and of the files, exactly as Windows stores them.
    byte[] dirSd, fileSd;
    string aside;
    try
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        dirSd  = GetSd(dir);
        fileSd = restoring ? GetSd(incoming) : GetSd(target);
        aside  = restoring ? Path.Combine(dir, $"{fileName}.rts-old-{DateTime.UtcNow:yyyyMMddHHmmss}")
                           : AsideName(target, dir, fileName);
    }
    catch (Exception ex) { Err($"Preparing the swap failed: {ex.Message}"); return Code(4, Win32Of(ex)); }

    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    int result = 0;
    bool folderOpened = false;
    try
    {
        // Open the folder: Administrators become owner, then get full control as the first ACE.
        // SetFileSecurity changes this folder only. icacls and .NET's SetAccessControl go through
        // SetNamedSecurityInfo, which also re-writes the permissions of every subfolder that
        // inherits from Windows\SystemResources (42 of them on Windows 11 26300).
        folderOpened = true;
        var take = new RawSecurityDescriptor(dirSd, 0) { Owner = admins };
        SetSd(dir, OWNER_SECURITY_INFORMATION, Bin(take));
        var open = new RawSecurityDescriptor(dirSd, 0);
        open.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            FILE_ALL_ACCESS, admins, false, null));
        SetSd(dir, DACL_SECURITY_INFORMATION, Bin(open));

        if (!MoveFileEx(target, aside, MOVEFILE_WRITE_THROUGH))
        {
            result = Code(5, Marshal.GetLastWin32Error());
        }
        else if (!MoveFileEx(incoming, target, MOVEFILE_WRITE_THROUGH))
        {
            int err = Marshal.GetLastWin32Error();
            if (!MoveFileEx(aside, target, MOVEFILE_WRITE_THROUGH))
            {
                int rbErr = Marshal.GetLastWin32Error();
                Err($"CRITICAL: could not put the original back. It is at {aside}");
                result = Code(8, rbErr);
            }
            else result = Code(6, err);
        }
        else
        {
            // Replace: the new file gets the live file's exact owner, group and DACL.
            // Restore: the original moved back by rename and kept its own; only check it.
            try
            {
                if (!restoring)
                    SetSd(target, OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                          ForRestore(fileSd));
                if (!GetSd(target).AsSpan().SequenceEqual(fileSd))
                    { Err("The file's permissions differ from what they should be."); result = Code(7, 0); }
            }
            catch (Exception ex) { Err($"Setting the new file's permissions failed: {ex.Message}"); result = Code(7, Win32Of(ex)); }

            DeleteStaleAsideFiles(dir, fileName, aside);
        }
    }
    catch (Exception ex)
    {
        Err($"Opening the folder failed: {ex.Message}");
        result = Code(4, Win32Of(ex));
    }
    finally
    {
        if (folderOpened)
        {
            // Put the folder's exact owner, group and DACL back, and check it byte for byte.
            try
            {
                SetSd(dir, OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                      ForRestore(dirSd));
                if (!GetSd(dir).AsSpan().SequenceEqual(dirSd))
                    throw new IOException("folder permissions differ after restore");
            }
            catch (Exception ex)
            {
                Err($"CRITICAL: {dir} permissions not restored: {ex.Message}");
                if (result == 0) result = Code(7, Win32Of(ex));
            }
        }
    }
    return result;
}

static string FileVersionOf(string path)
{
    string raw = FileVersionInfo.GetVersionInfo(path).FileVersion?.Split(' ')[0] ?? "";
    string ver = new string(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
    return ver.Length == 0 ? "unknown" : ver;
}

/// <summary>Puts the preserved Windows original of the live file's version back.</summary>
static int Restore(string target)
{
    string dir = Path.GetDirectoryName(target)!, fileName = Path.GetFileName(target);
    try
    {
        // A live file linked into WinSxS is Windows' own (never patched, or replaced by an update):
        // there is nothing of ours to undo, and putting an older original over it would be wrong.
        if (LinkCount(target) != 1) { Err("The live file is Windows' own; there is nothing to restore."); return Code(9, 0); }
        string ver = FileVersionOf(target);
        var original = Directory.GetFiles(dir, $"{fileName}.rts-original-{ver}-*")
                                .OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        if (original is null) { Err($"No preserved original for version {ver}."); return Code(9, 2); }
        if ((File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0) { Err("Preserved original is a reparse point."); return Code(9, 0); }
        return Swap(target, dir, fileName, original, restoring: true);
    }
    catch (Exception ex) { Err($"Restore failed: {ex.Message}"); return Code(9, Win32Of(ex)); }
}

static string AsideName(string target, string dir, string fileName)
{
    string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    string ver   = FileVersionOf(target);

    // No preserved original for this version yet: the live file is Windows' own. Keep it.
    // Otherwise the live file is a copy this helper put in place earlier.
    bool haveOriginal = Directory.GetFiles(dir, $"{fileName}.rts-original-{ver}-*").Length > 0;
    return haveOriginal
        ? Path.Combine(dir, $"{fileName}.rts-old-{stamp}")
        : Path.Combine(dir, $"{fileName}.rts-original-{ver}-{stamp}");
}

/// <summary>
/// Deletes earlier ".rts-old-*" files: copies this helper put in place before. Files still
/// mapped by a running process can't be deleted and are left for the next run. Originals
/// (".rts-original-*") are never deleted.
/// </summary>
static void DeleteStaleAsideFiles(string dir, string fileName, string keep)
{
    foreach (var f in Directory.GetFiles(dir, $"{fileName}.rts-old-*"))
    {
        if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            if (LinkCount(f) != 1) continue;   // never delete anything linked into WinSxS
            File.Delete(f);
        }
        catch { /* still in use; next run */ }
    }
}

static void CreatePrivateDirectory(string path)
{
    // Admin-only from the moment it exists, so nothing unelevated can touch the working copy.
    var sec = new DirectorySecurity();
    sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
    new DirectoryInfo(path).Create(sec);
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Work folder is a reparse point.");
}

// ── Patch ────────────────────────────────────────────────────────────────────

static int PatchCopy(string path, MunJob job,
    Dictionary<int, List<(byte w, byte h, ushort bc, byte[] data)>> icons)
{
    PeResourcePatcher pe;
    try { pe = new PeResourcePatcher(File.ReadAllBytes(path)); }
    catch (Exception ex) { Err($"Cannot parse the working copy: {ex.Message}"); return Code(2, Win32Of(ex)); }

    try
    {
        int applied = 0;
        foreach (var rep in job.Replacements)
        {
            if (pe.ReplaceIconGroup(rep.GroupId, icons[rep.GroupId])) applied++;
            else Err($"Group {rep.GroupId} not found in target, skipping.");
        }
        if (applied == 0) { Err("No listed group exists in the target."); return Code(2, 0); }

        File.WriteAllBytes(path, pe.Save());
        return 0;
    }
    catch (Exception ex) { Err($"Patching the working copy failed: {ex.Message}"); return Code(2, Win32Of(ex)); }
}

/// <summary>
/// Checks the patched copy before it can go anywhere near Windows\SystemResources:
/// the checksum is right, Windows loads it, every replaced group keeps its slot count,
/// and Windows can draw each replaced group at 16, 32 and 256.
/// </summary>
static int VerifyCopy(string original, string patched, MunJob job)
{
    var check = new PeResourcePatcher(File.ReadAllBytes(patched));
    if (check.HeaderChecksum != check.ComputeChecksum())
        { Err("Verify: PE checksum mismatch in patched copy."); return Code(3, 0); }

    var before = ReadGroups(original);
    var after  = ReadGroups(patched);
    foreach (var rep in job.Replacements)
    {
        if (!before.TryGetValue(rep.GroupId, out var slots)) continue;   // skipped, not in target
        if (!after.TryGetValue(rep.GroupId, out var now) || now.Count != slots.Count)
            { Err($"Verify: group {rep.GroupId} lost slots ({slots.Count} before)."); return Code(3, 0); }
    }

    IntPtr mod = LoadLibraryEx(patched, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (mod == IntPtr.Zero) { int e = Marshal.GetLastWin32Error(); Err("Verify: patched copy does not load."); return Code(3, e); }
    try
    {
        foreach (var rep in job.Replacements)
        {
            if (!before.ContainsKey(rep.GroupId)) continue;
            foreach (int size in new[] { 16, 32, 256 })
            {
                IntPtr icon = LoadImageById(mod, (IntPtr)rep.GroupId, IMAGE_ICON, size, size, 0);
                if (icon == IntPtr.Zero)
                {
                    int e = Marshal.GetLastWin32Error();
                    Err($"Verify: Windows cannot draw group {rep.GroupId} at {size}px.");
                    return Code(3, e);
                }
                DestroyIcon(icon);
            }
        }
    }
    finally { FreeLibrary(mod); }
    return 0;
}

static int FrameSize(byte w) => w == 0 ? 256 : w;

static Dictionary<int, List<(ushort id, int size)>> ReadGroups(string path)
{
    IntPtr hMod = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (hMod == IntPtr.Zero) throw new IOException($"LoadLibraryEx: {Marshal.GetLastWin32Error()}");

    var result = new Dictionary<int, List<(ushort id, int size)>>();
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
            IntPtr hRes = FindResource(hMod, (IntPtr)gid, (IntPtr)RT_GROUP_ICON);
            if (hRes == IntPtr.Zero) continue;
            IntPtr pData = LockResource(LoadResource(hMod, hRes));
            if (pData == IntPtr.Zero) continue;

            // GRPICONDIR: 6-byte header, then 14-byte entries (width at +0, child id at +12).
            int count = Marshal.ReadInt16(pData + 4);
            var children = new List<(ushort id, int size)>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr entry = pData + 6 + i * 14;
                children.Add(((ushort)Marshal.ReadInt16(entry + 12), FrameSize(Marshal.ReadByte(entry))));
            }
            result[gid] = children;
        }
    }
    finally { FreeLibrary(hMod); }
    return result;
}

// ── Job validation ───────────────────────────────────────────────────────────

static void ValidateTarget(MunJob job)
{
    // Target: only imageres.dll.mun in the real Windows\SystemResources folder.
    string allowed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources", "imageres.dll.mun");
    string target = Path.GetFullPath(job.Target ?? "");
    if (!string.Equals(target, allowed, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"target must be {allowed}");
    if (!File.Exists(target))
        throw new FileNotFoundException("Target not found.", target);
    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("target is a reparse point");
    job.Target = allowed;
}

static Dictionary<int, List<(byte w, byte h, ushort bc, byte[] data)>> ValidateAndLoad(MunJob job, string jobDir)
{
    const int MaxReplacements = 512;
    const long MaxIcoBytes    = 4 * 1024 * 1024;

    ValidateTarget(job);

    if (job.Replacements is null || job.Replacements.Count == 0 || job.Replacements.Count > MaxReplacements)
        throw new InvalidDataException("job must list 1 to 512 replacements");

    var result = new Dictionary<int, List<(byte w, byte h, ushort bc, byte[] data)>>();
    foreach (var rep in job.Replacements)
    {
        if (rep.GroupId < 1 || rep.GroupId > ushort.MaxValue)
            throw new InvalidDataException($"bad group id {rep.GroupId}");
        if (result.ContainsKey(rep.GroupId))
            throw new InvalidDataException($"group {rep.GroupId} listed twice");
        if (!string.Equals(Path.GetExtension(rep.IcoPath), ".ico", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"not an .ico file: {rep.IcoPath}");

        // Open once, check where the handle really points, then read from that same handle.
        using var fs = new FileStream(rep.IcoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string real = FinalPath(fs.SafeFileHandle);
        if (!string.Equals(Path.GetDirectoryName(real), jobDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"icon is outside the job folder: {rep.IcoPath}");
        if (fs.Length == 0 || fs.Length > MaxIcoBytes)
            throw new InvalidDataException($"icon size out of range: {rep.IcoPath}");

        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        result[rep.GroupId] = ParseIco(bytes);
    }
    return result;
}

static List<(byte w, byte h, ushort bc, byte[] data)> ParseIco(byte[] buf)
{
    // Every offset and size is checked against the buffer: this runs elevated on
    // bytes that came from a user-writable folder.
    if (buf.Length < 6) throw new InvalidDataException("ICO too small.");
    using var ms = new MemoryStream(buf, writable: false);
    using var r  = new BinaryReader(ms);
    r.ReadUInt16();
    if (r.ReadUInt16() != 1) throw new InvalidDataException("Not an ICO file.");
    int count = r.ReadUInt16();
    if (count < 1 || count > 64) throw new InvalidDataException($"Bad frame count {count}.");
    int dirEnd = 6 + count * 16;
    if (dirEnd > buf.Length) throw new InvalidDataException("ICO directory truncated.");

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
        if (sz == 0 || off < dirEnd || (long)off + sz > buf.Length)
            throw new InvalidDataException("ICO frame out of bounds.");
        frames.Add((w, h, bc, buf.AsSpan((int)off, (int)sz).ToArray()));
    }
    return frames;
}

// ── Small helpers ────────────────────────────────────────────────────────────

static void Err(string msg) => Console.Error.WriteLine(msg);

static int Code(int stage, int win32) => stage * 100000 + Math.Clamp(win32, 0, 99999);

static int Win32Of(Exception ex) =>
    ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode :
    (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;
