using DartForge.App.Services;
namespace DartForge.App;
public partial class App : Application
{
    public App(AdState ads) { InitializeComponent(); MainPage = new MainPage(ads); }
}
