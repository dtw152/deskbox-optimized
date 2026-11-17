using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class TimerWidgetContentAdapter : WidgetContentAdapterBase
{
    public TimerWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Func<TimerWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new TimerWidgetViewModel(config, localizationService, settingsService),
            viewFactory)
    {
    }

    private TimerWidgetContentAdapter(
        WidgetConfig config,
        TimerWidgetViewModel viewModel,
        Func<TimerWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new TimerWidgetContent(vm)))(viewModel))
    {
        ViewModel = viewModel;
    }

    public TimerWidgetViewModel ViewModel { get; }

    public override Task InitializeAsync() => ViewModel.InitializeAsync();

    public override Task RefreshAsync() => ViewModel.RefreshAsync();

    public override void ApplyAppearance() => ViewModel.ApplyAppearance();

    public override void OnActivated() => ViewModel.OnActivated();

    public override void OnDeactivated() => ViewModel.OnDeactivated();

    public override void OnWindowVisibilityChanged(bool visible) => ViewModel.OnWindowVisibilityChanged(visible);

    public override void OnWindowRevealCompleted() => ViewModel.OnWindowRevealCompleted();

    protected override void Dispose(bool disposing)
    {
        ViewModel.Dispose();
    }
}
