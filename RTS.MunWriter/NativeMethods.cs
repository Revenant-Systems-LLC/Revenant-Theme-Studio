using System.Runtime.InteropServices;

namespace RTS.MunWriter
{
    internal static class NativeMethods
    {
        internal const uint LOAD_LIBRARY_AS_DATAFILE       = 0x00000002;
        internal const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
        internal const int  RT_ICON                        = 3;
        internal const int  RT_GROUP_ICON                  = 14;

        internal delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType,
            EnumResNameProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LockResource(IntPtr hResData);

        // BeginUpdateResource/UpdateResource/EndUpdateResource were removed on purpose: they
        // refuse to change icon types in a MUI language-neutral file such as imageres.dll.mun.
        // See PeResourcePatcher.

        // ── Security descriptors ─────────────────────────────────────────────
        // GetFileSecurity/SetFileSecurity read and write one object's descriptor as-is. Unlike
        // SetNamedSecurityInfo (used by icacls and .NET's SetAccessControl) they do not walk
        // and re-write child objects.

        internal const uint OWNER_SECURITY_INFORMATION = 0x1;
        internal const uint GROUP_SECURITY_INFORMATION = 0x2;
        internal const uint DACL_SECURITY_INFORMATION  = 0x4;
        internal const int  FILE_ALL_ACCESS            = 0x1F01FF;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileSecurityW(string path, uint info, byte[]? sd, uint length, out uint needed);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileSecurityW(string path, uint info, byte[] sd);

        /// <summary>Owner, group and DACL of a file or folder, self-relative, exactly as stored.</summary>
        internal static byte[] GetSd(string path)
        {
            const uint info = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION;
            GetFileSecurityW(path, info, null, 0, out uint needed);
            if (needed == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"GetFileSecurity {path}");
            var sd = new byte[needed];
            if (!GetFileSecurityW(path, info, sd, needed, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"GetFileSecurity {path}");
            return sd;
        }

        internal static void SetSd(string path, uint info, byte[] sd)
        {
            if (!SetFileSecurityW(path, info, sd))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"SetFileSecurity {path}");
        }

        internal static byte[] Bin(System.Security.AccessControl.RawSecurityDescriptor sd)
        {
            var b = new byte[sd.BinaryLength];
            sd.GetBinaryForm(b, 0);
            return b;
        }

        /// <summary>
        /// A saved descriptor ready to write back. SetFileSecurity drops the "auto-inherited" DACL
        /// flag unless the descriptor also asks for auto-inheritance, and Windows\SystemResources
        /// carries that flag (D:PAI). Tested on scratch folders 2026-11-09: with the request bit the
        /// descriptor comes back byte-identical and no child object changes.
        /// </summary>
        internal static byte[] ForRestore(byte[] saved)
        {
            var sd = new System.Security.AccessControl.RawSecurityDescriptor(saved, 0);
            if ((sd.ControlFlags & System.Security.AccessControl.ControlFlags.DiscretionaryAclAutoInherited) == 0) return saved;
            var withRequest = new System.Security.AccessControl.RawSecurityDescriptor(
                sd.ControlFlags | System.Security.AccessControl.ControlFlags.DiscretionaryAclAutoInheritRequired,
                sd.Owner, sd.Group, sd.SystemAcl, sd.DiscretionaryAcl);
            return Bin(withRequest);
        }

        // ── Privileges ───────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state,
            uint length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// Turns on a privilege the elevated token already holds (SeTakeOwnership to own the folder
        /// briefly, SeRestore to give TrustedInstaller ownership back). Fails if it is not held.
        /// </summary>
        internal static void EnablePrivilege(string name)
        {
            const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValueW(null, name, out long luid))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                int err = Marshal.GetLastWin32Error();
                if (err == 1300) throw new System.ComponentModel.Win32Exception(err, $"{name} is not held by this token");
            }
            finally { CloseHandle(token); }
        }

        internal const uint IMAGE_ICON = 1;

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "LoadImageW")]
        internal static extern IntPtr LoadImageById(IntPtr hInst, IntPtr id, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        internal const uint MOVEFILE_WRITE_THROUGH = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        internal struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        /// <summary>
        /// Hard-link count. A Windows-serviced system file is linked into WinSxS (2+);
        /// a file this helper put in place has exactly 1.
        /// </summary>
        internal static uint LinkCount(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(fs.SafeFileHandle, out var info))
                throw new IOException($"GetFileInformationByHandle failed: {Marshal.GetLastWin32Error()}");
            return info.NumberOfLinks;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        /// <summary>
        /// The path an open handle really points to, after every junction, symlink and
        /// object-manager redirect has been resolved. Used so the elevated helper checks
        /// the file it actually opened, not the name it was given.
        /// </summary>
        internal static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            var buffer = new char[1024];
            uint len = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len >= buffer.Length)
                throw new IOException($"GetFinalPathNameByHandle failed: {Marshal.GetLastWin32Error()}");
            return new string(buffer, 0, (int)len);
        }
    }
}
