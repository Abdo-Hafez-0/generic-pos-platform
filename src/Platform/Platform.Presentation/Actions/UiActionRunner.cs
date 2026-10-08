using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Core.Results;
using Platform.Presentation.Resources;

namespace Platform.Presentation.Actions;

/// <summary>The services of ONE user action. Valid only inside the action that received it; never keep it.</summary>
public interface IActionScope
{
    /// <summary>A required service (for example an application handler) from this action's DI scope.</summary>
    T Get<T>() where T : notnull;

    /// <summary>An optional service (for example an optional module's contract), or null when it is not composed.</summary>
    T? Find<T>() where T : class;
}

/// <summary>
/// Runs one user action (a click, a scan, a screen load) in its OWN dependency-injection scope (FIX-01 decision 1).
///
/// Why: module contexts track what they read and an operation that failed before saving keeps its in-memory change until its scope
/// ends (Stage 12 decision 3, Stage 13 decision 3). A screen that held one scope all day (a POS screen) would read stale data and could
/// carry a failed change into the next action. A fresh scope per action makes every action start from the database.
///
/// It is also the one place where an UNEXPECTED failure (an exception, not a business <see cref="Result"/> failure) is logged in full and
/// turned into a plain sentence: the user never sees exception text, paths or SQL.
/// </summary>
public interface IUiActionRunner
{
    /// <summary>Runs an action that returns a <see cref="Result"/>; an exception becomes a failure with a plain message.</summary>
    Task<Result> RunAsync(Func<IActionScope, CancellationToken, Task<Result>> action, CancellationToken cancellationToken = default);

    /// <summary>Runs an action that returns a <see cref="Result{TValue}"/>; an exception becomes a failure with a plain message.</summary>
    Task<Result<T>> RunAsync<T>(Func<IActionScope, CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken = default);

    /// <summary>Runs an action that returns a plain value (a read, or a contract result type); an exception becomes a failure.</summary>
    Task<Result<T>> QueryAsync<T>(Func<IActionScope, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}

/// <summary>Error codes of the runner.</summary>
public static class UiErrors
{
    public const string OperationFailedCode = "UI.OperationFailed";

    public static Error OperationFailed() => Error.Failure(OperationFailedCode, PresentationText.OperationFailed);
}

/// <summary>Default <see cref="IUiActionRunner"/>: one async DI scope per action, disposed when the action ends.</summary>
public sealed class UiActionRunner(IServiceScopeFactory scopes, ILogger<UiActionRunner> logger) : IUiActionRunner
{
    public Task<Result> RunAsync(Func<IActionScope, CancellationToken, Task<Result>> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(action, static () => Result.Failure(UiErrors.OperationFailed()), cancellationToken);

    public Task<Result<T>> RunAsync<T>(Func<IActionScope, CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(action, static () => Result.Failure<T>(UiErrors.OperationFailed()), cancellationToken);

    public Task<Result<T>> QueryAsync<T>(Func<IActionScope, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (scope, ct) => Result.Success(await action(scope, ct)), static () => Result.Failure<T>(UiErrors.OperationFailed()), cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<IActionScope, CancellationToken, Task<TResult>> action,
        Func<TResult> failed,
        CancellationToken cancellationToken)
    {
        ActionScope? actionScope = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            actionScope = new ActionScope(scope.ServiceProvider);
            return await action(actionScope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A user action failed unexpectedly.");
            return failed();
        }
        finally
        {
            actionScope?.End();
        }
    }

    /// <summary>
    /// FIX-07: the services of one action, and of that action only. Once the action has ended the scope refuses to hand out anything, so a
    /// view model that kept it (and would read through contexts that still track yesterday's data) fails loudly instead of reading stale data.
    /// </summary>
    private sealed class ActionScope(IServiceProvider services) : IActionScope
    {
        private volatile bool _ended;

        public void End() => _ended = true;

        public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

        public T? Find<T>() where T : class => Services.GetService<T>();

        private IServiceProvider Services => _ended
            ? throw new InvalidOperationException("An action scope was used after its action ended. Run each user action through IUiActionRunner; never keep its scope or services.")
            : services;
    }
}
