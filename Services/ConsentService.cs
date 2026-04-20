using System;
using System.IO;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Tracks whether the user has agreed to the advanced-operations TOS.
    /// Consent is stored as a timestamp file in LocalApplicationData.
    /// </summary>
    public sealed class ConsentService
    {
        private static readonly string ConsentFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Revenant Theme Studio",
            "advanced_consent.dat");

        public static ConsentService Instance { get; } = new();

        public bool HasConsented { get; private set; }

        private ConsentService()
        {
            HasConsented = File.Exists(ConsentFilePath);
        }

        /// <summary>
        /// Record consent, take a registry snapshot, then return true.
        /// </summary>
        public void RecordConsent()
        {
            HasConsented = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConsentFilePath)!);
                File.WriteAllText(ConsentFilePath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch { /* non-fatal */ }

            // Snapshot registry state at moment of consent
            RegistrySnapshotService.Instance.TakeSnapshot();
        }

        public void RevokeConsent()
        {
            HasConsented = false;
            try { if (File.Exists(ConsentFilePath)) File.Delete(ConsentFilePath); }
            catch { }
        }
    }
}
