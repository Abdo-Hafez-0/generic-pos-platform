using Microsoft.Extensions.DependencyInjection;
using Users.Application.Commands;

namespace Users.Tests.Security;

/// <summary>Changes to who exists and what they may do are exactly what an audit trail is for.</summary>
public sealed class UsersAuditTests
{
    private const string Password = AuthenticationTestHarness.Password;

    [Fact]
    public async Task User_role_and_permission_changes_are_recorded_with_the_acting_administrator()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        var admin = await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);

        var user = (await h.Run(sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("ann", "Ann")))).Value;
        var role = (await h.Run(sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Clerk")))).Value;
        await h.Run(sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role, "pos.sale.create")));
        await h.Run(sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));
        await h.Run(sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role, "pos.sale.create")));
        await h.Run(sp => sp.GetRequiredService<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user, role)));
        await h.Run(sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(user)));
        await h.Run(sp => sp.GetRequiredService<ReactivateUserCommandHandler>().HandleAsync(new ReactivateUserCommand(user)));

        var actions = h.Events.Events.Where(e => e.ActorId == admin).Select(e => e.Action).ToList();
        foreach (var expected in new[]
        {
            "security.user.created", "security.role.created", "security.permission.granted", "security.role.assigned",
            "security.permission.revoked", "security.role.removed", "security.user.deactivated", "security.user.reactivated"
        })
            Assert.Contains(expected, actions);

        var granted = Assert.Single(h.Events.Events, e => e.Action == "security.permission.granted");
        Assert.Equal(role.ToString(), granted.SubjectId);
        Assert.Contains("pos.sale.create", granted.Summary);
        Assert.Equal("admin", granted.ActorName);
    }

    [Fact]
    public async Task A_refused_change_is_not_recorded_as_a_change_only_as_a_denial()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        await h.CreateUserWithPasswordAsync("cashier");
        h.Session.SignOut();
        await h.SignInAsync("cashier", Password);
        h.Events.Events.Clear();

        await h.Run(sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("sneaky", "Sneaky")));

        Assert.DoesNotContain(h.Events.Events, e => e.Action == "security.user.created");
        Assert.Contains(h.Events.Events, e => e.Action == "security.authorization.denied");
    }
}
