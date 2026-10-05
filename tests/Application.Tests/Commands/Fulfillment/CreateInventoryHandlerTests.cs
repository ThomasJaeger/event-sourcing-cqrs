using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class CreateInventoryHandlerTests
{
    [Fact]
    public async Task A_second_inventory_id_cannot_claim_an_existing_sku()
    {
        var fixture = new InventoryTestFixture();
        var handler = CreateHandler(fixture);
        await handler.HandleAsync(
            new CreateInventory(InventoryTestFixture.InventoryId, InventoryTestFixture.Sku),
            CancellationToken.None);
        var otherId = Guid.NewGuid();

        var act = () => handler.HandleAsync(
            new CreateInventory(otherId, InventoryTestFixture.Sku), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>();
        (await fixture.Repository.LoadAsync(otherId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_creates_a_new_inventory_and_persists_it()
    {
        var fixture = new InventoryTestFixture();
        var handler = CreateHandler(fixture);

        await handler.HandleAsync(
            new CreateInventory(InventoryTestFixture.InventoryId, InventoryTestFixture.Sku),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(InventoryTestFixture.InventoryId);
        loaded.Sku.Should().Be(InventoryTestFixture.Sku);
    }

    private static CreateInventoryHandler CreateHandler(InventoryTestFixture fixture)
        => new(fixture.Repository, fixture.Accessor,
            new StubTenantAccessor { Current = WellKnownTenants.Default }, new SkuClaims());

    private sealed class SkuClaims : IInventorySkuRegistry
    {
        private readonly Dictionary<(TenantId Tenant, string Sku), Guid> _claims = [];

        public Task ClaimAsync(TenantId tenant, Guid inventoryId, string sku, CancellationToken ct)
        {
            if (_claims.TryGetValue((tenant, sku), out var owner) && owner != inventoryId)
                throw new DomainException("SKU is already claimed.");
            _claims[(tenant, sku)] = inventoryId;
            return Task.CompletedTask;
        }
    }
}
