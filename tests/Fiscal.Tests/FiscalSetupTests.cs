using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.Queries;
using Fiscal.Application.Security;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>MISS-11a-1: the fiscal settings (country, the shop's tax details) and this till's record.</summary>
public sealed class FiscalSetupTests
{
    private sealed class EgyptPart : IFiscalCountryPart
    {
        public FiscalCountry Country => FiscalCountry.Egypt;

        public Platform.Core.Results.Result<FiscalBuiltDocument> Build(FiscalBuildInput input) => throw new NotSupportedException();

        public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync(Action<IServiceCollection>? more = null)
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            more?.Invoke(s);
        });

    private static SaveFiscalSettingsCommand Egypt(string tax = "123456789") => new(FiscalCountry.Egypt, tax, "0", "4711", "متجر التجربة", new Fiscal.Domain.Entities.FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"));

    [Fact]
    public async Task Before_anything_is_saved_receipts_are_off_and_the_till_is_created_once_named_after_the_computer()
    {
        await using var db = await DatabaseAsync();

        var first = await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()));
        var second = await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()));

        Assert.Equal((FiscalCountry.None, false, false, 1L), (first.Value.Country, first.Value.IsOn, first.Value.CountryLocked, first.Value.NextNumber));
        Assert.Equal((Environment.MachineName, FiscalTillRegistration.NotRegistered), (first.Value.TillName, first.Value.TillRegistration));
        Assert.Null(first.Value.UpdatedAt);
        Assert.Equal(1, await db.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<FiscalDbContext>().Tills.Count())));
        Assert.Equal(first.Value.TillName, second.Value.TillName);
    }

    [Fact]
    public async Task Egypts_details_are_saved_and_read_back_and_without_the_country_part_nothing_is_issued_yet()
    {
        await using var db = await DatabaseAsync();

        var saved = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(Egypt()));
        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        Assert.Equal((FiscalCountry.Egypt, "123456789", "0", "4711", "متجر التجربة"), (setup.Country, setup.TaxNumber, setup.BranchCode, setup.ActivityCode, setup.SellerNameArabic));
        Assert.True(setup.IsOn);
        Assert.False(setup.IsIssuing);                                                // the Egypt part comes in 11c
        Assert.Equal(FiscalCountryContract.Egypt, await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSetupReader>().GetCountryAsync()));
    }

    [Fact]
    public async Task With_the_country_part_installed_the_setup_says_documents_are_issued()
    {
        await using var db = await DatabaseAsync(s => s.AddSingleton<IFiscalCountryPart, EgyptPart>());
        await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(Egypt()));

        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;

        Assert.True(setup.IsIssuing);
    }

    [Theory]
    [InlineData(FiscalCountry.Egypt, "12345678", "0", "4711", "Fiscal.Settings.TaxNumberInvalid")]      // 8 digits
    [InlineData(FiscalCountry.Egypt, "123456789", "A", "4711", "Fiscal.Settings.BranchInvalid")]
    [InlineData(FiscalCountry.Egypt, "123456789", "0", "47", "Fiscal.Settings.ActivityInvalid")]
    [InlineData(FiscalCountry.SaudiArabia, "300000000000004", null, null, "Fiscal.Settings.TaxNumberInvalid")]   // must end with 3
    [InlineData(FiscalCountry.SaudiArabia, "30000000000003", null, null, "Fiscal.Settings.TaxNumberInvalid")]    // 14 digits
    public async Task The_tax_details_must_be_in_the_authoritys_form(FiscalCountry country, string tax, string? branch, string? activity, string code)
    {
        await using var db = await DatabaseAsync();

        var refused = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(country, tax, branch, activity, "متجر", new Fiscal.Domain.Entities.FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))));

        Assert.Equal(code, refused.Error.Code);
        Assert.Null(await db.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<FiscalDbContext>().Settings.FirstOrDefault())));
    }

    [Fact]
    public async Task Saudi_Arabia_needs_its_VAT_number_and_the_legal_name_in_Arabic_and_keeps_no_branch_or_activity()
    {
        await using var db = await DatabaseAsync();

        var noName = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.SaudiArabia, "300000000000003", null, null, " ", null)));
        var saved = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.SaudiArabia, " 300000000000003 ", "7", "1234", "متجر", null)));

        Assert.Equal("Fiscal.Settings.NameRequired", noName.Error.Code);
        Assert.True(saved.IsSuccess);
        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Equal(("300000000000003", (string?)null, (string?)null), (setup.TaxNumber, setup.BranchCode, setup.ActivityCode));
    }

    [Fact]
    public async Task Once_the_till_has_issued_a_document_the_country_is_locked_but_the_details_can_still_be_corrected()
    {
        await using var db = await DatabaseAsync();
        await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(Egypt()));
        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<FiscalDbContext>();
            context.Tills.Single().Chain("hash-of-the-first-document");
            return await context.SaveChangesAsync();
        });

        var other = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.SaudiArabia, "300000000000003", null, null, "متجر", null)));
        var off = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(Egypt() with { Country = FiscalCountry.None }));
        var corrected = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(Egypt("987654321")));

        Assert.Equal("Fiscal.Settings.CountryLocked", other.Error.Code);
        Assert.Equal("Fiscal.Settings.CountryLocked", off.Error.Code);
        Assert.True(corrected.IsSuccess);
        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Equal((FiscalCountry.Egypt, "987654321", true, 2L), (setup.Country, setup.TaxNumber, setup.CountryLocked, setup.NextNumber));
    }

    [Fact]
    public async Task Setting_up_electronic_receipts_needs_the_fiscal_manage_permission()
    {
        var authorization = new ScriptedAuthorizationService();
        await using var db = await DatabaseAsync(s => s.AddSingleton<IAuthorizationService>(authorization));

        var read = await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()));
        var save = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(Egypt()));

        Assert.True(read.IsFailure);
        Assert.True(save.IsFailure);
        Assert.All(authorization.Asked, c => Assert.Equal(FiscalCapabilities.Manage, c));
        Assert.Null(await db.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<FiscalDbContext>().Settings.FirstOrDefault())));
    }

    [Fact]
    public void The_permission_is_sensitive_and_belongs_to_the_licensed_fiscal_module()
    {
        var capability = Assert.Single(new FiscalCapabilityProvider().GetCapabilities());
        Assert.Equal(("fiscal.manage", "fiscal", true, LicenseRequirement.Module), (capability.Code, capability.Module, capability.IsSensitive, capability.License));
    }

    [Fact]
    public void The_chain_takes_the_next_number_and_keeps_the_last_hash()
    {
        var till = FiscalTill.Create("  ", DateTime.UtcNow);

        Assert.Equal("Till", till.Name);
        Assert.False(till.HasIssued);
        Assert.Equal(1, till.Chain("a"));
        Assert.Equal(2, till.Chain("b"));
        Assert.Equal(("b", true), (till.LastDocumentHash, till.HasIssued));
        Assert.Throws<ArgumentException>(() => till.Chain(" "));
    }
}
