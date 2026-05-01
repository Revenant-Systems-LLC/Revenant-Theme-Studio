using System;
using System.IO;

namespace Revenant_Theme_Studio.Services
{
    public class MunBackupService
    {
        private static readonly string BackupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Revenant Theme Studio", "Backups");

        public string Backup(string targetPath)
        {
            string stamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string folder = Path.Combine(BackupRoot, stamp);
            Directory.CreateDirectory(folder);

            string dest = Path.Combine(folder, Path.GetFileName(targetPath) + ".bak");
            File.Copy(targetPath, dest, overwrite: true);
            return dest;
        }

        public void Restore(string backupPath, string targetPath)
        {
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("Backup file not found.", backupPath);
            File.Copy(backupPath, targetPath, overwrite: true);
        }
    }
}
