using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio
{
    public partial class LicenseKeyWindow : Window, INotifyPropertyChanged
    {
        private string _keyInput = string.Empty;
        private string _statusMessage = string.Empty;
        private Brush _statusColor = Brushes.Gray;

        public string KeyInput
        {
            get => _keyInput;
            set { _keyInput = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public LicenseKeyWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private async void Activate_Click(object sender, RoutedEventArgs e)
        {
            StatusMessage = "Validating…";
            StatusColor = Brushes.Gray;

            if (await LicenseService.Instance.ValidateAndActivateAsync(KeyInput))
            {
                StatusMessage = "Activated! RTS Pro is now unlocked.";
                StatusColor = Brushes.LimeGreen;
                DialogResult = true;
            }
            else
            {
                StatusMessage = "Invalid key or no internet connection. Check for typos and try again.";
                StatusColor = Brushes.OrangeRed;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
