using System.Collections.ObjectModel;
using Payments.Application.Commands;
using Payments.Application.DTOs;
using Payments.Application.Queries;
using Payments.Domain.Enums;

namespace Payments.UI.ViewModels;

/// <summary>Minimal payments screen: show the payments of a reference, record a payment and void one. Application handlers only.</summary>
public sealed class PaymentsViewModel(
    GetPaymentsForReferenceQueryHandler list,
    RecordPaymentCommandHandler record,
    VoidPaymentCommandHandler voidPayment) : ViewModelBase
{
    private string? _error;
    private decimal _lastChangeDue;

    public ObservableCollection<PaymentDto> Payments { get; } = [];
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public decimal LastChangeDue { get => _lastChangeDue; private set => Set(ref _lastChangeDue, value); }

    public async Task LoadAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
    {
        Payments.Clear();
        foreach (var p in await list.HandleAsync(new GetPaymentsForReferenceQuery(referenceType, referenceId), cancellationToken)) Payments.Add(p);
    }

    public async Task RecordAsync(string referenceType, Guid referenceId, decimal amount, PaymentMethod method, decimal? tendered, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var r = await record.HandleAsync(new RecordPaymentCommand(referenceType, referenceId, amount, method, TenderedAmount: tendered), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }

        LastChangeDue = r.Value.ChangeDue;
        await LoadAsync(referenceType, referenceId, cancellationToken);
    }

    public async Task VoidAsync(Guid paymentId, string reason, string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var r = await voidPayment.HandleAsync(new VoidPaymentCommand(paymentId, reason), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(referenceType, referenceId, cancellationToken);
    }
}
