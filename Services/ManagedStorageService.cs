using System;
using System.IO;

namespace Revenant_Theme_Studio.Services
{
    public class ManagedStorageService
    {
        public string RootPath { get; }
        public string ManagedIconsPath { get; }
        public string BackupsPath { get; }
        public string ManifestsPath { get; }
        public string CachePath { get; }

        public ManagedStorageService()
        {
            RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revenant Theme Studio");
            ManagedIconsPath = Path.Combine(RootPath, "ManagedIcons");
            BackupsPath = Path.Combine(RootPath, "Backups");
            ManifestsPath = Path.Combine(RootPath, "Manifests");
            CachePath = Path.Combine(RootPath, "Cache");
            EnsureDirectories();
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ManagedIconsPath);
            Directory.CreateDirectory(BackupsPath);
            Directory.CreateDirectory(ManifestsPath);
            Directory.CreateDirectory(CachePath);
        }

        public string ImportIcon(string sourcePath)
        {
            var extension = Path.GetExtension(sourcePath);
            var fileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}{extension}";
            var destination = Path.Combine(ManagedIconsPath, fileName);
            File.Copy(sourcePath, destination, overwrite: false);
            return destination;
        }
    }
}
