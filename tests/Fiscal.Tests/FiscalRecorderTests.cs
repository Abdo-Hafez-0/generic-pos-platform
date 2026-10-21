using System.Security.Cryptography;
using System.Text;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Results;
using Tests.Common;

namespace Fiscal.Tests;

/// <summary>
/// A country part for tests: the payload names the document, its number and the previous hash; the hash is the SHA-256 of the payload (like
/// Egypt's UUID). <see cref="Refuse"/> makes it refuse to build (a product without the authority's code), <see cref="Throw"/> makes it throw.
/// </summary>
public sealed class TestCountryPart(FiscalCountry country = FiscalCountry.Egypt) : IFiscalCountryPart
{
    public FiscalCountry Country => country;
    public string? Refuse { get; set; }
    public bool Throw { get; set; }
    public List<FiscalBuildInput> Built { get; } = [];

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        if (Throw) throw new InvalidOperationException("the part broke");
        if (Refuse is { } reason) return Result.Failure<FiscalBuiltDocument>(Error.Validation("Test.Refused", reason));

        Built.Add(input);
        var payload = $"{input.Request.Kind}|{input.Request.Reference}|{input.Number}|{input.PreviousHash}|{input.Request.Total:0.00}|{input.Settings.TaxNumber}|{input.ReferenceOldHash}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return Result.Success(new FiscalBuiltDocument(payload, hash, $"QR:{hash}"));
    }

    /// <summary>MISS-11a-3: what the authority does with a document handed over (default: takes it, answer later).</summary>
    public Func<FiscalOutgoingDocument, FiscalSendOutcome>? OnSend { get; set; }

    /// <summary>MISS-11a-3: the authority's answer when checked (default: accepted).</summary>
    public Func<FiscalOutgoingDocument, FiscalSendOutcome>? OnCheck { get; set; }

    public bool SendThrows { get; set; }
    public List<IReadOnlyList<FiscalOutgoingDocument>> Sends { get; } = [];
    public List<IReadOnlyList<FiscalOutgoingDocument>> Checks { get; } = [];

    /// <summary>MISS-11c-4: how many sends asked for a fresh sign-in ("Send late receipts").</summary>
    public int FreshSignIns { get; private set; }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
    {
        if (input.FreshSignIn) FreshSignIns++;
        if (SendThrows) throw new HttpRequestException("No connection to the tax authority.");
        Sends.Add(input.Documents);
        return Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents
            .Select(d => OnSend?.Invoke(d) ?? new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Sent, "SUB-" + d.Number)).ToList());
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
    {
        Checks.Add(input.Documents);
        return Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents
            .Select(d => OnCheck?.Invoke(d) ?? new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Accepted)).ToList());
    }
}

/// <summary>MISS-11a-2: the fiscal document of each sale and return, chained per till.</summary>
public sealed class FiscalRecorderTests
{
    private readonly TestCountryPart _part = new();

