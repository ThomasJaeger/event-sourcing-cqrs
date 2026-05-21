namespace EventSourcingCqrs.Domain.Abstractions;

// The system actors that originate commands without a human principal. The Guid
// values are stable hand-generated constants: an audit query for "everything
// the order-fulfillment process manager did" filters on the Guid, so it must
// not drift across restarts or deployments. ServiceName carries the
// human-readable process-manager identity into EventMetadata.Source (ADR 0014).
public static class SystemActors
{
    public static readonly SystemActor OrderFulfillment =
        new(Guid.Parse("0a3f0514-d51a-4699-9e3b-d311e5c57036"), "pm:order-fulfillment");

    public static readonly SystemActor Return =
        new(Guid.Parse("f37643fd-25b9-42f2-95dd-b4334630fbc5"), "pm:return");
}
