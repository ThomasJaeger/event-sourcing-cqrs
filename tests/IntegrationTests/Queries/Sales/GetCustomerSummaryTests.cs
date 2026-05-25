using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Sales;

public class GetCustomerSummaryTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public GetCustomerSummaryTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetCustomerSummary_for_an_unknown_customer_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostQueryAsync(
            "GetCustomerSummary",
            new { customerId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCustomerSummary_for_a_seeded_customer_returns_200_with_the_summary()
    {
        var client = _fixture.Factory.CreateClient();
        var customerId = Guid.NewGuid();

        var store = _fixture.Factory.Services.GetRequiredService<ICustomerSummaryStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.ApplyPlacementAsync(
                customerId,
                new Money(75.00m, Currency.USD),
                SeededAt,
                SeededAt,
                default);
            await uow.CommitAsync("get-customer-summary-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetCustomerSummary",
            new { customerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadFromJsonAsync<CustomerSummaryRow>();
        row.Should().NotBeNull();
        row!.CustomerId.Should().Be(customerId);
        row.OrderCount.Should().Be(1);
        row.LifetimeValue.Amount.Should().Be(75.00m);
        row.LastOrderUtc.Should().Be(SeededAt);
    }

    [Fact]
    public async Task GetCustomerSummary_after_placement_and_cancellation_returns_netted_totals()
    {
        var client = _fixture.Factory.CreateClient();
        var customerId = Guid.NewGuid();
        var total = new Money(75.00m, Currency.USD);

        var store = _fixture.Factory.Services.GetRequiredService<ICustomerSummaryStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.ApplyPlacementAsync(customerId, total, SeededAt, SeededAt, default);
            await uow.ApplyCancellationAsync(customerId, total, SeededAt.AddMinutes(5), default);
            await uow.CommitAsync("get-customer-summary-netting-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetCustomerSummary",
            new { customerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadFromJsonAsync<CustomerSummaryRow>();
        row.Should().NotBeNull();
        row!.OrderCount.Should().Be(0);
        row.LifetimeValue.Amount.Should().Be(0m);
        row.LastOrderUtc.Should().Be(SeededAt);
    }
}
