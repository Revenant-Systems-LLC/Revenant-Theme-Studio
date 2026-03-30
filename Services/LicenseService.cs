using System;
using System.IO;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Simple license loader for gating Pro features.
    /// A non-empty license file beginning with "PRO-" unlocks Pro features.
    /// </summary>
    public class LicenseService
    {
        public bool IsPro { get; }

        public LicenseService()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var rtsDir = Path.Combine(appData, "Revenant Theme Studio");
                var licenseFile = Path.Combine(rtsDir, "license.key");
                if (File.Exists(licenseFile))
                {
                    var key = File.ReadAllText(licenseFile).Trim();
                    IsPro = !string.IsNullOrEmpty(key) && key.StartsWith("PRO-", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                IsPro = false;
            }
        }
    }
}