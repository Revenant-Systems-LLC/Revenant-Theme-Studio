using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Revenant_Theme_Studio.Models
{
    public enum MatchStrength
    {
        Strong,
        Weak,
        None
    }

    public enum MatchStrictness
    {
        Strict,
        Balanced,
        Loose
    }

    public class MatchDecision
    {
        public MatchStrength Strength { get; init; }
        public string? IconPath { get; init; }
        public string? Reason { get; init; }
    }

    public class AutoMatchResult : INotifyPropertyChanged
    {
        public required string FolderName { get; init; }
        public required string TargetPath { get; init; }
        public required string Reason { get; set; }

        // ── Current icon (what Windows renders right now, pre-scan) ──────────
        // Parsed once from the folder's desktop.ini by the scanner. If the
        // reference points at a .dll/.mun resource, CurrentIconPreview stays
        // null and CurrentIconLabel carries the text fallback.
        public string? CurrentIconPath { get; init; }
        public int CurrentIconIndex { get; init; }
        public BitmapImage? CurrentIconPreview { get; init; }
        public string CurrentIconLabel
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentIconPath)) return "(default)";
                var name = Path.GetFileName(CurrentIconPath);
                return CurrentIconIndex == 0 ? name : $"{name},{CurrentIconIndex}";
            }
        }

        private string? _assignedIconPath;
        public string? AssignedIconPath
        {
            get => _assignedIconPath;
            set
            {
                _assignedIconPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AssignedIconName));
                OnPropertyChanged(nameof(AssignedIconPreview));
            }
        }

        public string AssignedIconName =>
            string.IsNullOrEmpty(_assignedIconPath) ? "(unbound)" : Path.GetFileNameWithoutExtension(_assignedIconPath);

        public BitmapImage? AssignedIconPreview => LoadIcoPreview(_assignedIconPath);

        public static BitmapImage? LoadIcoPreview(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (!path.EndsWith(".ico", System.StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new System.Uri(path, System.UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>
        /// Parse a desktop.ini IconResource value into (path, index). Returns
        /// null path if the value is malformed.
        /// </summary>
        public static (string? path, int index) ParseIconReference(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, 0);
            var comma = raw.LastIndexOf(',');
            if (comma < 0) return (raw.Trim().Trim('"'), 0);
            var path = raw[..comma].Trim().Trim('"');
            var idx  = int.TryParse(raw[(comma + 1)..].Trim(), out var i) ? i : 0;
            return (string.IsNullOrWhiteSpace(path) ? null : path, idx);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
