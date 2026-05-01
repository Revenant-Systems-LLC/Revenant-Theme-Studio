using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    public class MunEditorViewModel : INotifyPropertyChanged
    {
        private readonly MunResourceReader _reader = new();

        private string  _status    = "Initialising…";
        private bool    _isLoading = true;
        private MunIconEntry? _selectedEntry;

        public ObservableCollection<MunIconEntry> Entries { get; } = new();

        public string MunPath { get; }

        public MunIconEntry? SelectedEntry
        {
            get => _selectedEntry;
            set { _selectedEntry = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool HasStagedChanges => Entries.Any(e => e.HasReplacement);

        public MunEditorViewModel(string munPath)
        {
            MunPath = munPath;
        }

        public async Task LoadAsync()
        {
            IsLoading = true;
            Status    = "Reading icon groups…";

            try
            {
                var groups = await Task.Run(() => _reader.ReadAllGroups(MunPath));

                int total = groups.Count;
                int done  = 0;

                foreach (var (groupId, icoBytes) in groups)
                {
                    var thumb = BuildThumbnail(icoBytes);
                    var entry = new MunIconEntry
                    {
                        GroupId          = groupId,
                        OriginalIcoBytes = icoBytes,
                        Thumbnail        = thumb
                    };

                    entry.PropertyChanged += (s, args) =>
                    {
                        if (args.PropertyName == nameof(MunIconEntry.HasReplacement))
                            OnPropertyChanged(nameof(HasStagedChanges));
                    };

                    Application.Current.Dispatcher.Invoke(() => Entries.Add(entry));

                    done++;
                    if (done % 100 == 0)
                        Status = $"Loaded {done} of {total}…";
                }

                Status = $"{Entries.Count} icon groups ready.";
            }
            catch (Exception ex)
            {
                Status = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static BitmapSource? BuildThumbnail(byte[] icoBytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource    = new MemoryStream(icoBytes);
                bmp.DecodePixelWidth = 64;
                bmp.CacheOption     = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
