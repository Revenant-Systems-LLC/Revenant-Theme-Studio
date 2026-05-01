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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName,
            ushort wLanguage, byte[] lpData, uint cbData);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
    }
}
