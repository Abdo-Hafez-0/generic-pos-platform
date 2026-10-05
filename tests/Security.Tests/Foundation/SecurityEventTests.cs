using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Client.Host.DependencyInjection;

namespace Security.Tests.Foundation;

public sealed class SecurityEventTests
{
    [Theory]
    [InlineData("password=Hunter2!", "Hunter2")]
    [InlineData("login failed, password: Hunter2!", "Hunter2")]
    [InlineData("token=abc123secretvalue", "abc123secretvalue")]
    [InlineData("api_key=sk-live-123456", "sk-live-123456")]
    [InlineData("backup token gpb_AbCdEfGhIjKlMnOpQrStUvWxYz012345", "AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("admin key gpa_AbCdEfGhIjKlMnOpQrStUvWxYz012345", "AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("Authorization: Bearer eyJhbGciOiJFUzI1NiJ9.payload.sig", "eyJhbGciOiJFUzI1NiJ9")]
    [InlineData("key ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2 was used", "ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2")]
    public void Secret_looking_text_never_reaches_an_event(string text, string secret)
    {
        var e = SecurityEvent.Create("security.test", SecurityEventOutcome.Failure, summary: text, subjectId: text, actorName: text);

        Assert.DoesNotContain(secret, e.Summary);
        Assert.DoesNotContain(secret, e.SubjectId);
        Assert.DoesNotContain(secret, e.ActorName);
        Assert.Contains(SecretRedactor.Redacted, e.Summary);
    }

    [Fact]
    public void A_pem_private_key_is_redacted()
    {
        const string pem = "-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg\n-----END PRIVATE KEY-----";

        var e = SecurityEvent.Create("security.test", SecurityEventOutcome.Failure, summary: "loaded " + pem);

        Assert.DoesNotContain("MIGHAgEAMBMGByqGSM49", e.Summary);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", e.Summary);
    }

    [Fact]
    public void Ordinary_text_is_kept_and_normalised()
    {
        var e = SecurityEvent.Create(" Security.SignIn.Failed ", SecurityEventOutcome.Failure,
            summary: "Wrong password for user ann", subjectType: "User", subjectId: "ann");

        Assert.Equal("security.signin.failed", e.Action);
        Assert.Equal("user", e.SubjectType);
        Assert.Equal("Wrong password for user ann", e.Summary);
    }

    [Fact]
    public void Text_is_truncated_and_control_characters_removed()
    {
        var e = SecurityEvent.Create("security.test", SecurityEventOutcome.Success, summary: new string('x', 2000) + "\r\nforged line");

        Assert.Equal(SecurityEvent.MaxTextLength, e.Summary!.Length);
        Assert.DoesNotContain('\n', e.Summary);

        var multi = SecurityEvent.Create("security.test", SecurityEventOutcome.Success, summary: "a\r\nb\tc");
        Assert.Equal("a  b c", multi.Summary);
    }

    [Fact]
    public void An_action_is_required_and_an_empty_actor_is_dropped()
    {
        Assert.Throws<ArgumentException>(() => SecurityEvent.Create(" ", SecurityEventOutcome.Success));
        Assert.Null(SecurityEvent.Create("a.b", SecurityEventOutcome.Success, actorId: Guid.Empty).ActorId);
    }

    private sealed class CountingListener(bool throws) : ISecurityEventListener
    {
        public int Calls { get; private set; }

        public Task OnEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            Calls++;
            return throws ? throw new InvalidOperationException("listener broke") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_failing_listener_never_stops_the_others_or_the_caller()
    {
        var broken = new CountingListener(throws: true);
        var healthy = new CountingListener(throws: false);
        var dispatcher = new SecurityEventDispatcher([broken, healthy]);

        await dispatcher.RecordAsync(SecurityEvent.Create("a.b", SecurityEventOutcome.Success));

        Assert.Equal(1, broken.Calls);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task With_no_listeners_recording_is_a_no_op()
    {
        await new SecurityEventDispatcher([]).RecordAsync(SecurityEvent.Create("a.b", SecurityEventOutcome.Success));
    }

    [Fact]
    public void The_host_registers_the_session_authorization_and_event_pipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformSecurity();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Same(provider.GetRequiredService<ICurrentUser>(), provider.GetRequiredService<ISessionManager>());
        Assert.Same(provider.GetRequiredService<ICapabilityCatalog>(), provider.GetRequiredService<ICapabilityCatalog>());
        using var scope = provider.CreateScope();
        Assert.IsType<AuthorizationService>(scope.ServiceProvider.GetRequiredService<IAuthorizationService>());
        Assert.IsType<SecurityEventDispatcher>(provider.GetRequiredService<ISecurityEventSink>());
    }

    [Fact]
    public async Task Without_a_permission_provider_the_registered_authorization_refuses_everything()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformSecurity();
        services.AddSingleton<ICapabilityProvider>(new FakeCapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "n", "d")));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISessionManager>().SignIn(new AuthenticatedIdentity(Guid.NewGuid(), "a", "A"));
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync("pos.sale.create");

        Assert.True(result.IsFailure);
    }
}
