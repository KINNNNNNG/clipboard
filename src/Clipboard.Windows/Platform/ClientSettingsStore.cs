using System.Security.Cryptography;
using System.Text.Json;

namespace Clipboard.Windows.Platform;

internal interface IClientSettingsStore
{
    Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        ClientSettings settings,
        CancellationToken cancellationToken = default);
}

internal sealed class ClientSettingsStore : IClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _path;

    public ClientSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipboard",
            "settings.json"))
    {
    }

    internal ClientSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return ClientSettings.Default;
        }
        byte[] bytes = await File.ReadAllBytesAsync(_path, cancellationToken);
        try
        {
            ClientSettings settings = JsonSerializer.Deserialize<ClientSettings>(bytes, JsonOptions)
                ?? throw new JsonException("Settings file contained an empty JSON value.");
            settings.Validate();
            return settings;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task SaveAsync(
        ClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
