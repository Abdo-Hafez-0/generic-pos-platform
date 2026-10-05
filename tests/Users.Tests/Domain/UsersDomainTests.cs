using Users.Domain.Entities;
using Users.Domain.Enums;
using Users.Domain.ValueObjects;

namespace Users.Tests.Domain;

public sealed class UsersDomainTests
{
    private static User NewUser(string username = "Jane.Doe", string name = "Jane Doe", string? email = null)
    {
        var r = User.Create(username, name, email);
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    private static Role NewRole(string name = "Cashier")
    {
        var r = Role.Create(name, "desc");
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    // ------------------------------------------------------------------ user

    [Fact]
    public void Create_NormalisesUsername_AndStartsActive()
    {
        var u = NewUser("  Jane.Doe ", " Jane Doe ", " jane@example.com ");

        Assert.Equal("jane.doe", u.Username);
        Assert.Equal("Jane Doe", u.DisplayName);
        Assert.Equal("jane@example.com", u.Email);
        Assert.Equal(UserStatus.Active, u.Status);
        Assert.Empty(u.Roles);
        Assert.NotEqual(UserId.Empty, u.Id);
    }

    [Theory]
    [InlineData("", "Users.User.UsernameRequired")]
    [InlineData("  ", "Users.User.UsernameRequired")]
    [InlineData("has space", "Users.User.UsernameInvalid")]
    [InlineData("bad@char", "Users.User.UsernameInvalid")]
    public void Create_RejectsBadUsernames(string username, string code)
        => Assert.Equal(code, User.Create(username, "Name").Error.Code);

    [Fact]
    public void Create_RejectsTooLongValues()
    {
        Assert.Equal("Users.User.UsernameTooLong", User.Create(new string('a', 51), "Name").Error.Code);
        Assert.Equal("Users.User.DisplayNameTooLong", User.Create("a", new string('x', 101)).Error.Code);
        Assert.Equal("Users.User.DisplayNameRequired", User.Create("a", " ").Error.Code);
        Assert.Equal("Users.User.EmailTooLong", User.Create("a", "n", new string('x', 195) + "@a.com").Error.Code);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("@a.com")]
    [InlineData("a@")]
    [InlineData("a@@b.com")]
    [InlineData("a b@c.com")]
    public void Create_RejectsInvalidEmail(string email)
        => Assert.Equal("Users.User.EmailInvalid", User.Create("a", "n", email).Error.Code);

    [Fact]
    public void Update_ChangesDisplayNameAndEmail_ButNotTheUsername()
    {
        var u = NewUser();

        Assert.True(u.Update("Janet", null).IsSuccess);

        Assert.Equal("Janet", u.DisplayName);
        Assert.Null(u.Email);
        Assert.Equal("jane.doe", u.Username);
        Assert.NotNull(u.UpdatedAt);
        Assert.Equal("Users.User.DisplayNameRequired", u.Update("", null).Error.Code);
    }

    [Fact]
    public void DeactivateAndReactivate_AreGuarded()
    {
        var u = NewUser();

        Assert.Equal("Users.User.AlreadyActive", u.Reactivate().Error.Code);
        Assert.True(u.Deactivate().IsSuccess);
        Assert.Equal(UserStatus.Inactive, u.Status);
        Assert.Equal("Users.User.AlreadyInactive", u.Deactivate().Error.Code);
        Assert.True(u.Reactivate().IsSuccess);
        Assert.Equal(UserStatus.Active, u.Status);
    }

    [Fact]
    public void AssignRole_AddsOnce_AndNotToAnInactiveUser()
    {
        var u = NewUser();
        var role = RoleId.New();

        Assert.True(u.AssignRole(role).IsSuccess);
        Assert.Equal("Users.User.RoleAlreadyAssigned", u.AssignRole(role).Error.Code);
        Assert.Equal("Users.User.RoleRequired", u.AssignRole(RoleId.Empty).Error.Code);
        Assert.Single(u.Roles);

        u.Deactivate();
        Assert.Equal("Users.User.Inactive", u.AssignRole(RoleId.New()).Error.Code);
    }

    [Fact]
    public void RemoveRole_RemovesAssigned_AndReportsMissing()
    {
        var u = NewUser();
        var role = RoleId.New();
        u.AssignRole(role);

        Assert.True(u.RemoveRole(role).IsSuccess);
        Assert.Empty(u.Roles);
        Assert.Equal("Users.User.RoleNotAssigned", u.RemoveRole(role).Error.Code);
    }

    [Fact]
    public void RemoveRole_StillWorksForAnInactiveUser()
    {
        var u = NewUser();
        var role = RoleId.New();
        u.AssignRole(role);
        u.Deactivate();

        Assert.True(u.RemoveRole(role).IsSuccess);
    }

    // ------------------------------------------------------------------ role

    [Fact]
    public void RoleCreate_ValidatesNameAndDescription()
    {
        Assert.Equal("Users.Role.NameRequired", Role.Create(" ").Error.Code);
        Assert.Equal("Users.Role.NameTooLong", Role.Create(new string('x', 51)).Error.Code);
        Assert.Equal("Users.Role.DescriptionTooLong", Role.Create("R", new string('x', 201)).Error.Code);

        var r = Role.Create("  Manager ", "  ");
        Assert.Equal("Manager", r.Value.Name);
        Assert.Null(r.Value.Description);
    }

    [Fact]
    public void RoleUpdateDescription_Validates()
    {
        var role = NewRole();

        Assert.True(role.UpdateDescription("new").IsSuccess);
        Assert.Equal("new", role.Description);
        Assert.Equal("Users.Role.DescriptionTooLong", role.UpdateDescription(new string('x', 201)).Error.Code);
        Assert.True(role.UpdateDescription(null).IsSuccess);
        Assert.Null(role.Description);
    }

    [Fact]
    public void Grant_NormalisesPermission_AndRejectsDuplicates()
    {
        var role = NewRole();

        Assert.True(role.Grant(" Sales.Refund ").IsSuccess);
        Assert.Equal("sales.refund", Assert.Single(role.Permissions).Permission);
        Assert.Equal("Users.Role.PermissionAlreadyGranted", role.Grant("sales.refund").Error.Code);
    }

    [Fact]
    public void Revoke_RemovesGranted_AndReportsMissing()
    {
        var role = NewRole();
        role.Grant("sales.refund");

        Assert.True(role.Revoke("SALES.REFUND").IsSuccess);
        Assert.Empty(role.Permissions);
        Assert.Equal("Users.Role.PermissionNotGranted", role.Revoke("sales.refund").Error.Code);
        Assert.Equal("Users.Permission.InvalidFormat", role.Revoke("bad code").Error.Code);
    }

    [Theory]
    [InlineData("", "Users.Permission.Required")]
    [InlineData("single", "Users.Permission.InvalidFormat")]
    [InlineData("has space.x", "Users.Permission.InvalidFormat")]
    [InlineData(".leading.dot", "Users.Permission.InvalidFormat")]
    [InlineData("trailing.", "Users.Permission.InvalidFormat")]
    [InlineData("1digit.first", "Users.Permission.InvalidFormat")]
    public void PermissionCode_RejectsInvalid(string value, string code)
        => Assert.Equal(code, PermissionCode.Create(value).Error.Code);

    [Fact]
    public void PermissionCode_AcceptsDottedLowerCase_AndLimitsLength()
    {
        Assert.Equal("inventory.stock.adjust", PermissionCode.Create("Inventory.Stock.Adjust").Value.Value);
        Assert.Equal("Users.Permission.TooLong", PermissionCode.Create("a." + new string('b', 99)).Error.Code);
    }

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.NotEqual(UserId.New(), UserId.New());
        Assert.NotEqual(RoleId.New(), RoleId.New());
    }
}
