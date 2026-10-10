namespace Client.Host.Hosting;

/// <summary>
/// Work that must happen BEFORE anything opens the database: after the host is built (configuration, logging and DI are ready) and
/// before the first hosted service starts (the platform's database initializer is the first one). Preparations run once per start, in
/// registration order. Example: putting a restored backup in place of the business database (MISS-04b).
///
/// A preparation handles its own expected failures (and reports them in plain words later); an exception it lets escape stops the start
/// with the usual plain start-up message (<see cref="StartupFailure"/>). It must not resolve services that open the database.
/// </summary>
public interface IStartupPreparation
{
    Task PrepareAsync(CancellationToken cancellationToken = default);
}
