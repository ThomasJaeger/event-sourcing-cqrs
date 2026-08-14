using Xunit;

namespace EventSourcingCqrs.PropertyTests;

// Per-hop facts for the scratch lineage's five links (Chapter 11: Testing Upcasters, Unit Tests).
// Every other assertion over these links runs through EventUpcasterPipeline, which always lifts to the
// terminal, so a link that dropped what it received or stamped the wrong mark could be masked by the
// links after it. These drive each link on its own and assert both members of the output, which is
// every member, rather than the ones a lift happens to expose.
//
// One theory per link rather than three or four facts each. The links are pure carry-forward, so
// hand-written facts would be five near-identical bodies repeated three times over, and near-identical
// facts are worse coverage wearing the shape of more: nobody rereads them when one starts lying. The
// rows carry the representative inputs the chapter asks for: an empty incoming Trail, one a prior hop
// left behind, and one whose content would expose a link that rebuilt the Trail instead of appending
// to it.
//
// The chapter's malformed-payload sample class is left out rather than invented. These are records
// with non-nullable members, so a malformed input cannot be constructed, and the links carry no
// defensive path to exercise.
public class ScratchLinkTests
{
    private const string Seed = "";
    private const string PriorHop = "L0;";
    private const string Unusual = "x;;y;L9;";

    [Theory]
    [InlineData(Seed)]
    [InlineData(PriorHop)]
    [InlineData(Unusual)]
    public void L1_carries_the_id_and_appends_its_own_mark(string incoming)
    {
        var id = Guid.NewGuid();

        var result = new L1().Upcast(new V1(id, incoming));

        Assert.Equal(id, result.Id);
        Assert.Equal(incoming + ScratchLineage.MarkFor(1), result.Trail);
    }

    [Theory]
    [InlineData(Seed)]
    [InlineData(PriorHop)]
    [InlineData(Unusual)]
    public void L2_carries_the_id_and_appends_its_own_mark(string incoming)
    {
        var id = Guid.NewGuid();

        var result = new L2().Upcast(new V2(id, incoming));

        Assert.Equal(id, result.Id);
        Assert.Equal(incoming + ScratchLineage.MarkFor(2), result.Trail);
    }

    [Theory]
    [InlineData(Seed)]
    [InlineData(PriorHop)]
    [InlineData(Unusual)]
    public void L3_carries_the_id_and_appends_its_own_mark(string incoming)
    {
        var id = Guid.NewGuid();

        var result = new L3().Upcast(new V3(id, incoming));

        Assert.Equal(id, result.Id);
        Assert.Equal(incoming + ScratchLineage.MarkFor(3), result.Trail);
    }

    [Theory]
    [InlineData(Seed)]
    [InlineData(PriorHop)]
    [InlineData(Unusual)]
    public void L4_carries_the_id_and_appends_its_own_mark(string incoming)
    {
        var id = Guid.NewGuid();

        var result = new L4().Upcast(new V4(id, incoming));

        Assert.Equal(id, result.Id);
        Assert.Equal(incoming + ScratchLineage.MarkFor(4), result.Trail);
    }

    [Theory]
    [InlineData(Seed)]
    [InlineData(PriorHop)]
    [InlineData(Unusual)]
    public void L5_carries_the_id_and_appends_its_own_mark(string incoming)
    {
        var id = Guid.NewGuid();

        var result = new L5().Upcast(new V5(id, incoming));

        Assert.Equal(id, result.Id);
        Assert.Equal(incoming + ScratchLineage.MarkFor(5), result.Trail);
    }
}
