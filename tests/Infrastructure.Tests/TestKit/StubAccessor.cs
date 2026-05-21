using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.TestKit;

internal sealed class StubAccessor : ICommandContextAccessor
{
    public ICommandContext? Current { get; set; }
}

internal sealed class StubContext : ICommandContext
{
    public Guid CorrelationId { get; init; } = Guid.Empty;
    public Guid CausationCommandId { get; init; } = Guid.Empty;
    public Guid ActorId { get; init; } = Guid.Empty;
    public string ServiceName { get; init; } = "TestService";
    public string? IdempotencyKey { get; init; }

    private readonly DateTime _utcNow = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    public DateTimeOffset UtcNow() => new(_utcNow, TimeSpan.Zero);
}
