using System.Diagnostics;
using System.Windows;

namespace Client.Desktop.Shell;

/// <summary>Restarts the application (MISS-04d: a confirmed restore happens at the next start).</summary>
public interface IApplicationRestarter
{
    void Restart();
}

/// <summary>
/// Starts a new copy of this executable (same arguments, same environment) and closes this one. The new copy is told this process's id
/// (<see cref="RestartHandoff"/>) and waits for it to end before anything opens the database: two copies never hold the data at once,
/// and a pending restore finds the database free.
/// </summary>
public sealed class DesktopRestarter : IApplicationRestarter
{
    public void Restart()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application's own path is not known.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
            start.ArgumentList.Add(argument);
        start.Environment[RestartHandoff.PreviousProcessVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Process.Start(start);
        Application.Current.Shutdown(0);
    }
}

/// <summary>The new copy's side of a restart: wait (bounded) until the copy that started it has ended.</summary>
public static class RestartHandoff
{
    public const string PreviousProcessVariable = "GENERICPOS_RESTART_AFTER_PROCESS";

    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits for the previous copy when this process was started by a restart; returns at once otherwise. The variable is removed so a
    /// later restart of THIS copy is not confused by it. After <see cref="MaximumWait"/> the start continues anyway (a pending restore
    /// then finds the data in use, puts nothing in place and says so).
    /// </summary>
    public static void WaitForPreviousInstance()
    {
        var value = Environment.GetEnvironmentVariable(PreviousProcessVariable);
        if (string.IsNullOrWhiteSpace(value)) return;
        Environment.SetEnvironmentVariable(PreviousProcessVariable, null);

        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == Environment.ProcessId)
            return;

        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(MaximumWait);
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }
}
