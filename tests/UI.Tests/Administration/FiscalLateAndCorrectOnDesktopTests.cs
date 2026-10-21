using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Client.Host.Hosting;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Fiscal.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using UI.Tests.Pos;

namespace UI.Tests.Administration;

/// <summary>
/// MISS-11c-4 (owner's choice A) on the offline desktop: the late receipts' line with "Send late receipts" above the documents, and a refused
/// or not-built row's "…" - "Correct and resend" / "Build and send" - building it again and sending it at once.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class FiscalLateAndCorrectOnDesktopTests
{
    /// <summary>Egypt's place taken by a stand-in: builds while allowed, and the authority takes everything (or refuses what is named).</summary>
    private sealed class StandIn : IFiscalCountryPart
    {
        public string? Refuse { get; set; }
        public HashSet<string> RefuseAtAuthority { get; } = [];
        public FiscalCountry Country => FiscalCountry.Egypt;

        public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
        {
            if (Refuse is { } reason) return Result.Failure<FiscalBuiltDocument>(Error.Validation("Test.Refused", reason));
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{input.Request.Reference}|{input.Number}|{input.PreviousHash}|{input.ReferenceOldHash}")));
            return Result.Success(new FiscalBuiltDocument("{}", hash, "QR:" + hash));
        }

        public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Sent, "SUB-1")).ToList());

        public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents.Select(d => RefuseAtAuthority.Remove(d.Reference)
                ? new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Rejected, Reason: "Item code is invalid")
                : new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Accepted)).ToList());
    }

    private sealed class StandInModule(StandIn part) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddSingleton<IFiscalCountryPart>(part);
    }

    private static async Task Run(FiscalViewModel vm, ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static FiscalDocumentRequest Sale(string reference, DateTime at)
        => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), reference, at, [new FiscalLineRequest(Guid.NewGuid(), "COLA", "Cola", 1m, 10m, 0m, 10m, 0.14m, 1.23m)],
            10m, 1.23m, [new FiscalPaymentRequest("Cash", 10m)]);

    [Fact]
    public async Task Late_receipts_are_sent_from_their_line_and_a_refused_or_not_built_one_from_its_row_menu()
    {
        var part = new StandIn();
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new StandInModule(part)]);
        async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work) { using var scope = desktop.Services.CreateScope(); return await work(scope.ServiceProvider); }
        Assert.True((await InScope(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt,
            "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))))).IsSuccess);

        // a sale the authority refuses, one that could not be built, and one from yesterday that never went out (late)
        part.RefuseAtAuthority.Add("S-REFUSED");
        await InScope(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-REFUSED", desktop.Clock.GetUtcNow().UtcDateTime)));
        part.Refuse = "'Cola' has no item code";
        await InScope(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-NOTBUILT", desktop.Clock.GetUtcNow().UtcDateTime)));
        part.Refuse = null;
        await InScope(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-LATE", desktop.Clock.GetUtcNow().UtcDateTime.AddHours(-30))));

        var vm = new FiscalViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await vm.OnNavigatedToAsync();
        await Run(vm, vm.SendNowCommand);

        Assert.True(vm.HasLate);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, FiscalText.LateLine, 1), vm.LateText);
        FiscalDocumentRow Row(string reference) => vm.Documents.Single(r => r.Document.Reference == reference && r.Document.State != FiscalDocumentState.Replaced);
        Assert.Equal((FiscalText.StateLate, false), (Row("S-LATE").StateText, Row("S-LATE").CanCorrect));
        Assert.Equal((FiscalText.StateRejected, true, FiscalText.RowCorrectAndResend), (Row("S-REFUSED").StateText, Row("S-REFUSED").CanCorrect, Row("S-REFUSED").CorrectText));
        Assert.Equal((true, FiscalText.RowBuildAndSend), (Row("S-NOTBUILT").CanCorrect, Row("S-NOTBUILT").CorrectText));
        Assert.False(vm.CorrectRowCommand.CanExecute(Row("S-LATE")));

        await Run(vm, vm.CorrectRowCommand, Row("S-REFUSED"));
        Assert.Null(vm.ErrorMessage);
        Assert.StartsWith(FiscalText.Corrected, vm.StatusMessage);
        Assert.Equal(FiscalText.StateAccepted, Row("S-REFUSED").StateText);
        Assert.Contains(vm.Documents, r => r.Document.Reference == "S-REFUSED" && r.StateText == FiscalText.StateReplaced);

        await Run(vm, vm.CorrectRowCommand, Row("S-NOTBUILT"));
        Assert.StartsWith(FiscalText.BuiltAgain, vm.StatusMessage);
        Assert.Equal(FiscalText.StateAccepted, Row("S-NOTBUILT").StateText);

        await Run(vm, vm.SendLateCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasLate);
        Assert.Equal(FiscalText.StateAccepted, Row("S-LATE").StateText);
    }
}
