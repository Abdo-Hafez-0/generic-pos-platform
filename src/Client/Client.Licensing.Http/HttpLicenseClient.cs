using System.Net.Http.Json;
using Client.Host.Hosting;
using Client.Licensing.Application;
using Client.Licensing.Infrastructure;
using Licensing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Client.Licensing.Http;

/// <summary>
/// HTTP implementation of <see cref="ILicenseClient"/>. Network problems become a failure response
/// (<see cref="LicenseErrorCodes.ServerUnreachable"/>) - they never throw into the licensing logic, and an unreachable
/// server never affects offline license evaluation. The signed license in a response is NOT trusted here;
/// LicenseService verifies it.
/// </summary>
public sealed class HttpLicenseClient(HttpClient httpClient) : ILicenseClient
{
    public const string ActivatePath = "api/licenses/activate";
    public const string RenewPath = "api/licenses/renew";

    public async Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        var (ok, body, code, message) = await PostAsync<ActivationRequest, ActivationResponse>(ActivatePath, request, cancellationToken);
        return ok && body is not null ? body : ActivationResponse.Failure(code, message);
    }

    public async Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
    {
        var (ok, body, code, message) = await PostAsync<RenewalRequest, RenewalResponse>(RenewPath, request, cancellationToken);
        return ok && body is not null ? body : RenewalResponse.Failure(code, message);
    }

    private async Task<(bool Ok, TResponse? Body, string Code, string Message)> PostAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(path, request, LicenseSerializer.Options, cancellationToken);

            // The server returns an Activation/RenewalResponse body for both success and business failures (4xx).
            TResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TResponse>(LicenseSerializer.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                // Not a licensing response (e.g. proxy error page).
            }

            if (body is not null)
                return (true, body, string.Empty, string.Empty);

            return (false, null, LicenseErrorCodes.ServerRejected,
                $"The license server returned an unexpected response ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return (false, null, LicenseErrorCodes.ServerUnreachable, "The license server could not be reached. Local operation is not affected; try again when the connection is back.");
        }
    }
}

public static class LicenseHttpExtensions
{
    /// <summary>
    /// Registers the HTTP transport. The base address comes from Licensing:ServerBaseUrl and must be HTTPS
    /// (plain HTTP is accepted only for loopback hosts, for local development).
    /// </summary>
    public static IServiceCollection AddLicenseHttpClient(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new LicensingConfiguration();
        configuration.GetSection(LicensingConfiguration.SectionName).Bind(config);

        services.AddHttpClient<ILicenseClient, HttpLicenseClient>(client =>
        {
            client.BaseAddress = ValidateBaseAddress(config.ServerBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        return services;
    }

    /// <summary>Returns the validated address, or null when unset (calls then fail as "unreachable" without throwing at startup).</summary>
    public static Uri? ValidateBaseAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return secure || loopbackHttp ? uri : null;
    }
}

/// <summary>IHostingModule for the HTTP license transport.</summary>
public sealed class LicenseHttpHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddLicenseHttpClient(context.Configuration);
}
