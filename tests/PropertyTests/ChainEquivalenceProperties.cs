using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;
using FG = FsCheck.Fluent.Gen;
using FP = FsCheck.Fluent.Prop;

namespace EventSourcingCqrs.PropertyTests;

// Chain equivalence, the property Chapter 11 names in Testing Upcasters: "a chain-equivalence property
// (v1-to-v2-to-v3 should equal v1-to-v3, for upcasters that compose)". It is the property that makes
// per-hop tests worth having, because per-hop facts pin what each link does and this pins that
// composing them is the same thing the pipeline does.
//
// What these can and cannot catch, stated so nobody mistakes their reach. Both forms compare two
// paths built from the same link instances, so a defect inside a link cancels: it lands identically
// on both sides and the property stays green. Link behaviour is pinned by the per-hop facts in
// ScratchLinkTests instead. What these catch is a defect in the composition itself, an off-by-one in
// the fold, links applied out of order, a hop skipped, a start version resolved to the wrong shape.
// That division is deliberate, and the scratch break that proves these facts non-vacuous is therefore
// a break in the pipeline rather than in a link.
//
// The Trail each shape carries is what makes composition observable. Every link copies the Id
// forward, so Id alone cannot distinguish a correct fold from one that ran the links in the wrong
// order or dropped one; the Trail records which links ran and in what order, so any of those shows up
// as a different string. Records give structural equality, so comparing whole shapes compares both
// members at once.
public class ChainEquivalenceProperties
{
    // Fold form. Lifting from a stored version straight to the terminal equals applying that
    // lineage's hops one at a time from the same starting point, for every chain length in the pool
    // and every version that chain carries. MaxTest 200 covers the twenty-one length-and-version
    // pairs the pool spans; Replay is fixed so a falsification reproduces from one seed.
    [Property(MaxTest = 200, Replay = "44444444,77777777")]
    public FsCheck.Property A_chain_lift_equals_the_same_hops_applied_one_at_a_time()
        => FP.ForAll(LengthAndStartVersion.ToArbitrary(), pair =>
        {
            var (length, version) = pair;
            var pipeline = new EventUpcasterPipeline(
                ScratchLineage.RegistryFor(length), ScratchLineage.LinksFor(length));

            var source = ScratchLineage.Instance(version);

            var byChain = pipeline.Upcast(ScratchLineage.StorageName, version, source);
            var byHops = ApplyHops(source, version, length);

            Assert.Equal(byChain, byHops);
            return true;
        });

    // Split form. Cutting the chain at any point and lifting in two stages equals lifting in one, for
    // every chain length and every split point that chain admits. Both stages are real pipelines
    // rather than hop folds: a pipeline whose terminal is the shape at the cut lifts the first
    // stage, and a pipeline built from the remaining links lifts the second, where the shape at the
    // cut is version 1 because the pipeline derives versions from chain topology.
    [Property(MaxTest = 200, Replay = "55555555,99999999")]
    public FsCheck.Property Lifting_through_a_split_equals_lifting_in_one_pass()
        => FP.ForAll(LengthAndSplitPoint.ToArbitrary(), pair =>
        {
            var (length, k) = pair;
            var source = ScratchLineage.Instance(1);

            var whole = new EventUpcasterPipeline(
                ScratchLineage.RegistryFor(length), ScratchLineage.LinksFor(length));
            var inOnePass = whole.Upcast(ScratchLineage.StorageName, 1, source);

            var firstStage = new EventUpcasterPipeline(
                ScratchLineage.RegistryFor(k), ScratchLineage.LinksBetween(1, k));
            var atTheCut = firstStage.Upcast(ScratchLineage.StorageName, 1, source);

            var secondStage = new EventUpcasterPipeline(
                ScratchLineage.RegistryFor(length), ScratchLineage.LinksBetween(k, length));
            var inTwoStages = secondStage.Upcast(ScratchLineage.StorageName, 1, atTheCut);

            Assert.Equal(inOnePass, inTwoStages);
            return true;
        });

    // The hop-by-hop side of the fold form, kept in the test rather than the fixture so the arithmetic
    // the property claims is visible where the claim is made.
    private static IDomainEvent ApplyHops(IDomainEvent from, int fromVersion, int toVersion)
    {
        var current = from;
        for (var v = fromVersion; v < toVersion; v++)
        {
            current = ScratchLineage.StepOnce(current, v);
        }
        return current;
    }

    // Every (chain length, stored version) pair the pool admits, with the version inside the chain.
    private static readonly Gen<(int Length, int Version)> LengthAndStartVersion =
        from length in FG.Choose(1, ScratchLineage.PoolSize)
        from version in FG.Choose(1, length)
        select (length, version);

    // Every (chain length, split point) pair. The cut may sit at either end: at 1 the first stage
    // runs nothing, at the length the second does, and both degenerate cases are lifts the property
    // should still hold for.
    private static readonly Gen<(int Length, int Split)> LengthAndSplitPoint =
        from length in FG.Choose(1, ScratchLineage.PoolSize)
        from k in FG.Choose(1, length)
        select (length, k);
}
