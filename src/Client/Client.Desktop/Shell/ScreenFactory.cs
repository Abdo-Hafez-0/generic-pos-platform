using Microsoft.Extensions.DependencyInjection;
using Platform.Presentation.Screens;
using System.Windows;

namespace Client.Desktop.Shell;

/// <summary>An open screen: its view and the view model bound to it.</summary>
public sealed record ScreenInstance(object View, object ViewModel);

/// <summary>Creates the view and view model of a declared screen.</summary>
public interface IScreenFactory
{
    ScreenInstance Create(ScreenDescriptor screen);
}

/// <summary>
/// Creates WPF screens: the view model from the APPLICATION services (singletons only - scoped services are reached per action through
/// IUiActionRunner; UI.Tests checks every declared view model against the real composition) and the view with its parameterless
/// constructor, bound through DataContext.
/// </summary>
public sealed class WpfScreenFactory(IServiceProvider services) : IScreenFactory
{
    public ScreenInstance Create(ScreenDescriptor screen)
    {
        var viewModel = ActivatorUtilities.CreateInstance(services, screen.ViewModelType);
        if (Activator.CreateInstance(screen.ViewType) is not FrameworkElement view)
            throw new InvalidOperationException($"The view of screen '{screen.Id}' ({screen.ViewType.Name}) is not a WPF element.");

        view.DataContext = viewModel;
        return new ScreenInstance(view, viewModel);
    }
}
