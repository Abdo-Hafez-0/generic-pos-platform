using System.Reflection;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace Sales.Tests.Contracts;

public sealed class SalesContractsTests
{
    [Fact]
    public void CreateSaleResult_Success_And_Failure()
    {
        var id = Guid.NewGuid();
        var ok = CreateSaleResult.Success(id);
        var fail = CreateSaleResult.Failure("C", "msg");

        Assert.True(ok.IsSuccess);
        Assert.Equal(id, ok.SaleId);
        Assert.Null(ok.ErrorCode);
        Assert.False(fail.IsSuccess);
        Assert.Equal(Guid.Empty, fail.SaleId);
        Assert.Equal("C", fail.ErrorCode);
        Assert.Equal("msg", fail.ErrorMessage);
    }

    [Fact]
    public void AddSaleItemResult_Success_And_Failure()
    {
        var id = Guid.NewGuid();
        var ok = AddSaleItemResult.Success(id);
        var fail = AddSaleItemResult.Failure("C", "msg");

        Assert.True(ok.IsSuccess);
        Assert.Equal(id, ok.SaleItemId);
        Assert.False(fail.IsSuccess);
        Assert.Equal(Guid.Empty, fail.SaleItemId);
        Assert.Equal("C", fail.ErrorCode);
    }

    [Fact]
    public void SaleOperationResult_Success_And_Failure()
    {
        Assert.True(SaleOperationResult.Success().IsSuccess);
        Assert.Null(SaleOperationResult.Success().ErrorCode);

        var fail = SaleOperationResult.Failure("C", "msg");
        Assert.False(fail.IsSuccess);
        Assert.Equal("C", fail.ErrorCode);
        Assert.Equal("msg", fail.ErrorMessage);
    }

    [Fact]
    public void SaleStatusContract_HasStableValues()
    {
        Assert.Equal(1, (int)SaleStatusContract.Draft);
        Assert.Equal(2, (int)SaleStatusContract.Confirmed);
        Assert.Equal(3, (int)SaleStatusContract.Completed);
        Assert.Equal(4, (int)SaleStatusContract.Cancelled);
    }

    [Fact]
    public void SaleSummaryResult_CarriesValues()
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();

        var summary = new SaleSummaryResult(id, SaleStatusContract.Completed, "R", 12.5m, 3, now, now);

        Assert.Equal(id, summary.SaleId);
        Assert.Equal(SaleStatusContract.Completed, summary.Status);
        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(summary, summary with { });
    }

    [Fact]
    public void ISalesService_And_ISalesReader_ExposeExpectedOperations()
    {
        var service = typeof(ISalesService).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "CreateSaleAsync", "AddItemAsync", "ConfirmSaleAsync", "CompleteSaleAsync", "CancelSaleAsync" },
            service);

        var reader = typeof(ISalesReader).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "FindByIdAsync", "GetRecentAsync" }, reader);
    }

    [Fact]
    public void SalesContracts_DoNotExposeDomainOrInfrastructureTypes()
    {
        var assembly = typeof(ISalesService).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Sales.Domain", references);
        Assert.DoesNotContain("Sales.Infrastructure", references);
        Assert.DoesNotContain("Sales.Application", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);

        // Public signatures use only primitives/contract types.
        foreach (var method in typeof(ISalesService).GetMethods().Concat(typeof(ISalesReader).GetMethods()))
        {
            foreach (var p in method.GetParameters())
                Assert.DoesNotContain("Sales.Domain", p.ParameterType.FullName ?? "");
            Assert.DoesNotContain("Sales.Domain", method.ReturnType.FullName ?? "");
        }
    }
}
