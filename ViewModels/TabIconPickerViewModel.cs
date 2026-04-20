using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    /// <summary>
    /// One per tab that exposes an icon library. Owns a folder path, an
    /// optional system-resource path, a loaded list, and the current
    /// selection. Three instances live on <see cref="MainViewModel"/> so the
    /// Folder / Drive / System tabs each have their own picker that doesn't
    /// bleed across tabs — Dave's call: "on each tab, it makes you reselect
    /// the folder that contains your icons."
    /// </summary>
    public class TabIconPickerViewModel : INotifyPropertyChanged
    {
        private readonly IconMatchingService _matchingService = new();
        private readonly SystemIconResourceService _systemIconService = new();

        private string _iconFolder = string.Empty;
        private string _systemResourcePath = string.Empty;
        private bool _includeSystemIcons;
        private IconChoice? _selectedIcon;

        public string TabName { get; }

        /// <summary>
        /// True for tabs whose features are Pro-gated. The rail uses this to
        /// reject the Pro-only gunmetal theme silently rather than appearing
        /// to load it. Status messages still flow up to MainViewModel.
        /// </summary>
        public bool RequiresProForGunmetal { get; }

        public Action<string>? OnStatusMessage { get; set; }

        public ObservableCollection<IconChoice> IconList { get; } = new();

        public TabIconPickerViewModel(string tabName, bool requiresProForGunmetal = false)
        {
            TabName = tabName;
            RequiresProForGunmetal = requiresProForGunmetal;
            // Default to imageres.dll.mun so the system-icons checkbox does
            // something useful out of the box.
            _systemResourcePath = Environment.ExpandEnvironmentVariables(
                @"%SystemRoot%\SystemResources\imageres.dll.mun");
        }

        public string IconFolder
        {
            get => _iconFolder;
            set
            {
                if (RequiresProForGunmetal &&
                    !string.IsNullOrEmpty(value) &&
                    value.Contains("gunmetal-theme", StringComparison.OrdinalIgnoreCase) &&
                    !LicenseService.Instance.IsPro)
                {
                    OnStatusMessage?.Invoke($"[{TabName}] Gunmetal theme requires RTS Pro.");
                    return;
                }
                _iconFolder = value;
                OnPropertyChanged();
                _matchingService.SetIconFolders(_iconFolder);
                ReloadIcons();
            }
        }

        public string SystemResourcePath
        {
            get => _systemResourcePath;
            set { _systemResourcePath = value; OnPropertyChanged(); }
        }

        public bool IncludeSystemIcons
        {
            get => _includeSystemIcons;
            set
            {
                if (value && !LicenseService.Instance.IsPro)
                {
                    OnStatusMessage?.Invoke($"[{TabName}] System icon browser requires RTS Pro.");
                    return;
                }
                _includeSystemIcons = value;
                OnPropertyChanged();
                ReloadIcons();
            }
        }

        public IconChoice? SelectedIcon
        {
            get => _selectedIcon;
            set { _selectedIcon = value; OnPropertyChanged(); }
        }

        public void ReloadIcons()
        {
            IconList.Clear();

            if (!string.IsNullOrWhiteSpace(_iconFolder) && Directory.Exists(_iconFolder))
            {
                foreach (var iconPath in _matchingService.GetAllIcons())
                {
                    if (TryCreateFileIconChoice(iconPath, out var choice))
                        IconList.Add(choice);
                }
            }

            if (IncludeSystemIcons &&
                !string.IsNullOrWhiteSpace(SystemResourcePath) &&
                File.Exists(SystemResourcePath))
            {
                foreach (var choice in _systemIconService.LoadIcons(SystemResourcePath))
                    IconList.Add(choice);
            }
        }

        private static bool TryCreateFileIconChoice(string iconPath, out IconChoice iconChoice)
        {
            iconChoice = null!;
            try
            {
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.UriSource   = new Uri(iconPath, UriKind.Absolute);
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.EndInit();
                preview.Freeze();
                iconChoice = new IconChoice
                {
                    ResourcePath  = iconPath,
                    ResourceIndex = 0,
                    DisplayName   = Path.GetFileNameWithoutExtension(iconPath),
                    PreviewImage  = preview
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
