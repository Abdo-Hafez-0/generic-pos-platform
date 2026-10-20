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

/// <summary>MISS-11a-3: which documents the list shows.</summary>
public sealed record DocumentFilter(IReadOnlyCollection<FiscalDocumentState>? States, string Text);

/// <summary>MISS-11a-3: one document as the list shows it (times in the computer's local time, kept in order in Arabic).</summary>
public sealed record FiscalDocumentRow(FiscalDocumentDto Document, string TimeText, string KindText, string NumberText, string TotalText, string StateText)
{
    public bool NeedsAttention => Document.State is FiscalDocumentState.NotBuilt or FiscalDocumentState.Rejected;
}

/// <summary>
/// MISS-11a: the Fiscal screen (Administration, fiscal.manage; owner's choice: two columns of white boxes, the documents underneath). The
/// country and the shop's tax details on one side, this till on the other. The country is locked once the till has issued a document.
/// </summary>
public sealed class FiscalViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private CountryChoice _country;
    private string _taxNumber = string.Empty, _branchCode = string.Empty, _activityCode = string.Empty, _sellerName = string.Empty, _address = string.Empty;
    private FiscalSetupDto? _setup;
    private DocumentFilter _filter;
    private string _alertsText = string.Empty;
    private bool _alertsNeedAttention;

    public FiscalViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        Countries = [new(FiscalCountry.None, FiscalText.CountryNone), new(FiscalCountry.Egypt, FiscalText.CountryEgypt), new(FiscalCountry.SaudiArabia, FiscalText.CountrySaudi)];
        _country = Countries[0];
        SaveCommand = Command(SaveAsync, () => _setup is not null);
        Filters =
        [
            new(null, FiscalText.FilterAll),
            new([FiscalDocumentState.Waiting, FiscalDocumentState.Sent], FiscalText.FilterOutstanding),
            new([FiscalDocumentState.NotBuilt, FiscalDocumentState.Rejected], FiscalText.FilterAttention),
            new([FiscalDocumentState.Accepted], FiscalText.FilterAccepted),
        ];
        _filter = Filters[0];
        SendNowCommand = Command(SendNowAsync, () => _setup is { IsIssuing: true });
        ShowDocumentsCommand = Command(() => LoadDocumentsAsync(CancellationToken.None));
    }

    // ---- MISS-11a-3: the documents

    public ICommand SendNowCommand { get; }
    public ICommand ShowDocumentsCommand { get; }

    public IReadOnlyList<DocumentFilter> Filters { get; }

    public DocumentFilter Filter { get => _filter; set { if (Set(ref _filter, value)) ShowDocumentsCommand.Execute(null); } }

    public System.Collections.ObjectModel.ObservableCollection<FiscalDocumentRow> Documents { get; } = [];

    public bool HasNoDocuments => Documents.Count == 0;

    /// <summary>"3 waiting to reach the tax authority." or the warning (could not be made / refused / waiting too long).</summary>
    public string AlertsText { get => _alertsText; private set => Set(ref _alertsText, value); }

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
            Raise(nameof(NeedsDetails));
            Raise(nameof(TaxNumberHint));
        }
    }

    public string TaxNumber { get => _taxNumber; set => Set(ref _taxNumber, value); }
    public string BranchCode { get => _branchCode; set => Set(ref _branchCode, value); }
    public string ActivityCode { get => _activityCode; set => Set(ref _activityCode, value); }
    public string SellerNameArabic { get => _sellerName; set => Set(ref _sellerName, value); }
    public string AddressArabic { get => _address; set => Set(ref _address, value); }

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
        Country = Countries.First(c => c.Country == setup.Country);
        (TaxNumber, BranchCode, ActivityCode) = (setup.TaxNumber ?? string.Empty, setup.BranchCode ?? string.Empty, setup.ActivityCode ?? string.Empty);
        (SellerNameArabic, AddressArabic) = (setup.SellerNameArabic ?? string.Empty, setup.AddressArabic ?? string.Empty);
        foreach (var name in new[] { nameof(IsCountryLocked), nameof(CanChooseCountry), nameof(TillName), nameof(TillRegistrationText), nameof(NextNumberText), nameof(IssuingText), nameof(CanSendNow) })
            Raise(name);
    }

    private async Task SaveAsync()
    {
        var command = new SaveFiscalSettingsCommand(Country.Country, TaxNumber, BranchCode, ActivityCode, SellerNameArabic, AddressArabic);
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SaveFiscalSettingsCommandHandler>().HandleAsync(command, ct));
        if (!Accept(saved)) return;

        await LoadAsync(CancellationToken.None);
        if (ErrorMessage is null) StatusMessage = FiscalText.Saved;
    }

    private string Describe(FiscalCountry country) => Countries.First(c => c.Country == country).Text;
}
