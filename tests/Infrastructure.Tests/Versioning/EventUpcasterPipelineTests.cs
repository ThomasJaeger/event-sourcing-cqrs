using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Versioning;

// Facts for the read-time upcaster pipeline (Chapter 11: Upcasting). The pipeline resolves a
// stored (storage name, schema version) to the CLR type that shape was written as, lifts a
// deserialized event through its lineage's upcaster chain to the current shape, and derives
// version numbers from chain topology rather than declared constants. Scratch lineages local
// to this file stand in for real events so the facts do not couple to a chosen v1-to-v2 change.
public class EventUpcasterPipelineTests
{
    // Lineage A: three shapes, two links, current version 3. The registry knows only the
    // terminal (AV3) under a stable storage name; AV1 and AV2 are reachable through the chain.
    // Lineage B: two shapes, one link, current version 2. LoneEvent: registered, no upcasters,
    // current version 1.
    private static EventTypeRegistry BuildRegistry()
        => new EventTypeRegistry()
            .Register(typeof(AV3), "a_event")
            .Register(typeof(BV2), "b_event")
            .Register(typeof(LoneEvent), "lone_event");

    private static object[] AllUpcasters()
        => [new AV1ToV2(), new AV2ToV3(), new BV1ToV2()];

    private static EventUpcasterPipeline BuildPipeline()
        => new(BuildRegistry(), AllUpcasters());

    // (a) Single-link chain: (name, 1) resolves to the V1 shape and upcasts to V2.
    [Fact]
    public void Single_link_chain_resolves_v1_type_and_upcasts_to_the_v2_shape()
    {
        var pipeline = BuildPipeline();
        var id = Guid.NewGuid();

        pipeline.ResolveType("b_event", 1).Should().Be(typeof(BV1));

        var upcast = pipeline.Upcast("b_event", 1, new BV1(id));
        upcast.Should().BeOfType<BV2>();
        ((BV2)upcast).Id.Should().Be(id);
        ((BV2)upcast).Flag.Should().BeFalse();
    }

    // (b) Two-link chain composes in order: (name, 1) lifts through V2 to V3; (name, 2) lifts
    // through the second link only.
    [Fact]
    public void Two_link_chain_composes_in_order_from_each_stored_version()
    {
        var pipeline = BuildPipeline();
        var id = Guid.NewGuid();

        var fromV1 = pipeline.Upcast("a_event", 1, new AV1(id));
        fromV1.Should().BeOfType<AV3>();
        ((AV3)fromV1).Id.Should().Be(id);
        ((AV3)fromV1).Revision.Should().Be(1);

        var fromV2 = pipeline.Upcast("a_event", 2, new AV2(id, "named"));
        fromV2.Should().BeOfType<AV3>();
        ((AV3)fromV2).Name.Should().Be("named");
    }

    // (c) Identity at the current version resolves the registry type and returns the same
    // instance (no upcasting to do).
    [Fact]
    public void Identity_at_the_current_version_resolves_the_registry_type_and_returns_the_same_instance()
    {
        var registry = BuildRegistry();
        var pipeline = new EventUpcasterPipeline(registry, AllUpcasters());
        var current = new AV3(Guid.NewGuid(), "named", 5);

        pipeline.ResolveType("a_event", 3).Should().Be(registry.TypeFor("a_event"));
        pipeline.Upcast("a_event", 3, current).Should().BeSameAs(current);
    }

    // (d) CurrentVersionFor is chain length plus one, and one for a registered name with no
    // upcasters.
    [Fact]
    public void CurrentVersionFor_is_chain_length_plus_one_and_one_for_a_name_without_upcasters()
    {
        var pipeline = BuildPipeline();

        pipeline.CurrentVersionFor("a_event").Should().Be(3);
        pipeline.CurrentVersionFor("lone_event").Should().Be(1);
    }

    // (e) Out-of-range versions throw UnknownEventSchemaVersionException, naming the storage
    // name and version. Split into the two boundaries so each reads as its own specification.
    [Fact]
    public void Version_below_one_throws_UnknownEventSchemaVersionException_naming_name_and_version()
    {
        var pipeline = BuildPipeline();

        var act = () => pipeline.ResolveType("a_event", 0);

        var ex = act.Should().Throw<UnknownEventSchemaVersionException>().Which;
        ex.StorageName.Should().Be("a_event");
        ex.Version.Should().Be(0);
    }

    [Fact]
    public void Version_above_the_current_throws_UnknownEventSchemaVersionException_naming_name_and_version()
    {
        var pipeline = BuildPipeline();

        var act = () => pipeline.ResolveType("a_event", 4);

        var ex = act.Should().Throw<UnknownEventSchemaVersionException>().Which;
        ex.StorageName.Should().Be("a_event");
        ex.Version.Should().Be(4);
    }

