using System.Windows;
using Revenant_Theme_Studio.Features.Themes.Views;
using Xunit;

namespace RTS.Tests;

public class ViewTests
{
    /// <summary>Loads the Themes tab's XAML and lays it out, which catches markup errors without launching the app.</summary>
    [Fact]
    public void ThemesViewLoadsAndLaysOut()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var view = new ThemesView();
                view.Measure(new Size(1200, 800));
                view.Arrange(new Rect(0, 0, 1200, 800));
                view.UpdateLayout();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
