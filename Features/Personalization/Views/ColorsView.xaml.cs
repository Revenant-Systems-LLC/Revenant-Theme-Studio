using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Revenant_Theme_Studio.Features.Personalization.ViewModels;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Personalization.Views
{
    public partial class ColorsView : UserControl
    {
        public ColorsView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) =>
            {
                if (e.NewValue is ColorsViewModel vm)
                    vm.ConsentGate = EnsureConsent;
            };
        }

        private ColorsViewModel? VM => DataContext as ColorsViewModel;

        /// <summary>Same gate as every other registry-touching feature in RTS.</summary>
        private bool EnsureConsent()
        {
            if (ConsentService.Instance.HasConsented) return true;
            var dlg = new ConsentDialog { Owner = Window.GetWindow(this) };
            return dlg.ShowDialog() == true;
        }

        private void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: AccentSwatch swatch })
                VM.SelectedSwatch = swatch;
        }

        private void CustomHex_Click(object sender, RoutedEventArgs e) => VM?.ApplyCustomHex();
    }

    /// <summary>True when the swatch item equals the VM's selected swatch.</summary>
    public sealed class SwatchSelectedConverter : IMultiValueConverter
    {
        public static readonly SwatchSelectedConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.Length == 2 && values[0] is AccentSwatch a && values[1] is AccentSwatch b && a == b;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
