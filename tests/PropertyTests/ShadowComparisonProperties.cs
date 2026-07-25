using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.Shadow;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using FG = FsCheck.Fluent.Gen;
using FP = FsCheck.Fluent.Prop;

namespace EventSourcingCqrs.PropertyTests;

// Properties over the shadow-mode comparator (Chapter 18). Shadow mode emits events in parallel with a
// legacy write and compares them; these pin the comparator's two obligations over generated legacy
// operation sequences rather than one example. The builders below are pure and mirror the demo's
// conventions, each named where it is mirrored: a placed order's legacy status is "new" and a paid one
// is "paid"; the emitted events are OrderDrafted then, if paid, OrderPlaced, with identity through
// OrderIdFor/CustomerIdFor and the placed total as Money(total, USD). The comparator ignores timestamps
// (the two sides record them independently), so the modeled events carry a fixed one.
public class ShadowComparisonProperties
{
    private const long OrderId = 1;
    private static readonly DateTime FixedUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<string> AnyCustomerName =
        FG.Elements("Ada Lovelace", "Grace Hopper", "Katherine Johnson", string.Empty);

    // Cents scaled to a decimal, so a total round-trips through the numeric column and the Money amount.
    private static readonly Gen<decimal> AnyTotal =
        FG.Choose(1, 1_000_000).Select(cents => cents / 100m);

    private static readonly Gen<(string Name, decimal Total, bool Paid)> Sequence =
        from name in AnyCustomerName
        from total in AnyTotal
        from paid in FG.Elements(true, false)
        select (name, total, paid);

    private static readonly Gen<(string Name, decimal Total, bool Paid, int DropSeed)> DropCase =
        from name in AnyCustomerName
        from total in AnyTotal
        from paid in FG.Elements(true, false)
        from dropSeed in FG.Choose(0, 100)
        select (name, total, paid, dropSeed);

    // Agreement: a faithful legacy sequence and its emitted events compare as a match. MaxTest 200 spans
    // the name, total, and paid axes; Replay is fixed so a falsification reproduces from one seed.
    [Property(MaxTest = 200, Replay = "20260725,10101011")]
    public FsCheck.Property Faithful_shadow_emission_matches_the_legacy_row()
        => FP.ForAll(Sequence.ToArbitrary(), seq =>
        {
            var (name, total, paid) = seq;
            return ShadowComparator.Compare(LegacyState(name, total, paid), EmittedEvents(name, total, paid))
                .IsMatch;
        });

    // Detection: dropping any one emitted event breaks the match. DECLARED green-on-write under the
    // placeholder (Compare always mismatches), so this passes trivially until GREEN lands the fold.
    [Property(MaxTest = 200, Replay = "20260725,20202023")]
    public FsCheck.Property Dropping_an_emitted_event_is_a_mismatch()
        => FP.ForAll(DropCase.ToArbitrary(), testCase =>
        {
            var (name, total, paid, dropSeed) = testCase;
            var events = EmittedEvents(name, total, paid);
            var reduced = events.Where((_, i) => i != dropSeed % events.Count).ToList();
            return !ShadowComparator.Compare(LegacyState(name, total, paid), reduced).IsMatch;
        });

    private static LegacyOrderState LegacyState(string name, decimal total, bool paid)
        => new(OrderId, name, paid ? "paid" : "new", total);

    private static IReadOnlyList<IDomainEvent> EmittedEvents(string name, decimal total, bool paid)
    {
        var orderId = LegacyChangeTranslator.OrderIdFor(OrderId);
        var customerId = LegacyChangeTranslator.CustomerIdFor(name);
        var events = new List<IDomainEvent>
        {
            new OrderDrafted(orderId, customerId, FixedUtc, LegacyChangeTranslator.LegacyChannel),
        };
        if (paid)
        {
            events.Add(new OrderPlaced(orderId, customerId, new Money(total, Currency.USD), FixedUtc));
        }

        return events;
    }
}
