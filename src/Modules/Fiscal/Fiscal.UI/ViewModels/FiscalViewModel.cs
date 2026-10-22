using System.Globalization;
using System.Windows.Input;
using Fiscal.Application.Commands;
using Fiscal.Application.DTOs;
using Fiscal.Application.Queries;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Fiscal.UI.ViewModels;

/// <summary>A country the screen offers, with its words.</summary>
public sealed record CountryChoice(FiscalCountry Country, string Text);

/// <summary>MISS-11d: a type of the seller's identification ZATCA takes (schemeID), with its words.</summary>
public sealed record SellerIdChoice(string Scheme, string Text);

/// <summary>MISS-11a-3: which documents the list shows.</summary>
public sealed record DocumentFilter(IReadOnlyCollection<FiscalDocumentState>? States, string Text);

/// <summary>MISS-11a-3: one document as the list shows it (times in the computer's local time, kept in order in Arabic).</summary>
public sealed record FiscalDocumentRow(FiscalDocumentDto Document, string TimeText, string KindText, string NumberText, string TotalText, string StateText)
{
    public bool NeedsAttention => Document.State is FiscalDocumentState.NotBuilt or FiscalDocumentState.Rejected or FiscalDocumentState.Late;

    /// <summary>MISS-11c-4 (owner's choice A): a refused or not-built document's "…" offers to build it again.</summary>
    public bool CanCorrect => Document.State is FiscalDocumentState.Rejected or FiscalDocumentState.NotBuilt;

    /// <summary>"Correct and resend" (refused) or "Build and send" (could not be made).</summary>
    public string CorrectText => Document.State == FiscalDocumentState.NotBuilt ? FiscalText.RowBuildAndSend : FiscalText.RowCorrectAndResend;
}

