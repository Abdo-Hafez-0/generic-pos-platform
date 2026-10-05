using System.Collections.ObjectModel;
using CashManagement.Application.Commands;
using CashManagement.Application.DTOs;
using CashManagement.Application.Queries;
using CashManagement.Domain.Enums;

namespace CashManagement.UI.ViewModels;

/// <summary>Minimal cash drawer screen: open a shift, record pay-ins/pay-outs, close with a count. Application handlers only.</summary>
public sealed class CashSessionViewModel(
    GetOpenCashSessionQueryHandler getOpen,
    OpenCashSessionCommandHandler open,
    RecordCashMovementCommandHandler record,
    CloseCashSessionCommandHandler close) : ViewModelBase
{
    private string? _error;
    private CashSessionDto? _session;

    public ObservableCollection<CashMovementDto> Movements { get; } = [];
    public CashSessionDto? Session { get => _session; private set => Set(ref _session, value); }
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }

    public async Task LoadAsync(string drawerCode, CancellationToken cancellationToken = default)
    {
        Session = await getOpen.HandleAsync(new GetOpenCashSessionQuery(drawerCode), cancellationToken);
        Movements.Clear();
        if (Session is not null)
            foreach (var m in Session.Movements) Movements.Add(m);
    }

    public async Task OpenAsync(string drawerCode, string cashier, decimal openingFloat, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var r = await open.HandleAsync(new OpenCashSessionCommand(drawerCode, cashier, openingFloat), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(drawerCode, cancellationToken);
    }

    public async Task RecordAsync(string drawerCode, CashMovementKind kind, decimal amount, string? reason, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (Session is null) { ErrorMessage = "There is no open cash session."; return; }

        var r = await record.HandleAsync(new RecordCashMovementCommand(Session.SessionId, kind, amount, reason, RecordedBy: Session.OpenedBy), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(drawerCode, cancellationToken);
    }

    public async Task CloseAsync(string drawerCode, decimal counted, string cashier, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (Session is null) { ErrorMessage = "There is no open cash session."; return; }

        var r = await close.HandleAsync(new CloseCashSessionCommand(Session.SessionId, counted, cashier), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(drawerCode, cancellationToken);
    }
}
