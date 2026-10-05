using Audit.Domain.Entities;
using Audit.Domain.ValueObjects;

namespace Audit.Tests.Domain;

public sealed class AuditDomainTests
{
    [Fact]
    public void Record_NormalisesCase_TrimsText_AndDefaultsToNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var actor = Guid.NewGuid();

        var r = AuditEntry.Record(" Sales ", " Sale.Cancelled ", " Sale ", " S-1 ", actor, " Cashier ", " Cancelled by cashier ", " {\"reason\":\"x\"} ");

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        var e = r.Value;
        Assert.Equal("sales", e.Module);
        Assert.Equal("sale.cancelled", e.Action);
        Assert.Equal("sale", e.EntityType);
        Assert.Equal("S-1", e.EntityId);          // identifiers keep their case
        Assert.Equal(actor, e.ActorId);
        Assert.Equal("Cashier", e.ActorName);
        Assert.Equal("Cancelled by cashier", e.Summary);
        Assert.Equal("{\"reason\":\"x\"}", e.Details);
        Assert.InRange(e.OccurredAt, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(DateTimeKind.Utc, e.OccurredAt.Kind);
        Assert.NotEqual(AuditEntryId.Empty, e.Id);
    }

    [Fact]
    public void Record_OnlyModuleAndActionAreRequired_AndBlankOptionalsBecomeNull()
    {
        var e = AuditEntry.Record("inventory", "stock.adjusted", " ", "", null, " ", "  ", null).Value;

        Assert.Null(e.EntityType);
        Assert.Null(e.EntityId);
        Assert.Null(e.ActorId);
        Assert.Null(e.ActorName);
        Assert.Null(e.Summary);
        Assert.Null(e.Details);
    }

    [Fact]
    public void Record_RequiresModuleAndAction()
    {
        Assert.Equal("Audit.Entry.ModuleRequired", AuditEntry.Record(" ", "a").Error.Code);
        Assert.Equal("Audit.Entry.ActionRequired", AuditEntry.Record("m", "").Error.Code);
    }

    [Fact]
    public void Record_LimitsLengths()
    {
        Assert.Equal("Audit.Entry.ModuleTooLong", AuditEntry.Record(new string('x', 51), "a").Error.Code);
        Assert.Equal("Audit.Entry.ActionTooLong", AuditEntry.Record("m", new string('x', 101)).Error.Code);
        Assert.Equal("Audit.Entry.EntityTypeTooLong", AuditEntry.Record("m", "a", new string('x', 101)).Error.Code);
        Assert.Equal("Audit.Entry.EntityIdTooLong", AuditEntry.Record("m", "a", "t", new string('x', 101)).Error.Code);
        Assert.Equal("Audit.Entry.ActorNameTooLong", AuditEntry.Record("m", "a", actorName: new string('x', 101)).Error.Code);
        Assert.Equal("Audit.Entry.SummaryTooLong", AuditEntry.Record("m", "a", summary: new string('x', 501)).Error.Code);
        Assert.Equal("Audit.Entry.DetailsTooLong", AuditEntry.Record("m", "a", details: new string('x', 4001)).Error.Code);
        Assert.True(AuditEntry.Record("m", "a", details: new string('x', 4000)).IsSuccess);
    }

    [Fact]
    public void Record_RejectsAnEmptyActorGuid()
        => Assert.Equal("Audit.Entry.ActorInvalid", AuditEntry.Record("m", "a", actorId: Guid.Empty).Error.Code);

    [Fact]
    public void Record_ConvertsLocalTimesToUtc()
    {
        var local = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local);
        var utc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(local.ToUniversalTime(), AuditEntry.Record("m", "a", occurredAt: local).Value.OccurredAt);
        Assert.Equal(utc, AuditEntry.Record("m", "a", occurredAt: utc).Value.OccurredAt);
    }

    [Fact]
    public void AnEntryIsImmutable_ThereAreNoPublicSetters_OrMutatingMethods()
    {
        var type = typeof(AuditEntry);

        Assert.All(type.GetProperties(), p => Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, $"{p.Name} has a public setter"));
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).ToList();
        Assert.Empty(methods);
    }

    [Fact]
    public void Ids_AreUnique()
        => Assert.NotEqual(AuditEntryId.New(), AuditEntryId.New());
}