/// <summary>
/// MISS-11a: the Fiscal screen (Administration, fiscal.manage; owner's choice: two columns of white boxes, the documents underneath). The
/// country and the shop's tax details on one side, this till on the other. The country is locked once the till has issued a document.
/// </summary>
public sealed class FiscalViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private CountryChoice _country;
    private string _taxNumber = string.Empty, _branchCode = string.Empty, _activityCode = string.Empty, _sellerName = string.Empty;
    private string? _governorate;
    private string _city = string.Empty, _street = string.Empty, _building = string.Empty, _postalCode = string.Empty, _floor = string.Empty, _room = string.Empty,
        _landmark = string.Empty, _moreDetails = string.Empty, _district = string.Empty, _additionalNumber = string.Empty, _sellerId = string.Empty;
    private SellerIdChoice _sellerIdType;
    private FiscalSetupDto? _setup;
    private DocumentFilter _filter;
    private string _alertsText = string.Empty;
    private bool _alertsNeedAttention;

    public FiscalViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        Countries = [new(FiscalCountry.None, FiscalText.CountryNone), new(FiscalCountry.Egypt, FiscalText.CountryEgypt), new(FiscalCountry.SaudiArabia, FiscalText.CountrySaudi)];
        _country = Countries[0];
        SellerIdTypes =
        [
            new("CRN", FiscalText.SellerIdCrn), new("MOM", FiscalText.SellerIdMom), new("MLS", FiscalText.SellerIdMls),
            new("700", FiscalText.SellerId700), new("SAG", FiscalText.SellerIdSag), new("OTH", FiscalText.SellerIdOth),
        ];
        _sellerIdType = SellerIdTypes[0];
        SaveCommand = Command(SaveAsync, () => _setup is not null);
        Filters =
        [
            new(null, FiscalText.FilterAll),
            new([FiscalDocumentState.Waiting, FiscalDocumentState.Sent], FiscalText.FilterOutstanding),
            new([FiscalDocumentState.NotBuilt, FiscalDocumentState.Rejected, FiscalDocumentState.Late], FiscalText.FilterAttention),
            new([FiscalDocumentState.Accepted], FiscalText.FilterAccepted),
        ];
        _filter = Filters[0];
        SendNowCommand = Command(SendNowAsync, () => _setup is { IsIssuing: true });
        Codes = new FiscalCodesPanel(runner);   // MISS-11b
        ShowDocumentsCommand = Command(() => LoadDocumentsAsync(CancellationToken.None));
        TestSignInCommand = Command(TestSignInAsync, () => _setup?.Connection is { HasSecrets: true } && _setup.IsIssuing);
        SendLateCommand = Command(SendLateAsync, () => LateCount > 0);                                     // MISS-11c-4
        CorrectRowCommand = Command<FiscalDocumentRow>(CorrectAsync, row => row is { CanCorrect: true });
    }

    // ---- MISS-11c-3 (owner's choice, drawn option B): this till's registration as a summary; the details in a window

    public ICommand TestSignInCommand { get; }

    public FiscalTillRegistrationDto? Registration => _setup?.Connection;

    public bool IsRegistered => _setup?.TillRegistration == FiscalTillRegistration.Registered;

    public string EnvironmentText => Registration?.Environment switch
    {
        FiscalEnvironment.PreProduction => FiscalText.EnvironmentPreProduction,
        FiscalEnvironment.Production => FiscalText.EnvironmentProduction,
        _ => "–",
    };

    /// <summary>Part of the client ID ("a1b2…9f"): enough to recognise it, not to copy it.</summary>
    public string ClientIdText => Registration?.ClientId is { Length: > 0 } id ? (id.Length <= 8 ? id : id[..4] + "…" + id[^2..]) : "–";

    /// <summary>"today 10:42 - accepted" / "refused", or "–" before the first test.</summary>
    public string LastSignInText => Registration is { LastSignInAt: { } at } r
        ? "‎" + DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + "‎ – "
          + (r.LastSignInOk == true ? FiscalText.SignInAccepted : FiscalText.SignInRefused)
        : "–";

    /// <summary>The registration window's view model, filled with what is kept (never the secrets).</summary>
    public TillRegistrationViewModel CreateRegistration() => new(_runner, Registration, _setup?.DeviceSerial);

    /// <summary>After the window saved: the summary again.</summary>
    public Task RegisteredAsync() => BusyAsync(async () =>
    {
        await LoadAsync(CancellationToken.None);
        if (ErrorMessage is null) StatusMessage = FiscalText.TillSaved;
    });

    private async Task TestSignInAsync()
    {
        var tested = await _runner.RunAsync((scope, ct) => scope.Get<TestFiscalSignInCommandHandler>().HandleAsync(new TestFiscalSignInCommand(), ct));
        var ok = Accept(tested);
        await LoadAsync(CancellationToken.None);
        if (ok && ErrorMessage is null) StatusMessage = FiscalText.SignInWorked;
        else if (!ok) ErrorMessage = tested.Error.Description;
    }

    /// <summary>MISS-11b: the Codes box (the tax authority's codes for products, units and tax rates).</summary>
    public FiscalCodesPanel Codes { get; }

    // ---- MISS-11a-3: the documents

    public ICommand SendNowCommand { get; }
    public ICommand ShowDocumentsCommand { get; }

    public IReadOnlyList<DocumentFilter> Filters { get; }

    public DocumentFilter Filter { get => _filter; set { if (Set(ref _filter, value)) ShowDocumentsCommand.Execute(null); } }

    public System.Collections.ObjectModel.ObservableCollection<FiscalDocumentRow> Documents { get; } = [];

    public bool HasNoDocuments => Documents.Count == 0;

    /// <summary>"3 waiting to reach the tax authority." or the warning (could not be made / refused / waiting too long).</summary>
    public string AlertsText { get => _alertsText; private set => Set(ref _alertsText, value); }

    // ---- MISS-11c-4 (owner's choice A): late receipts above the list; "Correct and resend" / "Build and send" in a row's "…"

    private int _lateCount;

    public ICommand SendLateCommand { get; }
    public ICommand CorrectRowCommand { get; }

    public int LateCount { get => _lateCount; private set { if (Set(ref _lateCount, value)) { Raise(nameof(HasLate)); Raise(nameof(LateText)); } } }
    public bool HasLate => LateCount > 0;

    /// <summary>"3 receipts are late (over 24 hours). File a late-submission request on the ETA portal, then send them."</summary>
    public string LateText => string.Format(CultureInfo.CurrentCulture, FiscalText.LateLine, LateCount);

    private async Task SendLateAsync()
    {
        var sent = await _runner.RunAsync((scope, ct) => scope.Get<SendLateFiscalDocumentsCommandHandler>().HandleAsync(new SendLateFiscalDocumentsCommand(), ct));
        if (!Accept(sent)) return;
        await LoadDocumentsAsync(CancellationToken.None);
        var s = sent.Value;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, FiscalText.SentNow, s.Sent, s.Accepted, s.Rejected, s.NotSent)
            + (s.NotSent > 0 && s.LastReason is { } why ? " " + string.Format(CultureInfo.CurrentCulture, FiscalText.NotSentBecause, why) : string.Empty);
    }

    /// <summary>Builds the document again and sends what waits at once (Send now).</summary>
    private async Task CorrectAsync(FiscalDocumentRow? row)
    {
        if (row is null) return;
        var corrected = await _runner.RunAsync((scope, ct) => scope.Get<CorrectFiscalDocumentCommandHandler>().HandleAsync(new CorrectFiscalDocumentCommand(row.Document.DocumentId), ct));
        if (!Accept(corrected)) return;
        await LoadAsync(CancellationToken.None);   // the next number of the chain may have moved
        await SendNowAsync();
        if (ErrorMessage is null) StatusMessage = (row.Document.State == FiscalDocumentState.NotBuilt ? FiscalText.BuiltAgain : FiscalText.Corrected) + " " + StatusMessage;
    }

    public bool AlertsNeedAttention { get => _alertsNeedAttention; private set => Set(ref _alertsNeedAttention, value); }

    private async Task LoadDocumentsAsync(CancellationToken cancellationToken)
    {
        var states = Filter.States;
        var listed = await _runner.RunAsync((scope, ct) => scope.Get<ListFiscalDocumentsQueryHandler>().HandleAsync(new ListFiscalDocumentsQuery(states), ct), cancellationToken);
        if (!Accept(listed)) return;
        Documents.Clear();
        foreach (var d in listed.Value) Documents.Add(Row(d));
        Raise(nameof(HasNoDocuments));

        var alerts = await _runner.RunAsync((scope, ct) => scope.Get<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery(), ct), cancellationToken);
        if (!Accept(alerts)) return;
        LateCount = alerts.Value?.Late ?? 0;
        (AlertsNeedAttention, AlertsText) = alerts.Value switch
        {
            null => (false, string.Empty),
            { NeedsAttention: true } a => (true, Fiscal.UI.Screens.FiscalShellNotice.Describe(a)),
            { Outstanding: > 0 } a => (false, string.Format(CultureInfo.CurrentCulture, FiscalText.AlertsSummary, a.Outstanding)),
            _ => (false, Documents.Count > 0 ? FiscalText.AlertsAllWell : string.Empty),
        };
    }

    private async Task SendNowAsync()
    {
        var sent = await _runner.RunAsync((scope, ct) => scope.Get<SendFiscalDocumentsCommandHandler>().HandleAsync(ct));
        if (!Accept(sent)) return;
        await LoadDocumentsAsync(CancellationToken.None);
        var s = sent.Value;
        StatusMessage = !s.Ran ? FiscalText.NothingToSend
            : string.Format(CultureInfo.CurrentCulture, FiscalText.SentNow, s.Sent, s.Accepted, s.Rejected, s.NotSent)
              + (s.NotSent > 0 && s.LastReason is { } why ? " " + string.Format(CultureInfo.CurrentCulture, FiscalText.NotSentBecause, why) : string.Empty);
    }

    private static FiscalDocumentRow Row(FiscalDocumentDto d) => new(d,
        "‎" + DateTime.SpecifyKind(d.IssuedAt, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + "‎",
        d.Kind == FiscalDocumentKind.Return ? string.Format(CultureInfo.CurrentCulture, FiscalText.KindReturn, "‎" + d.OriginalReference + "‎") : FiscalText.KindSale,
        d.Number?.ToString(CultureInfo.CurrentCulture) ?? "–",
        (d.Kind == FiscalDocumentKind.Return ? "‎-" : string.Empty) + d.Total.ToString("N2", CultureInfo.CurrentCulture) + (d.Kind == FiscalDocumentKind.Return ? "‎" : string.Empty),
        d.State switch
        {
            FiscalDocumentState.Waiting => FiscalText.StateWaiting,
            FiscalDocumentState.Sent => FiscalText.StateSent,
            FiscalDocumentState.Accepted => FiscalText.StateAccepted,
            FiscalDocumentState.Rejected => FiscalText.StateRejected,
            FiscalDocumentState.Late => FiscalText.StateLate,
            FiscalDocumentState.Replaced => FiscalText.StateReplaced,
            _ => FiscalText.StateNotBuilt,
        });

    public IReadOnlyList<CountryChoice> Countries { get; }

    public ICommand SaveCommand { get; }

    public CountryChoice Country
    {
        get => _country;
        set
        {
            if (!Set(ref _country, value)) return;
            Raise(nameof(IsEgypt));
            Raise(nameof(IsSaudi));
            Raise(nameof(NeedsDetails));
            Raise(nameof(TaxNumberHint));
        }
    }

    public string TaxNumber { get => _taxNumber; set => Set(ref _taxNumber, value); }
    public string BranchCode { get => _branchCode; set => Set(ref _branchCode, value); }
    public string ActivityCode { get => _activityCode; set => Set(ref _activityCode, value); }
    public string SellerNameArabic { get => _sellerName; set => Set(ref _sellerName, value); }

    // MISS-11c: the branch address in the authority's fields
    public IReadOnlyList<string> Governorates { get; } = Fiscal.Domain.Entities.FiscalAddress.EgyptGovernorates;
    public string? Governorate { get => _governorate; set => Set(ref _governorate, value); }
    public string City { get => _city; set => Set(ref _city, value); }
    public string Street { get => _street; set => Set(ref _street, value); }
    public string BuildingNumber { get => _building; set => Set(ref _building, value); }
    public string PostalCode { get => _postalCode; set => Set(ref _postalCode, value); }
    public string Floor { get => _floor; set => Set(ref _floor, value); }
    public string Room { get => _room; set => Set(ref _room, value); }
    public string Landmark { get => _landmark; set => Set(ref _landmark, value); }
    public string AdditionalInformation { get => _moreDetails; set => Set(ref _moreDetails, value); }

    // MISS-11d (owner's choice, drawn option A): Saudi Arabia - the seller's identification with its type (CRN first) and the national address
    public IReadOnlyList<SellerIdChoice> SellerIdTypes { get; }
    public SellerIdChoice SellerIdType { get => _sellerIdType; set => Set(ref _sellerIdType, value ?? SellerIdTypes[0]); }
    public string SellerId { get => _sellerId; set => Set(ref _sellerId, value); }
    public string District { get => _district; set => Set(ref _district, value); }
    public string AdditionalNumber { get => _additionalNumber; set => Set(ref _additionalNumber, value); }

    /// <summary>ZATCA's invoices carry the seller's identification and the national address.</summary>
    public bool IsSaudi => Country.Country == FiscalCountry.SaudiArabia;

    /// <summary>MISS-11c: the till's serial number, to register on the authority's portal.</summary>
    public string DeviceSerial => _setup?.DeviceSerial ?? string.Empty;

    /// <summary>Egypt's receipts carry the branch and the activity code.</summary>
    public bool IsEgypt => Country.Country == FiscalCountry.Egypt;

    /// <summary>A country is chosen: its tax details are asked for.</summary>
    public bool NeedsDetails => Country.Country != FiscalCountry.None;

    public string TaxNumberHint => Country.Country == FiscalCountry.SaudiArabia ? FiscalText.TaxNumberHintSaudi : FiscalText.TaxNumberHintEgypt;

    /// <summary>Documents exist: the country cannot change (owner's decision).</summary>
    public bool IsCountryLocked => _setup?.CountryLocked == true;
    public bool CanChooseCountry => !IsCountryLocked;

    public string TillName => _setup?.TillName ?? string.Empty;

    /// <summary>"Send now" shows when this till issues documents (a country part is installed).</summary>
    public bool CanSendNow => _setup is { IsIssuing: true };

    public string TillRegistrationText => _setup?.TillRegistration == FiscalTillRegistration.Registered ? FiscalText.TillRegistered : FiscalText.TillNotRegistered;

    public string NextNumberText => (_setup?.NextNumber ?? 1).ToString(CultureInfo.CurrentCulture);

    /// <summary>What this till does now: off, waiting for the country's part, or issuing.</summary>
    public string IssuingText => _setup switch
    {
        null or { IsOn: false } => FiscalText.StateOff,
        { IsIssuing: false } => string.Format(CultureInfo.CurrentCulture, FiscalText.StateNoPart, Describe(_setup.Country)),
        _ => string.Format(CultureInfo.CurrentCulture, FiscalText.StateIssuing, Describe(_setup.Country)),
    };

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        await LoadAsync(cancellationToken);
        if (ErrorMessage is null) await LoadDocumentsAsync(cancellationToken);
        if (ErrorMessage is null) await Codes.LoadAsync(cancellationToken);
    });

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var setup = await _runner.RunAsync((scope, ct) => scope.Get<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery(), ct), cancellationToken);
        if (!Accept(setup)) return;
        Show(setup.Value);
    }

    private void Show(FiscalSetupDto setup)
    {
        _setup = setup;
        Codes.TaxNumber = setup.TaxNumber;
        Country = Countries.First(c => c.Country == setup.Country);
        (TaxNumber, BranchCode, ActivityCode) = (setup.TaxNumber ?? string.Empty, setup.BranchCode ?? string.Empty, setup.ActivityCode ?? string.Empty);
        SellerNameArabic = setup.SellerNameArabic ?? string.Empty;
        var a = setup.Address;
        Governorate = a.Governorate is { } g && Governorates.Contains(g) ? g : null;
        (City, Street, BuildingNumber, PostalCode) = (a.City ?? string.Empty, a.Street ?? string.Empty, a.BuildingNumber ?? string.Empty, a.PostalCode ?? string.Empty);
        (Floor, Room, Landmark, AdditionalInformation) = (a.Floor ?? string.Empty, a.Room ?? string.Empty, a.Landmark ?? string.Empty, a.AdditionalInformation ?? string.Empty);
        (District, AdditionalNumber, SellerId) = (a.District ?? string.Empty, a.AdditionalNumber ?? string.Empty, setup.SellerId ?? string.Empty);
        SellerIdType = SellerIdTypes.FirstOrDefault(t => t.Scheme == setup.SellerIdScheme) ?? SellerIdTypes[0];
        foreach (var name in new[] { nameof(IsCountryLocked), nameof(CanChooseCountry), nameof(TillName), nameof(TillRegistrationText), nameof(NextNumberText), nameof(IssuingText), nameof(CanSendNow), nameof(DeviceSerial),
                     nameof(Registration), nameof(IsRegistered), nameof(EnvironmentText), nameof(ClientIdText), nameof(LastSignInText) })
            Raise(name);
    }

    private async Task SaveAsync()
    {
        // each country keeps only its own fields (the hidden ones are not sent)
        var address = IsSaudi
            ? new Fiscal.Domain.Entities.FiscalAddress(null, City, Street, BuildingNumber, PostalCode, District: District, AdditionalNumber: AdditionalNumber)
            : new Fiscal.Domain.Entities.FiscalAddress(IsEgypt ? Governorate : null, City, Street, BuildingNumber, PostalCode, Floor, Room, Landmark, AdditionalInformation);
        var command = new SaveFiscalSettingsCommand(Country.Country, TaxNumber, BranchCode, ActivityCode, SellerNameArabic, address,
            IsSaudi ? SellerIdType.Scheme : null, IsSaudi ? SellerId : null);
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SaveFiscalSettingsCommandHandler>().HandleAsync(command, ct));
        if (!Accept(saved)) return;

        await LoadAsync(CancellationToken.None);
        if (ErrorMessage is null) await Codes.LoadAsync(CancellationToken.None);   // the country decides which codes are needed
        if (ErrorMessage is null) StatusMessage = FiscalText.Saved;
    }

    private string Describe(FiscalCountry country) => Countries.First(c => c.Country == country).Text;
}
