using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.ViewModels;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Themes.Views
{
    /// <summary>The Themes tab. Dialogs and the consent gate live here; the work lives in the view model.</summary>
    public partial class ThemesView : UserControl
    {
        public static readonly IValueConverter ZeroToVisible = new ZeroToVisibleConverter();
        private bool _checkedInterrupted;

        public ThemesView()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                // Once per run: a theme left half applied by a crash or power cut.
                if (_checkedInterrupted || VM is null) return;
                _checkedInterrupted = true;
                if (VM.Interrupted() is { } tx &&
                    Ask($"Applying \"{tx.BundleName}\" was interrupted (the app closed or the PC lost power).\n\n" +
                        "Put everything it changed back now?", "Interrupted theme") == MessageBoxResult.Yes)
                    await VM.RecoverAsync(tx);
            };
        }

        private ThemesViewModel? VM => DataContext as ThemesViewModel;
        private Window? Owner => Window.GetWindow(this);

        private MessageBoxResult Ask(string text, string title) =>
            MessageBox.Show(Owner!, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        /// <summary>Same consent as every other feature that changes Windows settings.</summary>
        private bool EnsureConsent()
        {
            if (ConsentService.Instance.HasConsented) return true;
            var dlg = new ConsentDialog { Owner = Owner };
            return dlg.ShowDialog() == true && ConsentService.Instance.HasConsented;
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (VM is null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Import themes",
                Multiselect = true,
                Filter = "All themes|*.rtstheme;*.theme;*.themepack;*.deskthemepack;*.ddw|" +
                         "Revenant themes (*.rtstheme)|*.rtstheme|Windows themes (*.theme, *.themepack)|*.theme;*.themepack;*.deskthemepack|" +
                         "WinDynamicDesktop (*.ddw)|*.ddw",
            };
            if (dlg.ShowDialog(Owner) == true) await VM.ImportAsync(dlg.FileNames);
        }

        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (VM is not null) await VM.CaptureAsync();
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (VM?.Selected is null) return;
            var plan = VM.PlanSelected();
            if (plan is null) return;
            if (!plan.CanApply)
            {
                MessageBox.Show(Owner!, "This theme has nothing this version can apply on this PC:\n\n" + Lines(plan),
                    "Apply theme", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string admin = plan.NeedsAdmin ? "\n\nWindows will ask for administrator permission once, for the system icons." : "";
            if (Ask($"Apply \"{VM.Selected.Name}\"?\n\n{Lines(plan)}{admin}\n\nEverything can be undone with \"Undo last theme\".",
                    "Apply theme") != MessageBoxResult.Yes) return;
            if (!EnsureConsent()) return;
            await VM.ApplySelectedAsync();
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (VM is null) return;
            var conflicts = await VM.UndoLastAsync();
            if (conflicts is not null &&
                Ask("Some of these settings were changed after the theme was applied, so they were left as they are now.\n\n" +
                    "Put them back to how they were before the theme anyway?", "Undo theme") == MessageBoxResult.Yes)
                await VM.UndoLastAsync(conflicts);
        }

        private async void ExportBundle_Click(object sender, RoutedEventArgs e)
        {
            if (VM?.Selected is null) return;
            var dlg = new SaveFileDialog
            {
                Title = "Export theme",
                FileName = VM.Selected.Entry.Manifest.Id + ".rtstheme",
                Filter = "Revenant theme (*.rtstheme)|*.rtstheme",
            };
            if (dlg.ShowDialog(Owner) == true) await VM.ExportBundleAsync(dlg.FileName);
        }

        private async void ExportTheme_Click(object sender, RoutedEventArgs e)
        {
            if (VM?.Selected is null) return;
            var dlg = new OpenFolderDialog
            {
                Title = "Choose where to save the Windows theme",
                InitialDirectory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Themes"),
            };
            if (dlg.ShowDialog(Owner) == true) await VM.ExportWindowsThemeAsync(dlg.FolderName);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (VM?.Selected is null) return;
            if (Ask($"Remove \"{VM.Selected.Name}\" from your library?\n\nIf it's applied, it stays applied until you undo it.",
                    "Remove theme") == MessageBoxResult.Yes)
                VM.RemoveSelected();
        }

        private static string Lines(Preflight plan)
        {
            var sb = new StringBuilder();
            foreach (var p in plan.Plans)
            {
                string what = p.Decision switch
                {
                    Decision.Apply => "will change" + (p.Reason is null ? "" : $" ({p.Reason})"),
                    Decision.Skip => $"skipped: {p.Reason}",
                    _ => $"can't apply: {p.Reason}",
                };
                sb.AppendLine($"• {ThemesViewModel.Label(p.Key)}: {what}");
            }
            return sb.ToString().TrimEnd();
        }

        private sealed class ZeroToVisibleConverter : IValueConverter
        {
            public object Convert(object value, Type t, object p, CultureInfo c) =>
                value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
        }
    }
}
