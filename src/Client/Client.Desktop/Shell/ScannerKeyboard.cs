namespace Client.Desktop.Shell;

/// <summary>
/// Hands the key presses of the shell window to the keyboard-wedge scanner decoder (FIX-02). It decodes nothing and does not know which
/// scanner, if any, is behind it: the composition root gives it the decoder (a "None" scanner ignores every key).
///
/// Keys typed into a text box are NOT forwarded: there the scanner's "typing" lands in the box and its Enter runs the box's own action
/// (on the cashier screen the barcode box adds the product), so forwarding them as well would add the product twice. Everywhere else - a
/// grid, a button, nothing focused - the decoder sees the keys, and the Enter that ends a scan is swallowed so it cannot also press the
/// focused button (Checkout, for example).
/// </summary>
public sealed class ScannerKeyboard(Func<char, bool> decoder)
{
    /// <summary>A shell without scanner input (tests, or a composition without the hardware bridge).</summary>
    public static ScannerKeyboard None { get; } = new(_ => false);

    /// <summary>Text typed in the window; <paramref name="intoTextInput"/> when it goes into a text box.</summary>
    public void OnText(string text, bool intoTextInput)
    {
        if (intoTextInput) return;

        // Enter arrives through OnEnter (key down comes first); other control characters mean nothing to a scan.
        foreach (var character in text)
            if (!char.IsControl(character)) decoder(character);
    }

    /// <summary>The Enter key. True when it ended a scan: the window then marks the key handled.</summary>
    public bool OnEnter(bool intoTextInput) => !intoTextInput && decoder('\r');
}
