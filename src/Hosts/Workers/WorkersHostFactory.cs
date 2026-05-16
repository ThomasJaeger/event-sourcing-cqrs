using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventSourcingCqrs.Hosts.Workers;

// Composes the Workers host wiring in one place. Static so the integration
// test in tests/Workers.Tests can build the same host shape Program.cs
// builds against testcontainer connection strings, without duplicating
// registrations. Migrations orchestration lives in Program.cs, not here:
// the test fixture migrates ahead of the test, so the test path stays
// migration-free.
public static class WorkersHostFactory
{
    public static IHost Build(
        string eventStoreConnectionString,
        string readModelConnectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventStoreConnectionString);
        ArgumentException.ThrowIfNullOrEmpty(readModelConnectionString);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        builder.Services.AddPostgresEventStore(opts =>
            opts.ConnectionString = eventStoreConnectionString);
        builder.Services.AddReadModels(opts =>
            opts.ConnectionString = readModelConnectionString);
        builder.Services.AddHostedService<ProjectionStartupCatchUpService>();
        return builder.Build();
    }
}
