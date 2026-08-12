using System.Text;
using System.Threading.Channels;

namespace Clipboard.Windows.Platform;

internal enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

internal sealed record LogSnapshot(
    IReadOnlyList<string> Lines,
    long TotalBytes,
    int FileCount,
    DateTimeOffset? LastWriteUtc)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}

internal interface IGlobalLog : IAsyncDisposable
{
    LogLevel Level { get; }

    void ApplySettings(LoggingSettings settings);

    void Write(
        LogLevel level,
        string component,
        string eventName,
        IReadOnlyDictionary<string, string>? fields = null);

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task<LogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);

    Task CleanAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed class FileGlobalLog : IGlobalLog
{
    private const int QueueCapacity = 2048;
    private const int MaxSnapshotBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "provider",
        "phase",
        "status",
        "duration_ms",
        "count",
        "error_category",
        "error_code",
        "error_detail",
        "error_operation",
        "operation",
        "command",
    };

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<LogWork> _queue = Channel.CreateBounded<LogWork>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Task _worker;
    private LoggingSettings _settings = LoggingSettings.Default;
    private int _level = (int)LogLevel.Info;
    private long _dropped;
    private bool _disposed;

    public FileGlobalLog(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
        _worker = Task.Run(ProcessQueueAsync);
    }

    public LogLevel Level => (LogLevel)Volatile.Read(ref _level);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void ApplySettings(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
        Volatile.Write(ref _level, (int)ParseLevel(settings.Level));
        _ = CleanAsync();
    }

    public void Write(
        LogLevel level,
        string component,
        string eventName,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        if (_disposed || level < Level)
        {
            return;
        }

        LogEntry entry = new(
            _timeProvider.GetUtcNow(),
            level,
            SanitizeToken(component),
            SanitizeToken(eventName),
            SanitizeFields(fields));
        if (!_queue.Writer.TryWrite(new LogWork(entry, null)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new LogWork(null, completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task<LogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        string[] files = GetLogFiles()
            .OrderByDescending(static file => file, StringComparer.Ordinal)
            .ToArray();
        var lines = new List<string>();
        long totalBytes = 0;
        DateTimeOffset? lastWrite = null;
        foreach (string file in files)
        {
            FileInfo info = new(file);
            totalBytes += info.Length;
            DateTimeOffset write = info.LastWriteTimeUtc;
            lastWrite = lastWrite is null || write > lastWrite ? write : lastWrite;
            if (lines.Sum(static line => line.Length) >= MaxSnapshotBytes)
            {
                break;
            }
            string text = await File.ReadAllTextAsync(file, cancellationToken);
            lines.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        if (lines.Sum(static line => line.Length) > MaxSnapshotBytes)
        {
            lines = lines.TakeLast(MaxSnapshotBytes / 80).ToList();
        }
        return new LogSnapshot(lines, totalBytes, files.Length, lastWrite);
    }

    public Task CleanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime today = _timeProvider.GetUtcNow().UtcDateTime.Date;
        DateTime cutoff = today.AddDays(-(_settings.RetentionDays - 1));
        List<FileInfo> files = GetLogFiles().Select(static file => new FileInfo(file)).ToList();
        foreach (FileInfo file in files.Where(file => file.LastWriteTimeUtc.Date < cutoff).ToList())
        {
            file.Delete();
        }
        files = files.Where(static file => file.Exists).ToList();
        long total = files.Sum(static file => file.Exists ? file.Length : 0);
        foreach (FileInfo file in files.OrderBy(static file => file.LastWriteTimeUtc))
        {
            if (total <= (long)_settings.MaxSizeBytes)
            {
                break;
            }
            total -= file.Exists ? file.Length : 0;
            file.Delete();
        }
        return Task.CompletedTask;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        foreach (string file in GetLogFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _queue.Writer.TryComplete();
        await _worker;
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (LogWork work in _queue.Reader.ReadAllAsync())
        {
            if (work.Entry is not null)
            {
                string file = Path.Combine(
                    _directory,
                    $"clipboard-{work.Entry.TimestampUtc:yyyy-MM-dd}.log");
                Directory.CreateDirectory(_directory);
                await File.AppendAllTextAsync(file, work.Entry.Format() + Environment.NewLine);
            }
            work.Completion?.TrySetResult();
        }
    }

    private string[] GetLogFiles() => Directory.Exists(_directory)
        ? Directory.GetFiles(_directory, "clipboard-*.log")
        : [];

    private static LogLevel ParseLevel(string value) => value switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string SanitizeToken(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-').ToArray());

    private static IReadOnlyDictionary<string, string> SanitizeFields(
        IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null)
        {
            return new Dictionary<string, string>();
        }
        return fields
            .Where(pair => AllowedFields.Contains(pair.Key))
            .Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                SanitizeToken(pair.Value)[..Math.Min(128, SanitizeToken(pair.Value).Length)]))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    private sealed record LogWork(LogEntry? Entry, TaskCompletionSource? Completion);

    private sealed record LogEntry(
        DateTimeOffset TimestampUtc,
        LogLevel Level,
        string Component,
        string EventName,
        IReadOnlyDictionary<string, string> Fields)
    {
        public string Format() => string.Join(
            '|',
            TimestampUtc.ToString("O"),
            Level.ToString().ToUpperInvariant(),
            Component,
            EventName,
            string.Join(';', Fields.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}={pair.Value}")));
    }
}
