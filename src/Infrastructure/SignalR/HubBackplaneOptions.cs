namespace EventSourcingCqrs.Infrastructure.SignalR;

// Configuration for PostgresHubBackplaneConnection. ConnectionString is
// required; the adapter constructs its own NpgsqlDataSource from it rather
// than reusing the event-store's or read-model's connection factory, since
// the hub's listener is independent of those adapters' lifecycles and the
// backplane lives in Hosts.Web which has no other Postgres dependency.
// ChannelName stays overridable but defaults to NotificationContract.ChannelName,
// the single source both the publisher (NOTIFY) and this backplane (LISTEN)
// reference, so the two sides agree by construction rather than by two literals
// that happen to match.
//
// No validation framework usage; constructor guards on
// PostgresHubBackplaneConnection enforce the required-field contract at
// first-resolution which happens during DI container build at startup. This
// matches the codebase's existing options-validation pattern: every existing
// options class uses constructor guards rather than IValidateOptions or
// ValidateDataAnnotations.
public sealed class HubBackplaneOptions
{
    public string ConnectionString { get; init; } = "";
    public string ChannelName { get; init; } = NotificationContract.ChannelName;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
}
