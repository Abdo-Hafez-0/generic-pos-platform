using Microsoft.EntityFrameworkCore;
using Platform.Application.Abstractions.Data;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for PlatformUnitOfWork — verifying the IUnitOfWork implementation.
///
/// Tested behaviors:
/// - IUnitOfWork can be resolved and used.
/// - SaveChangesAsync works correctly.
/// - Transaction begins and commits successfully.
/// - Transaction rollback works.
/// - Double begin-transaction is rejected (correct isolation).
/// </summary>
public sealed class PlatformUnitOfWorkTests
{
    [Fact(DisplayName = "UnitOfWork: SaveChangesAsync succeeds with no tracked entities")]
    public async Task UnitOfWork_SaveChangesAsync_SucceedsWithNoChanges()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        // Act
        var result = await uow.SaveChangesAsync();

        // Assert
        Assert.Equal(0, result);
    }

    [Fact(DisplayName = "UnitOfWork: BeginTransactionAsync returns a valid transaction")]
    public async Task UnitOfWork_BeginTransactionAsync_ReturnsTransaction()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        // Act
        var transaction = await uow.BeginTransactionAsync();

        // Assert
        Assert.NotNull(transaction);

        // Cleanup
        await uow.RollbackTransactionAsync();
    }

    [Fact(DisplayName = "UnitOfWork: Transaction can be committed successfully")]
    public async Task UnitOfWork_Transaction_CanBeCommitted()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        // Act — begin and commit (with no changes, this should succeed)
        await uow.BeginTransactionAsync();
        await uow.CommitTransactionAsync();

        // Assert — no exception means success
        Assert.True(true, "Transaction committed successfully.");
    }

    [Fact(DisplayName = "UnitOfWork: Transaction can be rolled back")]
    public async Task UnitOfWork_Transaction_CanBeRolledBack()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        // Act — begin and rollback
        await uow.BeginTransactionAsync();
        await uow.RollbackTransactionAsync();

        // Assert — subsequent begin should work after rollback
        var nextTransaction = await uow.BeginTransactionAsync();
        Assert.NotNull(nextTransaction);

        await uow.RollbackTransactionAsync();
    }

    [Fact(DisplayName = "UnitOfWork: Second BeginTransaction throws when one is already active")]
    public async Task UnitOfWork_BeginTransaction_ThrowsWhenAlreadyActive()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        await uow.BeginTransactionAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => uow.BeginTransactionAsync());

        // Cleanup
        await uow.RollbackTransactionAsync();
    }

    [Fact(DisplayName = "UnitOfWork: RollbackTransaction is safe to call when no transaction is active")]
    public async Task UnitOfWork_RollbackTransaction_SafeWhenNoTransaction()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await using var uow = new PlatformUnitOfWork(context);

        // Act — rollback with no active transaction should not throw
        await uow.RollbackTransactionAsync();

        // Assert — if we get here, no exception was thrown
        Assert.True(true, "RollbackTransactionAsync is safe to call with no active transaction.");
    }
}
