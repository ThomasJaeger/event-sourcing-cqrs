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

    [Fact]
    public async Task An_authorized_order_subscribe_joins_the_group()
    {
        var orderId = Guid.NewGuid();
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource($"order:{orderId}");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", $"order:{orderId}"));
        client.Calls.Should().ContainSingle();
        client.Calls[0].ActorId.Should().Be(Actor);
        client.Calls[0].Request.ResourceType.Should().Be(SubscriptionResourceType.Order);
        client.Calls[0].Request.ResourceId.Should().Be(orderId.ToString());
    }

    [Fact]
    public async Task A_refused_order_subscribe_throws_and_does_not_join()
    {
        var orderId = Guid.NewGuid();
        var client = new StubSubscriptionAuthorizationClient(allowed: false);
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
    public async Task An_authorized_inventory_subscribe_joins_and_sends_the_sku_unparsed()
    {
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource("inventory:SKU-1");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", "inventory:SKU-1"));
        client.Calls.Should().ContainSingle();
        client.Calls[0].Request.ResourceType.Should().Be(SubscriptionResourceType.Inventory);
        client.Calls[0].Request.ResourceId.Should().Be("SKU-1");
    }

    [Fact]
    public async Task A_refused_inventory_subscribe_throws_and_does_not_join()
    {
        var client = new StubSubscriptionAuthorizationClient(allowed: false);
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
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
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
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
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
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
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
        var client = new StubSubscriptionAuthorizationClient(allowed: true);
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
    public async Task Unsubscribe_removes_the_callers_group_without_authorization()
    {
        var client = new StubSubscriptionAuthorizationClient(allowed: false);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.UnsubscribeFromResource("order:order-7");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("remove", "conn-1", "order:order-7"));
        client.Calls.Should().BeEmpty();
    }

    private static ClaimsPrincipal PrincipalFor(Guid actorId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) }, "Test"));

    // Records the (actorId, request) of every authorize call and returns a configured decision, so a
    // test can assert both that the hub consulted the client and what it asked.
    private sealed class StubSubscriptionAuthorizationClient : ISubscriptionAuthorizationClient
    {
        private readonly bool _allowed;

        public StubSubscriptionAuthorizationClient(bool allowed) => _allowed = allowed;

        public List<(Guid ActorId, SubscriptionAuthorizationRequest Request)> Calls { get; } = [];

        public Task<bool> AuthorizeAsync(
            Guid actorId, SubscriptionAuthorizationRequest request, CancellationToken ct)
        {
            Calls.Add((actorId, request));
            return Task.FromResult(_allowed);
        }
    }
}
