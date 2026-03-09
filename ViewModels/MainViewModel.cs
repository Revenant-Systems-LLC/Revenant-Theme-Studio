using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly FolderIconService _folderIconService = new();
        private readonly IconMatchingService _matchingService = new();
        private readonly SystemIconResourceService _systemIconService = new();

        private string _iconFolder = string.Empty;
        private string _selectedFolder = string.Empty;
        private IconChoice? _selectedIcon;
        private string _statusMessage = "Ready.";
        private string _systemResourcePath = string.Empty;

        private string _scanRoot = string.Empty;
        private int _matchThreshold = 2;

        private bool _isAutoRunning;
        private string _autoProgressText = string.Empty;
        private bool _isAutoWarningAccepted;

        private CancellationTokenSource? _autoCts;

        public ObservableCollection<IconChoice> IconList { get; } = new();
        public ObservableCollection<IconMapping> PinnedMappings { get; } = new();

        public string IconFolder
        {
            get => _iconFolder;
            set
            {
                _iconFolder = value;
                OnPropertyChanged();

                _matchingService.SetIconFolders(_iconFolder);
                ReloadIcons();
            }
        }

        public string SystemResourcePath
        {
            get => _systemResourcePath;
            set
            {
                _systemResourcePath = value;
                OnPropertyChanged();
            }
        }

        public string SelectedFolder
        {
            get => _selectedFolder;
            set { _selectedFolder = value; OnPropertyChanged(); }
        }

        public IconChoice? SelectedIcon
        {
            get => _selectedIcon;
            set { _selectedIcon = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string ScanRoot
        {
            get => _scanRoot;
            set { _scanRoot = value; OnPropertyChanged(); }
        }

        public int MatchThreshold
        {
            get => _matchThreshold;
            set { _matchThreshold = value; OnPropertyChanged(); }
        }

        public bool IsAutoRunning
        {
            get => _isAutoRunning;
            set { _isAutoRunning = value; OnPropertyChanged(); }
        }

        public string AutoProgressText
        {
            get => _autoProgressText;
            set { _autoProgressText = value; OnPropertyChanged(); }
        }

        public bool IsAutoWarningAccepted
        {
            get => _isAutoWarningAccepted;
            set { _isAutoWarningAccepted = value; OnPropertyChanged(); }
        }

        public MainViewModel()
        {
            // start empty; user selects a library folder
            _matchingService.SetIconFolders();
            SystemResourcePath = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SystemResources\imageres.dll.mun");
            ReloadIcons();
            IsAutoWarningAccepted = false;
        }

        public void ReloadIcons()
        {
            IconList.Clear();
            int loadedFromFolders = 0;
            int loadedFromSystemResource = 0;

            if (!string.IsNullOrWhiteSpace(_iconFolder) && Directory.Exists(_iconFolder))
            {
                foreach (var iconPath in _matchingService.GetAllIcons())
                {
                    if (!TryCreateFileIconChoice(iconPath, out var iconChoice))
                        continue;

                    IconList.Add(iconChoice);
                    loadedFromFolders++;
                }
            }

            if (!string.IsNullOrWhiteSpace(SystemResourcePath) && File.Exists(SystemResourcePath))
            {
                foreach (var iconChoice in _systemIconService.LoadIcons(SystemResourcePath))
                {
                    IconList.Add(iconChoice);
                    loadedFromSystemResource++;
                }
            }

            if (loadedFromFolders == 0 && loadedFromSystemResource == 0)
            {
                StatusMessage = "No icons were loaded. Select an icon folder and/or a valid imageres.dll.mun path.";
                return;
            }

            StatusMessage = $"Loaded {loadedFromFolders} file icons and {loadedFromSystemResource} system icons.";
        }

        public void ApplyManual()
        {
            if (string.IsNullOrEmpty(SelectedFolder) || SelectedIcon == null)
            {
                StatusMessage = "Select a folder and an icon first.";
                return;
            }

            try
            {
                _folderIconService.ApplyIcon(SelectedFolder, SelectedIcon.ResourcePath, SelectedIcon.ResourceIndex);
                StatusMessage =
                    $"Applied {SelectedIcon.DisplayName} to {Path.GetFileName(SelectedFolder)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private static bool TryCreateFileIconChoice(string iconPath, out IconChoice iconChoice)
        {
            iconChoice = null!;

            try
            {
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.UriSource = new Uri(iconPath, UriKind.Absolute);
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.EndInit();
                preview.Freeze();

                iconChoice = new IconChoice
                {
                    ResourcePath = iconPath,
                    ResourceIndex = 0,
                    DisplayName = Path.GetFileNameWithoutExtension(iconPath),
                    PreviewImage = preview
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task RunAutoMatchAsync()
        {
            if (IsAutoRunning) return;

            if (!IsAutoWarningAccepted)
            {
                StatusMessage = "Auto mode is locked. Accept the warning page before running auto operations.";
                return;
            }

            if (string.IsNullOrWhiteSpace(ScanRoot) || !Directory.Exists(ScanRoot))
            {
                StatusMessage = "Scan root does not exist.";
                return;
            }

            IsAutoRunning = true;
            AutoProgressText = "Starting...";
            StatusMessage = "Auto-match running...";

            _autoCts = new CancellationTokenSource();
            var token = _autoCts.Token;

            int matched = 0, skipped = 0, scanned = 0;
            string lastError = "";

            // IMPORTANT: type as IProgress<> so Report() exists
            IProgress<(int scanned, int matched, int skipped, string current)> progress =
                new Progress<(int scanned, int matched, int skipped, string current)>(p =>
                {
                    AutoProgressText =
                        $"Scanned: {p.scanned} | Matched: {p.matched} | Skipped: {p.skipped} | {p.current}";
                });

            try
            {
                await Task.Run(() =>
                {
                    foreach (var dir in SafeEnumerateDirectories(ScanRoot, token))
                    {
                        token.ThrowIfCancellationRequested();
                        scanned++;

                        try
                        {
                            string folderName = Path.GetFileName(dir);
                            string? icon = _matchingService.FindBestMatch(folderName, MatchThreshold);

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

                        if (scanned % 25 == 0)
                            progress.Report((scanned, matched, skipped, Path.GetFileName(dir)));
                    }
                }, token);

                StatusMessage = skipped > 0
                    ? $"Done. {matched} matched, {skipped} skipped. Last error: {lastError}"
                    : $"Auto-match complete. {matched} matched, 0 skipped.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"Canceled. Scanned {scanned}, matched {matched}, skipped {skipped}.";
            }
            finally
            {
                IsAutoRunning = false;
                AutoProgressText = "";
                _autoCts?.Dispose();
                _autoCts = null;
            }
        }

        public void CancelAutoMatch()
        {
            _autoCts?.Cancel();
        }

        // Safer than Directory.GetDirectories(...AllDirectories) because it won't die on access-denied
        private static IEnumerable<string> SafeEnumerateDirectories(string root, CancellationToken token)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var dir in children)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue; // skip junctions/symlinks
                    }
                    catch
                    {
                        continue;
                    }

                    yield return dir;
                    stack.Push(dir);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
