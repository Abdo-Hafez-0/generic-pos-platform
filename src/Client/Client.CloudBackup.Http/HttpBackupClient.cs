using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Host.Hosting;
using CloudBackup.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Core.Results;

namespace Client.CloudBackup.Http;

/// <summary>
/// <see cref="IBackupClient"/> over HTTPS to BackupServer.Api (Stage 9): <c>Authorization: Bearer &lt;access key&gt;</c>;
/// POST/GET <c>api/backups</c>, GET <c>api/backups/{id}/content</c>, DELETE <c>api/backups/{id}</c>. The wire shape is mirrored here (clients
/// never reference server projects). Every network, timeout and server problem becomes a plain result; the access key is never logged.
/// </summary>
public sealed class HttpBackupClient(HttpClient http) : IBackupClient
{
    public const string BackupsPath = "api/backups";
    public const string Sha256Header = "X-Backup-Sha256";
    public const string ClientVersionHeader = "X-Client-Version";

    /// <summary>BackupServer.Api's BackupDto, as far as the client needs it.</summary>
    private sealed record WireBackup(Guid BackupId, DateTimeOffset CreatedAt, long SizeBytes, string Sha256, string? Label);

    private sealed record WireError(string Code, string Message);

    public async Task<Result<CloudBackupItem>> UploadAsync(string access, Stream content, string sha256, string label, string clientVersion, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, $"{BackupsPath}?label={Uri.EscapeDataString(label)}", access);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(Sha256Header, sha256);
        request.Headers.Add(ClientVersionHeader, clientVersion);
        return await SendAsync(request, async r => ToItem((await r.Content.ReadFromJsonAsync<WireBackup>(cancellationToken))!), cancellationToken);
    }

    public async Task<Result<IReadOnlyList<CloudBackupItem>>> ListAsync(string access, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, BackupsPath, access);
        return await SendAsync<IReadOnlyList<CloudBackupItem>>(request,
            async r => (await r.Content.ReadFromJsonAsync<List<WireBackup>>(cancellationToken) ?? []).Select(ToItem).ToList(), cancellationToken);
    }

    public async Task<Result<Stream>> DownloadAsync(string access, Guid id, CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Get, $"{BackupsPath}/{id}/content", access);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await FailureAsync(response, cancellationToken);
                response.Dispose();
                return Result.Failure<Stream>(failure);
            }

            return Result.Success<Stream>(new OwningStream(await response.Content.ReadAsStreamAsync(cancellationToken), response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<Stream>(Unreachable());
        }
        finally
        {
            request.Dispose();
        }
    }

    public async Task<Result> DeleteAsync(string access, Guid id, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Delete, $"{BackupsPath}/{id}", access);
        var result = await SendAsync(request, _ => Task.FromResult(true), cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string access)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }

    private async Task<Result<T>> SendAsync<T>(HttpRequestMessage request, Func<HttpResponseMessage, Task<T>> read, CancellationToken cancellationToken)
    {
        if (http.BaseAddress is null) return Result.Failure<T>(NotConfigured());
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return Result.Failure<T>(await FailureAsync(response, cancellationToken));
            return Result.Success(await read(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<T>(Unreachable());
        }
    }

    private static CloudBackupItem ToItem(WireBackup b) => new(b.BackupId, b.CreatedAt, b.SizeBytes, b.Sha256, b.Label);

    private static async Task<Error> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? serverMessage = null;
        try
        {
            serverMessage = (await response.Content.ReadFromJsonAsync<WireError>(cancellationToken))?.Message;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            // not the server's error shape (e.g. a proxy page)
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Error.Unauthorized(BackupClientErrorCodes.Refused,
                "The backup server refused this shop's access key or license. Check the access key, or ask the vendor whether cloud backup is included."),
            HttpStatusCode.RequestEntityTooLarge => Error.Validation(BackupClientErrorCodes.TooLarge, "The backup is larger than the backup server accepts."),
            HttpStatusCode.NotFound => Error.NotFound(BackupClientErrorCodes.NotFound, "The backup server has no such backup."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError
                => Unreachable(),
            _ => Error.Failure(BackupClientErrorCodes.Failed, $"The backup server did not accept the request ({(int)response.StatusCode}){(serverMessage is null ? "" : ": " + serverMessage)}."),
        };
    }

    private static Error Unreachable() => Error.Failure(BackupClientErrorCodes.Unavailable,
        "The backup server cannot be reached right now. The local backups are not affected; the copy is sent later.");

    private static Error NotConfigured() => Error.Failure(BackupClientErrorCodes.Unavailable, "No backup server is configured. The local backups are not affected.");

    /// <summary>Keeps the response alive as long as its content stream is read.</summary>
    private sealed class OwningStream(Stream inner, HttpResponseMessage owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public static class CloudBackupHttpExtensions
{
    /// <summary>Registers the transport. CloudBackup:ServerBaseUrl must be HTTPS (plain HTTP only for loopback, for development).</summary>
    public static IServiceCollection AddCloudBackupHttpClient(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration.GetSection("CloudBackup")["ServerBaseUrl"];
        services.AddHttpClient<IBackupClient, HttpBackupClient>(client =>
        {
            client.BaseAddress = ValidateBaseAddress(url);
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        return services;
    }

    public static Uri? ValidateBaseAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return secure || loopbackHttp ? uri : null;
    }
}

public sealed class CloudBackupHttpHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddCloudBackupHttpClient(context.Configuration);
}
