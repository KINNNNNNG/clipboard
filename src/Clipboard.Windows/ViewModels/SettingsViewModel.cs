using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;

namespace Clipboard.Windows.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly IClientSettingsStore _store;
    private readonly ISettingsRetentionService _retention;
    private readonly IGlobalShortcutConfigurator _shortcuts;
    private readonly IStartupSettingsService _startup;
    private readonly TimeProvider _timeProvider;
    private readonly IRetentionPolicyProvider? _retentionPolicy;
    private ClientSettings _persisted = ClientSettings.Default;
    private bool _maxRegularItemsEnabled = true;
    private int _maxRegularItems = 1000;
    private bool _maxAgeDaysEnabled = true;
    private uint _maxAgeDays = 30;
    private bool _maxImageGiBEnabled = true;
    private double _maxImageGiB = 1;
    private bool _interceptWinV = true;
    private string _fallbackHotkey = "Alt+V";
    private bool _startWithWindows;
    private string _theme = "system";
    private string? _errorMessage;

    public SettingsViewModel(
        IClientSettingsStore store,
        ISettingsRetentionService retention,
        IGlobalShortcutConfigurator shortcuts,
        IStartupSettingsService startup,
        TimeProvider? timeProvider = null,
        IRetentionPolicyProvider? retentionPolicy = null)
    {
        _store = store;
        _retention = retention;
        _shortcuts = shortcuts;
        _startup = startup;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionPolicy = retentionPolicy;
    }

    public bool MaxRegularItemsEnabled
    {
        get => _maxRegularItemsEnabled;
        set => SetProperty(ref _maxRegularItemsEnabled, value);
    }

    public int MaxRegularItems
    {
        get => _maxRegularItems;
        set => SetProperty(ref _maxRegularItems, value);
    }

    public bool MaxAgeDaysEnabled
    {
        get => _maxAgeDaysEnabled;
        set => SetProperty(ref _maxAgeDaysEnabled, value);
    }

    public uint MaxAgeDays
    {
        get => _maxAgeDays;
        set => SetProperty(ref _maxAgeDays, value);
    }

    public bool MaxImageGiBEnabled
    {
        get => _maxImageGiBEnabled;
        set => SetProperty(ref _maxImageGiBEnabled, value);
    }

    public double MaxImageGiB
    {
        get => _maxImageGiB;
        set => SetProperty(ref _maxImageGiB, value);
    }

    public bool InterceptWinV
    {
        get => _interceptWinV;
        set => SetProperty(ref _interceptWinV, value);
    }

    public string FallbackHotkey
    {
        get => _fallbackHotkey;
        set => SetProperty(ref _fallbackHotkey, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ClientSettings settings = await _store.LoadAsync(cancellationToken);
        Apply(settings);
        _persisted = settings;
        _retentionPolicy?.Update(settings);
        ErrorMessage = null;
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        ClientSettings candidate;
        HotkeyChord chord;
        try
        {
            candidate = BuildSettings();
            candidate.Validate();
            chord = HotkeyChord.Parse(candidate.FallbackHotkey);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            ErrorMessage = "设置值无效。";
            return false;
        }

        bool startupChanged = candidate.StartWithWindows != _persisted.StartWithWindows;
        if (startupChanged)
        {
            try
            {
                await _startup.SetEnabledAsync(candidate.StartWithWindows, cancellationToken);
            }
            catch
            {
                StartWithWindows = _persisted.StartWithWindows;
                ErrorMessage = "无法更新开机启动设置。";
                return false;
            }
        }

        try
        {
            await _store.SaveAsync(candidate, cancellationToken);
            await _retention.ApplyRetentionAsync(
                new ApplyRetentionRequestDto(
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    new RetentionPolicyDto(
                        candidate.MaxRegularItems,
                        candidate.MaxAgeDays,
                        candidate.MaxImageBytes)),
                cancellationToken);
            _shortcuts.Configure(candidate.InterceptWinV, chord);
            _persisted = candidate;
            _retentionPolicy?.Update(candidate);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (startupChanged)
            {
                try
                {
                    await _startup.SetEnabledAsync(_persisted.StartWithWindows, CancellationToken.None);
                }
                catch
                {
                }
                StartWithWindows = _persisted.StartWithWindows;
            }
            ErrorMessage = "无法保存或应用设置。";
            return false;
        }
    }

    private ClientSettings BuildSettings()
    {
        ulong? maxImageBytes = null;
        if (MaxImageGiBEnabled)
        {
            if (!double.IsFinite(MaxImageGiB) || MaxImageGiB <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxImageGiB));
            }
            maxImageBytes = checked((ulong)Math.Round(
                MaxImageGiB * ClientSettings.BytesPerGiB,
                MidpointRounding.AwayFromZero));
        }
        return new ClientSettings(
            MaxRegularItemsEnabled ? MaxRegularItems : null,
            MaxAgeDaysEnabled ? MaxAgeDays : null,
            maxImageBytes,
            InterceptWinV,
            FallbackHotkey,
            StartWithWindows,
            Theme);
    }

    private void Apply(ClientSettings settings)
    {
        MaxRegularItemsEnabled = settings.MaxRegularItems.HasValue;
        MaxRegularItems = settings.MaxRegularItems ?? 1000;
        MaxAgeDaysEnabled = settings.MaxAgeDays.HasValue;
        MaxAgeDays = settings.MaxAgeDays ?? 30;
        MaxImageGiBEnabled = settings.MaxImageBytes.HasValue;
        MaxImageGiB = settings.MaxImageBytes.HasValue
            ? settings.MaxImageBytes.Value / (double)ClientSettings.BytesPerGiB
            : 1;
        InterceptWinV = settings.InterceptWinV;
        FallbackHotkey = settings.FallbackHotkey;
        StartWithWindows = settings.StartWithWindows;
        Theme = settings.Theme;
    }
}
