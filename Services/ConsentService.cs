using System;
using System.IO;
using System.Windows;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Tracks whether the user has consented to registry edits and triggers a registry backup on first consent.
    /// A marker file is written after consent so that the prompt is only shown once.
    /// </summary>
    public class ConsentService
    {
        private readonly RegistryBackupService _backupService;
        private readonly string _consentMarkerPath;

        public ConsentService(RegistryBackupService backupService)
        {
            _backupService = backupService;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Revenant Theme Studio");
            Directory.CreateDirectory(dir);
            _consentMarkerPath = Path.Combine(dir, "registry_consent.txt");
        }

        /// <summary>
        /// Returns true if the user has already consented to registry edits.
        /// </summary>
        public bool HasConsented => File.Exists(_consentMarkerPath);

        /// <summary>
        /// Ensure the user has given consent to perform registry edits. If consent has not been
        /// given yet, a confirmation dialog is shown. When the user accepts, a backup of the current
        /// registry icon settings is created and a marker file is written. Returns true if consent is
        /// granted, false if the user declines.
        /// </summary>
        public bool EnsureConsent()
        {
            if (HasConsented) return true;
            var result = System.Windows.MessageBox.Show(
                "You are about to perform actions that modify the Windows Registry. Before proceeding, Revenant Theme Studio will create a backup of your current drive and shell icon settings. Do you want to continue?",
                "Registry Edit Consent",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    _backupService.BackupRegistry();
                }
                catch
                {
                    // Backup is best-effort; if it fails we still proceed as the user has consented.
                }
                try
                {
                    File.WriteAllText(_consentMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
                }
                catch
                {
                    // If we cannot write the marker, we'll prompt again next time.
                }
                return true;
            }
            return false;
        }
    }
}