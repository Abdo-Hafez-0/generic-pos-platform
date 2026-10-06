using System.Data.Common;
using Platform.Application.Modules;

namespace Client.Host.Hosting;

/// <summary>
/// Turns an exception that stopped the application from starting into the sentence the user sees. Exception text (database errors, file
/// paths, connection details) never reaches the screen: it is for the log. The wording says what is true in every case - nothing was
/// deleted or overwritten - and what the user can do next.
/// </summary>
public static class StartupFailure
{
    public const string Database =
        "The local database could not be opened. Nothing was changed or deleted. Close any other copy of the program and try again; " +
        "if the problem continues, restore the latest backup or contact support.";

    public const string Files =
        "A file or folder the application needs could not be accessed. Nothing was changed or deleted. Check that the disk is available " +
        "and that you are allowed to use the application's folder, then try again.";

    public const string Modules =
        "The installed modules do not fit together, so the application was not started. Nothing was changed or deleted. Reinstall the " +
        "application or contact support.";

    public const string Unknown =
        "The application could not start. Nothing was changed or deleted. Try again; if the problem continues, contact support.";

    public static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ModuleCompositionException modules) return $"{Modules} ({modules.Message})";
            if (current is DbException) return Database;
            if (current is IOException or UnauthorizedAccessException) return Files;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var described = Describe(inner);
                    if (described != Unknown) return described;
                }
        }

        return Unknown;
    }
}
