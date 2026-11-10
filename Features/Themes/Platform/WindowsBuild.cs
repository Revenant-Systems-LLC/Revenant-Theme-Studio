using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Features.Themes.Platform
{
    /// <summary>
    /// A Windows build and revision, e.g. 26300.9711. Windows 10 and 11 both report version 10.0,
    /// so gates compare build numbers only (spec F8).
    /// </summary>
    public readonly record struct BuildNumber(int Build, int Ubr) : IComparable<BuildNumber>
    {
        public int CompareTo(BuildNumber other) =>
            Build != other.Build ? Build.CompareTo(other.Build) : Ubr.CompareTo(other.Ubr);

        public static bool operator <(BuildNumber a, BuildNumber b) => a.CompareTo(b) < 0;
        public static bool operator >(BuildNumber a, BuildNumber b) => a.CompareTo(b) > 0;
        public static bool operator <=(BuildNumber a, BuildNumber b) => a.CompareTo(b) <= 0;
        public static bool operator >=(BuildNumber a, BuildNumber b) => a.CompareTo(b) >= 0;

        public override string ToString() => Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();

        /// <summary>Parses "26300" or "26300.9711". A gate without a revision matches every revision.</summary>
        public static bool TryParse(string? text, out BuildNumber value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Split('.');
            if (parts.Length is < 1 or > 2) return false;
            if (!int.TryParse(parts[0], out int build) || build <= 0) return false;
            int ubr = 0;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out ubr) || ubr < 0)) return false;
            value = new BuildNumber(build, ubr);
            return true;
        }
    }

    /// <summary>A min/max build gate, as carried by a bundle, a section or an item.</summary>
    public sealed class BuildGate
    {
        public string? MinBuild { get; init; }
        public string? MaxBuild { get; init; }

        /// <summary>
        /// True if <paramref name="build"/> falls inside the gate. A maximum written without a revision
        /// ("26300") covers every revision of that build. Unparseable bounds fail closed.
        /// </summary>
        public bool Allows(BuildNumber build)
        {
            if (MinBuild is not null)
            {
                if (!BuildNumber.TryParse(MinBuild, out var min)) return false;
                if (build < min) return false;
            }
            if (MaxBuild is not null)
            {
                if (!BuildNumber.TryParse(MaxBuild, out var max)) return false;
                var ceiling = MaxBuild.Contains('.') ? max : new BuildNumber(max.Build, int.MaxValue);
                if (build > ceiling) return false;
            }
            return true;
        }
    }

    public static class WindowsBuild
    {
        /// <summary>The running Windows build, or null if it cannot be read.</summary>
        public static BuildNumber? Current { get; } = Read();

        private static BuildNumber? Read()
        {
            try
            {
                // RtlGetVersion returns the real running version, unlike GetVersionEx, which
                // depends on the app manifest.
                var info = new OSVERSIONINFOEXW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<OSVERSIONINFOEXW>() };
                if (RtlGetVersion(ref info) != 0) return null;

                int ubr = 0;
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("UBR") is int u) ubr = u;
                return new BuildNumber((int)info.dwBuildNumber, ubr);
            }
            catch { return null; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
            public byte wProductType, wReserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);
    }
}
