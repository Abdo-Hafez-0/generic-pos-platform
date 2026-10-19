namespace Platform.Presentation.Screens;

/// <summary>
/// MISS-10b: asks the shell to open whichever screen handles <see cref="Kind"/> for the record <see cref="Id"/> (for example Sales history asks
/// for "the return of this sale" without knowing that the till handles it). The kinds are module-neutral words in <see cref="ScreenRequests"/>;
/// a screen declares the kinds it handles in <see cref="ScreenDescriptor.Handles"/> and its view model implements
/// <see cref="IScreenRequestHandler"/>.
/// </summary>
public sealed record ScreenRequest(string Kind, Guid Id);

/// <summary>The request kinds screens may handle (lower-case dot-separated words, like screen IDs).</summary>
public static class ScreenRequests
{
    /// <summary>Return items of a completed sale (the id is the sale's).</summary>
    public const string ReturnSale = "sale.return";
}

/// <summary>A view model whose screen handles requests (<see cref="ScreenDescriptor.Handles"/>): called after the screen is shown.</summary>
public interface IScreenRequestHandler
{
    Task OnRequestAsync(ScreenRequest request, CancellationToken cancellationToken = default);
}
