using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Migration.Demo;
using EventSourcingCqrs.Migration.Demo.Scenarios;
using Microsoft.Extensions.DependencyInjection;

// Chapter 18: the migration-patterns demo. It stands up a CRUD-shaped legacy database and an event store,
// then runs one or all of the four patterns (CDC, outbox-on-legacy, strangler, shadow) end to end,
// narrating each on the console: what was written to the legacy side, what the pattern did, and what the
// event store holds afterward. Run it through src/Migration/docker-compose.yml.

const string usage =
    "Usage: Migration.Demo <scenario>, where <scenario> is one of: cdc, outbox, strangler, shadow, all";

var scenario = args.Length > 0 ? args[0] : "all";
string[] known = ["cdc", "outbox", "strangler", "shadow", "all"];
if (!known.Contains(scenario))
{
    Console.Error.WriteLine(usage);
    return 64;
}

var legacyConnectionString = Environment.GetEnvironmentVariable("LEGACY_CONNECTION_STRING")
    ?? throw new InvalidOperationException("LEGACY_CONNECTION_STRING is not set.");
var eventStoreConnectionString = Environment.GetEnvironmentVariable("EVENT_STORE_CONNECTION_STRING")
    ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");

try
{
    await DemoStartup.PrepareDatabasesAsync(legacyConnectionString, eventStoreConnectionString);
    await using var provider = DemoStartup.Compose(eventStoreConnectionString);
    var context = new DemoContext(
        legacyConnectionString,
        provider.GetRequiredService<IEventStore>(),
        provider.GetRequiredService<ICurrentEventSchemaVersions>(),
        provider.GetRequiredService<EventTypeRegistry>(),
        provider.GetRequiredService<JsonSerializerOptions>(),
        provider.GetRequiredService<ICommandBus>());

    await RunScenarioAsync(scenario, context);

    Console.WriteLine();
    Console.WriteLine("Demo complete.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Demo failed: {ex.Message}");
    return 1;
}

static async Task RunScenarioAsync(string scenario, DemoContext context)
{
    switch (scenario)
    {
        case "cdc":
            await CdcScenario.RunAsync(context);
            break;
        case "outbox":
            await OutboxScenario.RunAsync(context);
            break;
        case "strangler":
            await StranglerScenario.RunAsync(context);
            break;
        case "shadow":
            await ShadowScenario.RunAsync(context);
            break;
        case "all":
            await CdcScenario.RunAsync(context);
            await OutboxScenario.RunAsync(context);
            await StranglerScenario.RunAsync(context);
            await ShadowScenario.RunAsync(context);
            break;
    }
}
