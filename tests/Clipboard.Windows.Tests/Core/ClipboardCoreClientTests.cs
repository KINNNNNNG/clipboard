using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Clipboard.Windows.Core;
using Xunit;

namespace Clipboard.Windows.Tests.Core;

public sealed class ClipboardCoreClientTests
{
    [Fact]
    public async Task Native_client_round_trips_text_through_open_v2()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"clipboard-core-client-{Guid.NewGuid():N}");
        byte[] key = Enumerable.Repeat((byte)0x52, 32).ToArray();
        try
        {
            using var client = ClipboardCoreClient.Open(directory, Guid.NewGuid(), key);
            await client.IngestTextAsync(new IngestTextRequestDto(
                "native round trip",
                "xunit.exe",
                100));

            SearchResponseDto result = await client.SearchAsync(new SearchRequestDto(
                "round",
                SearchModeDto.Substring,
                SearchFiltersDto.Empty));

            ClipboardItemDto item = Assert.Single(result.Items);
            Assert.Equal("native round trip", item.Preview);
            Assert.Equal("xunit.exe", item.SourceApp);

            byte[] png = Convert.FromHexString(
                "89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C489" +
                "0000000D4944415408D763F8CFC0F01F00050001FF89993D1D0000000049454E44" +
                "AE426082");
            MutationResponseDto image = await client.IngestImageAsync(
                new IngestImageRequestDto(1, 1, "xunit.exe", 200),
                png);
            Assert.Equal(png, await client.ReadImageAsync(image.ItemId));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Open_uses_v2_and_commands_serialize_with_snake_case()
    {
        var native = new FakeNative
        {
            ExecuteResponse = Encoding.UTF8.GetBytes("{\"items\":[]}"),
        };
        byte[] key = Enumerable.Repeat((byte)0x42, 32).ToArray();
        Guid vaultId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        using var client = ClipboardCoreClient.Open("C:\\clipboard-data", vaultId, key, native);

        await client.SearchAsync(new SearchRequestDto(
            "clip",
            SearchModeDto.Regex,
            new SearchFiltersDto(10, 20, ["notepad.exe"], ["text"])));

        Assert.Equal(1, native.OpenV2Count);
        Assert.Equal(vaultId, new Guid(native.OpenVaultId, bigEndian: true));
        using JsonDocument request = JsonDocument.Parse(native.LastExecuteRequest!);
        JsonElement root = request.RootElement;
        Assert.Equal(1, root.GetProperty("api_version").GetInt32());
        Assert.Equal("search", root.GetProperty("type").GetString());
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal("regex", payload.GetProperty("mode").GetString());
        Assert.Equal(10, payload.GetProperty("filters").GetProperty("created_after_ms").GetInt64());
        Assert.Equal("notepad.exe", payload.GetProperty("filters").GetProperty("source_apps")[0].GetString());
    }

    [Fact]
    public void Dispose_closes_native_handle_only_once()
    {
        var native = new FakeNative();
        byte[] key = new byte[32];
        var client = ClipboardCoreClient.Open("C:\\clipboard-data", Guid.NewGuid(), key, native);

        client.Dispose();
        client.Dispose();

        Assert.Equal(1, native.CloseCount);
    }

    [Fact]
    public async Task Response_buffer_is_freed_when_json_is_invalid()
    {
        var native = new FakeNative
        {
            ExecuteResponse = "{"u8.ToArray(),
        };
        using var client = ClipboardCoreClient.Open(
            "C:\\clipboard-data",
            Guid.NewGuid(),
            new byte[32],
            native);

        await Assert.ThrowsAsync<JsonException>(() =>
            client.SearchAsync(new SearchRequestDto(
                "x",
                SearchModeDto.Substring,
                SearchFiltersDto.Empty)));

        Assert.Equal(1, native.FreeCount);
    }

    [Fact]
    public async Task Native_errors_do_not_include_clipboard_content()
    {
        var native = new FakeNative
        {
            ExecuteStatus = CoreStatus.CoreError,
        };
        using var client = ClipboardCoreClient.Open(
            "C:\\clipboard-data",
            Guid.NewGuid(),
            new byte[32],
            native);
        const string secret = "sensitive clipboard body";

        ClipboardCoreException error = await Assert.ThrowsAsync<ClipboardCoreException>(() =>
            client.IngestTextAsync(new IngestTextRequestDto(secret, "notepad.exe", 100)));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Equal(CoreStatus.CoreError, error.Status);
    }

    private sealed class FakeNative : IClipboardCoreNative
    {
        public int OpenV2Count { get; private set; }
        public int CloseCount { get; private set; }
        public int FreeCount { get; private set; }
        public byte[] OpenVaultId { get; private set; } = [];
        public byte[]? LastExecuteRequest { get; private set; }
        public CoreStatus ExecuteStatus { get; init; } = CoreStatus.Ok;
        public byte[] ExecuteResponse { get; init; } = Encoding.UTF8.GetBytes(
            "{\"item_id\":\"00000000-0000-0000-0000-000000000001\"}");

        public CoreStatus OpenV2(
            ReadOnlySpan<byte> dataDirectory,
            ReadOnlySpan<byte> vaultKey,
            ReadOnlySpan<byte> vaultId,
            out nint handle)
        {
            OpenV2Count++;
            OpenVaultId = vaultId.ToArray();
            handle = 123;
            return CoreStatus.Ok;
        }

        public CoreStatus Execute(nint handle, ReadOnlySpan<byte> request, out CoreBuffer response)
        {
            LastExecuteRequest = request.ToArray();
            response = Allocate(ExecuteResponse);
            return ExecuteStatus;
        }

        public CoreStatus IngestImage(
            nint handle,
            ReadOnlySpan<byte> metadata,
            ReadOnlySpan<byte> png,
            out CoreBuffer response)
        {
            response = Allocate(ExecuteResponse);
            return CoreStatus.Ok;
        }

        public CoreStatus ReadImage(
            nint handle,
            ReadOnlySpan<byte> itemId,
            out CoreBuffer response)
        {
            response = Allocate([]);
            return CoreStatus.Ok;
        }

        public void FreeBuffer(CoreBuffer buffer)
        {
            FreeCount++;
            if (buffer.Pointer != 0)
            {
                Marshal.FreeHGlobal(buffer.Pointer);
            }
        }

        public void Close(nint handle)
        {
            CloseCount++;
        }

        private static CoreBuffer Allocate(byte[] bytes)
        {
            nint pointer = bytes.Length == 0 ? 0 : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            return new CoreBuffer(pointer, (nuint)bytes.Length, (nuint)bytes.Length);
        }
    }
}
