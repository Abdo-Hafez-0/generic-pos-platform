using System.Net.Http.Headers;
using System.Text;
using Client.Host.Hosting;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Client.Fiscal.Http;

/// <summary>
/// MISS-11c-3: <see cref="IFiscalHttpTransport"/> over HTTPS: carries a country part's call to the tax authority (Egypt's ETA: the sign-in,
/// the batch submission, reading a submission back) and returns the answer as it came. Only HTTPS addresses are called (plain HTTP only on
/// this computer, for tests). No connection, a timeout or a refused address is an answer with status 0 and the reason - never an
/// exception. Nothing is logged here: the requests carry the till's secrets.
/// </summary>
public sealed class HttpFiscalTransport(HttpClient http) : IFiscalHttpTransport
{
    public const string ClientName = "fiscal";

    public async Task<FiscalHttpResponse> SendAsync(FiscalHttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            return new FiscalHttpResponse(0, "The tax authority's address must be HTTPS.");

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Bearer ", StringComparison.Ordinal))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value["Bearer ".Length..]);
            else
                message.Headers.TryAddWithoutValidation(name, value);
        }

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var retryAfter = response.Headers.RetryAfter is { } retry
                ? retry.Delta ?? (retry.Date is { } at ? at - DateTimeOffset.UtcNow : null)
                : null;
            return new FiscalHttpResponse((int)response.StatusCode, body, retryAfter);
        }
        catch (HttpRequestException ex)
        {
            return new FiscalHttpResponse(0, ex.HttpRequestError == HttpRequestError.NameResolutionError || ex.HttpRequestError == HttpRequestError.ConnectionError
                ? "no connection to the internet or to the tax authority"
                : ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FiscalHttpResponse(0, "the tax authority did not answer in time");
        }
    }
}

public static class FiscalHttpExtensions
{
    public static IServiceCollection AddFiscalHttpTransport(this IServiceCollection services)
    {
        services.AddHttpClient<IFiscalHttpTransport, HttpFiscalTransport>(HttpFiscalTransport.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
        return services;
    }
}

/// <summary>MISS-11c-3: plugs the tax authorities' HTTPS transport into the desktop (composition root).</summary>
public sealed class FiscalHttpHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddFiscalHttpTransport();
}
