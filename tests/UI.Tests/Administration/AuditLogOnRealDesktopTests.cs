using System.Windows.Input;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Audit.UI.Resources;
using Audit.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using UI.Tests.Pos;

namespace UI.Tests.Administration;

/// <summary>FIX-01d: the audit log screen on the production-like offline desktop (its clock starts at OfflineDesktop.Start).</summary>
[Collection(nameof(RealDesktop))]
public sealed class AuditLogOnRealDesktopTests
{
    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static AuditLogViewModel Screen(IServiceProvider services) => new(new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance))
    {
        // a period that contains the test clock and today, whatever the test clock is
        FromDate = OfflineDesktop.Start.LocalDateTime.Date.AddDays(-1),
        ToDate = DateTime.Today.AddDays(1),
    };

    [Fact]
    public async Task The_sign_in_is_in_the_log_and_filters_narrow_it_down()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = Screen(desktop.Services);

        (vm.Module, vm.Action) = ("security", "security.signin.succeeded");
        await Run(vm, vm.ShowCommand);
        vm.Selected = Assert.Single(vm.Entries);
        Assert.Equal("security", vm.Selected.Entry.Module);
        Assert.NotNull(vm.Selected.Entry.ActorName);

        vm.Module = "no-such-module";
        await Run(vm, vm.ShowCommand);
        Assert.Empty(vm.Entries);
        Assert.Equal(AuditText.NoEntries, vm.PageInfo);
    }

    [Fact]
    public async Task More_than_a_page_is_paged_newest_first_and_the_details_of_an_entry_are_shown()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        using (var scope = desktop.Services.CreateScope())
        {
            var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
            for (var i = 0; i < 120; i++)
                Assert.True((await recorder.RecordAsync(new AuditRecordRequest("uitest", "uitest.step", "step", i.ToString(), Summary: $"step {i}", Details: $"details of step {i}"))).IsSuccess);
        }

        var vm = Screen(desktop.Services);
        vm.Module = "uitest";
        await Run(vm, vm.ShowCommand);

        Assert.Equal(AuditLogViewModel.PageSize, vm.Entries.Count);
        Assert.False(vm.NewerCommand.CanExecute(null));
        await Run(vm, vm.OlderCommand);
        Assert.Equal(20, vm.Entries.Count);
        Assert.False(vm.OlderCommand.CanExecute(null));

        vm.Selected = vm.Entries.Single(e => e.Entry.EntityId == "0");
        Assert.Contains("details of step 0", vm.DetailsText);
    }
}
