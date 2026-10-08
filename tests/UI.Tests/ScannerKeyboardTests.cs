using Client.Desktop.Shell;

namespace UI.Tests;

/// <summary>FIX-02: which key presses of the shell window reach the barcode scanner decoder, and which Enter is swallowed.</summary>
public sealed class ScannerKeyboardTests
{
    private readonly List<char> _decoded = [];
    private bool _enterEndsScan;
    private readonly ScannerKeyboard _keyboard;

    public ScannerKeyboardTests()
    {
        _keyboard = new ScannerKeyboard(c =>
        {
            _decoded.Add(c);
            return c == '\r' && _enterEndsScan;
        });
    }

    [Fact]
    public void Keys_outside_a_text_box_reach_the_decoder()
    {
        _keyboard.OnText("6001", intoTextInput: false);
        _keyboard.OnText("2", intoTextInput: false);

        Assert.Equal("60012", new string([.. _decoded]));
    }

    [Fact]
    public void Keys_typed_into_a_text_box_do_not_reach_the_decoder_so_a_scan_there_is_added_once()
    {
        _enterEndsScan = true;

        _keyboard.OnText("6001234567890", intoTextInput: true);

        Assert.False(_keyboard.OnEnter(intoTextInput: true));     // the box's own Enter action runs
        Assert.Empty(_decoded);
    }

    [Fact]
    public void Control_characters_in_text_are_not_forwarded_enter_comes_through_its_key()
    {
        _keyboard.OnText("\r\b\t", intoTextInput: false);

        Assert.Empty(_decoded);
    }

    [Fact]
    public void The_enter_that_ends_a_scan_is_swallowed_and_an_ordinary_enter_is_not()
    {
        Assert.False(_keyboard.OnEnter(intoTextInput: false));    // e.g. a person pressing Enter on a focused button: the button works

        _enterEndsScan = true;
        Assert.True(_keyboard.OnEnter(intoTextInput: false));     // the scan's Enter must not press Checkout

        Assert.Equal(['\r', '\r'], _decoded);
    }

    [Fact]
    public void Without_a_scanner_nothing_is_ever_swallowed()
    {
        ScannerKeyboard.None.OnText("6001234567890", intoTextInput: false);

        Assert.False(ScannerKeyboard.None.OnEnter(intoTextInput: false));
    }
}
