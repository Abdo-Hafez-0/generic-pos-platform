using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cloud.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cloud.Tests;

/// <summary>Captures log messages so a test can read, for example, the ephemeral development key a host logs at startup.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return _messages.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages) owner._messages.Add(formatter(state, exception));
        }
    }
}

internal static class ApiHosting
{
    /// <summary>Hosts <typeparamref name="T"/> in-process with the given settings (the same SQLite file and directories as a <see cref="CloudWorld"/>).</summary>
    public static WebApplicationFactory<T> Host<T>(
        IReadOnlyDictionary<string, string?> settings, string environment = "Development", CapturingLoggerProvider? logs = null,
        TimeProvider? clock = null) where T : class
        => new WebApplicationFactory<T>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);

            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
            if (logs is not null) b.ConfigureLogging(l => l.AddProvider(logs));
            if (clock is not null) b.ConfigureTestServices(s => s.AddSingleton(clock));
        });

    public static HttpClient WithBearer(this HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<T> Json<T>(this HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public static async Task<ApiError> Error(this HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiError>())!;
    }
}
