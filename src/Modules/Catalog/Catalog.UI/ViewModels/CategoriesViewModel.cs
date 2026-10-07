using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Catalog.Application.Commands;
using Catalog.Application.DTOs;
using Catalog.Application.Queries;
using Catalog.UI.Resources;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Catalog.UI.ViewModels;

/// <summary>
/// Categories and units (FIX-01c): what a product must be filed under before it can be created. Lists and adds; the handlers authorize
/// (catalog.category.manage, catalog.unit.manage) and refuse duplicates in plain words.
/// </summary>
public sealed class CategoriesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private string _newCategoryName = string.Empty;
    private string _newCategoryDescription = string.Empty;
    private string _newUnitName = string.Empty;
    private string _newUnitAbbreviation = string.Empty;

    public CategoriesViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        AddCategoryCommand = Command(AddCategoryAsync, () => !string.IsNullOrWhiteSpace(NewCategoryName));
        AddUnitCommand = Command(AddUnitAsync, () => !string.IsNullOrWhiteSpace(NewUnitName) && !string.IsNullOrWhiteSpace(NewUnitAbbreviation));
    }

    public ObservableCollection<CategoryDto> Categories { get; } = [];
    public ObservableCollection<UnitDto> Units { get; } = [];

    public ICommand AddCategoryCommand { get; }
    public ICommand AddUnitCommand { get; }

    public string NewCategoryName { get => _newCategoryName; set => Set(ref _newCategoryName, value); }
    public string NewCategoryDescription { get => _newCategoryDescription; set => Set(ref _newCategoryDescription, value); }
    public string NewUnitName { get => _newUnitName; set => Set(ref _newUnitName, value); }
    public string NewUnitAbbreviation { get => _newUnitAbbreviation; set => Set(ref _newUnitAbbreviation, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => ReloadAsync(cancellationToken));

    private async Task AddCategoryAsync()
    {
        var (name, description) = (NewCategoryName.Trim(), string.IsNullOrWhiteSpace(NewCategoryDescription) ? null : NewCategoryDescription.Trim());
        var added = await _runner.RunAsync((scope, ct) => AsResult(scope.Get<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand(name, description), ct)));
        if (!Accept(added)) return;

        NewCategoryName = NewCategoryDescription = string.Empty;
        await ReloadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CatalogText.CategoryAdded, name);
    }

    private async Task AddUnitAsync()
    {
        var (name, abbreviation) = (NewUnitName.Trim(), NewUnitAbbreviation.Trim());
        var added = await _runner.RunAsync((scope, ct) => AsResult(scope.Get<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand(name, abbreviation), ct)));
        if (!Accept(added)) return;

        NewUnitName = NewUnitAbbreviation = string.Empty;
        await ReloadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CatalogText.UnitAdded, name);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _runner.QueryAsync(async (scope, ct) => (
            Categories: await scope.Get<GetAllCategoriesQueryHandler>().HandleAsync(new GetAllCategoriesQuery(), ct),
            Units: await scope.Get<GetAllUnitsQueryHandler>().HandleAsync(new GetAllUnitsQuery(), ct)), cancellationToken);
        if (!Accept(loaded)) return;

        Categories.Clear();
        if (loaded.Value.Categories.IsSuccess)
            foreach (var category in loaded.Value.Categories.Value.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)) Categories.Add(category);

        Units.Clear();
        if (loaded.Value.Units.IsSuccess)
            foreach (var unit in loaded.Value.Units.Value.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)) Units.Add(unit);
    }

    private static async Task<Result> AsResult<T>(Task<Result<T>> pending) => await pending;
}
