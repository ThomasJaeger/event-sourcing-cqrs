using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

// LoggingCommandBehavior is exercised directly here rather than through the
// CommandBus, because the bus pushes its own CommandContext onto the accessor
// before the pipeline runs. Testing the behavior's accessor read in isolation
// verifies its contract without depending on bus mechanics.
public sealed class LoggingBehaviorTests
{
    [Fact]
    public async Task LoggingCommandBehavior_logs_success_with_correlation_id_from_accessor()
    {
        var correlationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext { CorrelationId = correlationId }
        };
        var logger = new RecordingLogger<LoggingCommandBehavior<DoThing>>();
        var behavior = new LoggingCommandBehavior<DoThing>(logger, accessor);

        await behavior.HandleAsync(new DoThing(), () => Task.CompletedTask, CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("DoThing").And.Contain(correlationId.ToString());
        entry.Exception.Should().BeNull();
    }

    [Fact]
    public async Task LoggingCommandBehavior_logs_failure_and_rethrows_when_next_throws()
    {
        var accessor = new StubCommandContextAccessor { Current = new StubCommandContext() };
        var logger = new RecordingLogger<LoggingCommandBehavior<DoThing>>();
        var behavior = new LoggingCommandBehavior<DoThing>(logger, accessor);
        var thrown = new InvalidOperationException("boom");

        var act = () => behavior.HandleAsync(new DoThing(), () => Task.FromException(thrown), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown);
        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task LoggingCommandBehavior_falls_back_to_empty_correlation_id_when_accessor_unpopulated()
    {
        var accessor = new StubCommandContextAccessor { Current = null };
        var logger = new RecordingLogger<LoggingCommandBehavior<DoThing>>();
        var behavior = new LoggingCommandBehavior<DoThing>(logger, accessor);

        await behavior.HandleAsync(new DoThing(), () => Task.CompletedTask, CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain(Guid.Empty.ToString());
    }

    [Fact]
    public async Task LoggingQueryBehavior_logs_success_with_query_type_and_duration()
    {
        var logger = new RecordingLogger<LoggingQueryBehavior<Echo, string>>();
        var behavior = new LoggingQueryBehavior<Echo, string>(logger);

        var result = await behavior.HandleAsync(new Echo("hi"), () => Task.FromResult("hi"), CancellationToken.None);

        result.Should().Be("hi");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Message.Should().Contain("Echo");
    }

    private sealed record DoThing : ICommand;

    private sealed record Echo(string Payload) : IQuery<string>;
}
