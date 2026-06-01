using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

// The command write path carries the tenant from the principal to the event. A command dispatched
// under a non-default tenant produces events whose metadata tenant and whose generated tenant_id
// column both carry that tenant, and that land on the tenant-scoped stream id. The principal factory
// is overridden because the real one returns the default tenant until per-user onboarding lands.
public class CommandTenantTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    public CommandTenantTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_command_under_a_non_default_tenant_carries_it_to_metadata_and_the_stream_id()
    {
        var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IPrincipalFactory>(new FixedTenantPrincipalFactory(OtherTenant))));
        var client = factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.PlaceOrderAsync(client, orderId);

        var eventStore = factory.Services.GetRequiredService<IEventStore>();
        var streamId = StreamId.ForAggregate<Order>(OtherTenant, orderId);
        var envelopes = await eventStore.ReadStreamAsync(streamId, fromVersion: 0);
        envelopes.Should().NotBeEmpty();
        envelopes.Should().AllSatisfy(e => e.Metadata.Tenant.Should().Be(OtherTenant));

        var columnTenants = await DistinctStreamTenantColumnAsync(streamId.Value);
        columnTenants.Should().ContainSingle().Which.Should().Be(OtherTenant.Value);
    }

    private async Task<IReadOnlyList<Guid>> DistinctStreamTenantColumnAsync(string streamId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tenant_id FROM event_store.events WHERE stream_id = @stream_id";
        cmd.Parameters.AddWithValue("stream_id", streamId);
        var tenants = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tenants.Add(reader.GetGuid(0));
        }
        return tenants;
    }

    // Returns a principal on a fixed non-default tenant, with Admin so the command authorizes. The
    // forwarded header carries the actor id; the real factory would load that actor's tenant, which is
    // the default today, so the test substitutes a non-default tenant to prove the threading.
    private sealed class FixedTenantPrincipalFactory : IPrincipalFactory
    {
        private readonly TenantId _tenant;

        public FixedTenantPrincipalFactory(TenantId tenant) => _tenant = tenant;

        public Task<AuthenticatedPrincipal> CreateAsync(Guid actorId, CancellationToken ct)
            => Task.FromResult(new AuthenticatedPrincipal(actorId, [Role.Admin], _tenant));
    }
}
