using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Revenant_Theme_Studio.Features.Personalization.ViewModels;

namespace Revenant_Theme_Studio.Features.Personalization.Views
{
    public partial class PersonalizationHubView : UserControl
    {
        public PersonalizationHubView()
        {
            InitializeComponent();
        }

        private PersonalizationHubViewModel? VM => DataContext as PersonalizationHubViewModel;

        private void Home_Click(object sender, MouseButtonEventArgs e) => VM?.GoHome();

        private void Background_Click(object sender, MouseButtonEventArgs e) =>
            VM?.Navigate(PersonalizationSection.Background);

        private void Display_Click(object sender, MouseButtonEventArgs e) =>
            VM?.Navigate(PersonalizationSection.Display);

        private void Colors_Click(object sender, MouseButtonEventArgs e) =>
            VM?.Navigate(PersonalizationSection.Colors);
    }

    /// <summary>Visible when false — the inverse of BooleanToVisibilityConverter.</summary>
    public sealed class InverseBoolToVis : IValueConverter
    {
        public static readonly InverseBoolToVis Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
