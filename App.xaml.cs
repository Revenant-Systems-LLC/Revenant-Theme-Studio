using System.Windows;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Captures the user's pristine HKCU registry state before any RTS feature
    /// can mutate it. Idempotent — only writes the snapshot the first time it
    /// runs. This guarantees a restoration baseline exists even if the user
    /// never accepts the consent dialog (which previously was the only trigger).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            RegistrySnapshotService.Instance.TakeSnapshot();
        }
        catch
        {
            // Snapshot is best-effort safety. Never block app launch on it.
        }
    }
}
