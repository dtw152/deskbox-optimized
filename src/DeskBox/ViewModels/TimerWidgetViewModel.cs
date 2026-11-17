using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

public enum TimerWidgetMode
{
    Countdown = 0,
    Stopwatch = 1
}

/// <summary>
/// Countdown + stopwatch state for the Timer widget.
/// A single 1s dispatcher timer runs only while the surface is visible and a
/// run is active, so the idle footprint stays near zero. The countdown keeps
/// an end timestamp as its source of truth, which lets the value survive
/// window hiding, focus loss and an app restart.
/// </summary>
public sealed class TimerWidgetViewModel : ObservableObject, IDisposable
{
    internal const string MetadataDurationKey = "Timer.DurationSeconds";
    internal const string MetadataRemainingKey = "Timer.RemainingSeconds";
    internal const string MetadataEndsAtKey = "Timer.EndsAtUtcTicks";
    internal const string MetadataModeKey = "Timer.Mode";
    internal const string MetadataElapsedKey = "Timer.ElapsedSeconds";
    internal const string MetadataSoundKey = "Timer.SoundEnabled";
    internal const string MetadataLoopKey = "Timer.LoopEnabled";

    private const int DefaultDurationSeconds = 5 * 60;
    private const int MinimumDurationSeconds = 1;
    private const int MaximumDurationSeconds = 24 * 60 * 60;
    private const int MaximumLaps = 8;
    private const uint MessageBeepTypeExclamation = 0x00000040;

    private readonly WidgetConfig _config;
    private readonly LocalizationService _localization;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _tickTimer;

    private TimerWidgetMode _mode = TimerWidgetMode.Countdown;
    private int _durationSeconds = DefaultDurationSeconds;
    private int _remainingSeconds = DefaultDurationSeconds;
    private int _elapsedSeconds;
    private bool _isRunning;
    private bool _isWindowVisible = true;
    private bool _soundEnabled = true;
    private bool _loopEnabled;
    private bool _isFinished;
    private bool _disposed;
    private DateTimeOffset? _endsAtUtc;
    private DateTimeOffset? _stopwatchStartedAt;
    private int _stopwatchBaseElapsed;
    private double _customHours;
    private double _customMinutes;
    private double _customSeconds;
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

    public ObservableCollection<string> Laps { get; } = new();

    /// <summary>Countdown text (mm:ss or h:mm:ss) or stopwatch elapsed time.</summary>
    public string TimeText => _mode == TimerWidgetMode.Countdown
        ? FormatSeconds(_remainingSeconds)
        : FormatSeconds(_elapsedSeconds);

    public string StateText
    {
        get
        {
            if (_mode == TimerWidgetMode.Stopwatch)
            {
                return _isRunning
                    ? _localization.T("Timer.State.Running")
                    : _elapsedSeconds > 0
                        ? _localization.T("Timer.State.Paused")
                        : _localization.T("Timer.State.Ready");
            }

            if (_isFinished)
            {
                return _localization.T("Timer.State.Done");
            }

            if (_isRunning)
            {
                return _localization.T("Timer.State.Running");
            }

            return _remainingSeconds == _durationSeconds
                ? _localization.T("Timer.State.Ready")
                : _localization.T("Timer.State.Paused");
        }
    }

    public bool IsCountdownMode => _mode == TimerWidgetMode.Countdown;

    public bool IsStopwatchMode => _mode == TimerWidgetMode.Stopwatch;

    /// <summary>0..1 elapsed part of the countdown, drives the bar.</summary>
    public double ProgressValue => _mode == TimerWidgetMode.Stopwatch || _durationSeconds <= 0
        ? 0
        : 1.0 - (double)_remainingSeconds / _durationSeconds;

    public string DurationText => FormatSeconds(_durationSeconds);

    public bool IsRunning => _isRunning;

    public bool IsFinished => _isFinished;

    public bool CanReset => _isRunning ||
                            (_mode == TimerWidgetMode.Countdown
                                ? _remainingSeconds != _durationSeconds
                                : _elapsedSeconds > 0);

    public bool CanRecordLap => _mode == TimerWidgetMode.Stopwatch && _isRunning;

    public string PrimaryButtonText => _isRunning
        ? _localization.T("Timer.Button.Pause")
        : _mode == TimerWidgetMode.Countdown && _isFinished
            ? _localization.T("Timer.Button.Restart")
            : _localization.T("Timer.Button.Start");

    public string PrimaryButtonGlyph => _isRunning ? "\uE769" : "\uE768";

    public int ModeIndex
    {
        get => (int)_mode;
        set
        {
            TimerWidgetMode mode = value == (int)TimerWidgetMode.Stopwatch
                ? TimerWidgetMode.Stopwatch
                : TimerWidgetMode.Countdown;
            if (mode == _mode)
            {
                return;
            }

            Pause();
            _mode = mode;
            _isFinished = false;
            RaiseStateChanged();
            PersistState();
        }
    }

    // Custom duration inputs (hours/minutes/seconds boxes).
    public double CustomHours
    {
        get => _customHours;
        set
        {
            double clamped = NormalizeCustomPart(value, 24);
            if (Math.Abs(_customHours - clamped) < 0.0001)
            {
                return;
            }

            _customHours = clamped;
            OnPropertyChanged(nameof(CustomHours));
        }
    }

