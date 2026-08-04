using System.Security.Cryptography;
using System.Text;

namespace Clipboard.Windows.Platform;

internal sealed class ClipboardSuppression
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTimeOffset> _tokens = [];

    public ClipboardSuppression(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void RegisterText(string text) => Register("text", HashText(text));

    public void RegisterImage(ReadOnlySpan<byte> png) => Register("image", SHA256.HashData(png));

    public void DiscardText(string text) => Discard("text", HashText(text));

    public void DiscardImage(ReadOnlySpan<byte> png) => Discard("image", SHA256.HashData(png));

    public bool TryConsumeText(string text) => TryConsume("text", HashText(text));

    public bool TryConsumeImage(ReadOnlySpan<byte> png) =>
        TryConsume("image", SHA256.HashData(png));

    public void RegisterFileBundle(IEnumerable<string> paths) =>
        Register("file_bundle", HashFileBundle(paths));

    public void DiscardFileBundle(IEnumerable<string> paths) =>
        Discard("file_bundle", HashFileBundle(paths));

    public bool TryConsumeFileBundle(IEnumerable<string> paths) =>
        TryConsume("file_bundle", HashFileBundle(paths));

    private void Discard(string kind, ReadOnlySpan<byte> hash)
    {
        lock (_sync)
        {
            _tokens.Remove(TokenKey(kind, hash));
        }
    }

    private void Register(string kind, ReadOnlySpan<byte> hash)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            RemoveExpired(now);
            _tokens[TokenKey(kind, hash)] = now + DefaultLifetime;
        }
    }

    private bool TryConsume(string kind, ReadOnlySpan<byte> hash)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            RemoveExpired(now);
            string key = TokenKey(kind, hash);
            return _tokens.Remove(key);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in _tokens
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _tokens.Remove(key);
        }
    }

    private static byte[] HashText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        try
        {
            return SHA256.HashData(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static byte[] HashFileBundle(IEnumerable<string> paths)
    {
        string canonical = string.Join("\n", paths.Select(path => path.ToUpperInvariant()));
        byte[] utf8 = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return SHA256.HashData(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static string TokenKey(string kind, ReadOnlySpan<byte> hash) =>
        $"{kind}:{Convert.ToHexString(hash)}";
}
