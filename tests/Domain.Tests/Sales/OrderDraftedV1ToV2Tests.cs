using EventSourcingCqrs.Domain.Sales.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Sales;

// Per-hop facts for the one upcaster that ships (Chapter 11: Testing Upcasters, Unit Tests). The
// chapter's standard is that every upcaster ships with unit tests over three or four representative
// old payloads, and until these landed the only coverage of this link ran through the pipeline, which
// always lifts to the terminal. A hop tested only through a chain can misread a field that a later hop
// overwrites, so each fact here drives the link directly and asserts every member of the output rather
// than the ones the lift happened to expose.
//
// The chapter's third sample class, a malformed payload the defensive code handles, has no counterpart
// here and is left out rather than invented: OrderDraftedV1 is a record with three non-nullable
// members, so a malformed input cannot be constructed, and the upcaster carries no defensive path to
// exercise.
public class OrderDraftedV1ToV2Tests
{
    private static readonly OrderDraftedV1ToV2 Upcaster = new();

    // (a) The typical case: ordinary identifiers and an ordinary timestamp.
    [Fact]
    public void Upcast_carries_every_member_forward_and_defaults_the_channel()
    {
        var orderId = Guid.Parse("6f1c2d3e4a5b46c78d9e0f1a2b3c4d5e");
        var customerId = Guid.Parse("1a2b3c4d5e6f478899aabbccddeeff00");
        var draftedUtc = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

        var upcast = Upcaster.Upcast(new OrderDraftedV1(orderId, customerId, draftedUtc));

        upcast.OrderId.Should().Be(orderId);
        upcast.CustomerId.Should().Be(customerId);
        upcast.DraftedUtc.Should().Be(draftedUtc);
        upcast.Channel.Should().Be(OrderDrafted.UnknownChannel);
    }

    // (b) An edge case: empty identifiers and the minimum timestamp. A v1 row could hold these, and the
    // upcaster has no business substituting anything for them.
    [Fact]
    public void Upcast_carries_empty_identifiers_and_a_minimum_timestamp_unchanged()
    {
        var draftedUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        var upcast = Upcaster.Upcast(new OrderDraftedV1(Guid.Empty, Guid.Empty, draftedUtc));

        upcast.OrderId.Should().Be(Guid.Empty);
        upcast.CustomerId.Should().Be(Guid.Empty);
        upcast.DraftedUtc.Should().Be(draftedUtc);
        upcast.Channel.Should().Be(OrderDrafted.UnknownChannel);
    }

    // (c) An edge case on the timestamp's kind. The member is a DateTime rather than a
    // DateTimeOffset, so Kind is part of the value, and a lift that normalized it would change what a
    // historical row meant. Unspecified is what an older serializer could leave behind.
    [Fact]
    public void Upcast_preserves_the_timestamp_kind_rather_than_normalizing_it()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var unspecified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        var upcast = Upcaster.Upcast(new OrderDraftedV1(orderId, customerId, unspecified));

        upcast.OrderId.Should().Be(orderId);
        upcast.CustomerId.Should().Be(customerId);
        upcast.DraftedUtc.Should().Be(unspecified);
        upcast.DraftedUtc.Kind.Should().Be(DateTimeKind.Unspecified);
        upcast.Channel.Should().Be(OrderDrafted.UnknownChannel);
    }

    // (d) The invariant the other three share, stated once as its own specification: whatever the input
    // carried, the channel a v1 row could not hold comes back as the unknown default and never as null
    // or empty. This is the claim Chapter 11's worked example makes.
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("6f1c2d3e-4a5b-46c7-8d9e-0f1a2b3c4d5e")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void Upcast_always_produces_the_unknown_channel(string orderId)
    {
        var upcast = Upcaster.Upcast(
            new OrderDraftedV1(Guid.Parse(orderId), Guid.NewGuid(), DateTime.UnixEpoch));

        upcast.Channel.Should().Be(OrderDrafted.UnknownChannel);
        upcast.Channel.Should().NotBeNullOrEmpty();
    }
}
