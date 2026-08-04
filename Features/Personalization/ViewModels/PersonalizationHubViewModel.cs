using System.ComponentModel;
using System.Runtime.CompilerServices;
using Revenant_Theme_Studio.Features.Wallpaper.Services;
using Revenant_Theme_Studio.Features.Wallpaper.ViewModels;

namespace Revenant_Theme_Studio.Features.Personalization.ViewModels
{
    public enum PersonalizationSection { Home, Background, Display, Colors }

    /// <summary>
    /// The Personalization hub: Settings-style landing page that navigates into
    /// sections. Section view-models live here and persist across navigation, so
    /// leaving Background and coming back keeps your sources, results, and layout.
    /// </summary>
    public class PersonalizationHubViewModel : INotifyPropertyChanged
    {
        private PersonalizationSection _current = PersonalizationSection.Home;

        public WallpaperViewModel Background { get; } = new();
        public DisplayViewModel Display { get; } = new();
        public ColorsViewModel Colors { get; } = new();

        public PersonalizationSection Current
        {
            get => _current;
            private set
            {
                _current = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHome));
                OnPropertyChanged(nameof(BreadcrumbSection));
            }
        }

        public bool IsHome => Current == PersonalizationSection.Home;

        public string BreadcrumbSection => Current switch
        {
            PersonalizationSection.Background => "Background",
            PersonalizationSection.Display => "Display",
            PersonalizationSection.Colors => "Colors",
            _ => string.Empty
        };

        public void Navigate(PersonalizationSection section)
        {
            if (section == PersonalizationSection.Display)
                Display.RefreshMonitors();
            if (section == PersonalizationSection.Colors)
                Colors.LoadFromSystem();
            Current = section;
        }

        public void GoHome()
        {
            Current = PersonalizationSection.Home;
            OnPropertyChanged(nameof(CurrentWallpaperPath));
        }

        /// <summary>Wallpaper on the first monitor, for the landing preview card.</summary>
        public string? CurrentWallpaperPath =>
            new WallpaperEngineService().GetCurrentWallpaperPath();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
