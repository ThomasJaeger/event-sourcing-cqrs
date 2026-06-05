using System.Security.Claims;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// The dashboard hub's subscription-authorization boundary (P9.6). The hub is constructed directly with
// a stub authorization client; the client's recorded calls and the RecordingGroupManager together prove
// the layered enforcement. A refused subscribe throws AND never reaches AddToGroupAsync, and a
// fail-closed prefix or a malformed id is refused at the hub with no call to the client at all. The
// authorized-subscribe and the ungated-unsubscribe cases carry forward the behaviors the pre-P9.6
// DashboardHubTests covered.
public class DashboardHubAuthorizationTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public Task An_authorized_order_subscribe_joins_the_group()
        => SubscriptionResourceCoverageTests.OrderSubscribeCaseAsync();

    [Fact]
    public async Task A_refused_order_subscribe_throws_and_does_not_join()
    {
        var orderId = Guid.NewGuid();
        var client = StubSubscriptionAuthorizationClient.Deny();
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource($"order:{orderId}");

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
        client.Calls.Should().ContainSingle();
    }

    [Fact]
    public Task An_authorized_inventory_subscribe_joins_and_sends_the_sku_unparsed()
        => SubscriptionResourceCoverageTests.InventorySubscribeCaseAsync();

    [Fact]
    public async Task Subscribe_joins_the_tenant_qualified_group_from_the_authorized_tenant()
    {
        // Inventory is the load-bearing family: SKU-1 is legal under two tenants (P10.6), so the joined
        // group must be qualified by the authorized tenant or two tenants' subscribers collide on
        // inventory:SKU-1. The tenant comes from the authorize response, not the client group string
        // (still unqualified) or any principal claim, and renders in the StreamId {guid:N} form.
        var tenantB = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var client = StubSubscriptionAuthorizationClient.Allow(tenantB);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource("inventory:SKU-1");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", "tenant:55555555555555555555555555555555:inventory:SKU-1"));
    }

    [Fact]
    public async Task A_refused_inventory_subscribe_throws_and_does_not_join()
    {
        var client = StubSubscriptionAuthorizationClient.Deny();
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource("inventory:SKU-1");

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("admin:metrics")]
    [InlineData("customer:11111111-1111-1111-1111-111111111111")]
    [InlineData("garbage")]
    [InlineData("nocolon")]
    public async Task An_unknown_prefix_is_refused_at_the_hub_without_calling_the_client(string group)
    {
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource(group);

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_order_id_is_refused_at_the_hub_without_calling_the_client()
    {
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource("order:not-a-guid");

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_or_blank_group_throws_the_argument_guard(string group)
    {
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = new RecordingGroupManager(),
        };

        var act = () => hub.SubscribeToResource(group);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_connection_without_a_name_identifier_is_refused_without_calling_the_client()
    {
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            // No principal on the connection: the route gate establishes one in production, so this is
            // the hub's defense-in-depth path.
            Context = new FakeHubCallerContext("conn-1"),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource($"order:{Guid.NewGuid()}");

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsubscribe_without_a_prior_subscribe_is_a_no_op()
    {
        var client = StubSubscriptionAuthorizationClient.Deny();
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        // No prior authorized subscribe, so the connection cached no tenant: there is nothing to leave.
        // Unsubscribe stays ungated (no authorize-client call) and removes nothing rather than guessing
        // an unqualified or defaulted group.
        await hub.UnsubscribeFromResource("inventory:SKU-1");

        groups.Calls.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_allow_with_no_tenant_is_refused_at_the_hub()
    {
        // A malformed allow (allowed, but no authoritative tenant) is a contract violation. The hub fails
        // closed rather than join an unqualified or defaulted group, which would cross-tenant leak. This
        // is the one place a null-tenant allow is intentional, fed directly through the stub.
        var client = StubSubscriptionAuthorizationClient.WithResult(
            new SubscriptionAuthorizationResult(true, null));
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        var act = () => hub.SubscribeToResource("inventory:SKU-1");

        await act.Should().ThrowAsync<UnauthorizedSubscriptionException>();
        groups.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsubscribe_leaves_the_tenant_qualified_group()
    {
        // A caller subscribes under a tenant (joining the qualified group), then unsubscribes the same
        // resource. It must leave the qualified group it actually joined, not the unqualified client
        // string, or its membership lingers. Subscribe and unsubscribe must operate on the same group.
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource("inventory:SKU-1");
        await hub.UnsubscribeFromResource("inventory:SKU-1");

        groups.Calls.Should().Contain(
            ("remove", "conn-1", "tenant:55555555555555555555555555555555:inventory:SKU-1"));
    }

    private static ClaimsPrincipal PrincipalFor(Guid actorId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) }, "Test"));
}
