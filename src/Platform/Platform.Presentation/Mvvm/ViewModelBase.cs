using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Platform.Core.Results;
using Platform.Presentation.Resources;

namespace Platform.Presentation.Mvvm;

/// <summary>
/// Shared base of every module view model (FIX-01 decision 3: handwritten MVVM, no toolkit). Property notification, the busy flag,
/// the plain error/status messages, and commands that never let an exception escape into the UI thread.
///
/// Commands created through <see cref="Command(Func{Task}, Func{bool}?)"/> re-evaluate whether they can run whenever a property of the
/// view model changes, so a view model only has to raise its own properties.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    private readonly List<AsyncCommand> _commands = [];
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while a command (or <see cref="BusyAsync"/>) runs; commands do not start while it is set.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>The last failure in plain words, or null.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => Set(ref _errorMessage, value);
    }

    /// <summary>The last success message, or null.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        protected set => Set(ref _statusMessage, value);
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
    }

    /// <summary>Shows a business failure in plain words and clears the status; true when the result succeeded.</summary>
    protected bool Accept(Result result)
    {
        if (result.IsSuccess) return true;
        ErrorMessage = result.Error.Description;
        StatusMessage = null;
        return false;
    }

    /// <summary>A command that runs <paramref name="execute"/> as a busy section (see <see cref="BusyAsync"/>).</summary>
    protected ICommand Command(Func<Task> execute, Func<bool>? canExecute = null)
        => Register(new AsyncCommand(this, _ => execute(), canExecute is null ? null : _ => canExecute()));

    /// <summary>The same as <see cref="Command(Func{Task}, Func{bool}?)"/> with the command parameter.</summary>
    protected ICommand Command<TParameter>(Func<TParameter?, Task> execute, Func<TParameter?, bool>? canExecute = null)
        => Register(new AsyncCommand(this, p => execute(p is TParameter t ? t : default), canExecute is null ? null : p => canExecute(p is TParameter t ? t : default)));

    /// <summary>
    /// Runs <paramref name="work"/> while <see cref="IsBusy"/> is set, after clearing the previous error. Does nothing when already busy.
    /// An exception that escapes (actions should go through the runner, so this is a programming error) becomes the plain failure
    /// message instead of crashing the UI thread.
    /// </summary>
    protected async Task BusyAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work();
        }
        catch (Exception)
        {
            ErrorMessage = PresentationText.OperationFailed;
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AsyncCommand Register(AsyncCommand command)
    {
        _commands.Add(command);
        return command;
    }

    private sealed class AsyncCommand(ViewModelBase owner, Func<object?, Task> execute, Func<object?, bool>? canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !owner.IsBusy && (canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            await owner.BusyAsync(() => execute(parameter));
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
