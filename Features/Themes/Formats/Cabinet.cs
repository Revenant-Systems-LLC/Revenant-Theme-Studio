using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Revenant_Theme_Studio.Features.Themes.Bundles;

namespace Revenant_Theme_Studio.Features.Themes.Formats
{
    /// <summary>
    /// Extracts a .themepack (a cabinet) in-process with Windows' FDI API
    /// (DOC: windows/win32/devnotes/cabinet-api-functions). RTS opens every file itself, so the
    /// cabinet never chooses a path on disk: names are checked against the bundle path rules and
    /// only theme file types are written. Sizes and counts use the bundle caps.
    /// </summary>
    public sealed class Cabinet
    {
        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
            { ".theme", ".jpg", ".jpeg", ".bmp", ".png", ".wav", ".cur", ".ani", ".ico" };

        private const string Token = "RTSCAB";   // the cabinet "path" FDI asks to open; mapped to our stream

        private readonly string _cabPath;
        private readonly string _outDir;
        private readonly Dictionary<IntPtr, Stream> _handles = new();
        private readonly Dictionary<IntPtr, string> _paths = new();
        private int _next = 1;
        private long _total;
        private Exception? _error;

        public List<string> Extracted { get; } = new();
        public List<string> Skipped { get; } = new();

        private Cabinet(string cabPath, string outDir) { _cabPath = cabPath; _outDir = outDir; }

        /// <summary>Extracts the allowed files of <paramref name="cabPath"/> into <paramref name="outDir"/> (created fresh).</summary>
        public static Cabinet Extract(string cabPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var c = new Cabinet(Path.GetFullPath(cabPath), outDir);
            c.Run();
            return c;
        }

        private void Run()
        {
            // Delegates stay referenced for the whole call so the GC cannot collect them mid-extract.
            PfnAlloc alloc = cb => Marshal.AllocHGlobal((int)cb);
            PfnFree free = p => Marshal.FreeHGlobal(p);
            PfnOpen open = Open;
            PfnRead read = Read;
            PfnWrite write = Write;
            PfnClose close = Close;
            PfnSeek seek = Seek;
            PfnNotify notify = Notify;

            IntPtr erf = Marshal.AllocHGlobal(16);
            try
            {
                IntPtr fdi = FDICreate(alloc, free, open, read, write, close, seek, -1, erf);
                if (fdi == IntPtr.Zero) throw new InvalidDataException("This .themepack could not be opened.");
                try
                {
                    bool ok = FDICopy(fdi, Token, "", 0, notify, IntPtr.Zero, IntPtr.Zero);
                    if (_error is not null) throw new InvalidDataException(_error.Message, _error);
                    if (!ok) throw new InvalidDataException($"This .themepack is damaged or not a cabinet (FDI error {Marshal.ReadInt32(erf)}).");
                }
                finally { FDIDestroy(fdi); }
            }
            finally
            {
                Marshal.FreeHGlobal(erf);
                foreach (var s in _handles.Values) s.Dispose();
                _handles.Clear();
                GC.KeepAlive(alloc); GC.KeepAlive(free); GC.KeepAlive(open); GC.KeepAlive(read);
                GC.KeepAlive(write); GC.KeepAlive(close); GC.KeepAlive(seek); GC.KeepAlive(notify);
            }
        }

        private IntPtr Add(Stream s) { var h = (IntPtr)_next++; _handles[h] = s; return h; }

        private IntPtr Open(string name, int flags, int mode)
        {
            // FDI only ever opens the cabinet through this callback; output files come from Notify.
            // It may open the cabinet more than once, so each open gets its own stream and position.
            if (name != Token) return (IntPtr)(-1);
            return Add(new FileStream(_cabPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        private uint Read(IntPtr h, IntPtr buffer, uint cb)
        {
            if (!_handles.TryGetValue(h, out var s)) return unchecked((uint)-1);
            var tmp = new byte[cb];
            int n = s.Read(tmp, 0, (int)cb);
            Marshal.Copy(tmp, 0, buffer, n);
            return (uint)n;
        }

        private uint Write(IntPtr h, IntPtr buffer, uint cb)
        {
            if (!_handles.TryGetValue(h, out var s)) return unchecked((uint)-1);
            _total += cb;
            if (s.Length + cb > AssetRules.MaxFileBytes || _total > AssetRules.MaxTotalBytes)
            {
                _error ??= new InvalidDataException("This .themepack unpacks larger than the theme size limit.");
                return unchecked((uint)-1);
            }
            var tmp = new byte[cb];
            Marshal.Copy(buffer, tmp, 0, (int)cb);
            s.Write(tmp, 0, (int)cb);
            return cb;
        }

        private int Close(IntPtr h)
        {
            if (_handles.Remove(h, out var s)) s.Dispose();
            return 0;
        }

        private int Seek(IntPtr h, int offset, int origin)
        {
            if (!_handles.TryGetValue(h, out var s)) return -1;
            return (int)s.Seek(offset, (SeekOrigin)origin);
        }

        private IntPtr Notify(int type, IntPtr info)
        {
            const int CopyFile = 2, CloseFileInfo = 3;
            try
            {
                if (type == CopyFile)
                {
                    var n = Marshal.PtrToStructure<FDINOTIFICATION>(info);
                    string raw = (n.attribs & 0x80) != 0 ? Marshal.PtrToStringUTF8(n.psz1) ?? "" : Marshal.PtrToStringAnsi(n.psz1) ?? "";
                    string path = raw.Replace('\\', '/');
                    if (Extracted.Count + Skipped.Count >= AssetRules.MaxEntries) throw new InvalidDataException("This .themepack holds too many files.");
                    if (!Allowed.Contains(Path.GetExtension(path))) { Skipped.Add(raw); return IntPtr.Zero; }
                    try { AssetRules.ValidatePath(path); }
                    catch (InvalidDataException) { Skipped.Add(raw); return IntPtr.Zero; }
                    if (n.cb > AssetRules.MaxFileBytes) throw new InvalidDataException($"\"{raw}\" is larger than 256 MiB.");

                    string dest = Path.GetFullPath(Path.Combine(_outDir, path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(Path.GetFullPath(_outDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        { Skipped.Add(raw); return IntPtr.Zero; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    var h = Add(new FileStream(dest, FileMode.CreateNew, FileAccess.Write));
                    _paths[h] = dest;
                    return h;
                }
                if (type == CloseFileInfo)
                {
                    var n = Marshal.PtrToStructure<FDINOTIFICATION>(info);
                    Close(n.hf);
                    if (_paths.Remove(n.hf, out var dest)) Extracted.Add(dest);
                    return (IntPtr)1;
                }
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                _error ??= ex;
                return (IntPtr)(-1);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FDINOTIFICATION
        {
            public int cb;
            public IntPtr psz1, psz2, psz3, pv, hf;
            public ushort date, time, attribs, setID, iCabinet, iFolder;
            public int fdie;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PfnAlloc(uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PfnFree(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr PfnOpen(string name, int flags, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint PfnRead(IntPtr h, IntPtr buffer, uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint PfnWrite(IntPtr h, IntPtr buffer, uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PfnClose(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PfnSeek(IntPtr h, int offset, int origin);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PfnNotify(int type, IntPtr info);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FDICreate(PfnAlloc alloc, PfnFree free, PfnOpen open, PfnRead read, PfnWrite write,
            PfnClose close, PfnSeek seek, int cpuType, IntPtr erf);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool FDICopy(IntPtr fdi, string cabinet, string cabPath, int flags, PfnNotify notify, IntPtr decrypt, IntPtr user);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FDIDestroy(IntPtr fdi);
    }
}
