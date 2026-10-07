using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Users.Application.Commands;
using Users.Application.DTOs;
using Users.Application.Queries;
using Users.UI.Resources;

namespace Users.UI.ViewModels;

/// <summary>
/// One permission as the role's list shows it: what it allows, whether it is sensitive, and whether the role has it. Changing
/// <see cref="IsGranted"/> (a tick by mouse, keyboard or UI Automation) asks the screen to grant or revoke; the list is then rebuilt from
/// what was stored, so a refused change shows the old state again.
/// </summary>
public sealed class PermissionRow(string code, string module, string name, string description, bool isSensitive, bool isGranted, Action<PermissionRow, bool> changed)
    : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isGranted = isGranted;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Code { get; } = code;
    public string Module { get; } = module;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsSensitive { get; } = isSensitive;

    public bool IsGranted
    {
        get => _isGranted;
        set
        {
            if (_isGranted == value) return;
            _isGranted = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsGranted)));
            changed(this, value);
        }
    }
}

/// <summary>
/// Roles and permissions (FIX-01e): the roles, a new role, and for the selected role every capability the installed modules declare
/// (from the capability catalog) with a tick for "allowed". Ticking grants, unticking revokes - immediately, through the handlers, which
/// authorize (users.manage) and refuse taking users.manage away from the last role that lets someone manage users. A permission a role holds
/// that no installed module declares is still listed, so it can be removed.
/// </summary>
public sealed class RolesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly ICapabilityCatalog _capabilities;
    private RoleDto? _selectedRole;
    private string _newName = string.Empty;
    private string _newDescription = string.Empty;

    public RolesViewModel(IUiActionRunner runner, ICapabilityCatalog capabilities)
    {
        _runner = runner;
        _capabilities = capabilities;
        CreateCommand = Command(CreateAsync, () => !string.IsNullOrWhiteSpace(NewName));
    }

    public ObservableCollection<RoleDto> Roles { get; } = [];
    public ObservableCollection<PermissionRow> Permissions { get; } = [];

    public ICommand CreateCommand { get; }

    public RoleDto? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (!Set(ref _selectedRole, value)) return;
            ShowPermissions(value);
            Raise(nameof(HasRole));
            Raise(nameof(HasNoRole));
            Raise(nameof(PermissionsHeading));
        }
    }

    public bool HasRole => SelectedRole is not null;
    public bool HasNoRole => SelectedRole is null;
    public string PermissionsHeading => SelectedRole is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, UsersText.Permissions, SelectedRole.Name);

    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string NewDescription { get => _newDescription; set => Set(ref _newDescription, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => ReloadAsync(SelectedRole?.RoleId, cancellationToken));

    private async Task CreateAsync()
    {
        var (name, description) = (NewName.Trim(), string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim());
        var created = await _runner.RunAsync((scope, ct) => scope.Get<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand(name, description), ct));
        if (!Accept(created)) return;

        NewName = NewDescription = string.Empty;
        await ReloadAsync(created.Value, CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.RoleCreated, name);
    }

    /// <summary>Grants (true) or revokes (false) the permission of the row for the selected role; the list is rebuilt afterwards.</summary>
    public Task SetGrantedAsync(PermissionRow row, bool grant) => BusyAsync(() => ToggleAsync(row, grant));

    private async Task ToggleAsync(PermissionRow row, bool grant)
    {
        if (SelectedRole is not { } role) return;
        var done = await _runner.RunAsync((scope, ct) => grant
            ? scope.Get<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role.RoleId, row.Code), ct)
            : scope.Get<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role.RoleId, row.Code), ct));

        // reload in every case: a refused change must show the stored state again (the tick the user clicked is undone)
        var message = done.IsSuccess ? string.Format(CultureInfo.CurrentCulture, grant ? UsersText.Granted : UsersText.Revoked, row.Name, role.Name) : null;
        await ReloadAsync(role.RoleId, CancellationToken.None);
        if (Accept(done)) StatusMessage = message;
    }

    private async Task ReloadAsync(Guid? selectRoleId, CancellationToken cancellationToken)
    {
        var roles = await _runner.RunAsync((scope, ct) => scope.Get<ListRolesQueryHandler>().HandleAsync(new ListRolesQuery(), ct), cancellationToken);
        if (!Accept(roles)) return;

        Roles.Clear();
        foreach (var role in roles.Value.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)) Roles.Add(role);
        _selectedRole = null;
        SelectedRole = Roles.FirstOrDefault(r => r.RoleId == selectRoleId);
    }

    private void ShowPermissions(RoleDto? role)
    {
        Permissions.Clear();
        if (role is null) return;

        var granted = new HashSet<string>(role.Permissions, StringComparer.OrdinalIgnoreCase);
        foreach (var capability in _capabilities.All.OrderBy(c => c.Module, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal))
            Permissions.Add(new PermissionRow(capability.Code, capability.Module, capability.DisplayName, capability.Description, capability.IsSensitive, granted.Contains(capability.Code), OnChanged));

        // a permission the role holds that no installed module declares (for example a module that was removed) stays visible and removable
        foreach (var orphan in role.Permissions.Where(p => _capabilities.Find(p) is null).OrderBy(p => p, StringComparer.Ordinal))
            Permissions.Add(new PermissionRow(orphan, string.Empty, orphan, UsersText.UnknownPermission, false, true, OnChanged));
    }

    private void OnChanged(PermissionRow row, bool grant) => _ = SetGrantedAsync(row, grant);
}
