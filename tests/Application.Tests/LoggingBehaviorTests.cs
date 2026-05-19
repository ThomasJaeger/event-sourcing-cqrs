using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

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
        var services = BuildServices(accessor, logger, handler: new PassingHandler());
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("DoThing").And.Contain(correlationId.ToString());
        entry.Exception.Should().BeNull();
    }

    [Fact]
    public async Task LoggingCommandBehavior_logs_failure_and_rethrows_when_handler_throws()
    {
        var accessor = new StubCommandContextAccessor { Current = new StubCommandContext() };
        var logger = new RecordingLogger<LoggingCommandBehavior<DoThing>>();
        var thrown = new InvalidOperationException("boom");
        var services = BuildServices(accessor, logger, handler: new ThrowingHandler(thrown));
        var bus = new CommandBus(services);

        var act = () => bus.SendAsync(new DoThing(), CancellationToken.None);

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
        var services = BuildServices(accessor, logger, handler: new PassingHandler());
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain(Guid.Empty.ToString());
    }

    [Fact]
    public async Task LoggingQueryBehavior_logs_success_with_query_type_and_duration()
    {
        var logger = new RecordingLogger<LoggingQueryBehavior<Echo, string>>();
        var services = new ServiceCollection()
            .AddSingleton<ILogger<LoggingQueryBehavior<Echo, string>>>(logger)
            .AddSingleton<IQueryHandler<Echo, string>>(new EchoHandler())
            .AddSingleton<IQueryPipelineBehavior<Echo, string>, LoggingQueryBehavior<Echo, string>>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        var result = await bus.AskAsync(new Echo("hi"), CancellationToken.None);

        result.Should().Be("hi");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Message.Should().Contain("Echo");
    }

    private static IServiceProvider BuildServices(
        StubCommandContextAccessor accessor,
        RecordingLogger<LoggingCommandBehavior<DoThing>> logger,
        ICommandHandler<DoThing> handler)
    {
        return new ServiceCollection()
            .AddSingleton<ICommandContextAccessor>(accessor)
            .AddSingleton<ILogger<LoggingCommandBehavior<DoThing>>>(logger)
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<ICommandPipelineBehavior<DoThing>, LoggingCommandBehavior<DoThing>>()
            .BuildServiceProvider();
    }

    private sealed record DoThing : ICommand;

    private sealed record Echo(string Payload) : IQuery<string>;

    private sealed class PassingHandler : ICommandHandler<DoThing>
    {
        public Task HandleAsync(DoThing command, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingHandler : ICommandHandler<DoThing>
    {
        private readonly Exception _toThrow;
        public ThrowingHandler(Exception toThrow) => _toThrow = toThrow;
        public Task HandleAsync(DoThing command, CancellationToken ct) => Task.FromException(_toThrow);
    }

    private sealed class EchoHandler : IQueryHandler<Echo, string>
    {
        public Task<string> HandleAsync(Echo query, CancellationToken ct) => Task.FromResult(query.Payload);
    }
}
