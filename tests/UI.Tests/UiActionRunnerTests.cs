using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Resources;

namespace UI.Tests;

/// <summary>FIX-01 decision 1: one DI scope per user action, disposed after it; unexpected failures become a plain message and are logged.</summary>
public sealed class UiActionRunnerTests
{
    private sealed class ScopedThing : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<UiActionRunner>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }

    private readonly RecordingLogger _logger = new();
    private readonly UiActionRunner _runner;

    public UiActionRunnerTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedThing>();
        _runner = new UiActionRunner(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).GetRequiredService<IServiceScopeFactory>(), _logger);
    }

    [Fact]
    public async Task Every_action_gets_its_own_scope_which_is_disposed_when_the_action_ends()
    {
        ScopedThing? first = null, again = null, second = null;

        await _runner.RunAsync((scope, _) =>
        {
            first = scope.Get<ScopedThing>();
            again = scope.Get<ScopedThing>();
            return Task.FromResult(Result.Success());
        });
        await _runner.RunAsync((scope, _) =>
        {
            second = scope.Get<ScopedThing>();
            return Task.FromResult(Result.Success());
        });

        Assert.Same(first, again);
        Assert.NotSame(first, second);
        Assert.True(first!.Disposed);
        Assert.True(second!.Disposed);
    }

    [Fact]
    public async Task A_business_failure_is_returned_unchanged()
    {
        var refused = Error.Validation("Thing.Invalid", "That is not allowed.");

        var result = await _runner.RunAsync<int>((_, _) => Task.FromResult(Result.Failure<int>(refused)));

        Assert.Equal(refused, result.Error);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task An_exception_becomes_the_plain_failure_and_the_details_go_to_the_log_only()
    {
        var boom = new InvalidOperationException("SQLite Error 5: 'database is locked' at C:\\secret\\path.db");

        var result = await _runner.RunAsync((_, _) => throw boom);

        Assert.True(result.IsFailure);
        Assert.Equal(UiErrors.OperationFailedCode, result.Error.Code);
        Assert.Equal(PresentationText.OperationFailed, result.Error.Description);
        Assert.DoesNotContain("SQLite", result.Error.Description);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);
    }

    [Fact]
    public async Task A_query_returns_its_value_or_the_plain_failure()
    {
        var value = await _runner.QueryAsync((_, _) => Task.FromResult(42));
        var failed = await _runner.QueryAsync<int>((_, _) => throw new TimeoutException());

        Assert.Equal(42, value.Value);
        Assert.Equal(UiErrors.OperationFailedCode, failed.Error.Code);
    }

    [Fact]
    public async Task A_missing_required_service_is_an_unexpected_failure_and_an_optional_one_is_null()
    {
        var missing = await _runner.QueryAsync((scope, _) => Task.FromResult(scope.Get<Uri>()));
        var optional = await _runner.QueryAsync((scope, _) => Task.FromResult(scope.Find<Uri>()));

        Assert.True(missing.IsFailure);
        Assert.Null(optional.Value);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_turned_into_a_failure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync((_, ct) => Task.FromException<Result>(new OperationCanceledException(ct)), cancelled.Token));
        Assert.Empty(_logger.Entries);
    }
}
