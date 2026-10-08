using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public class OrderReplayReaderTests
{
    [Theory]
    [InlineData("not-a-stream")]
    [InlineData("order:00000000000000000000000000000001:11111111111111111111111111111111")]
    [InlineData("order:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("payment:11111111111111111111111111111111")]
    [InlineData("pm-order:11111111111111111111111111111111")]
    [InlineData("order:00000000000000000000000000000000")]
    public async Task Only_valid_nonempty_order_streams_reach_the_reader(string streamId)
    {
        var port = new CaptureReader();
        var sut = new OrderReplayReader(new OrderHistoryReader(port));
        var result = await sut.ReadAsync(streamId, CancellationToken.None);
        result.History.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
        port.Stream.Should().BeNull();
    }

    [Theory]
    [InlineData("order:11111111111111111111111111111111")]
    [InlineData("order:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:11111111111111111111111111111111")]
    public async Task Explicit_stream_tenant_is_preserved_in_the_read(string streamId)
    {
        var port = new CaptureReader();
        var sut = new OrderReplayReader(new OrderHistoryReader(port));
        var result = await sut.ReadAsync(streamId, CancellationToken.None);
        port.Stream!.Value.Should().Be(streamId);
        result.History.Should().BeNull();
        result.Message.Should().Contain("No order events");
    }

    [Fact]
    public async Task An_unsupported_provider_has_a_helpful_read_only_outcome()
    {
        var sut = new OrderReplayReader(new OrderHistoryReader(new UnavailableEventStreamReader()));
        var result = await sut.ReadAsync("order:11111111111111111111111111111111", CancellationToken.None);
        result.History.Should().BeNull();
        result.Message.Should().Contain("not available");
    }

    private sealed class CaptureReader : IBoundedEventStreamReader
    {
        public StreamId? Stream { get; private set; }
        public Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
        {
            Stream = streamId;
            return Task.FromResult(new EventStreamWindow([], false));
        }
    }
}