    // (f) An unknown storage name fails through the existing UnknownEventTypeException.
    [Fact]
    public void Unknown_storage_name_fails_through_UnknownEventTypeException()
    {
        var pipeline = BuildPipeline();

        var act = () => pipeline.ResolveType("never_registered", 1);

        act.Should().Throw<UnknownEventTypeException>()
            .Which.TypeName.Should().Contain("never_registered");
    }

    // (g) Construction validation, each its own fact. The exception type is
    // InvalidOperationException per the EventTypeRegistry precedent for structural conflicts;
    // the message wording that names the offense is GREEN's contract, so the RED pins the throw
    // and its timing (at construction), not the exact text.
    [Fact]
    public void Construction_throws_when_a_chain_has_a_gap_below_the_terminal()
    {
        // Terminal AV3 is registered, but only the AV1->AV2 link is supplied: the chain reaches
        // AV2 and stops, never producing the registered terminal.
        var registry = new EventTypeRegistry().Register(typeof(AV3), "a_event");

        var act = () => new EventUpcasterPipeline(registry, new object[] { new AV1ToV2() });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Construction_throws_when_a_terminal_type_is_not_registered()
    {
        // A complete AV1->AV2->AV3 chain, but the registry does not know the terminal AV3.
        var registry = new EventTypeRegistry();

        var act = () => new EventUpcasterPipeline(registry, new object[] { new AV1ToV2(), new AV2ToV3() });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Construction_throws_when_a_link_is_duplicated()
    {
        // Two links from the same source shape (AV1) are ambiguous.
        var registry = new EventTypeRegistry().Register(typeof(AV2), "a_event");

        var act = () => new EventUpcasterPipeline(registry, new object[] { new AV1ToV2(), new AV1ToV2() });

        act.Should().Throw<InvalidOperationException>();
    }

    // (h) Two independent lineages registered together resolve independently; one lineage's
    // chain length never contaminates the other's version numbering.
    [Fact]
    public void Two_independent_lineages_do_not_cross_contaminate_their_version_numbering()
    {
        var pipeline = BuildPipeline();

        pipeline.CurrentVersionFor("a_event").Should().Be(3);
        pipeline.CurrentVersionFor("b_event").Should().Be(2);
        pipeline.ResolveType("a_event", 1).Should().Be(typeof(AV1));
        pipeline.ResolveType("b_event", 1).Should().Be(typeof(BV1));

        // b_event's lineage stops at version 2; A's length of 3 must not leak into B.
        var act = () => pipeline.ResolveType("b_event", 3);
        act.Should().Throw<UnknownEventSchemaVersionException>();
    }

    // AUTHORIZED ADDITION (Phase 15 V1 GREEN): green-on-write characterization of the
    // non-upcaster-element guard. It has no RED ancestor because the guard is a construction
    // choice the pipeline makes, not a behavior the RED facts named; provenance is the V1 GREEN
    // directive's authorized addition. A plain object in the collection is rejected at
    // construction, naming the element's type. The registry here knows AV2 as the terminal, so
    // removing the guard would leave a valid AV1->AV2 chain and the fact would stop failing: that
    // is exactly the tooth the teeth-check exercises.
    [Fact]
    public void Construction_throws_when_a_collection_element_is_not_an_upcaster()
    {
        var registry = new EventTypeRegistry().Register(typeof(AV2), "a_event");

        var act = () => new EventUpcasterPipeline(registry, new object[] { new AV1ToV2(), "not an upcaster" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*String*");
    }

    // Scratch lineages local to this file.

    // Lineage A: three shapes, two links. Current version 3.
    private sealed record AV1(Guid Id) : IDomainEvent;
    private sealed record AV2(Guid Id, string Name) : IDomainEvent;
    private sealed record AV3(Guid Id, string Name, int Revision) : IDomainEvent;

    private sealed class AV1ToV2 : Upcaster<AV1, AV2>
    {
        public override AV2 Upcast(AV1 from) => new(from.Id, Name: "");
    }

    private sealed class AV2ToV3 : Upcaster<AV2, AV3>
    {
        public override AV3 Upcast(AV2 from) => new(from.Id, from.Name, Revision: 1);
    }

    // Lineage B: two shapes, one link. Current version 2. Independent of A.
    private sealed record BV1(Guid Id) : IDomainEvent;
    private sealed record BV2(Guid Id, bool Flag) : IDomainEvent;

    private sealed class BV1ToV2 : Upcaster<BV1, BV2>
    {
        public override BV2 Upcast(BV1 from) => new(from.Id, Flag: false);
    }

    // A registered name with no upcasters. Current version 1.
    private sealed record LoneEvent(Guid Id) : IDomainEvent;
}
