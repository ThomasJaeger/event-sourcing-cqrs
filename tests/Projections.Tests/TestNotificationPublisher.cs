using EventSourcingCqrs.Infrastructure.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventSourcingCqrs.Projections.Tests;

// Builds a PostgresPgNotifyPublisher for the Postgres store tests. The publisher
// sources its serializer and channel name from NotificationContract, so the
// helper supplies only the logger. Most store tests never stage a notification,
// so the publisher is inert there.
internal static class TestNotificationPublisher
{
    public static PostgresPgNotifyPublisher Create()
        => new(NullLogger<PostgresPgNotifyPublisher>.Instance);
}
