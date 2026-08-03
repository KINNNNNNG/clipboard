using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

byte[] key = Enumerable.Repeat((byte)0x55, 32).ToArray();
string dataDirectory = Path.Combine(Path.GetTempPath(), $"clipboard-ffi-{Guid.NewGuid():N}");
Directory.CreateDirectory(dataDirectory);
nint handle = 0;

try
{
    byte[] pathBytes = Encoding.UTF8.GetBytes(dataDirectory);
    unsafe
    {
        fixed (byte* pathPointer = pathBytes)
        fixed (byte* keyPointer = key)
        {
            RequireOk(NativeMethods.clipboard_core_open(
                pathPointer,
                (nuint)pathBytes.Length,
                keyPointer,
                (nuint)key.Length,
                out handle));
        }
    }

    Execute(handle, """
        {"api_version":1,"type":"ingest_text","payload":{"text":"跨设备剪贴板","source_app":"ffi-smoke","captured_ms":10}}
        """);
    using JsonDocument result = JsonDocument.Parse(Execute(handle, """
        {"api_version":1,"type":"search","payload":{"pattern":"设备","mode":"substring"}}
        """));
    int count = result.RootElement.GetProperty("items").GetArrayLength();
    if (count != 1)
    {
        throw new InvalidOperationException($"Expected 1 result; found {count}.");
    }

    Console.WriteLine("FFI smoke test passed: 1 item found.");
}
finally
{
    if (handle != 0)
    {
        NativeMethods.clipboard_core_close(handle);
    }
    CryptographicOperations.ZeroMemory(key);
    if (Directory.Exists(dataDirectory))
    {
        Directory.Delete(dataDirectory, recursive: true);
    }
}

static unsafe string Execute(nint handle, string requestJson)
{
    byte[] request = Encoding.UTF8.GetBytes(requestJson);
    fixed (byte* requestPointer = request)
    {
        RequireOk(NativeMethods.clipboard_core_execute(
            handle,
            requestPointer,
            (nuint)request.Length,
            out CoreBuffer response));
        try
        {
            return Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>((void*)response.Pointer, checked((int)response.Length)));
        }
        finally
        {
            NativeMethods.clipboard_core_free_buffer(response);
        }
    }
}

static void RequireOk(CoreStatus status)
{
    if (status != CoreStatus.Ok)
    {
        throw new InvalidOperationException($"Core returned {status}.");
    }
}
