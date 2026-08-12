using System.Collections.ObjectModel;
using Clipboard.Windows.Platform;

namespace Clipboard.Windows.ViewModels;

internal sealed class LogViewModel : ObservableObject
{
    private readonly IGlobalLog _log;
    private readonly Func<string, CancellationToken, Task<bool>> _saveLevel;
    private readonly List<string> _allLines = [];
    private string _searchText = string.Empty;
    private LogLevel? _minimumLevel;
    private string _selectedLevel;
    private string _storageSummary = string.Empty;

    public LogViewModel(
        IGlobalLog log,
        Func<string, CancellationToken, Task<bool>> saveLevel)
    {
        _log = log;
        _saveLevel = saveLevel;
        _selectedLevel = ToSettingsValue(log.Level);
    }

    public ObservableCollection<string> VisibleLines { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilters();
            }
        }
    }

    public LogLevel? MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                ApplyFilters();
            }
        }
    }

    public string SelectedLevel
    {
        get => _selectedLevel;
        private set => SetProperty(ref _selectedLevel, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => SetProperty(ref _storageSummary, value);
    }

    public string VisibleText => string.Join(Environment.NewLine, VisibleLines);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        LogSnapshot snapshot = await _log.ReadSnapshotAsync(cancellationToken);
        _allLines.Clear();
        _allLines.AddRange(snapshot.Lines);
        StorageSummary = $"{snapshot.FileCount} 个文件，{FormatBytes(snapshot.TotalBytes)}";
        ApplyFilters();
    }

    public async Task<bool> ChangeLevelAsync(
        string level,
        CancellationToken cancellationToken = default)
    {
        bool saved = await _saveLevel(level, cancellationToken);
        if (saved)
        {
            SelectedLevel = level;
        }
        return saved;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _log.ClearAsync(cancellationToken);
        _allLines.Clear();
        VisibleLines.Clear();
        StorageSummary = "0 个文件，0 B";
    }

    private void ApplyFilters()
    {
        IEnumerable<string> filtered = _allLines;
        if (MinimumLevel is LogLevel minimum)
        {
            filtered = filtered.Where(line => TryParseLevel(line, out LogLevel level) && level >= minimum);
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(line => line.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }
        VisibleLines.Clear();
        foreach (string line in filtered)
        {
            VisibleLines.Add(line);
        }
        OnPropertyChanged(nameof(VisibleText));
    }

    private static bool TryParseLevel(string line, out LogLevel level)
    {
        level = LogLevel.Trace;
        string[] fields = line.Split('|', 4);
        return fields.Length >= 2 && Enum.TryParse(fields[1], true, out level);
    }

    private static string ToSettingsValue(LogLevel level) => level.ToString().ToLowerInvariant();

    private static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024d:F1} KB",
        _ => $"{value / (1024d * 1024d):F1} MB",
    };
}
