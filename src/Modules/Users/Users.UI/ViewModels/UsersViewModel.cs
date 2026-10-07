using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Users.Application.Commands;
using Users.Application.DTOs;
using Users.Application.Queries;
using Users.Application.Security;
using Users.Domain.Enums;
using Users.UI.Resources;

namespace Users.UI.ViewModels;

/// <summary>One user as the list shows it.</summary>
public sealed record UserRow(UserSummaryDto User, string StatusText)
{
    public bool IsActive => User.Status == UserStatus.Active;
}

/// <summary>
/// Users (FIX-01e): find users; create one with a temporary password (the user must change it at the first sign-in); change name and e-mail;
/// deactivate / reactivate; give and take away roles; set a new temporary password. Reading needs users.view, every change users.manage
/// (the handlers authorize). The handlers also refuse any change that would leave nobody able to manage users, and deactivating oneself.
///
/// Passwords: the view hands a password over at the moment of use (from a password box); this view model never stores, shows or logs it.
/// </summary>
public sealed class UsersViewModel : ViewModelBase, INavigationAware
{
    public const int PageSize = 200;

    private readonly IUiActionRunner _runner;

    private string _searchText = string.Empty;
    private bool _showInactive;
    private UserRow? _selectedRow;
    private UserDto? _user;
    private string? _resultInfo;
    private string _newUsername = string.Empty;
    private string _newDisplayName = string.Empty;
    private string _newEmail = string.Empty;
    private string _editDisplayName = string.Empty;
    private string _editEmail = string.Empty;
    private RoleDto? _roleToAssign;
    private RoleSummaryDto? _selectedRole;

