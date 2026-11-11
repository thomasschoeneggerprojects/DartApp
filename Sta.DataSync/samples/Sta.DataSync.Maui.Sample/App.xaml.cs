using Sta.DataSync.Maui.Sample.Pages;

namespace Sta.DataSync.Maui.Sample;

public partial class App : Application
{
    private readonly SyncSettingsPage _page;

    public App(SyncSettingsPage page)
    {
        InitializeComponent();
        _page = page;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new NavigationPage(_page)) { Title = "Sta.DataSync Sample" };
}
