using System.Security.Claims;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// The structural subscription coverage harness (ADR 0031's coverage mandate, ADR 0027). The subscription
// surface is the SubscriptionResourceType enum, not a DI-registered type set, so coverage is a direct
// enum-exhaustiveness walk rather than the FindUncovered(Type) detector the query, command, and projection
// boundaries use. The meta-test asserts the closed loop: every resource type has a subscribe case
// (completeness), each case drives the production ParseResource and parses back to its type (liveness), each
// known projection routes through DispatchAsync to its group (broadcast routing), and the prefix it routes
// to is one the subscribe door recognizes (round-trip).
//
// The per-member subscribe and per-entry broadcast cases are the same bodies the standalone hub [Fact]s
// invoke, so the isolation and routing logic lives once. GroupPrefixes is private, so the broadcast checks
// drive the known projections and do not prove the absence of an extra unmapped entry; that limitation is
// recorded in the P10.9 close-doc. The cross-tenant isolation of the tenant-qualified group is proven in
// DashboardHubAuthorizationTests and HubBackplaneHostedServiceTests, so this harness gates exhaustiveness,
// not isolation.
public class SubscriptionResourceCoverageTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // One subscribe case per resource type. The completeness gate keys on this map, and each case is the
    // per-member subscribe assertion the standalone hub test also invokes.
    private static readonly IReadOnlyDictionary<SubscriptionResourceType, Func<Task>> SubscribeCases =
        new Dictionary<SubscriptionResourceType, Func<Task>>
        {
            [SubscriptionResourceType.Order] = OrderSubscribeCaseAsync,
            [SubscriptionResourceType.Inventory] = InventorySubscribeCaseAsync,
        };

    // One broadcast case per known publishing projection, the per-entry assertion the standalone hub test
    // also invokes. GroupPrefixes is private, so this is a hand-list driven through DispatchAsync.
    private static readonly IReadOnlyDictionary<string, Func<Task>> BroadcastCases =
        new Dictionary<string, Func<Task>>
        {
            ["order-detail"] = OrderDetailBroadcastCaseAsync,
            ["inventory-dashboard"] = InventoryDashboardBroadcastCaseAsync,
        };

    [Fact]
    public async Task Every_subscription_resource_type_is_covered_live_and_round_trips()
    {
        var members = Enum.GetValues<SubscriptionResourceType>();

        // Completeness: a resource type with no subscribe case fails here, so a type added to the enum
        // without a case fails the build.
        members.Where(m => !SubscribeCases.ContainsKey(m)).Should().BeEmpty();

        // Liveness: each member's case drives the production ParseResource through SubscribeToResource and
        // asserts the parsed member and the tenant-qualified join.
        foreach (var run in SubscribeCases.Values)
        {
            await run();
        }

        // Broadcast routing: each known publishing projection's case drives DispatchAsync to its group.
        foreach (var run in BroadcastCases.Values)
        {
            await run();
        }

        // Round-trip: the prefix each known projection broadcasts to is one the subscribe door recognizes,
        // so no projection broadcasts to an unsubscribable group. This drives the known projections;
        // GroupPrefixes is private, so it does not prove the absence of an extra unmapped entry.
        foreach (var projectionName in BroadcastCases.Keys)
        {
            await AssertBroadcastPrefixIsRecognizedAsync(projectionName);
        }
    }

    // The per-member subscribe cases, the bodies An_authorized_order_subscribe and
    // An_authorized_inventory_subscribe invoke.
    internal static async Task OrderSubscribeCaseAsync()
    {
        var orderId = Guid.NewGuid();
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource($"order:{orderId}");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", $"tenant:55555555555555555555555555555555:order:{orderId}"));
        client.Calls.Should().ContainSingle();
        client.Calls[0].ActorId.Should().Be(Actor);
        client.Calls[0].Request.ResourceType.Should().Be(SubscriptionResourceType.Order);
        client.Calls[0].Request.ResourceId.Should().Be(orderId.ToString());
    }

    internal static async Task InventorySubscribeCaseAsync()
    {
        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = groups,
        };

        await hub.SubscribeToResource("inventory:SKU-1");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", "tenant:55555555555555555555555555555555:inventory:SKU-1"));
        client.Calls.Should().ContainSingle();
        client.Calls[0].Request.ResourceType.Should().Be(SubscriptionResourceType.Inventory);
        client.Calls[0].Request.ResourceId.Should().Be("SKU-1");
    }

    // The per-entry broadcast cases, the bodies Dispatch_order_detail_envelope and
    // Dispatch_inventory_dashboard_envelope invoke.
    internal static async Task OrderDetailBroadcastCaseAsync()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);
        var envelope = new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default);

        await service.DispatchAsync(envelope, CancellationToken.None);

        hubContext.Broadcasts.Should().ContainSingle();
        var (group, method, args) = hubContext.Broadcasts[0];
        group.Should().Be("tenant:00000000000000000000000000000001:order:order-7");
        method.Should().Be(HubBackplaneHostedService.ClientMethod);
        args.Should().ContainSingle().Which.Should().Be(envelope);
    }

    internal static async Task InventoryDashboardBroadcastCaseAsync()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);
        var envelope = new NotificationEnvelope("inventory-dashboard", "SKU-1", "InventoryAdjusted", ["on_hand"], WellKnownTenants.Default);

        await service.DispatchAsync(envelope, CancellationToken.None);

        hubContext.Broadcasts.Should().ContainSingle();
        hubContext.Broadcasts[0].Group.Should().Be("tenant:00000000000000000000000000000001:inventory:SKU-1");
    }

    // Drives a known projection's broadcast, reads the prefix off the group it routed to, and confirms the
    // subscribe door's ParseResource recognizes that prefix. A guid id is valid for both the order prefix,
    // which requires one, and the free-form inventory sku.
    private static async Task AssertBroadcastPrefixIsRecognizedAsync(string projectionName)
    {
        var hubContext = new RecordingHubContext();
        await Service(hubContext).DispatchAsync(
            new NotificationEnvelope(projectionName, "round-trip", "Changed", [], TenantId.From(Tenant)),
            CancellationToken.None);
        var prefix = hubContext.Broadcasts.Should().ContainSingle().Which.Group.Split(':')[2];

        var client = StubSubscriptionAuthorizationClient.Allow(Tenant);
        using var hub = new DashboardHub(client)
        {
            Context = new FakeHubCallerContext("conn-1", PrincipalFor(Actor)),
            Groups = new RecordingGroupManager(),
        };
        await hub.SubscribeToResource($"{prefix}:{Guid.NewGuid()}");
        client.Calls.Should().ContainSingle();
    }

    private static HubBackplaneHostedService Service(RecordingHubContext hubContext)
        => new(new StubBackplane(), hubContext, NullLogger<HubBackplaneHostedService>.Instance,
            new RecordingResourceNotificationDispatcher());

    private static ClaimsPrincipal PrincipalFor(Guid actorId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) }, "Test"));
}
