using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly FolderIconService _folderIconService = new();
        private readonly IconMatchingService _matchingService;

        private string _iconFolder = string.Empty;
        private string _selectedFolder = string.Empty;
        private string _selectedIcon = string.Empty;
        private string _statusMessage = "Ready.";

        public ObservableCollection<string> IconList { get; } = new();
        public ObservableCollection<IconMapping> PinnedMappings { get; } = new();

        public string IconFolder
        {
            get => _iconFolder;
            set { _iconFolder = value; OnPropertyChanged(); LoadIcons(); }
        }

        public string SelectedFolder
        {
            get => _selectedFolder;
            set { _selectedFolder = value; OnPropertyChanged(); }
        }

        public string SelectedIcon
        {
            get => _selectedIcon;
            set { _selectedIcon = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public MainViewModel()
        {
            _iconFolder = string.Empty;
            _matchingService = new IconMatchingService(
                @"C:\The-Ossuary\Revenant-Systems\Projects\Revenant-Theme-Studio\IconEngine\icons",
                @"C:\The-Ossuary\Revenant-Systems\Projects\Revenant-Theme-Studio\IconEngine\drive-icons",
                @"C:\The-Ossuary\Revenant-Systems\Projects\Revenant-Theme-Studio\IconEngine\folder-icons",
                @"C:\The-Ossuary\Revenant-Systems\Projects\Revenant-Theme-Studio\IconEngine\archive-icons"
            );
            LoadIcons();
        }

        private void LoadIcons()
        {
            IconList.Clear();
            var icons = _matchingService.GetAllIcons();
            foreach (var icon in icons)
                IconList.Add(icon);
        }

        public void ApplyManual()
        {
            if (string.IsNullOrEmpty(SelectedFolder) || string.IsNullOrEmpty(SelectedIcon))
            {
                StatusMessage = "Select a folder and an icon first.";
                return;
            }

            try
            {
                _folderIconService.ApplyIcon(SelectedFolder, SelectedIcon);
                StatusMessage = $"Applied {Path.GetFileNameWithoutExtension(SelectedIcon)} to {Path.GetFileName(SelectedFolder)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        public void RunAutoMatch(string scanRoot)
        {
            if (!Directory.Exists(scanRoot))
            {
                StatusMessage = "Scan root does not exist.";
                return;
            }

            int matched = 0;
            int skipped = 0;
            string lastError = "";

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.GetDirectories(scanRoot, "*", SearchOption.AllDirectories);
            }
            catch
            {
                dirs = Directory.GetDirectories(scanRoot, "*", SearchOption.TopDirectoryOnly);
            }

            foreach (var dir in dirs)
            {
                try
                {
                    // Skip junction points and symlinks
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    string folderName = Path.GetFileName(dir);
                    string? icon = _matchingService.FindBestMatch(folderName);
                    if (icon != null)
                    {
                        _folderIconService.ApplyIcon(dir, icon);
                        matched++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    lastError = ex.Message;
                }
            }

            StatusMessage = skipped > 0
                ? $"Done. {matched} matched, {skipped} skipped. Last error: {lastError}"
                : $"Auto-match complete. {matched} matched, 0 skipped.";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
