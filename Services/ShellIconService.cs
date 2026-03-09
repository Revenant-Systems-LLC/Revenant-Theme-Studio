using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Services
{
    public class ShellIconService
    {
        private const string ClsidRoot = @"Software\Classes\CLSID";

        public IReadOnlyDictionary<string, (string Clsid, string DefaultIcon)> Targets { get; } = new Dictionary<string, (string, string)>
        {
            ["3D Objects"] = ("{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}", @"%SystemRoot%\System32\imageres.dll,-198"),
            ["Desktop"] = ("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"%SystemRoot%\System32\imageres.dll,-183"),
            ["Documents"] = ("{D3162B92-9365-467A-956B-92703ACA08AF}", @"%SystemRoot%\System32\imageres.dll,-112"),
            ["Downloads"] = ("{088E3905-0323-4B02-9826-5D99428E115F}", @"%SystemRoot%\System32\imageres.dll,-184"),
            ["Music"] = ("{3DFDF296-DBEC-4FB4-81D1-6A3438BCF4DE}", @"%SystemRoot%\System32\imageres.dll,-108"),
            ["Pictures"] = ("{24AD3AD4-A569-4530-98E1-AB02F9417AA8}", @"%SystemRoot%\System32\imageres.dll,-113"),
            ["Videos"] = ("{F86FA3AB-70D2-4FC7-9C99-FCBF05467F3A}", @"%SystemRoot%\System32\imageres.dll,-189"),
            ["Libraries"] = ("{031E4825-7B94-4DC3-B131-E946B44C8DD5}", @"%SystemRoot%\System32\imageres.dll,-1003"),
            ["Recycle Bin"] = ("{645FF040-5081-101B-9F08-00AA002F954E}", @"%SystemRoot%\System32\imageres.dll,0"),
            ["This PC"] = ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", @"%SystemRoot%\System32\imageres.dll,-109"),
            ["Control Panel"] = ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", @"%SystemRoot%\System32\imageres.dll,-27")
        };

        public string? GetCurrentOverride(string targetName)
        {
            var clsid = ResolveClsid(targetName);
            using var key = Registry.CurrentUser.OpenSubKey($"{ClsidRoot}\\{clsid}\\DefaultIcon");
            return key?.GetValue(null) as string;
        }

        public void ApplyOverride(string targetName, string iconReference)
        {
            var clsid = ResolveClsid(targetName);
            using var key = Registry.CurrentUser.CreateSubKey($"{ClsidRoot}\\{clsid}\\DefaultIcon", true);
            key?.SetValue(null, iconReference);
        }

        public void RestoreDefault(string targetName)
        {
            var clsid = ResolveClsid(targetName);
            Registry.CurrentUser.DeleteSubKeyTree($"{ClsidRoot}\\{clsid}\\DefaultIcon", throwOnMissingSubKey: false);
        }

        private string ResolveClsid(string targetName)
        {
            if (!Targets.TryGetValue(targetName, out var value))
            {
                throw new ArgumentException($"Unknown shell target: {targetName}");
            }

            return value.Clsid;
        }
    }
}
