using System.Globalization;

namespace Sta.DataSync.Maui.Sample.Pages;

public partial class SyncSettingsPage : ContentPage
{
    private readonly SyncSettingsViewModel _viewModel;

    public SyncSettingsPage(SyncSettingsViewModel viewModel)
    {
        Resources.Add("Invert", new InvertConverter());
        Resources.Add("NotEmpty", new NotEmptyConverter());
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (SyncException ex)
        {
            _viewModel.Error = ex.Message;
        }
    }

    private sealed class InvertConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    }

    private sealed class NotEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrEmpty(value as string);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
