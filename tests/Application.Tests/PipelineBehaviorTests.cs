using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class PipelineBehaviorTests
{
    [Fact]
    public async Task Command_behaviors_execute_in_registration_order_around_the_handler()
    {
        var log = new List<string>();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(new RecordingCommandHandler(log))
            .AddSingleton<ICommandPipelineBehavior<DoThing>>(new RecordingCommandBehavior(log, "first"))
            .AddSingleton<ICommandPipelineBehavior<DoThing>>(new RecordingCommandBehavior(log, "second"))
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        log.Should().Equal("first:before", "second:before", "handler", "second:after", "first:after");
    }

    [Fact]
    public async Task Command_behavior_short_circuits_when_it_skips_next()
    {
        var log = new List<string>();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(new RecordingCommandHandler(log))
            .AddSingleton<ICommandPipelineBehavior<DoThing>>(new SkipNextBehavior(log))
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        log.Should().Equal("skip");
    }

    [Fact]
    public async Task Query_behaviors_execute_in_registration_order_around_the_handler()
    {
        var log = new List<string>();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(new RecordingEchoHandler(log))
            .AddSingleton<IQueryPipelineBehavior<Echo, string>>(new RecordingQueryBehavior(log, "first"))
            .AddSingleton<IQueryPipelineBehavior<Echo, string>>(new RecordingQueryBehavior(log, "second"))
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        var result = await bus.AskAsync(new Echo("hello"), CancellationToken.None);

        result.Should().Be("hello");
        log.Should().Equal("first:before", "second:before", "handler", "second:after", "first:after");
    }

    private sealed record DoThing : ICommand;

    private sealed record Echo(string Payload) : IQuery<string>;

    private sealed class RecordingCommandHandler : ICommandHandler<DoThing>
    {
        private readonly List<string> _log;
        public RecordingCommandHandler(List<string> log) => _log = log;
        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            _log.Add("handler");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandBehavior : ICommandPipelineBehavior<DoThing>
    {
        private readonly List<string> _log;
        private readonly string _name;
        public RecordingCommandBehavior(List<string> log, string name)
        {
            _log = log;
            _name = name;
        }
        public async Task HandleAsync(DoThing command, CommandHandlerDelegate next, CancellationToken ct)
        {
            _log.Add($"{_name}:before");
            await next();
            _log.Add($"{_name}:after");
        }
    }

    private sealed class SkipNextBehavior : ICommandPipelineBehavior<DoThing>
    {
        private readonly List<string> _log;
        public SkipNextBehavior(List<string> log) => _log = log;
        public Task HandleAsync(DoThing command, CommandHandlerDelegate next, CancellationToken ct)
        {
            _log.Add("skip");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEchoHandler : IQueryHandler<Echo, string>
    {
        private readonly List<string> _log;
        public RecordingEchoHandler(List<string> log) => _log = log;
        public Task<string> HandleAsync(Echo query, CancellationToken ct)
        {
            _log.Add("handler");
            return Task.FromResult(query.Payload);
        }
    }

    private sealed class RecordingQueryBehavior : IQueryPipelineBehavior<Echo, string>
    {
        private readonly List<string> _log;
        private readonly string _name;
        public RecordingQueryBehavior(List<string> log, string name)
        {
            _log = log;
            _name = name;
        }
        public async Task<string> HandleAsync(Echo query, QueryHandlerDelegate<string> next, CancellationToken ct)
        {
            _log.Add($"{_name}:before");
            var result = await next();
            _log.Add($"{_name}:after");
            return result;
        }
    }
}
