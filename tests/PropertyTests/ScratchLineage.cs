using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;

namespace EventSourcingCqrs.PropertyTests;

// A bounded pool of scratch event shapes and adjacent-pair upcasters, the property-test analog of the
// per-file V1/V2/V3 records the example-based EventUpcasterPipelineTests declare. Chains are generated
// by choosing a length over this fixed pool, because a C# property test cannot synthesize new CLR types
// at run time: the pool is the type universe, and the generators pick a slice of it. Pattern from
// Chapter 11: Upcasting.

// Every scratch shape carries an Id, which a lift proves survived the chain, and a Trail, which proves
// how it got there. The Id alone cannot: every link copies it forward, so a hop that dropped its own
// work or misread the hop before it produced the same Id as a correct chain and no assertion could see
// the difference. The Trail is the field an intermediate hop sets and every later hop carries forward
// untouched, so both failure modes surface at the terminal: a hop that drops what it received loses the
// prefix, and a hop that stamps the wrong mark writes the wrong one.
internal interface IScratchEvent : IDomainEvent
{
    Guid Id { get; }

    string Trail { get; }
}

internal sealed record V1(Guid Id, string Trail) : IScratchEvent;
internal sealed record V2(Guid Id, string Trail) : IScratchEvent;
internal sealed record V3(Guid Id, string Trail) : IScratchEvent;
internal sealed record V4(Guid Id, string Trail) : IScratchEvent;
internal sealed record V5(Guid Id, string Trail) : IScratchEvent;
internal sealed record V6(Guid Id, string Trail) : IScratchEvent;

// The linear links, one per adjacent pair. Each copies the Id forward and appends its own mark to the
// Trail it received, so version 1 lifted to the terminal carries the same Id it started with and a
// Trail naming every link that ran, in the order they ran.
internal sealed class L1 : Upcaster<V1, V2> { public override V2 Upcast(V1 from) => new(from.Id, from.Trail + ScratchLineage.MarkFor(1)); }
internal sealed class L2 : Upcaster<V2, V3> { public override V3 Upcast(V2 from) => new(from.Id, from.Trail + ScratchLineage.MarkFor(2)); }
internal sealed class L3 : Upcaster<V3, V4> { public override V4 Upcast(V3 from) => new(from.Id, from.Trail + ScratchLineage.MarkFor(3)); }
internal sealed class L4 : Upcaster<V4, V5> { public override V5 Upcast(V4 from) => new(from.Id, from.Trail + ScratchLineage.MarkFor(4)); }
internal sealed class L5 : Upcaster<V5, V6> { public override V6 Upcast(V5 from) => new(from.Id, from.Trail + ScratchLineage.MarkFor(5)); }

// Defect links, off the linear pool so they collide with it only in the one way each defect names. They
// carry the Trail forward without marking it, because no property lifts through them: they exist to be
// rejected at construction.
// A branch adds a second link out of V1 to a target no chain produces (source collision, not target).
internal sealed record BranchTarget(Guid Id, string Trail) : IScratchEvent;
internal sealed class BranchLink : Upcaster<V1, BranchTarget> { public override BranchTarget Upcast(V1 from) => new(from.Id, from.Trail); }

// A merge adds a second producer of V2 from a source no chain consumes (target collision, not source).
internal sealed record MergeSource(Guid Id, string Trail) : IScratchEvent;
internal sealed class MergeLink : Upcaster<MergeSource, V2> { public override V2 Upcast(MergeSource from) => new(from.Id, from.Trail); }

// A cycle is a self-contained two-shape loop, so it trips the topology walk's seen-set without
// colliding on any source or target the linear chain uses.
internal sealed record CycleA(Guid Id, string Trail) : IScratchEvent;
internal sealed record CycleB(Guid Id, string Trail) : IScratchEvent;
internal sealed class CycleAB : Upcaster<CycleA, CycleB> { public override CycleB Upcast(CycleA from) => new(from.Id, from.Trail); }
internal sealed class CycleBA : Upcaster<CycleB, CycleA> { public override CycleA Upcast(CycleB from) => new(from.Id, from.Trail); }

// The construction defects the pipeline rejects. Merge and cycle discharge the V1 deferral: the pipeline
// shipped both guards in V1 with no dedicated fact, because the duplicate-link fact trips the co-located
// branch guard first and no example built a merge or a cyclic link set.
internal enum LinkDefect
{
    Duplicate,
    Gap,
    Orphan,
    Branch,
    Merge,
    Cycle,
    NonUpcaster,
}

internal sealed record DefectCase(EventTypeRegistry Registry, object[] Upcasters);

internal static class ScratchLineage
{
    public const int PoolSize = 6;
    public const string StorageName = "scratch";