    public UsersViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        SearchCommand = Command(() => LoadAsync(CancellationToken.None));
        CreateCommand = Command<string>(CreateAsync, _ => CanCreate);
        SaveDetailsCommand = Command(SaveDetailsAsync, () => User is not null && !string.IsNullOrWhiteSpace(EditDisplayName));
        DeactivateCommand = Command(() => SetActiveAsync(false), () => User is { Status: UserStatus.Active });
        ReactivateCommand = Command(() => SetActiveAsync(true), () => User is { Status: UserStatus.Inactive });
        AssignRoleCommand = Command(AssignRoleAsync, () => User is not null && RoleToAssign is not null && User.Roles.All(r => r.RoleId != RoleToAssign.RoleId));
        RemoveRoleCommand = Command(RemoveRoleAsync, () => User is not null && SelectedRole is not null);
        ResetPasswordCommand = Command<string>(ResetPasswordAsync, _ => User is { Status: UserStatus.Active });
    }

    public ObservableCollection<UserRow> Users { get; } = [];
    public ObservableCollection<RoleDto> AllRoles { get; } = [];
    public ObservableCollection<RoleSummaryDto> UserRoles { get; } = [];

    public ICommand SearchCommand { get; }

    /// <summary>Parameter: the temporary password, read from the password box at the moment of use.</summary>
    public ICommand CreateCommand { get; }
    public ICommand SaveDetailsCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ReactivateCommand { get; }
    public ICommand AssignRoleCommand { get; }
    public ICommand RemoveRoleCommand { get; }

    /// <summary>Parameter: the new temporary password, read from the password box at the moment of use.</summary>
    public ICommand ResetPasswordCommand { get; }

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool ShowInactive { get => _showInactive; set => Set(ref _showInactive, value); }
    public string? ResultInfo { get => _resultInfo; private set => Set(ref _resultInfo, value); }

    /// <summary>The user selected in the list; selecting one loads their details and roles.</summary>
    public UserRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value) && value is not null && value.User.UserId != User?.UserId)
                _ = BusyAsync(() => LoadUserAsync(value.User.UserId));
        }
    }

    /// <summary>The user being worked on, or null.</summary>
    public UserDto? User
    {
        get => _user;
        private set
        {
            if (!Set(ref _user, value)) return;
            EditDisplayName = value?.DisplayName ?? string.Empty;
            EditEmail = value?.Email ?? string.Empty;
            UserRoles.Clear();
            if (value is not null) foreach (var role in value.Roles.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)) UserRoles.Add(role);
            SelectedRole = null;
            Raise(nameof(HasUser));
            Raise(nameof(HasNoUser));
            Raise(nameof(UserHeading));
        }
    }

    public bool HasUser => User is not null;
    public bool HasNoUser => User is null;
    public string UserHeading => User is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, UsersText.SelectedHeading, User.DisplayName, User.Username);

    public string NewUsername { get => _newUsername; set { if (Set(ref _newUsername, value)) Raise(nameof(CanCreate)); } }
    public string NewDisplayName { get => _newDisplayName; set { if (Set(ref _newDisplayName, value)) Raise(nameof(CanCreate)); } }
    public string NewEmail { get => _newEmail; set => Set(ref _newEmail, value); }

    /// <summary>True when user name and name are filled in (the password is checked when the button is used).</summary>
    public bool CanCreate => !string.IsNullOrWhiteSpace(NewUsername) && !string.IsNullOrWhiteSpace(NewDisplayName);

    public string EditDisplayName { get => _editDisplayName; set => Set(ref _editDisplayName, value); }
    public string EditEmail { get => _editEmail; set => Set(ref _editEmail, value); }
    public RoleDto? RoleToAssign { get => _roleToAssign; set => Set(ref _roleToAssign, value); }
    public RoleSummaryDto? SelectedRole { get => _selectedRole; set => Set(ref _selectedRole, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        var roles = await _runner.RunAsync((scope, ct) => scope.Get<ListRolesQueryHandler>().HandleAsync(new ListRolesQuery(), ct), cancellationToken);
        if (roles.IsSuccess)
        {
            AllRoles.Clear();
            foreach (var role in roles.Value.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)) AllRoles.Add(role);
        }

        await LoadAsync(cancellationToken);
        if (User is { } user) await LoadUserAsync(user.UserId);
    });

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var (search, inactive) = (string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(), ShowInactive);
        var page = await _runner.RunAsync((scope, ct) => scope.Get<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery(search, inactive, 1, PageSize), ct), cancellationToken);
        if (!Accept(page)) return;

        Users.Clear();
        foreach (var user in page.Value.Items)
            Users.Add(new UserRow(user, user.Status == UserStatus.Active ? UsersText.Active : UsersText.Inactive));
        _selectedRow = Users.FirstOrDefault(u => u.User.UserId == User?.UserId);
        Raise(nameof(SelectedRow));
        ResultInfo = page.Value.TotalCount > Users.Count ? string.Format(CultureInfo.CurrentCulture, UsersText.Count, Users.Count, page.Value.TotalCount) : null;
    }

    private async Task LoadUserAsync(Guid userId)
    {
        var user = await _runner.RunAsync((scope, ct) => scope.Get<GetUserQueryHandler>().HandleAsync(new GetUserQuery(userId), ct));
        if (Accept(user)) User = user.Value;
    }

    private async Task CreateAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) { Fail(UsersText.PasswordRequired); return; }

        var (username, displayName, email) = (NewUsername.Trim(), NewDisplayName.Trim(), Blank(NewEmail));
        var created = await _runner.RunAsync<Guid>(async (scope, ct) =>
        {
            // the password policy first, so a refused password never leaves a user who cannot sign in
            var normalized = global::Users.Domain.Entities.User.NormalizeUsername(username);
            var policy = scope.Get<UsersSecurityOptions>().Passwords.Validate(password, normalized.IsSuccess ? normalized.Value : username);
            if (policy.IsFailure) return Result.Failure<Guid>(policy.Error);

            var id = await scope.Get<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand(username, displayName, email), ct);
            if (id.IsFailure) return id;

            var set = await scope.Get<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(id.Value, password, MustChangeOnNextSignIn: true), ct);
            return set.IsFailure ? Result.Failure<Guid>(set.Error) : id;
        });
        if (!Accept(created)) return;

        (NewUsername, NewDisplayName, NewEmail) = (string.Empty, string.Empty, string.Empty);
        await LoadAsync(CancellationToken.None);
        await LoadUserAsync(created.Value);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.UserCreated, username);
    }

    private async Task SaveDetailsAsync()
    {
        if (User is not { } user) return;
        var (name, email) = (EditDisplayName.Trim(), Blank(EditEmail));
        if (await ChangeAsync(user.UserId, (scope, ct) => scope.Get<UpdateUserCommandHandler>().HandleAsync(new UpdateUserCommand(user.UserId, name, email), ct)))
            StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.DetailsSaved, name);
    }

    private async Task SetActiveAsync(bool active)
    {
        if (User is not { } user) return;
        if (await ChangeAsync(user.UserId, (scope, ct) => active
                ? scope.Get<ReactivateUserCommandHandler>().HandleAsync(new ReactivateUserCommand(user.UserId), ct)
                : scope.Get<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(user.UserId), ct)))
            StatusMessage = string.Format(CultureInfo.CurrentCulture, active ? UsersText.Reactivated : UsersText.Deactivated, user.DisplayName);
    }

    private async Task AssignRoleAsync()
    {
        if (User is not { } user || RoleToAssign is not { } role) return;
        if (await ChangeAsync(user.UserId, (scope, ct) => scope.Get<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user.UserId, role.RoleId), ct)))
            StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.RoleAssigned, user.DisplayName, role.Name);
    }

    private async Task RemoveRoleAsync()
    {
        if (User is not { } user || SelectedRole is not { } role) return;
        if (await ChangeAsync(user.UserId, (scope, ct) => scope.Get<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user.UserId, role.RoleId), ct)))
            StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.RoleRemoved, user.DisplayName, role.Name);
    }

    private async Task ResetPasswordAsync(string? password)
    {
        if (User is not { } user) return;
        if (string.IsNullOrEmpty(password)) { Fail(UsersText.PasswordRequired); return; }

        var set = await _runner.RunAsync((scope, ct) => scope.Get<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(user.UserId, password, MustChangeOnNextSignIn: true), ct));
        if (Accept(set)) StatusMessage = string.Format(CultureInfo.CurrentCulture, UsersText.PasswordSet, user.DisplayName);
    }

    /// <summary>Runs a change and reloads the user and the list; false (with the plain message shown) when the change was refused.</summary>
    private async Task<bool> ChangeAsync(Guid userId, Func<IActionScope, CancellationToken, Task<Result>> change)
    {
        var changed = await _runner.RunAsync<UserDto?>(async (scope, ct) =>
        {
            var result = await change(scope, ct);
            if (result.IsFailure) return Result.Failure<UserDto?>(result.Error);
            return await scope.Get<GetUserQueryHandler>().HandleAsync(new GetUserQuery(userId), ct);
        });
        if (!Accept(changed)) return false;

        User = changed.Value;
        await LoadAsync(CancellationToken.None);
        return true;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
