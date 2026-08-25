using Microsoft.UI.Xaml;
using Shellvis.Shell.Views;

namespace Shellvis.Shell;

public partial class App : Application
{
    internal static PillWindow? Pill { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Pill = new PillWindow();
        Pill.Activate();
    }
}
