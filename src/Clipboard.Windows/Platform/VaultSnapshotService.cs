using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

/// <summary>One verified local snapshot of the vault.</summary>
internal sealed record VaultSnapshot(string Directory, long CreatedMs, int FileCount, long TotalBytes);

internal interface IVaultSnapshotService
{
    /// <summary>Writes a verified snapshot, or returns null when the core refused it.</summary>
    VaultSnapshot? Create(VaultMaterial vault, string snapshotRoot, int keep);

    /// <summary>Returns verified snapshots newest first; damaged ones are omitted.</summary>
    IReadOnlyList<VaultSnapshot> List(VaultMaterial vault, string snapshotRoot);

    /// <summary>Restores one verified snapshot over the vault data directory.</summary>
    bool Restore(VaultMaterial vault, string snapshotDirectory, long createdMs);
}

internal sealed class VaultSnapshotService : IVaultSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IClipboardSnapshotNative _native;
    private readonly TimeProvider _timeProvider;

    public VaultSnapshotService()
        : this(PInvokeClipboardCoreNative.Instance, TimeProvider.System)
    {
    }

    internal VaultSnapshotService(IClipboardSnapshotNative native, TimeProvider timeProvider)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public VaultSnapshot? Create(VaultMaterial vault, string snapshotRoot, int keep)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        CoreBuffer buffer = default;
        try
        {
            CoreStatus status = _native.SnapshotCreate(
                Encoding.UTF8.GetBytes(vault.DataDirectory),
                EncodeVaultId(vault.VaultId),
                vault.VaultKey,
                Encoding.UTF8.GetBytes(snapshotRoot),
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                (nuint)keep,
                out buffer);
            if (status != CoreStatus.Ok || Parse<SnapshotDto>(buffer) is not { } created)
            {
                return null;
            }
            return ToSnapshot(created);
        }
        finally
        {
            Free(buffer);
        }
    }

    public IReadOnlyList<VaultSnapshot> List(VaultMaterial vault, string snapshotRoot)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        CoreBuffer buffer = default;
        try
        {
            CoreStatus status = _native.SnapshotList(
                Encoding.UTF8.GetBytes(snapshotRoot),
                EncodeVaultId(vault.VaultId),
                vault.VaultKey,
                out buffer);
            SnapshotListDto? payload = status == CoreStatus.Ok
                ? Parse<SnapshotListDto>(buffer)
                : null;
            return payload is null
                ? []
                : [.. payload.Snapshots.Select(ToSnapshot)];
        }
        finally
        {
            Free(buffer);
        }
    }

    public bool Restore(VaultMaterial vault, string snapshotDirectory, long createdMs)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        CoreBuffer buffer = default;
        try
        {
            CoreStatus status = _native.SnapshotRestore(
                Encoding.UTF8.GetBytes(snapshotDirectory),
                Encoding.UTF8.GetBytes(vault.DataDirectory),
                EncodeVaultId(vault.VaultId),
                vault.VaultKey,
                createdMs,
                out buffer);
            return status == CoreStatus.Ok && Parse<SnapshotDto>(buffer) is not null;
        }
        finally
        {
            Free(buffer);
        }
    }

    private static VaultSnapshot ToSnapshot(SnapshotDto dto) =>
        new(dto.Directory, dto.CreatedMs, dto.FileCount, dto.TotalBytes);

    private static byte[] EncodeVaultId(Guid vaultId)
    {
        byte[] bytes = new byte[16];
        if (!vaultId.TryWriteBytes(bytes, bigEndian: true, out int written)
            || written != bytes.Length)
        {
            throw new InvalidOperationException("Unable to encode the vault identifier.");
        }
        return bytes;
    }

    private static T? Parse<T>(CoreBuffer buffer)
    {
        if (buffer.Pointer == 0 || buffer.Length == 0 || buffer.Length > int.MaxValue)
        {
            return default;
        }
        byte[] bytes = new byte[checked((int)buffer.Length)];
        Marshal.Copy(buffer.Pointer, bytes, 0, bytes.Length);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void Free(CoreBuffer buffer)
    {
        if (buffer.Pointer != 0)
        {
            PInvokeClipboardCoreNative.Instance.FreeBuffer(buffer);
        }
    }

    private sealed record SnapshotDto(string Directory, long CreatedMs, int FileCount, long TotalBytes);

    private sealed record SnapshotListDto(IReadOnlyList<SnapshotDto> Snapshots);
}
