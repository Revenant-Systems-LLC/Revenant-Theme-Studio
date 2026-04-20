using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Per-user shell icon override service. Two registry paths are in play:
    ///
    ///   1. <see cref="ShellRegistryScope.LibraryShort"/> — the 7 library folders
    ///      (3D Objects, Desktop, Documents, Downloads, Music, Pictures, Videos)
    ///      live at <c>HKCU\CLSID\{GUID}\DefaultIcon</c>. The "short" HKCU path
    ///      is the one Explorer actually reads for these targets. Writing to the
    ///      HKCR-redirected long path silently fails (this was the old bug).
    ///
    ///   2. <see cref="ShellRegistryScope.HkcrOverride"/> — every other shell
    ///      namespace target (This PC, Recycle Bin, Libraries, Control Panel,
    ///      Network, User Profile, Quick Access, Home) is rethemed by writing
    ///      to <c>HKCU\Software\Classes\CLSID\{GUID}\DefaultIcon</c>, which
    ///      Windows merges into HKCR for the current user.
    ///
    /// Two-state targets (Recycle Bin) need separate Empty / Full icon values
    /// in addition to the default; <see cref="ApplyRecycleBinOverride"/> writes
    /// both atomically. Some targets also have a paired secondary CLSID (e.g.
    /// Control Panel's category-view GUID) that we mirror so the icon stays
    /// consistent regardless of which view the user is in.
    ///
    /// Every mutation broadcasts <c>SHCNE_ASSOCCHANGED</c> via SHChangeNotify
    /// so Explorer picks up the change without requiring a logoff.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ShellIconService
    {
        public enum ShellRegistryScope
        {
            /// <summary>HKCU\CLSID\{GUID}\DefaultIcon — the 7 library folders only.</summary>
            LibraryShort,

            /// <summary>HKCU\Software\Classes\CLSID\{GUID}\DefaultIcon — everything else.</summary>
            HkcrOverride
        }

        public sealed record ShellTargetSpec(
            string Clsid,
            string DefaultIcon,
            ShellRegistryScope Scope,
            bool TwoState = false,
            string? SecondaryClsid = null,
            string? Notes = null);

        public IReadOnlyDictionary<string, ShellTargetSpec> Targets { get; } = new Dictionary<string, ShellTargetSpec>
        {
            // ── Library folders: HKCU\CLSID short path (verified per Dave's registry doc) ──
            ["3D Objects"]   = new("{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}", @"%SystemRoot%\System32\imageres.dll,-198", ShellRegistryScope.LibraryShort),
            ["Desktop"]      = new("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"%SystemRoot%\System32\imageres.dll,-183", ShellRegistryScope.LibraryShort),
            ["Documents"]    = new("{D3162B92-9365-467A-956B-92703ACA08AF}", @"%SystemRoot%\System32\imageres.dll,-112", ShellRegistryScope.LibraryShort),
            ["Downloads"]    = new("{088E3905-0323-4B02-9826-5D99428E115F}", @"%SystemRoot%\System32\imageres.dll,-184", ShellRegistryScope.LibraryShort),
            ["Music"]        = new("{3DFDF296-DBEC-4FB4-81D1-6A3438BCF4DE}", @"%SystemRoot%\System32\imageres.dll,-108", ShellRegistryScope.LibraryShort),
            ["Pictures"]     = new("{24AD3AD4-A569-4530-98E1-AB02F9417AA8}", @"%SystemRoot%\System32\imageres.dll,-113", ShellRegistryScope.LibraryShort),
            ["Videos"]       = new("{F86FA3AB-70D2-4FC7-9C99-FCBF05467F3A}", @"%SystemRoot%\System32\imageres.dll,-189", ShellRegistryScope.LibraryShort),

            // ── System namespace targets: HKCR-redirected per-user override ──
            ["This PC"]      = new("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", @"%SystemRoot%\System32\imageres.dll,-109", ShellRegistryScope.HkcrOverride),
            ["Recycle Bin"]  = new("{645FF040-5081-101B-9F08-00AA002F954E}", @"%SystemRoot%\System32\imageres.dll,-55",  ShellRegistryScope.HkcrOverride, TwoState: true,
                                   Notes: "Two-state target; uses Empty/Full named values in addition to default."),
            ["Libraries"]    = new("{031E4825-7B94-4DC3-B131-E946B44C8DD5}", @"%SystemRoot%\System32\imageres.dll,-1003", ShellRegistryScope.HkcrOverride),
            ["Control Panel"]= new("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", @"%SystemRoot%\System32\imageres.dll,-27",  ShellRegistryScope.HkcrOverride,
                                   SecondaryClsid: "{26EE0668-A00A-44D7-9371-BEB064C98683}",
                                   Notes: "Secondary CLSID is the category-view Control Panel; mirror so both views match."),
            ["Network"]      = new("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", @"%SystemRoot%\System32\imageres.dll,-25",  ShellRegistryScope.HkcrOverride),
            ["User Profile"] = new("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", @"%SystemRoot%\System32\imageres.dll,-123", ShellRegistryScope.HkcrOverride),
            ["Quick Access"] = new("{679F85CB-0220-4080-B29B-5540CC05AAB6}", @"%SystemRoot%\System32\imageres.dll,-1024", ShellRegistryScope.HkcrOverride,
                                   Notes: "Windows 10."),
            ["Home"]         = new("{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", @"%SystemRoot%\System32\imageres.dll,-1024", ShellRegistryScope.HkcrOverride,
                                   Notes: "Windows 11 replacement for Quick Access."),
        };

        public string? GetCurrentOverride(string targetName)
        {
            var spec = ResolveSpec(targetName);
            using var key = Registry.CurrentUser.OpenSubKey(BuildPath(spec));
            return key?.GetValue(null) as string;
        }

        public void ApplyOverride(string targetName, string iconReference)
        {
            var spec = ResolveSpec(targetName);
            var clean = StripWrappingQuotes(iconReference);

            WriteDefaultIcon(BuildPath(spec), clean);

            // Two-state targets get matching Empty/Full siblings so both states
            // pick up the new icon. Callers that want different icons for each
            // state should use ApplyRecycleBinOverride instead.
            if (spec.TwoState)
            {
                WriteTwoStateValues(BuildPath(spec), clean, clean);
            }

            // Mirror to the secondary CLSID (e.g. Control Panel category view)
            // so the icon stays consistent across views.
            if (!string.IsNullOrEmpty(spec.SecondaryClsid))
            {
                WriteDefaultIcon(BuildPath(spec.Scope, spec.SecondaryClsid!), clean);
            }

            BroadcastShellRefresh();
        }

        /// <summary>
        /// Writes distinct Empty and Full icons for the Recycle Bin (or any
        /// other two-state target). Use this when the user wants visually
        /// different states (e.g. crumpled vs flat). For a single icon used
        /// in both states, the regular <see cref="ApplyOverride"/> works.
        /// </summary>
        public void ApplyRecycleBinOverride(string emptyIconRef, string fullIconRef)
        {
            var spec = ResolveSpec("Recycle Bin");
            var path = BuildPath(spec);

            // The DefaultIcon (unnamed) value is what Explorer falls back to;
            // Empty/Full are what it uses based on contents.
            WriteDefaultIcon(path, StripWrappingQuotes(emptyIconRef));
            WriteTwoStateValues(path, StripWrappingQuotes(emptyIconRef), StripWrappingQuotes(fullIconRef));

            BroadcastShellRefresh();
        }

        public void RestoreDefault(string targetName)
        {
            var spec = ResolveSpec(targetName);

            // DeleteSubKeyTree on DefaultIcon nukes the subkey itself, which is
            // what we want — Explorer then falls back to the system default.
            try { Registry.CurrentUser.DeleteSubKeyTree(BuildPath(spec), throwOnMissingSubKey: false); } catch { }

            if (!string.IsNullOrEmpty(spec.SecondaryClsid))
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(BuildPath(spec.Scope, spec.SecondaryClsid!), throwOnMissingSubKey: false); } catch { }
            }

            BroadcastShellRefresh();
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private ShellTargetSpec ResolveSpec(string targetName)
        {
            if (!Targets.TryGetValue(targetName, out var spec))
                throw new ArgumentException($"Unknown shell target: {targetName}");
            return spec;
        }

        private static string BuildPath(ShellTargetSpec spec) => BuildPath(spec.Scope, spec.Clsid);

        private static string BuildPath(ShellRegistryScope scope, string clsid) => scope switch
        {
            ShellRegistryScope.LibraryShort => $@"CLSID\{clsid}\DefaultIcon",
            ShellRegistryScope.HkcrOverride => $@"Software\Classes\CLSID\{clsid}\DefaultIcon",
            _ => throw new InvalidOperationException($"Unknown scope: {scope}")
        };

        private static void WriteDefaultIcon(string keyPath, string iconReference)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException($"Could not create/open registry key: HKCU\\{keyPath}");
            key.SetValue(null, iconReference, RegistryValueKind.String);
        }

        private static void WriteTwoStateValues(string keyPath, string emptyRef, string fullRef)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key == null) return;
            key.SetValue("Empty", emptyRef, RegistryValueKind.String);
            key.SetValue("Full",  fullRef,  RegistryValueKind.String);
        }

        /// <summary>
        /// Strip the outer pair of double-quotes the UI sometimes adds when the
        /// icon path has spaces. Explorer accepts <c>"path",index</c> and
        /// <c>path,index</c> equally, but the <em>icon-cache invalidation</em>
        /// is fussier when quotes are present, so we normalize on unquoted.
        /// </summary>
        private static string StripWrappingQuotes(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // Quote-then-comma form: "path with spaces.dll",-12
            if (value.Length > 1 && value[0] == '"')
            {
                var closing = value.IndexOf('"', 1);
                if (closing > 0)
                {
                    var inside = value.Substring(1, closing - 1);
                    var rest = value.Substring(closing + 1);
                    return inside + rest;
                }
            }
            return value;
        }

        // ── SHChangeNotify: tell Explorer to drop its cached icon and re-read ──

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static void BroadcastShellRefresh()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // If the broadcast fails (rare — typically only in stripped/server SKUs)
                // the user can still restart Explorer manually. Don't surface this.
            }
        }
    }
}
