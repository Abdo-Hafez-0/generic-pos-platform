using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Commands;
using Payments.Application.Security;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Payments.Domain.Enums;
using Payments.Infrastructure.DependencyInjection;
using Payments.Infrastructure.Persistence;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Tests.Common.Security;

namespace Payments.Tests.Security;

public sealed class PaymentsAuthorizationTests
{
    private static async Task<(TestModuleDatabase<PaymentsDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<PaymentsDbContext>.CreateAsync(s =>
        {
            s.AddPaymentsCore();
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    private static RecordPaymentCommand Cash(Guid sale) => new("sale", sale, 20m, PaymentMethod.Cash, null, 20m, "cashier");

    [Fact]
    public async Task Recording_and_voiding_through_the_handlers_need_their_capabilities()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var record = await sp.GetRequiredService<RecordPaymentCommandHandler>().HandleAsync(Cash(Guid.NewGuid()));
        var voided = await sp.GetRequiredService<VoidPaymentCommandHandler>().HandleAsync(new VoidPaymentCommand(Guid.NewGuid(), "mistake"));

        Assert.Equal(SecurityErrors.ForbiddenCode, record.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, voided.Error.Code);
        Assert.Contains(PaymentsCapabilities.RecordPayment, auth.Asked);
        Assert.Contains(PaymentsCapabilities.VoidPayment, auth.Asked);
    }

    [Fact]
    public async Task A_refused_record_stores_nothing_and_an_allowed_one_does()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var sale = Guid.NewGuid();
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<RecordPaymentCommandHandler>().HandleAsync(Cash(sale));
        Assert.Empty(await sp.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", sale));

        auth.Allowed.Add(PaymentsCapabilities.RecordPayment);
        var ok = await sp.GetRequiredService<RecordPaymentCommandHandler>().HandleAsync(Cash(sale));
        Assert.True(ok.IsSuccess, ok.IsFailure ? ok.Error.ToString() : null);
        Assert.Single(await sp.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", sale));
    }

    [Fact]
    public async Task The_contract_other_modules_use_runs_inside_their_authorization_and_asks_for_no_second_permission()
    {
        // POS checkout records the payment through IPaymentService: the cashier needs pos.sale.create, not payments.payment.record too.
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPaymentService>();

        var recorded = await service.RecordPaymentAsync(new RecordPaymentRequest("sale", Guid.NewGuid(), 15m, PaymentMethodContract.Cash, null, 15m, "pos"));

        Assert.True(recorded.IsSuccess, recorded.ErrorMessage);
        Assert.Empty(auth.Asked);

        var voided = await service.VoidPaymentAsync(recorded.PaymentId, "customer changed their mind");
        Assert.True(voided.IsSuccess, voided.ErrorMessage);
        Assert.Empty(auth.Asked);
    }

    [Fact]
    public void Every_capability_is_declared_once_and_owned_by_payments()
    {
        var catalog = new CapabilityCatalog([new PaymentsCapabilityProvider()]);

        Assert.Equal(PaymentsCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("payments", c.Module));
        Assert.All(catalog.All, c => Assert.True(c.IsSensitive));
    }
}
