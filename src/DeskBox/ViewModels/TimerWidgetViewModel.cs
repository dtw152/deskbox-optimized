using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// Countdown state for the Timer widget. Everything here is deliberately
/// self-contained: one dispatcher timer that only runs while the surface is
/// visible and a countdown is active, no watcher, no background service, no
/// image decode. That keeps the widget's footprint near zero when idle, which
/// is the point of the trimmed build.
/// </summary>
public sealed partial class TimerWidgetViewModel : ObservableObject, IDisposable
{
    internal const string MetadataDurationKey = "Timer.DurationSeconds";
    internal const string MetadataRemainingKey = "Timer.RemainingSeconds";
    internal const string MetadataEndsAtKey = "Timer.EndsAtUtcTicks";

    private const int DefaultDurationSeconds = 5 * 60;
    private const int MinimumDurationSeconds = 5;
    private const int MaximumDurationSeconds = 24 * 60 * 60;

    private readonly WidgetConfig _config;
    private readonly LocalizationService _localization;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _tickTimer;

    private int _durationSeconds = DefaultDurationSeconds;
    private int _remainingSeconds = DefaultDurationSeconds;
    private bool _isRunning;
    private bool _isWindowVisible = true;
    private bool _disposed;
    private DateTimeOffset? _endsAtUtc;
    private string _lastPersistedState = string.Empty;

    public TimerWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _settingsService = settingsService;
        _localization = localizationService ??
            throw new ArgumentNullException(nameof(localizationService));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Timer widget requires a UI dispatcher queue.");