    private Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync(bool withPart = true)
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            if (withPart) s.AddSingleton<IFiscalCountryPart>(_part);
        });

    private static async Task SwitchOnEgyptAsync(TestModuleDatabase<FiscalDbContext> db)
        => Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new Fiscal.Domain.Entities.FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))))).IsSuccess);

    private static FiscalDocumentRequest Sale(Guid? id = null, decimal total = 10m)
    {
        var saleId = id ?? Guid.NewGuid();
        return new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, saleId, "S-" + saleId.ToString("N")[..8].ToUpperInvariant(), DateTime.UtcNow,
            [new FiscalLineRequest(Guid.NewGuid(), "COLA", "Cola", 4m, 2.5m, 0m, total, 0.14m, decimal.Round(total * 0.14m / 1.14m, 2))],
            total, decimal.Round(total * 0.14m / 1.14m, 2), [new FiscalPaymentRequest("Cash", total)], "owner");
    }

    private static Task<FiscalDocumentResult> RecordAsync(TestModuleDatabase<FiscalDbContext> db, FiscalDocumentRequest request)
        => db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(request));

    private static Task<List<Fiscal.Domain.Entities.FiscalDocument>> DocumentsAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Number).ToListAsync());

    [Fact]
    public async Task With_receipts_off_or_without_the_countrys_part_nothing_is_made()
    {
        await using var off = await DatabaseAsync();
        var nothing = await RecordAsync(off, Sale());
        Assert.Equal((true, false, (Guid?)null), (nothing.IsSuccess, nothing.IsIssued, nothing.DocumentId));
        Assert.Empty(await DocumentsAsync(off));

        await using var noPart = await DatabaseAsync(withPart: false);
        await SwitchOnEgyptAsync(noPart);
        Assert.False((await RecordAsync(noPart, Sale())).IsIssued);
        Assert.Empty(await DocumentsAsync(noPart));
    }

    [Fact]
    public async Task Each_document_takes_the_next_number_of_the_till_and_names_the_previous_ones_hash()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);

        var first = await RecordAsync(db, Sale());
        var second = await RecordAsync(db, Sale(total: 5m));
        var saleId = Guid.NewGuid();
        var returned = await RecordAsync(db, Sale(saleId) with
        {
            Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "R-00000001", OriginalSourceId = saleId, OriginalReference = "S-ORIGINAL",
        });

        Assert.Equal([1L, 2L, 3L], new[] { first.Number!.Value, second.Number!.Value, returned.Number!.Value });
        var documents = await DocumentsAsync(db);
        Assert.All(documents, d => Assert.Equal(FiscalDocumentState.Waiting, d.State));
        Assert.Null(documents[0].PreviousHash);
        Assert.Equal(documents[0].Hash, documents[1].PreviousHash);
        Assert.Equal(documents[1].Hash, documents[2].PreviousHash);
        Assert.Equal((FiscalDocumentKind.Return, saleId, "S-ORIGINAL"), (documents[2].Kind, documents[2].OriginalSourceId, documents[2].OriginalReference));
        Assert.Equal("QR:" + documents[0].Hash, first.QrPayload);
        Assert.Equal("123456789", _part.Built[0].Settings.TaxNumber);   // the part builds with the seller's details
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.Equal((3L, documents[2].Hash), (till.Counter, till.LastDocumentHash));
    }

    [Fact]
    public async Task The_same_sale_asked_twice_keeps_its_one_document()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var sale = Sale();

        var first = await RecordAsync(db, sale);
        var again = await RecordAsync(db, sale);

        Assert.Equal((first.DocumentId, first.Number), (again.DocumentId, again.Number));
        Assert.Single(await DocumentsAsync(db));
        Assert.Single(_part.Built);
    }

    [Fact]
    public async Task A_document_the_part_cannot_build_is_kept_with_its_reason_outside_the_chain_and_the_sale_stands()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        await RecordAsync(db, Sale());

        _part.Refuse = "'Cola' has no item code from the tax authority.";
        var refused = await RecordAsync(db, Sale());
        _part.Refuse = null;
        _part.Throw = true;
        var broken = await RecordAsync(db, Sale());
        _part.Throw = false;
        var next = await RecordAsync(db, Sale());

        Assert.True(refused.IsSuccess && !refused.IsIssued);
        Assert.Equal("'Cola' has no item code from the tax authority.", refused.NotBuiltReason);
        Assert.Equal("the part broke", broken.NotBuiltReason);
        Assert.Equal(2, next.Number);                                                     // the chain was not advanced by the failures
        var documents = await DocumentsAsync(db);
        Assert.Equal(2, documents.Count(d => d.State == FiscalDocumentState.NotBuilt));
        Assert.All(documents.Where(d => d.State == FiscalDocumentState.NotBuilt), d => Assert.Null(d.Number));
        Assert.Equal(documents.Single(d => d.Number == 1).Hash, documents.Single(d => d.Number == 2).PreviousHash);
    }

    [Fact]
    public async Task Once_a_document_is_issued_the_country_is_locked()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        await RecordAsync(db, Sale());

        var changed = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.None, null, null, null, null, null)));

        Assert.Equal("Fiscal.Settings.CountryLocked", changed.Error.Code);
    }
}
