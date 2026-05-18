using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class QueryBusTests
{
    [Fact]
    public async Task AskAsync_dispatches_to_registered_handler_and_returns_result()
    {
        var handler = new EchoHandler();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        var result = await bus.AskAsync(new Echo("ping"), CancellationToken.None);

        result.Should().Be("ping");
    }

    [Fact]
    public async Task AskAsync_throws_when_handler_not_registered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var bus = new QueryBus(services);

        var act = () => bus.AskAsync(new Echo("x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AskAsync_passes_cancellation_token_to_handler()
    {
        var handler = new EchoHandler();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .BuildServiceProvider();
        var bus = new QueryBus(services);
        using var cts = new CancellationTokenSource();

        await bus.AskAsync(new Echo("x"), cts.Token);

        handler.ReceivedToken.Should().Be(cts.Token);
    }

    private sealed record Echo(string Payload) : IQuery<string>;

    private sealed class EchoHandler : IQueryHandler<Echo, string>
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<string> HandleAsync(Echo query, CancellationToken ct)
        {
            ReceivedToken = ct;
            return Task.FromResult(query.Payload);
        }
    }
}
