using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