    public double CustomMinutes
    {
        get => _customMinutes;
        set
        {
            double clamped = NormalizeCustomPart(value, 59);
            if (Math.Abs(_customMinutes - clamped) < 0.0001)
            {
                return;
            }

            _customMinutes = clamped;
            OnPropertyChanged(nameof(CustomMinutes));
        }
    }

    public double CustomSeconds
    {
        get => _customSeconds;
        set
        {
            double clamped = NormalizeCustomPart(value, 59);
            if (Math.Abs(_customSeconds - clamped) < 0.0001)
            {
                return;
            }

            _customSeconds = clamped;
            OnPropertyChanged(nameof(CustomSeconds));
        }
    }

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            if (SetProperty(ref _soundEnabled, value))
            {
                PersistState();
            }
        }
    }

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            if (SetProperty(ref _loopEnabled, value))
            {
                PersistState();
            }
        }
    }

    public bool HasLaps => Laps.Count > 0;

    public void Start()
    {
        if (_disposed || _isRunning)
        {
            return;
        }

        if (_mode == TimerWidgetMode.Countdown)
        {
            if (_remainingSeconds <= 0)
            {
                _remainingSeconds = _durationSeconds;
            }

            _isFinished = false;
            _endsAtUtc = DateTimeOffset.UtcNow.AddSeconds(_remainingSeconds);
        }
        else
        {
            _stopwatchBaseElapsed = _elapsedSeconds;
            _stopwatchStartedAt = DateTimeOffset.UtcNow;
        }

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

        if (_mode == TimerWidgetMode.Countdown)
        {
            _remainingSeconds = ResolveRemainingSeconds();
            _endsAtUtc = null;
        }
        else
        {
            _elapsedSeconds = ResolveElapsedSeconds();
            _stopwatchStartedAt = null;
        }

        _isRunning = false;
        _tickTimer.Stop();
        RaiseStateChanged();
        PersistState();
    }

    public void Reset()
    {
        _isRunning = false;
        _endsAtUtc = null;
        _stopwatchStartedAt = null;
        _isFinished = false;
        _remainingSeconds = _durationSeconds;
        _elapsedSeconds = 0;
        _stopwatchBaseElapsed = 0;
        Laps.Clear();
        _tickTimer.Stop();
        RaiseStateChanged();
        OnPropertyChanged(nameof(HasLaps));
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
        _isFinished = false;
        _endsAtUtc = null;
        _remainingSeconds = seconds;
        _tickTimer.Stop();
        SyncCustomPartsFromDuration();
        RaiseStateChanged();
        PersistState();
    }

    /// <summary>Applies the hours/minutes/seconds boxes; 0/0/0 falls back to 1 second.</summary>
    public void ApplyCustomDuration()
    {
        int hours = (int)Math.Clamp(Math.Round(_customHours), 0, 24);
        int minutes = (int)Math.Clamp(Math.Round(_customMinutes), 0, 59);
        int seconds = (int)Math.Clamp(Math.Round(_customSeconds), 0, 59);
        int total = Math.Clamp(
            (hours * 3600) + (minutes * 60) + seconds,
            MinimumDurationSeconds,
            MaximumDurationSeconds);

        _durationSeconds = total;
        _isRunning = false;
        _isFinished = false;
        _endsAtUtc = null;
        _remainingSeconds = total;
        _tickTimer.Stop();
        SyncCustomPartsFromDuration();
        RaiseStateChanged();
        PersistState();
    }

    public void RecordLap()
    {
        if (_mode != TimerWidgetMode.Stopwatch || !_isRunning)
        {
            return;
        }

        _elapsedSeconds = ResolveElapsedSeconds();
        Laps.Insert(0, $"#{Laps.Count + 1}  {FormatSeconds(_elapsedSeconds)}");
        while (Laps.Count > MaximumLaps)
        {
            Laps.RemoveAt(Laps.Count - 1);
        }

        OnPropertyChanged(nameof(HasLaps));
        RaiseClockChanged();
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
        // Only the render tick pauses; the end timestamp stays authoritative.
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

        if (_mode == TimerWidgetMode.Countdown)
        {
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

            return;
        }

        _elapsedSeconds = ResolveElapsedSeconds();
        RaiseClockChanged();
        _tickTimer.Start();
    }

    private void TickTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_mode == TimerWidgetMode.Countdown)
        {
            int remaining = ResolveRemainingSeconds();
            if (remaining <= 0)
            {
                CompleteCountdown();
                return;
            }

            _remainingSeconds = remaining;
            RaiseClockChanged();
            return;
        }

        _elapsedSeconds = ResolveElapsedSeconds();
        RaiseClockChanged();
    }

    private void CompleteCountdown()
    {
        _remainingSeconds = 0;
        _isRunning = false;
        _endsAtUtc = null;
        _tickTimer.Stop();
        _isFinished = true;

        if (_soundEnabled)
        {
            PlayCompletionBeep();
        }

        if (_loopEnabled)
        {
            _isFinished = false;
            _remainingSeconds = _durationSeconds;
            _isRunning = true;
            _endsAtUtc = DateTimeOffset.UtcNow.AddSeconds(_durationSeconds);
            _tickTimer.Start();
        }

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

    private int ResolveElapsedSeconds()
    {
        if (!_isRunning || _stopwatchStartedAt is null)
        {
            return Math.Max(0, _elapsedSeconds);
        }

        double seconds = (DateTimeOffset.UtcNow - _stopwatchStartedAt.Value).TotalSeconds;
        return (int)Math.Clamp(_stopwatchBaseElapsed + seconds, 0, MaximumDurationSeconds);
    }

    private void RestoreState()
    {
        _config.Metadata ??= [];

        if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataModeKey), out int storedMode) &&
            storedMode == (int)TimerWidgetMode.Stopwatch)
        {
            _mode = TimerWidgetMode.Stopwatch;
        }

        if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataDurationKey), out int storedDuration) &&
            storedDuration > 0)
        {
            _durationSeconds = Math.Clamp(storedDuration, MinimumDurationSeconds, MaximumDurationSeconds);
        }

        if (bool.TryParse(_config.Metadata.GetValueOrDefault(MetadataSoundKey), out bool storedSound))
        {
            _soundEnabled = storedSound;
        }

        if (bool.TryParse(_config.Metadata.GetValueOrDefault(MetadataLoopKey), out bool storedLoop))
        {
            _loopEnabled = storedLoop;
        }

        if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataElapsedKey), out int storedElapsed) &&
            storedElapsed > 0)
        {
            _elapsedSeconds = Math.Clamp(storedElapsed, 0, MaximumDurationSeconds);
        }

        long endsAtTicks = 0;
        if (long.TryParse(_config.Metadata.GetValueOrDefault(MetadataEndsAtKey), out long parsedTicks))
        {
            endsAtTicks = parsedTicks;
        }

        _remainingSeconds = _durationSeconds;
        if (endsAtTicks > 0)
        {
            DateTimeOffset endsAt = new(endsAtTicks, TimeSpan.Zero);
            if (endsAt - DateTimeOffset.UtcNow > TimeSpan.Zero)
            {
                _endsAtUtc = endsAt;
                _isRunning = true;
                _remainingSeconds = ResolveRemainingSeconds();
                _tickTimer.Start();
                SyncCustomPartsFromDuration();
                RaiseStateChanged();
                return;
            }

            // The countdown expired while the app was closed.
            _remainingSeconds = 0;
            _isFinished = true;
            _config.Metadata.Remove(MetadataEndsAtKey);
        }
        else if (int.TryParse(_config.Metadata.GetValueOrDefault(MetadataRemainingKey), out int storedRemaining))
        {
            _remainingSeconds = Math.Clamp(storedRemaining, 0, _durationSeconds);
        }

        SyncCustomPartsFromDuration();
        RaiseStateChanged();
    }

    private void SyncCustomPartsFromDuration()
    {
        _customHours = _durationSeconds / 3600;
        _customMinutes = _durationSeconds % 3600 / 60;
        _customSeconds = _durationSeconds % 60;
        OnPropertyChanged(nameof(CustomHours));
        OnPropertyChanged(nameof(CustomMinutes));
        OnPropertyChanged(nameof(CustomSeconds));
    }

    private static double NormalizeCustomPart(double value, int maximum)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            return 0;
        }

        return Math.Clamp(Math.Round(value), 0, maximum);
    }

    private void PersistState()
    {
        _config.Metadata ??= [];
        string fingerprint =
            $"{(int)_mode}|{_durationSeconds}|{_remainingSeconds}|{_elapsedSeconds}|{_isRunning}|" +
            $"{_soundEnabled}|{_loopEnabled}|{_endsAtUtc?.UtcTicks}";
        if (string.Equals(fingerprint, _lastPersistedState, StringComparison.Ordinal))
        {
            return;
        }

        _lastPersistedState = fingerprint;
        _config.Metadata[MetadataModeKey] = ((int)_mode).ToString();
        _config.Metadata[MetadataDurationKey] = _durationSeconds.ToString();
        _config.Metadata[MetadataRemainingKey] = _remainingSeconds.ToString();
        _config.Metadata[MetadataElapsedKey] = _elapsedSeconds.ToString();
        _config.Metadata[MetadataSoundKey] = _soundEnabled.ToString();
        _config.Metadata[MetadataLoopKey] = _loopEnabled.ToString();
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
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanRecordLap));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(PrimaryButtonGlyph));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(IsCountdownMode));
        OnPropertyChanged(nameof(IsStopwatchMode));
        OnPropertyChanged(nameof(ModeIndex));
    }

    private void RaiseClockChanged()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(StateText));
    }

    private void PlayCompletionBeep()
    {
        try
        {
            MessageBeep(MessageBeepTypeExclamation);
            MessageBeep(MessageBeepTypeExclamation);
            MessageBeep(MessageBeepTypeExclamation);
        }
        catch
        {
            // A missing user32 entry point must never break the countdown.
        }
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

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

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
