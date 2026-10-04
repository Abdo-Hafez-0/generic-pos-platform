using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Tests.Contracts;

public sealed class PosContractsTests
{
    [Fact]
    public void OperationResult_Success_And_Failure()
    {
        Assert.True(POSOperationResult.Success().IsSuccess);
        Assert.Null(POSOperationResult.Success().ErrorCode);

        var fail = POSOperationResult.Failure("C", "msg");
        Assert.False(fail.IsSuccess);
        Assert.Equal("C", fail.ErrorCode);
        Assert.Equal("msg", fail.ErrorMessage);
    }

    [Fact]
    public void IdResults_Success_CarryId_Failure_CarryEmptyId()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, POSOpenSessionResult.Success(id).SessionId);
        Assert.Equal(id, POSStartCartResult.Success(id).CartId);
        Assert.Equal(id, POSAddItemResult.Success(id).ItemId);
        Assert.Equal(id, POSCheckoutResult.Success(id).SaleId);

        Assert.Equal(Guid.Empty, POSOpenSessionResult.Failure("C", "m").SessionId);
        Assert.Equal(Guid.Empty, POSStartCartResult.Failure("C", "m").CartId);
        Assert.Equal(Guid.Empty, POSAddItemResult.Failure("C", "m").ItemId);
        Assert.Equal(Guid.Empty, POSCheckoutResult.Failure("C", "m").SaleId);

        Assert.False(POSCheckoutResult.Failure("C", "m").IsSuccess);
        Assert.Equal("C", POSAddItemResult.Failure("C", "m").ErrorCode);
    }

    [Fact]
    public void ReadModels_CarryValues_AndAreValueEqual()
    {
        var now = DateTime.UtcNow;
        var item = new POSCartItemResult(Guid.NewGuid(), Guid.NewGuid(), "SKU", "Name", 2m, 5m, 10m);
        var cart = new POSCartResult(Guid.NewGuid(), Guid.NewGuid(), POSCartStatusContract.Open, [item], 10m, 10m, null, now, null);
        var session = new POSSessionResult(Guid.NewGuid(), "alice", Guid.NewGuid(), POSSessionStatusContract.Open, now, null);

        Assert.Equal(10m, cart.Total);
        Assert.Single(cart.Items);
        Assert.Equal(cart, cart with { });
        Assert.Equal("alice", session.CashierReference);
        Assert.Equal(1, (int)POSSessionStatusContract.Open);
        Assert.Equal(2, (int)POSSessionStatusContract.Closed);
        Assert.Equal(1, (int)POSCartStatusContract.Open);
        Assert.Equal(2, (int)POSCartStatusContract.CheckedOut);
    }

    [Fact]
    public void IPOSService_And_IPOSReader_ExposeExpectedOperations()
    {
        var service = typeof(IPOSService).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "OpenSessionAsync", "CloseSessionAsync", "StartCartAsync", "AddProductAsync",
            "RemoveProductAsync", "ChangeQuantityAsync", "ClearCartAsync", "CheckoutAsync"
        }, service);

        var reader = typeof(IPOSReader).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "GetSessionAsync", "GetCartAsync", "GetCurrentCartAsync" }, reader);
    }

    [Fact]
    public void PosContracts_DoNotReferenceDomainApplicationInfrastructureOrEf()
    {
        var assembly = typeof(IPOSService).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("POS.Domain", references);
        Assert.DoesNotContain("POS.Application", references);
        Assert.DoesNotContain("POS.Infrastructure", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);

        foreach (var method in typeof(IPOSService).GetMethods().Concat(typeof(IPOSReader).GetMethods()))
        {
            foreach (var p in method.GetParameters())
                Assert.DoesNotContain("POS.Domain", p.ParameterType.FullName ?? "");
            Assert.DoesNotContain("POS.Domain", method.ReturnType.FullName ?? "");
        }
    }
}
