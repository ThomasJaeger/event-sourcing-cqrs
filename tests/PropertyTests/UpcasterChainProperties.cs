using EventSourcingCqrs.Infrastructure.Versioning;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;
using FG = FsCheck.Fluent.Gen;
using FP = FsCheck.Fluent.Prop;

namespace EventSourcingCqrs.PropertyTests;

// Properties over the upcaster pipeline's topology (Chapter 11: Upcasting), generalizing the
// example-based EventUpcasterPipelineTests. The RED that proved these mechanisms is that suite's V1
// facts; these pin the same behavior over generated chains rather than three hand-written ones. The
// merge and cycle defects discharge the V1 deferral: the pipeline shipped both guards with no dedicated
// fact, and a generator that mutates a valid chain into every defect shape now exercises them.
public class UpcasterChainProperties
{
    // A well-formed linear chain is total over its versions: CurrentVersionFor is the chain length,
    // ResolveType returns the shape at each version, Upcast lifts every version to the terminal shape
    // with the payload preserved, and a version outside [1, length] throws. MaxTest 120 samples the six
    // chain lengths the pool spans; Replay is fixed so a falsification reproduces from one seed.
    [Property(MaxTest = 120, Replay = "11111111,33333333")]
    public FsCheck.Property A_well_formed_chain_is_total_over_its_versions()
        => FP.ForAll(FG.Choose(1, ScratchLineage.PoolSize).ToArbitrary(), length =>
        {
            var pipeline = new EventUpcasterPipeline(
                ScratchLineage.RegistryFor(length), ScratchLineage.LinksFor(length));

            Assert.Equal(length, pipeline.CurrentVersionFor(ScratchLineage.StorageName));

            for (var version = 1; version <= length; version++)
            {
                Assert.Equal(
                    ScratchLineage.ShapeType(version),
                    pipeline.ResolveType(ScratchLineage.StorageName, version));

                var source = ScratchLineage.Instance(version);
                var lifted = pipeline.Upcast(ScratchLineage.StorageName, version, source);

                Assert.Equal(ScratchLineage.ShapeType(length), lifted.GetType());
                Assert.Equal(((IScratchEvent)source).Id, ((IScratchEvent)lifted).Id);

                // The Id proves the payload survived; the Trail proves which links produced it. A hop
                // that dropped what it received or stamped the wrong mark leaves the same Id and a
                // different Trail, so this is the assertion that can see a chain composing wrongly.
                Assert.Equal(
                    ScratchLineage.ExpectedTrail(version, length),
                    ((IScratchEvent)lifted).Trail);
            }

            Assert.Throws<UnknownEventSchemaVersionException>(
                () => pipeline.ResolveType(ScratchLineage.StorageName, 0));
            Assert.Throws<UnknownEventSchemaVersionException>(
                () => pipeline.ResolveType(ScratchLineage.StorageName, length + 1));

            return true;
        });

    // A valid chain mutated by exactly one defect is rejected at construction. Every defect throws
    // InvalidOperationException (no named subtype), so the property asserts the throw and its timing,
    // not message text. MaxTest 200 spans the four chain-length by seven-defect combinations.
    [Property(MaxTest = 200, Replay = "22222222,55555555")]
    public FsCheck.Property A_chain_with_any_single_defect_is_rejected_at_construction()
        => FP.ForAll(DefectCases.ToArbitrary(), defectCase =>
        {
            Assert.Throws<InvalidOperationException>(
                () => new EventUpcasterPipeline(defectCase.Registry, defectCase.Upcasters));
            return true;
        });

    // Base chains from length 3 so a dropped-last-link gap still leaves at least one link that
    // terminates short of the registered terminal.
    private static readonly Gen<DefectCase> DefectCases =
        from length in FG.Choose(3, ScratchLineage.PoolSize)
        from defect in FG.Elements(
            LinkDefect.Duplicate,
            LinkDefect.Gap,
            LinkDefect.Orphan,
            LinkDefect.Branch,
            LinkDefect.Merge,
            LinkDefect.Cycle,
            LinkDefect.NonUpcaster)
        select ScratchLineage.Mutate(length, defect);
}
