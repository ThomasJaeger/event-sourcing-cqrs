extern alias AdminConsoleHost;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// RED for slice 5's AdminConsole Kurrent arm. The console composes three read-side event-store ports
// (head position, replay/browse IEventStore, correlation trace) plus the correlation-tracer capability
// that gates the /correlations page. On PostgreSQL the ports are the relational readers and the tracer
// is available; on KurrentDB the ports are the engine's readers, the tracer is unavailable-with-reason,
// and its reader is the defense-in-depth throwing one; on SQL Server the console still refuses to
// compose, because it has no SQL Server read side.
//
// These boot the host through DI only, so placeholder connection strings suffice: no reader opens a
// connection at build time. The Kurrent case runs RED today because the inline guard in Program.cs
// rejects any non-Postgres provider before the arm would compose; the Postgres case runs RED because
// the capability is not registered yet. The GREEN slice adopts the parser twin, composes the arm, and
// registers the capability.
public class AdminConsoleProviderCompositionTests
{
    private const string ReadModelConnectionString =
        "Host=localhost;Database=unused;Username=u;Password=p";
    private const string PostgresEventStoreConnectionString =
        "Host=localhost;Database=unused;Username=u;Password=p";

    private static WebApplicationFactory<AdminConsoleHost::Program> Factory(
        string provider, string eventStoreConnectionString)
        => new WebApplicationFactory<AdminConsoleHost::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("EVENT_STORE_PROVIDER", provider)
            .UseSetting("EVENT_STORE_CONNECTION_STRING", eventStoreConnectionString)
            .UseSetting("READ_MODEL_CONNECTION_STRING", ReadModelConnectionString));

    [Fact]
    public void Kurrent_composes_the_kurrent_read_side_ports_and_an_unavailable_tracer()
    {
        using var factory = Factory("Kurrent", "esdb://localhost:2113?tls=false");
        var services = factory.Services;

        services.GetRequiredService<IEventStore>().Should().BeOfType<KurrentEventStore>();
        services.GetRequiredService<IEventStoreHeadPosition>()
            .Should().BeOfType<KurrentEventStoreHeadReader>();
        services.GetRequiredService<ICorrelationTraceReader>()
            .Should().BeOfType<KurrentCorrelationTraceReader>();
        var availability =
            services.GetRequiredService<AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Tracer.CorrelationTracerAvailability>();
        availability.IsAvailable.Should().BeFalse();
        availability.UnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Postgres_composes_the_postgres_read_side_ports_and_an_available_tracer()
    {
        using var factory = Factory("Postgres", PostgresEventStoreConnectionString);
        var services = factory.Services;

        var availability =
            services.GetRequiredService<AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Tracer.CorrelationTracerAvailability>();
        availability.IsAvailable.Should().BeTrue();

        services.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
        services.GetRequiredService<IEventStoreHeadPosition>()
            .Should().BeOfType<PostgresEventStoreHeadReader>();
        services.GetRequiredService<ICorrelationTraceReader>()
            .Should().BeOfType<PostgresCorrelationTraceReader>();
    }

    [Fact]
    public void SqlServer_refuses_to_compose_because_it_has_no_read_side()
    {
        using var factory = Factory("SqlServer", "Server=localhost;Database=unused;User Id=u;Password=p;");

        var boot = () => _ = factory.Services;

        boot.Should().Throw<InvalidOperationException>().WithMessage("*SqlServer*");
    }

    // Green on write, and declared rather than dressed up. The console's switch already refuses every
    // provider it has no arm for, and slice 3's parser scaffolding is what lets DynamoDb reach that
    // refusal as a parsed value instead of dying in Read. Nothing here discovers anything: it pins
    // that the console stays refused while the write side gains an engine, which is the property the
    // provider-switch slice must not break.
    //
    // The message prose is unpinned, and that is the fact's honest limit rather than laziness. The
    // console refuses SqlServer through a dedicated arm that says why, and refuses DynamoDb through
    // the default arm, which says only "Unhandled event store provider: DynamoDb." That prose is
    // byte-identical to ValidateConnectionString's own default arm, so an assertion on it could not
    // tell "the console has no DynamoDb read side" from "validation has no DynamoDb form." Pinning
    // an accident of arm ordering would pin the wrong thing.
    //
    // The AdminConsole slice supersedes and deletes this fact. When the console grows a DynamoDb read
    // side it gains a dedicated arm like the other engines, and the fact that replaces this one
    // asserts what that arm composes rather than that nothing does.
    [Fact]
    public void DynamoDb_refuses_to_compose_because_the_console_has_no_dynamodb_read_side()
    {
        using var factory = Factory("DynamoDb", "http://localhost:4566");

        var boot = () => _ = factory.Services;

        boot.Should().Throw<InvalidOperationException>();
    }
}