        _tickTimer = _dispatcherQueue.CreateTimer();
        _tickTimer.IsRepeating = true;
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.Tick += TickTimer_Tick;
    }

    /// <summary>Countdown text, mm:ss or h:mm:ss.</summary>
    public string TimeText => FormatSeconds(_remainingSeconds);

    public string StateText => _isRunning
        ? _localization.T("Timer.State.Running")
        : _remainingSeconds == 0
            ? _localization.T("Timer.State.Done")
            : _remainingSeconds == _durationSeconds
                ? _localization.T("Timer.State.Ready")
                : _localization.T("Timer.State.Paused");

    /// <summary>0..1 progress consumed, drives the ring/bar.</summary>
    public double ProgressValue =>
        _durationSeconds <= 0
            ? 0
            : 1.0 - (double)_remainingSeconds / _durationSeconds;

    /// <summary>Progress text next to the ring, e.g. "5 分钟".</summary>
    public string DurationText => FormatDurationLabel(_durationSeconds);

    public bool IsRunning => _isRunning;

    public bool CanPause => _isRunning;

    public bool CanReset => _isRunning || _remainingSeconds != _durationSeconds;

    public string PrimaryButtonText => _isRunning
        ? _localization.T("Timer.Button.Pause")
        : _remainingSeconds == 0
            ? _localization.T("Timer.Button.Restart")
            : _localization.T("Timer.Button.Start");

    public string PrimaryButtonGlyph => _isRunning ? "\uE769" : "\uE768";

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        if (_remainingSeconds <= 0)
        {
            _remainingSeconds = _durationSeconds;
        }

        _endsAtUtc = DateTimeOffset.UtcNow.AddSeconds(_remainingSeconds);
        _isRunning = true;
        _tickTimer.Start();
        RaiseStateChanged();
        PersistState();
    }

    public void Pause()
    {
        if (!_isRunning)
        {
            return;
        }

        _remainingSeconds = ResolveRemainingSeconds();
        _isRunning = false;
        _endsAtUtc = null;
        _tickTimer.Stop();
        RaiseStateChanged();
        PersistState();
    }

    public void Reset()
    {
        _isRunning = false;
        _endsAtUtc = null;
        _remainingSeconds = _durationSeconds;
        _tickTimer.Stop();
        RaiseStateChanged();
        PersistState();
    }

    public void TogglePrimary()
    {
        if (_isRunning)
        {
            Pause();
            return;
        }

        Start();
    }

    public void SetPreset(int minutes)
    {
        int seconds = Math.Clamp(minutes * 60, MinimumDurationSeconds, MaximumDurationSeconds);
        _durationSeconds = seconds;
        _isRunning = false;
        _endsAtUtc = null;
        _remainingSeconds = seconds;
        _tickTimer.Stop();
        RaiseStateChanged();
        PersistState();
    }

    public Task InitializeAsync()
    {
        RestoreState();
        return Task.CompletedTask;
    }

    public Task RefreshAsync() => Task.CompletedTask;

    public void ApplyAppearance()
    {
    }

    public void OnActivated()
    {
        ResumeTickingIfNeeded();
    }

    public void OnDeactivated()
    {
        // Keep the countdown alive across focus changes: only the render tick
        // pauses, the end timestamp stays authoritative.
        _tickTimer.Stop();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (visible)
        {
            ResumeTickingIfNeeded();
        }
        else
        {
            _tickTimer.Stop();
        }
    }

    public void OnWindowRevealCompleted()
    {
        ResumeTickingIfNeeded();
    }

    private void ResumeTickingIfNeeded()
    {
        if (_disposed || !_isRunning || !_isWindowVisible)
        {
            return;
        }

        // Fold the elapsed wall-clock time before restarting the tick so a
        // hidden window never shows a stale value on the first frame back.
        _remainingSeconds = ResolveRemainingSeconds();
        RaiseClockChanged();
        if (_remainingSeconds > 0)
        {
            _tickTimer.Start();
        }
        else
        {
            CompleteCountdown();
        }
    }

    private void TickTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        int remaining = ResolveRemainingSeconds();
        if (remaining > _remainingSeconds)
        {
            // System clock moved backwards; trust the timestamp.
            _remainingSeconds = remaining;
        }

        _remainingSeconds = remaining;
        if (_remainingSeconds <= 0)
        {
            CompleteCountdown();
            return;
        }

        RaiseClockChanged();
    }

    private void CompleteCountdown()
    {
        _remainingSeconds = 0;
        _isRunning = false;
        _endsAtUtc = null;
        _tickTimer.Stop();
        RaiseStateChanged();
        PersistState();
    }

    private int ResolveRemainingSeconds()
    {
        if (!_isRunning || _endsAtUtc is null)
        {
            return Math.Max(0, _remainingSeconds);
        }

        double seconds = (_endsAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds;
        return (int)Math.Clamp(Math.Ceiling(seconds), 0, MaximumDurationSeconds);
    }

    private void RestoreState()
    {
        _config.Metadata ??= [];
        if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataDurationKey), out int storedDuration) &&
            storedDuration > 0)
        {
            _durationSeconds = Math.Clamp(storedDuration, MinimumDurationSeconds, MaximumDurationSeconds);
        }

        long endsAtTicks = 0;
        if (long.TryParse(_config.Metadata.GetValueOrDefault(MetadataEndsAtKey), out long parsedTicks))
        {
            endsAtTicks = parsedTicks;
        }

        if (endsAtTicks > 0)
        {
            DateTimeOffset endsAt = new(endsAtTicks, TimeSpan.Zero);
            if (endsAt - DateTimeOffset.UtcNow > TimeSpan.Zero)
            {
                // The app was closed while the countdown was running and the
                // deadline is still ahead: pick it back up.
                _endsAtUtc = endsAt;
                _isRunning = true;
                _remainingSeconds = ResolveRemainingSeconds();
                _tickTimer.Start();
                RaiseStateChanged();
                return;
            }

            _remainingSeconds = 0;
            _config.Metadata.Remove(MetadataEndsAtKey);
        }
        else if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataRemainingKey), out int storedRemaining))
        {
            _remainingSeconds = Math.Clamp(storedRemaining, 0, _durationSeconds);
        }
        else
        {
            _remainingSeconds = _durationSeconds;
        }

        RaiseStateChanged();
    }

    private void PersistState()
    {
        _config.Metadata ??= [];
        string fingerprint = $"{_durationSeconds}|{_remainingSeconds}|{_isRunning}|{_endsAtUtc?.UtcTicks}";
        if (string.Equals(fingerprint, _lastPersistedState, StringComparison.Ordinal))
        {
            return;
        }

        _lastPersistedState = fingerprint;
        _config.Metadata[MetadataDurationKey] = _durationSeconds.ToString();
        _config.Metadata[MetadataRemainingKey] = _remainingSeconds.ToString();
        if (_isRunning && _endsAtUtc is not null)
        {
            _config.Metadata[MetadataEndsAtKey] = _endsAtUtc.Value.UtcTicks.ToString();
        }
        else
        {
            _config.Metadata.Remove(MetadataEndsAtKey);
        }

        _settingsService?.SaveDebounced();
    }

    private void RaiseStateChanged()
    {
        RaiseClockChanged();
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(PrimaryButtonGlyph));
        OnPropertyChanged(nameof(DurationText));
    }

    private void RaiseClockChanged()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(StateText));
    }

    private static string FormatSeconds(int totalSeconds)
    {
        int seconds = Math.Clamp(totalSeconds, 0, MaximumDurationSeconds);
        int hours = seconds / 3600;
        int minutes = seconds % 3600 / 60;
        int secs = seconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{secs:00}"
            : $"{minutes:00}:{secs:00}";
    }

    private string FormatDurationLabel(int totalSeconds)
    {
        int minutes = Math.Max(1, totalSeconds / 60);
        return minutes >= 60 && minutes % 60 == 0
            ? _localization.Format("Timer.Duration.Hours", minutes / 60)
            : _localization.Format("Timer.Duration.Minutes", minutes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tickTimer.Stop();
        _tickTimer.Tick -= TickTimer_Tick;
    }
}
