using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CashManagement.Application.Commands;
using CashManagement.Application.DTOs;
using CashManagement.Application.Queries;
using CashManagement.Domain.Enums;
using CashManagement.UI.Resources;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace CashManagement.UI.ViewModels;

/// <summary>A movement as the shift shows it.</summary>
public sealed record MovementRow(CashMovementDto Movement, string KindText, string TimeText);

/// <summary>A shift as the recent list shows it.</summary>
public sealed record ShiftRow(CashSessionSummaryDto Shift, string OpenedText, string ClosedText);

/// <summary>A manual movement kind the screen offers.</summary>
public sealed record KindChoice(CashMovementKind Kind, string Name);

/// <summary>
/// The cash drawer (FIX-01d): open a shift with a float, pay in / pay out with a reason, count and close (the expected amount and the
/// difference are kept), and the recent shifts. The person recorded is always the signed-in user (the handlers enforce it). Through the
/// per-action runner; the handlers authorize (cash.session.manage, cash.movement.record).
/// </summary>
public sealed class CashDrawerViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly ICurrentUser _currentUser;

    private string _drawerCode = "MAIN";
    private CashSessionDto? _session;
    private string _openingFloat = string.Empty;
    private KindChoice _kind;
    private string _amount = string.Empty;
    private string _reason = string.Empty;
    private string _counted = string.Empty;
    private string _closeNotes = string.Empty;

    public CashDrawerViewModel(IUiActionRunner runner, ICurrentUser currentUser)
    {
        _runner = runner;
        _currentUser = currentUser;
        Kinds = [new(CashMovementKind.PayIn, CashText.PayIn), new(CashMovementKind.PayOut, CashText.PayOut)];
        _kind = Kinds[1];

        ShowCommand = Command(() => LoadAsync(CancellationToken.None));
        OpenCommand = Command(OpenAsync, () => Session is null && !string.IsNullOrWhiteSpace(OpeningFloat));
        RecordCommand = Command(RecordAsync, () => Session is not null && !string.IsNullOrWhiteSpace(Amount) && !string.IsNullOrWhiteSpace(Reason));
        CloseCommand = Command(CloseAsync, () => Session is not null && !string.IsNullOrWhiteSpace(Counted));
    }

    public IReadOnlyList<KindChoice> Kinds { get; }
    public ObservableCollection<MovementRow> Movements { get; } = [];
    public ObservableCollection<ShiftRow> RecentShifts { get; } = [];

    public ICommand ShowCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand CloseCommand { get; }

    public string DrawerCode { get => _drawerCode; set => Set(ref _drawerCode, value); }

    /// <summary>The open shift of the drawer, or null.</summary>
    public CashSessionDto? Session
    {
        get => _session;
        private set
        {
            if (!Set(ref _session, value)) return;
            Raise(nameof(HasOpenShift));
            Raise(nameof(HasNoOpenShift));
            Raise(nameof(OpenedText));
        }
    }

    public bool HasOpenShift => Session is not null;
    public bool HasNoOpenShift => Session is null;

    public string OpenedText => Session is null ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, CashText.OpenedBy, Session.OpenedBy, LocalTime(Session.OpenedAt));

    public string OpeningFloat { get => _openingFloat; set => Set(ref _openingFloat, value); }
    public KindChoice Kind { get => _kind; set => Set(ref _kind, value); }
    public string Amount { get => _amount; set => Set(ref _amount, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public string Counted { get => _counted; set => Set(ref _counted, value); }
    public string CloseNotes { get => _closeNotes; set => Set(ref _closeNotes, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var drawer = DrawerCode.Trim();
        if (drawer.Length == 0) { Fail(CashText.DrawerRequired); return; }

        var loaded = await _runner.QueryAsync(async (scope, ct) => (
            Open: await scope.Get<GetOpenCashSessionQueryHandler>().HandleAsync(new GetOpenCashSessionQuery(drawer), ct),
            Recent: await scope.Get<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery(drawer, null, 1, 20), ct)), cancellationToken);
        if (!Accept(loaded)) return;

        Show(loaded.Value.Open);
        RecentShifts.Clear();
        foreach (var shift in loaded.Value.Recent.Items)
            RecentShifts.Add(new ShiftRow(shift, LocalTime(shift.OpenedAt), shift.ClosedAt is { } closed ? LocalTime(closed) : string.Empty));
    }

    private async Task OpenAsync()
    {
        if (!TryAmount(OpeningFloat, allowZero: true, out var opening)) { Fail(CashText.FloatInvalid); return; }

        var drawer = DrawerCode.Trim();
        var who = _currentUser.UserName;
        var opened = await _runner.RunAsync(async (scope, ct) =>
        {
            var created = await scope.Get<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand(drawer, who, opening), ct);
            return created.IsFailure ? Result.Failure(created.Error) : Result.Success();
        });
        if (!Accept(opened)) return;

        OpeningFloat = string.Empty;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CashText.ShiftOpened, drawer.ToUpperInvariant());
    }

    private async Task RecordAsync()
    {
        if (Session is not { } session) return;
        if (!TryAmount(Amount, allowZero: false, out var amount)) { Fail(CashText.AmountInvalid); return; }

        var (kind, reason, who) = (Kind.Kind, Reason.Trim(), _currentUser.UserName);
        var recorded = await _runner.RunAsync((scope, ct) => scope.Get<RecordCashMovementCommandHandler>()
            .HandleAsync(new RecordCashMovementCommand(session.SessionId, kind, amount, reason, RecordedBy: who), ct));
        if (!Accept(recorded)) return;

        Amount = Reason = string.Empty;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CashText.MovementRecorded, recorded.Value.BalanceAfter);
    }

    private async Task CloseAsync()
    {
        if (Session is not { } session) return;
        if (!TryAmount(Counted, allowZero: true, out var counted)) { Fail(CashText.CountInvalid); return; }

        var (notes, who) = (string.IsNullOrWhiteSpace(CloseNotes) ? null : CloseNotes.Trim(), _currentUser.UserName);
        var closed = await _runner.RunAsync((scope, ct) => scope.Get<CloseCashSessionCommandHandler>()
            .HandleAsync(new CloseCashSessionCommand(session.SessionId, counted, who, notes), ct));
        if (!Accept(closed)) return;

        Counted = CloseNotes = string.Empty;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CashText.ShiftClosed, closed.Value.ExpectedAmount, closed.Value.CountedAmount, closed.Value.Variance);
    }

    private void Show(CashSessionDto? session)
    {
        Session = session;
        Movements.Clear();
        if (session is null) return;
        foreach (var movement in session.Movements.OrderByDescending(m => m.RecordedAt))
            Movements.Add(new MovementRow(movement, Describe(movement.Kind), LocalTime(movement.RecordedAt)));
    }

    private static bool TryAmount(string text, bool allowZero, out decimal amount)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) && (amount > 0 || (allowZero && amount == 0));

    private static string Describe(CashMovementKind kind) => kind switch
    {
        CashMovementKind.PayIn => CashText.PayIn,
        CashMovementKind.PayOut => CashText.PayOut,
        CashMovementKind.CashSale => CashText.CashSale,
        _ => CashText.CashRefund,
    };

    private static string LocalTime(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
