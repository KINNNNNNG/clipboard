using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clipboard.Windows.Core;

internal sealed class ClipboardCoreClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IClipboardCoreNative _native;
    private readonly CoreSafeHandle _handle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    private ClipboardCoreClient(IClipboardCoreNative native, nint handle)
    {
        _native = native;
        _handle = new CoreSafeHandle(native, handle);
    }

    public static ClipboardCoreClient Open(
        string dataDirectory,
        Guid vaultId,
        ReadOnlySpan<byte> vaultKey) =>
        Open(dataDirectory, vaultId, vaultKey, PInvokeClipboardCoreNative.Instance);

    internal static ClipboardCoreClient Open(
        string dataDirectory,
        Guid vaultId,
        ReadOnlySpan<byte> vaultKey,
        IClipboardCoreNative native)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(native);
        if (vaultKey.Length != 32)
        {
            throw new ArgumentException("Vault keys must contain exactly 32 bytes.", nameof(vaultKey));
        }

        byte[] directoryBytes = Encoding.UTF8.GetBytes(dataDirectory);
        byte[] keyBytes = vaultKey.ToArray();
        byte[] vaultIdBytes = new byte[16];
        if (!vaultId.TryWriteBytes(vaultIdBytes, bigEndian: true, out int bytesWritten)
            || bytesWritten != vaultIdBytes.Length)
        {
            throw new InvalidOperationException("Unable to encode the vault identifier.");
        }

        nint handle = 0;
        try
        {
            CoreStatus status = native.OpenV2(directoryBytes, keyBytes, vaultIdBytes, out handle);
            EnsureOk(status);
            if (handle == 0)
            {
                throw new ClipboardCoreException(CoreStatus.CoreError);
            }
            return new ClipboardCoreClient(native, handle);
        }
        catch
        {
            if (handle != 0)
            {
                native.Close(handle);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(directoryBytes);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(vaultIdBytes);
        }
    }

    public Task<SearchResponseDto> SearchAsync(
        SearchRequestDto request,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync<SearchResponseDto, SearchRequestDto>(
            "search",
            request,
            cancellationToken);

    public Task<MutationResponseDto> IngestTextAsync(
        IngestTextRequestDto request,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync<MutationResponseDto, IngestTextRequestDto>(
            "ingest_text",
            request,
            cancellationToken);

    public Task<MutationResponseDto> SetFavoriteAsync(
        SetFavoriteRequestDto request,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync<MutationResponseDto, SetFavoriteRequestDto>(
            "set_favorite",
            request,
            cancellationToken);

    public Task<MutationResponseDto> DeleteAsync(
        DeleteRequestDto request,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync<MutationResponseDto, DeleteRequestDto>(
            "delete",
            request,
            cancellationToken);

    public Task<RetentionResponseDto> ClearUnfavoriteAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync<RetentionResponseDto>(
            new CommandWithoutPayload(1, "clear_unfavorite"),
            _native.Execute,
            cancellationToken);

    public Task<RetentionResponseDto> ApplyRetentionAsync(
        ApplyRetentionRequestDto request,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync<RetentionResponseDto, ApplyRetentionRequestDto>(
            "apply_retention",
            request,
            cancellationToken);

    public Task<MutationResponseDto> IngestImageAsync(
        IngestImageRequestDto request,
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken = default) =>
        ExecuteImageAsync(request, png, cancellationToken);

    public Task<byte[]> ReadImageAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) =>
        ReadImageCoreAsync(itemId, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _gate.Wait();
        try
        {
            _handle.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private Task<TResponse> ExecuteCommandAsync<TResponse, TPayload>(
        string type,
        TPayload payload,
        CancellationToken cancellationToken) =>
        ExecuteJsonAsync<TResponse>(
            new CommandEnvelope<TPayload>(1, type, payload),
            _native.Execute,
            cancellationToken);

    private async Task<TResponse> ExecuteJsonAsync<TResponse>(
        object request,
        NativeJsonCall nativeCall,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        CoreBuffer response = default;
        try
        {
            EnsureOk(nativeCall(_handle.DangerousGetHandle(), requestBytes, out response));
            return Deserialize<TResponse>(response);
        }
        finally
        {
            if (response.Pointer != 0)
            {
                _native.FreeBuffer(response);
            }
            CryptographicOperations.ZeroMemory(requestBytes);
            _gate.Release();
        }
    }

    private async Task<MutationResponseDto> ExecuteImageAsync(
        IngestImageRequestDto request,
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(
            new ImageMetadataEnvelope(
                1,
                request.Width,
                request.Height,
                request.SourceApp,
                request.CapturedMs),
            JsonOptions);
        CoreBuffer response = default;
        try
        {
            EnsureOk(_native.IngestImage(
                _handle.DangerousGetHandle(),
                metadata,
                png.Span,
                out response));
            return Deserialize<MutationResponseDto>(response);
        }
        finally
        {
            if (response.Pointer != 0)
            {
                _native.FreeBuffer(response);
            }
            CryptographicOperations.ZeroMemory(metadata);
            _gate.Release();
        }
    }

    private async Task<byte[]> ReadImageCoreAsync(Guid itemId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] itemIdBytes = Encoding.UTF8.GetBytes(itemId.ToString("D"));
        CoreBuffer response = default;
        try
        {
            EnsureOk(_native.ReadImage(
                _handle.DangerousGetHandle(),
                itemIdBytes,
                out response));
            return CopyBuffer(response);
        }
        finally
        {
            if (response.Pointer != 0)
            {
                _native.FreeBuffer(response);
            }
            CryptographicOperations.ZeroMemory(itemIdBytes);
            _gate.Release();
        }
    }

    private static T Deserialize<T>(CoreBuffer buffer)
    {
        byte[] bytes = CopyBuffer(buffer);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new JsonException("Clipboard core returned an empty JSON value.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] CopyBuffer(CoreBuffer buffer)
    {
        if (buffer.Length > int.MaxValue)
        {
            throw new ClipboardCoreException(CoreStatus.CoreError);
        }
        int length = checked((int)buffer.Length);
        if (length == 0)
        {
            return [];
        }
        if (buffer.Pointer == 0)
        {
            throw new ClipboardCoreException(CoreStatus.CoreError);
        }
        byte[] bytes = new byte[length];
        Marshal.Copy(buffer.Pointer, bytes, 0, length);
        return bytes;
    }

    private static void EnsureOk(CoreStatus status)
    {
        if (status != CoreStatus.Ok)
        {
            throw new ClipboardCoreException(status);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private delegate CoreStatus NativeJsonCall(
        nint handle,
        ReadOnlySpan<byte> request,
        out CoreBuffer response);
}

internal sealed class CoreSafeHandle : SafeHandle
{
    private readonly IClipboardCoreNative _native;

    public CoreSafeHandle(IClipboardCoreNative native, nint handle)
        : base(0, ownsHandle: true)
    {
        _native = native;
        SetHandle(handle);
    }

    public override bool IsInvalid => handle is 0 or -1;

    protected override bool ReleaseHandle()
    {
        try
        {
            _native.Close(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