    private static readonly Type[] ShapeTypes = [typeof(V1), typeof(V2), typeof(V3), typeof(V4), typeof(V5), typeof(V6)];

    // The CLR type of the shape at a 1-based version.
    public static Type ShapeType(int version) => ShapeTypes[version - 1];

    // The mark link N stamps on the Trail as it runs. Naming it once keeps the links, the expected
    // Trail, and any per-hop fact reading from one definition rather than three spellings of it.
    public static string MarkFor(int linkNumber) => $"L{linkNumber};";

    // The Trail a shape carries after lifting from one version to another. Lifting from version v to
    // version n runs links v through n-1, so the Trail names each of them in order. Lifting to the
    // version it started at runs nothing and leaves the seed.
    public static string ExpectedTrail(int fromVersion, int toVersion)
    {
        var trail = SeedTrail;
        for (var link = fromVersion; link < toVersion; link++)
        {
            trail += MarkFor(link);
        }
        return trail;
    }

    // The Trail a freshly-instanced shape starts with, before any link has run.
    public const string SeedTrail = "";

    // A fresh instance of the shape at a version, carrying a version-derived Id so the lift is
    // deterministic under a fixed replay seed, and the seed Trail so every mark on it was stamped by a
    // link that ran.
    public static IDomainEvent Instance(int version) => version switch
    {
        1 => new V1(SeededGuid(version), SeedTrail),
        2 => new V2(SeededGuid(version), SeedTrail),
        3 => new V3(SeededGuid(version), SeedTrail),
        4 => new V4(SeededGuid(version), SeedTrail),
        5 => new V5(SeededGuid(version), SeedTrail),
        6 => new V6(SeededGuid(version), SeedTrail),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    // A registry that knows the terminal shape of a length-N chain under the one storage name; the
    // older shapes are reachable only through the chain.
    public static EventTypeRegistry RegistryFor(int length)
        => new EventTypeRegistry().Register(ShapeType(length), StorageName);

    // The N-1 linear links a length-N chain needs, oldest first. Fresh instances each call so a
    // duplicate defect can add a second instance of the same link type.
    public static object[] LinksFor(int length) => LinksBetween(1, length);

    // The links a sub-chain needs, oldest first. Links are numbered by the version they consume, so a
    // lift from version v to version n runs links v through n-1, and an empty range is a lift that
    // runs nothing. This is what lets a property build a pipeline over part of the pool rather than
    // all of it, which is how the split form reaches a decomposition the public API cannot express.
    public static object[] LinksBetween(int fromVersion, int toVersion)
    {
        object[] all = [new L1(), new L2(), new L3(), new L4(), new L5()];
        return [.. all[(fromVersion - 1)..(toVersion - 1)]];
    }

    // Applies exactly one link: the one carrying the shape at fromVersion to the shape after it.
    //
    // This exists because EventUpcasterPipeline.Upcast lifts only to the terminal. There is no public
    // way to ask the seam for a single hop, so a property comparing a chain against its hops applied
    // one at a time has nothing to compare against without it. The links' own Upcast methods are
    // already public, so the affordance is a test-side switch over types this assembly already sees,
    // and nothing under src/ widens for it. Pattern from Chapter 11: Testing Upcasters.
    public static IDomainEvent StepOnce(IDomainEvent from, int fromVersion) => fromVersion switch
    {
        1 => new L1().Upcast((V1)from),
        2 => new L2().Upcast((V2)from),
        3 => new L3().Upcast((V3)from),
        4 => new L4().Upcast((V4)from),
        5 => new L5().Upcast((V5)from),
        _ => throw new ArgumentOutOfRangeException(nameof(fromVersion)),
    };

    // A valid length-N chain mutated by exactly one defect: the registry and upcaster set that make
    // new EventUpcasterPipeline(...) reject the shape.
    public static DefectCase Mutate(int length, LinkDefect defect)
    {
        var links = new List<object>(LinksFor(length));
        var registry = RegistryFor(length);
        switch (defect)
        {
            case LinkDefect.Duplicate:
                links.Add(new L1());
                break;
            case LinkDefect.Gap:
                links.RemoveAt(links.Count - 1);
                break;
            case LinkDefect.Orphan:
                registry = new EventTypeRegistry();
                break;
            case LinkDefect.Branch:
                links.Add(new BranchLink());
                break;
            case LinkDefect.Merge:
                links.Add(new MergeLink());
                break;
            case LinkDefect.Cycle:
                links.Add(new CycleAB());
                links.Add(new CycleBA());
                break;
            case LinkDefect.NonUpcaster:
                links.Add("not an upcaster");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
        return new DefectCase(registry, [.. links]);
    }

    private static Guid SeededGuid(int seed) => new(seed, 0, 0, new byte[8]);
}
