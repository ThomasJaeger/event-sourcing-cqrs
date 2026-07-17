extern alias AdminConsoleHost;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
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

    // The DynamoDb arm is addressed by its own key rather than a DSN, so it needs its own factory. No
    // EVENT_STORE_CONNECTION_STRING, and the absence is load-bearing: this host does not ask for a DSN
    // on an engine that has none, and a factory that supplied one anyway would compose green whether or
    // not that conditional existed.
    private static WebApplicationFactory<AdminConsoleHost::Program> DynamoDbFactory()
        => new WebApplicationFactory<AdminConsoleHost::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("EVENT_STORE_PROVIDER", "DynamoDb")
            .UseSetting("EVENT_STORE_DYNAMODB_SERVICE_URL", "http://localhost:4566")
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

    // The console's DynamoDb arm, the twin of the Kurrent case above and composed for the same
    // reasons. Green on write and declared: the arm's registrations are wiring, and resolving them
    // proves only that the container hands back the types the arm named. The head reader is the part
    // with behavior, and it is pinned against LocalStack in
    // Infrastructure.Tests/DynamoDb/DynamoDbEventStoreHeadReaderTests. This fact would not survive
    // the arm being deleted, which is what it is for.
    //
    // This pair supersedes and replaces the refusal fact that stood here, which asserted the console
    // threw on DynamoDb through its default arm. That fact was true of a console with no DynamoDb read
    // side and is false of this one.
    //
    // No connection is opened here. The host composes DI only, and DynamoDbEventStore builds its
    // client lazily from options, so a service URL pointing at nothing is enough to compose against.
    [Fact]
    public void DynamoDb_composes_the_dynamodb_read_side_ports()
    {
        using var factory = DynamoDbFactory();
        var services = factory.Services;

        services.GetRequiredService<IEventStore>().Should().BeOfType<DynamoDbEventStore>();
        services.GetRequiredService<IEventStoreHeadPosition>()
            .Should().BeOfType<DynamoDbEventStoreHeadReader>();
        services.GetRequiredService<ICorrelationTraceReader>()
            .Should().BeOfType<DynamoDbCorrelationTraceReader>();
    }

    // The tracer's unavailable state on this engine, and the reason is the fact rather than the flag.
    // An operator who opens /correlations on a DynamoDB deployment gets a notice, and the notice is
    // only useful if it says why, which is why an empty reason throws at composition.
    [Fact]
    public void DynamoDb_composes_an_unavailable_tracer_naming_the_missing_index()
    {
        using var factory = DynamoDbFactory();

        var availability =
            factory.Services.GetRequiredService<AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Tracer.CorrelationTracerAvailability>();

        availability.IsAvailable.Should().BeFalse();
        availability.UnavailableReason.Should().Contain("no cross-stream correlation-id index");
    }
}
