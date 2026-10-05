using Client.Hardware.Scanner;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class KeyboardWedgeScannerTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<BarcodeScan> _scans = [];

    private async Task<KeyboardWedgeBarcodeScanner> StartedAsync(int minLength = 4, int maxGapMs = 80)
    {
        var scanner = new KeyboardWedgeBarcodeScanner(minLength, maxGapMs, _clock);
        scanner.BarcodeScanned += (_, e) => _scans.Add(e.Scan);
        Assert.True((await scanner.StartAsync()).IsSuccess);
        return scanner;
    }

    private void Type(KeyboardWedgeBarcodeScanner s, string text, int gapMs = 5)
    {
        foreach (var c in text)
        {
            s.OnCharacter(c);
            _clock.Advance(TimeSpan.FromMilliseconds(gapMs));
        }
    }

    [Fact]
    public async Task FastCharacters_EndedByEnter_AreOneScan()
    {
        var s = await StartedAsync();

        Type(s, "6001234567890");
        s.OnCharacter('\r');

        var scan = Assert.Single(_scans);
        Assert.Equal("6001234567890", scan.Code);
        Assert.Equal(_clock.GetUtcNow(), scan.ScannedAt);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public async Task EitherLineEnding_EndsAScan(char terminator)
    {
        var s = await StartedAsync();

        Type(s, "ABCD1234");
        s.OnCharacter(terminator);

        Assert.Equal("ABCD1234", Assert.Single(_scans).Code);
    }

    [Fact]
    public async Task CarriageReturnLineFeed_IsOneScan_NotTwo()
    {
        var s = await StartedAsync();

        Type(s, "ABCD1234");
        s.OnCharacter('\r');
        s.OnCharacter('\n');

        Assert.Single(_scans);
    }

    [Fact]
    public async Task SlowTyping_IsNotAScan()
    {
        var s = await StartedAsync();

        Type(s, "1234567", gapMs: 300);       // a person typing
        s.OnCharacter('\r');

        Assert.Empty(_scans);
    }

    [Fact]
    public async Task StaleCharacters_AreDiscarded_SoTheNextScanIsClean()
    {
        var s = await StartedAsync();

        Type(s, "XX", gapMs: 5);
        _clock.Advance(TimeSpan.FromSeconds(5));       // somebody touched the keyboard earlier
        Type(s, "9780201379624");
        s.OnCharacter('\r');

        Assert.Equal("9780201379624", Assert.Single(_scans).Code);
    }

    [Fact]
    public async Task InputShorterThanTheMinimum_IsIgnored()
    {
        var s = await StartedAsync(minLength: 6);

        Type(s, "123");
        s.OnCharacter('\r');
        Type(s, "123456");
        s.OnCharacter('\r');

        Assert.Equal("123456", Assert.Single(_scans).Code);
    }

    [Fact]
    public async Task ControlCharacters_AreNotPartOfTheCode()
    {
        var s = await StartedAsync();

        Type(s, "AB");
        s.OnCharacter('\t');
        s.OnCharacter('\u001b');
        Type(s, "CD");
        s.OnCharacter('\r');

        Assert.Equal("ABCD", Assert.Single(_scans).Code);
    }

    [Fact]
    public async Task TwoScansInARow_AreTwoEvents()
    {
        var s = await StartedAsync();

        Type(s, "AAAA1111"); s.OnCharacter('\r');
        Type(s, "BBBB2222"); s.OnCharacter('\r');

        Assert.Equal(["AAAA1111", "BBBB2222"], _scans.Select(x => x.Code));
    }

    [Fact]
    public async Task CodesMayContainSpacesAndSymbols()
    {
        var s = await StartedAsync();

        Type(s, "AB 12-3/4"); s.OnCharacter('\r');

        Assert.Equal("AB 12-3/4", Assert.Single(_scans).Code);
    }

    [Fact]
    public void NothingIsReported_UntilStarted_OrAfterStop()
    {
        var s = new KeyboardWedgeBarcodeScanner(4, 80, _clock);
        s.BarcodeScanned += (_, e) => _scans.Add(e.Scan);

        Type(s, "ABCD1234"); s.OnCharacter('\r');
        Assert.Empty(_scans);
    }

    [Fact]
    public async Task StopClearsAnyPartialInput()
    {
        var s = await StartedAsync();
        Type(s, "ABCD");
        await s.StopAsync();
        await s.StartAsync();

        s.OnCharacter('\r');

        Assert.Empty(_scans);
    }

    [Fact]
    public async Task AFaultySubscriber_NeverStopsTheScanner_OrOtherSubscribers()
    {
        var scanner = new KeyboardWedgeBarcodeScanner(4, 80, _clock);
        scanner.BarcodeScanned += (_, _) => throw new InvalidOperationException("subscriber bug");
        scanner.BarcodeScanned += (_, e) => _scans.Add(e.Scan);
        await scanner.StartAsync();

        Type(scanner, "ABCD1234"); scanner.OnCharacter('\r');
        Type(scanner, "EFGH5678"); scanner.OnCharacter('\r');

        Assert.Equal(["ABCD1234", "EFGH5678"], _scans.Select(x => x.Code));
    }

    [Fact]
    public async Task StatusIsReady_AndStartNeverFails()
    {
        var scanner = new KeyboardWedgeBarcodeScanner(timeProvider: _clock);

        Assert.True((await scanner.GetStatusAsync()).IsReady);
        Assert.True((await scanner.StartAsync()).IsSuccess);
        Assert.True((await scanner.StartAsync()).IsSuccess);
    }

    [Fact]
    public async Task TheUiSeesOnlyTheSink_NotTheScanner()
    {
        IKeyboardInputSink sink = await StartedAsync();

        foreach (var c in "ABCD1234\r") sink.OnCharacter(c);

        Assert.Single(_scans);
    }
}
