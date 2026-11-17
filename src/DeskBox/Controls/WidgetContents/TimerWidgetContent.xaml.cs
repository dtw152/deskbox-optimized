using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TimerWidgetContent : UserControl
{
    public TimerWidgetContent(TimerWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public TimerWidgetViewModel ViewModel { get; }

    private void Primary_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePrimary();

    private void Reset_Click(object sender, RoutedEventArgs e) => ViewModel.Reset();

    private void Lap_Click(object sender, RoutedEventArgs e) => ViewModel.RecordLap();

    private void ApplyDuration_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyCustomDuration();

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !int.TryParse(tag, out int minutes))
        {
            return;
        }

        ViewModel.SetPreset(minutes);
    }
}

public sealed partial class TimerBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed partial class TimerInverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
