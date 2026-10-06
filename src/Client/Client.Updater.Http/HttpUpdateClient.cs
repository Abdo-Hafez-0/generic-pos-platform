using System.Net.Http.Json;
using Client.Host.Hosting;
using Client.Updater.Application;
using Client.Updater.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Core.Results;
using Updates.Contracts;

namespace Client.Updater.Http;

/// <summary>
/// HTTP implementation of <see cref="IUpdateClient"/>. Network problems become failure responses
/// (<see cref="UpdateErrorCodes.ServerUnavailable"/>); they never throw into the updater and never affect the application.
/// Nothing received here is trusted: the updater verifies the signed manifest and package.
/// </summary>
public sealed class HttpUpdateClient(HttpClient httpClient) : IUpdateClient
{
    public const string CheckPath = "api/updates/check";

    public async Task<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(CheckPath, request, PackageManifestSerializer.Options, cancellationToken);
            UpdateCheckResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<UpdateCheckResponse>(PackageManifestSerializer.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                // not an update response (e.g. proxy error page)
            }

            return body ?? UpdateCheckResponse.Failure(UpdateErrorCodes.ServerUnavailable,
                $"The update server returned an unexpected response ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return UpdateCheckResponse.Failure(UpdateErrorCodes.ServerUnavailable, "The update server could not be reached. Local operation is not affected; try again later.");
        }
    }

    public async Task<Result> DownloadAsync(Guid packageId, Stream destination, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"api/updates/packages/{packageId}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Error.Failure(UpdateErrorCodes.DownloadFailed, $"The update server returned {(int)response.StatusCode}.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await source.CopyToAsync(destination, cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return Error.Failure(UpdateErrorCodes.ServerUnavailable, "The update could not be downloaded. Nothing was installed and local operation is not affected; try again later.");
        }
    }
}

public static class UpdateHttpExtensions
{
    /// <summary>Registers the HTTP transport. Updater:ServerBaseUrl must be HTTPS (plain HTTP only for loopback, for development).</summary>
    public static IServiceCollection AddUpdateHttpClient(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new UpdaterConfiguration();
        configuration.GetSection(UpdaterConfiguration.SectionName).Bind(config);

        services.AddHttpClient<IUpdateClient, HttpUpdateClient>(client =>
        {
            client.BaseAddress = ValidateBaseAddress(config.ServerBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        return services;
    }

    public static Uri? ValidateBaseAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return secure || loopbackHttp ? uri : null;
    }
}

/// <summary>IHostingModule for the HTTP update transport.</summary>
public sealed class UpdateHttpHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddUpdateHttpClient(context.Configuration);
}
