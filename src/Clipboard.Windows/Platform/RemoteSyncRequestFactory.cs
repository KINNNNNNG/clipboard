using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal static class RemoteSyncRequestFactory
{
    public static async Task<RemoteConfigDto> CreateAsync(
        SyncSettings sync,
        ISyncCredentialStore credentials,
        string? accountOverride = null,
        string? secretOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(credentials);
        sync.Validate();

        SyncCredentials? saved = await credentials.LoadAsync(sync.CredentialProfileId!, cancellationToken);
        string account = string.IsNullOrWhiteSpace(accountOverride) ? saved?.Account ?? string.Empty : accountOverride;
        string secret = string.IsNullOrWhiteSpace(secretOverride) ? saved?.Secret ?? string.Empty : secretOverride;

        return sync.Provider == "oss"
            ? CreateOss(sync, account, secret)
            : CreateWebDav(sync, account, secret);
    }

    private static RemoteConfigDto CreateOss(SyncSettings sync, string account, string secret) => new(
        "oss",
        1,
        sync.Endpoint,
        Region: sync.Region,
        Bucket: sync.Bucket,
        Prefix: sync.Prefix,
        AccessKeyId: account,
        AccessKeySecret: secret);

    private static RemoteConfigDto CreateWebDav(SyncSettings sync, string account, string secret)
    {
        Uri endpoint = new(sync.Endpoint.TrimEnd('/') + "/");
        Uri remoteEndpoint = new(endpoint, sync.RootPath!.TrimStart('/'));
        if (remoteEndpoint.Scheme != endpoint.Scheme ||
            remoteEndpoint.Host != endpoint.Host ||
            remoteEndpoint.Port != endpoint.Port)
        {
            throw new InvalidOperationException();
        }

        return new RemoteConfigDto(
            "webdav",
            1,
            remoteEndpoint.ToString(),
            Username: account,
            Password: secret);
    }
}
