using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubCommandContext : ICommandContext
{
    public Guid CorrelationId { get; init; } = Guid.Empty;
    public Guid CausationCommandId { get; init; } = Guid.Empty;
    public string UserId { get; init; } = "test-user";
    public string ServiceName { get; init; } = "test-service";

    private readonly DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow() => _utcNow;
}
