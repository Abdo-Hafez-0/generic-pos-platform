using System.Windows.Input;
using Platform.Core.Results;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Resources;

namespace UI.Tests;

/// <summary>The shared view-model base (FIX-01 decision 3).</summary>
public sealed class ViewModelBaseTests
{
    private sealed class SampleViewModel : ViewModelBase
    {
        private string? _name;

        public SampleViewModel()
        {
            Save = Command(() => Work!.Invoke(), () => Name is not null);
            Pick = Command<string>(value => { Picked = value; return Task.CompletedTask; });
        }

        public Func<Task>? Work { get; set; }
        public string? Picked { get; private set; }
        public ICommand Save { get; }
        public ICommand Pick { get; }

        public string? Name { get => _name; set => Set(ref _name, value); }

        public bool Show(Result result) => Accept(result);

        public void SetStatus(string text) => StatusMessage = text;
    }

    [Fact]
    public void Set_raises_only_on_a_real_change()
    {
        var vm = new SampleViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Name = "a";
        vm.Name = "a";

        Assert.Equal(["Name"], raised);
    }

    [Fact]
    public void A_command_re_evaluates_when_a_property_changes()
    {
        var vm = new SampleViewModel();
        var changes = 0;
        vm.Save.CanExecuteChanged += (_, _) => changes++;

        Assert.False(vm.Save.CanExecute(null));
        vm.Name = "ready";

        Assert.True(vm.Save.CanExecute(null));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task A_command_cannot_start_again_while_it_runs()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var vm = new SampleViewModel { Name = "x", Work = () => { runs++; return gate.Task; } };

        vm.Save.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.Save.CanExecute(null));
        vm.Save.Execute(null);

        gate.SetResult();
        await Task.Yield();
        Assert.Equal(1, runs);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void An_exception_inside_a_command_becomes_the_plain_message_instead_of_crashing()
    {
        var vm = new SampleViewModel { Name = "x", Work = () => throw new InvalidOperationException("raw details") };
        vm.SetStatus("old status");

        vm.Save.Execute(null);

        Assert.Equal(PresentationText.OperationFailed, vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void A_command_clears_the_previous_error_and_receives_its_parameter()
    {
        var vm = new SampleViewModel();
        vm.Show(Result.Failure(Error.Validation("X", "Earlier failure.")));

        vm.Pick.Execute("chosen");

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("chosen", vm.Picked);
    }

    [Fact]
    public void Accept_shows_the_business_failure_in_plain_words()
    {
        var vm = new SampleViewModel();
        vm.SetStatus("saved");

        Assert.False(vm.Show(Result.Failure(Error.Conflict("X", "Someone else changed it."))));
        Assert.Equal("Someone else changed it.", vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
        Assert.True(vm.Show(Result.Success()));
    }
}
