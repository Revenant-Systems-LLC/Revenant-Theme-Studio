using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Revenant_Theme_Studio.Services
{
    public class MunResourceReader
    {
        private const uint LOAD_LIBRARY_AS_DATAFILE       = 0x00000002;
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
        private const int  RT_ICON                        = 3;
        private const int  RT_GROUP_ICON                  = 14;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType,
            EnumResNameProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

        // In-PE group icon directory (GRPICONDIR)
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GRPICONDIR
        {
            public ushort Reserved;
            public ushort Type;    // 1 = icon
            public ushort Count;
        }

        // In-PE group icon entry (GRPICONDIRENTRY) — differs from ICONDIRENTRY:
        // last field is nId (WORD, RT_ICON child id) instead of dwImageOffset (DWORD).
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GRPICONDIRENTRY
        {
            public byte   Width;
            public byte   Height;
            public byte   ColorCount;
            public byte   Reserved;
            public ushort Planes;
            public ushort BitCount;
            public uint   BytesInRes;
            public ushort IconId;  // RT_ICON child resource ID
        }

        public IReadOnlyList<(int groupId, byte[] icoBytes)> ReadAllGroups(string munPath)
        {
            if (!File.Exists(munPath))
                throw new FileNotFoundException("File not found.", munPath);

            IntPtr hModule = LoadLibraryEx(munPath, IntPtr.Zero,
                LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (hModule == IntPtr.Zero)
                throw new IOException(
                    $"Cannot open {Path.GetFileName(munPath)} (Win32 error {Marshal.GetLastWin32Error()}).");

            try
            {
                var groupIds = new List<int>();
                EnumResNameProc cb = (h, type, name, param) =>
                {
                    long v = name.ToInt64();
                    if (v > 0 && v <= ushort.MaxValue)
                        groupIds.Add((int)v);
                    return true;
                };
                EnumResourceNames(hModule, (IntPtr)RT_GROUP_ICON, cb, IntPtr.Zero);
                GC.KeepAlive(cb);

                var results = new List<(int, byte[])>(groupIds.Count);
                foreach (int gid in groupIds)
                {
                    try
                    {
                        byte[]? ico = BuildIcoFromGroup(hModule, gid);
                        if (ico != null)
                            results.Add((gid, ico));
                    }
                    catch { /* skip malformed entries */ }
                }
                return results;
            }
            finally
            {
                FreeLibrary(hModule);
            }
        }

        private static byte[]? BuildIcoFromGroup(IntPtr hModule, int groupId)
        {
            IntPtr hRes = FindResource(hModule, (IntPtr)groupId, (IntPtr)RT_GROUP_ICON);
            if (hRes == IntPtr.Zero) return null;

            IntPtr hData  = LoadResource(hModule, hRes);
            IntPtr pData  = LockResource(hData);
            uint   grpSz  = SizeofResource(hModule, hRes);
            if (pData == IntPtr.Zero || grpSz == 0) return null;

            var grpDir = Marshal.PtrToStructure<GRPICONDIR>(pData);
            int count  = grpDir.Count;
            int entSz  = Marshal.SizeOf<GRPICONDIRENTRY>();

            var frames = new List<(GRPICONDIRENTRY entry, byte[] data)>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr entPtr = pData + Marshal.SizeOf<GRPICONDIR>() + i * entSz;
                var    entry  = Marshal.PtrToStructure<GRPICONDIRENTRY>(entPtr);

                IntPtr hFrameRes  = FindResource(hModule, (IntPtr)entry.IconId, (IntPtr)RT_ICON);
                if (hFrameRes == IntPtr.Zero) continue;
                IntPtr hFrameData = LoadResource(hModule, hFrameRes);
                IntPtr pFrame     = LockResource(hFrameData);
                uint   frameSz    = SizeofResource(hModule, hFrameRes);
                if (pFrame == IntPtr.Zero || frameSz == 0) continue;

                var frameBytes = new byte[frameSz];
                Marshal.Copy(pFrame, frameBytes, 0, (int)frameSz);
                frames.Add((entry, frameBytes));
            }

            if (frames.Count == 0) return null;

            var specs = frames.ConvertAll(f =>
                (f.entry.Width, f.entry.Height, f.entry.BitCount, f.data));
            return IcoFileBuilder.WriteIcoBytes(specs);
        }
    }
}
