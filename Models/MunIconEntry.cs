using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Revenant_Theme_Studio.Models
{
    public class MunIconEntry : INotifyPropertyChanged
    {
        public int GroupId { get; init; }
        public string DisplayName => $"IconGroup{GroupId:D4}";
        public string ExportFileName => $"{DisplayName}.ico";
        public required byte[] OriginalIcoBytes { get; init; }

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        private byte[]? _replacementBytes;
        public byte[]? ReplacementBytes
        {
            get => _replacementBytes;
            set
            {
                _replacementBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasReplacement));
                OnPropertyChanged(nameof(ActiveBytes));
            }
        }

        private string? _replacementSourcePath;
        public string? ReplacementSourcePath
        {
            get => _replacementSourcePath;
            set { _replacementSourcePath = value; OnPropertyChanged(); }
        }

        public bool HasReplacement => _replacementBytes != null;
        public byte[] ActiveBytes => _replacementBytes ?? OriginalIcoBytes;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
