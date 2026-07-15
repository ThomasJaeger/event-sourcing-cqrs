using System.Data.Common;
using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Projections.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// The /projections page renders the projection-lag read for an operator. It injects the concrete
// ProjectionLagReader (a stable single-implementation owned leaf, injected directly) and calls
// ReadAsync once on render. These specs drive the page through bUnit over stub ports, the sanctioned
// owned-fake idiom: the happy path asserts one row per projection with the four lag columns, and the
// error path asserts the page renders an operator-visible error rather than a blank page or an
// unhandled exception. Authorization is the host fallback gate (ADR 0040), upstream of render, so the
// page carries no denied-state branch.
public class ProjectionStatusDashboardTests : BunitContext
{
    [Fact]
    public void Renders_one_row_per_projection_with_the_four_lag_columns()
    {
        var reader = new ProjectionLagReader(
            new StubHead(10),
            new StubCheckpointStore(new() { ["proj-a"] = 7, ["proj-b"] = 10 }),
            new StubRoster("proj-a", "proj-b"));
        Services.AddSingleton(reader);

        var cut = Render<ProjectionStatusDashboard>();

        cut.FindAll("tbody tr").Should().HaveCount(2);
        var firstRow = cut.FindAll("tbody tr")[0].QuerySelectorAll("td")
            .Select(td => td.TextContent.Trim());
        firstRow.Should().Equal("proj-a", "10", "7", "3");
    }

    [Fact]
    public void Renders_an_error_state_when_the_lag_read_throws()
    {
        var reader = new ProjectionLagReader(
            new ThrowingHead(),
            new StubCheckpointStore(new()),
            new StubRoster("proj-a"));
        Services.AddSingleton(reader);

        var cut = Render<ProjectionStatusDashboard>();

        cut.Markup.Should().Contain("Unable to load projection status");
        cut.FindAll("tbody tr").Should().BeEmpty();
    }

    private sealed class StubHead(long head) : IEventStoreHeadPosition
    {
        public Task<long> GetHeadPositionAsync(CancellationToken ct) => Task.FromResult(head);
    }

    private sealed class ThrowingHead : IEventStoreHeadPosition
    {
        public Task<long> GetHeadPositionAsync(CancellationToken ct) =>
            throw new InvalidOperationException("head read failed");
    }

    private sealed class StubCheckpointStore(Dictionary<string, long> positions) : ICheckpointStore
    {
        public Task<long> GetPositionAsync(string projectionName, CancellationToken ct) =>
            Task.FromResult(positions.TryGetValue(projectionName, out var position) ? position : 0);

        public Task<long> GetPositionAsync(
            string projectionName, DbTransaction transaction, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AdvanceAsync(
            string projectionName, long position, DbTransaction transaction, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AdvanceAsync(string projectionName, long position, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubRoster(params string[] names) : IProjectionRoster
    {
        public IReadOnlyCollection<string> Names { get; } = names;
    }
}
