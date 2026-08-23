using EventSourcingCqrs.Demo.Seeder;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// The demo seeder. It drives the reference implementation through named scenarios so a reader
// can watch an order run end to end, watch a compensation fire, and watch two tenants keep their
// data apart, without clicking through the UI to arrange each one.
//
// It is a runnable tool rather than a hosted service, so seeding is a choice the operator makes
// rather than something a host does on every start. Run it through the compose stack the README
// describes, after the migrations have been applied and while the Workers host is running.
//
// Workers is what advances projections. No scenario can finish without it, and this process has
// no way to check: the Workers host holds no lease, writes no heartbeat row, keeps no session
// advisory lock, and exposes no HTTP surface. The catch-up waiter's bound is the signal, and it
// reports which projections stayed behind and at what position.

const string usage =
    "Usage: Demo.Seeder <scenario>, where <scenario> is one of: clean, compensation, tenants, all";

var scenario = args.Length > 0 ? args[0] : "all";
string[] known = ["clean", "compensation", "tenants", "all"];
if (!known.Contains(scenario))
{
    Console.Error.WriteLine(usage);
    return 64; // EX_USAGE
}

var eventStoreConnectionString = Environment.GetEnvironmentVariable("EVENT_STORE_CONNECTION_STRING");
if (string.IsNullOrEmpty(eventStoreConnectionString))
{
    Console.Error.WriteLine("EVENT_STORE_CONNECTION_STRING is not set.");
    return 78; // EX_CONFIG
}

var readModelConnectionString = Environment.GetEnvironmentVariable("READ_MODEL_CONNECTION_STRING");
if (string.IsNullOrEmpty(readModelConnectionString))
{
    Console.Error.WriteLine("READ_MODEL_CONNECTION_STRING is not set.");
    return 78; // EX_CONFIG
}

try
{
    Console.WriteLine($"Demo seeder: running scenario '{scenario}'.");
    Console.WriteLine("The Workers host must be running, since projections advance there.");

    await using var provider = SeederStartup.Compose(
        eventStoreConnectionString, readModelConnectionString);

    // Resolved once so the wiring is exercised on every run rather than on the first scenario
    // that reaches for it. Opening a connection is deferred, so this stays cheap and says
    // nothing about whether the database is reachable.
    _ = provider.GetRequiredService<ProjectionCatchUpWaiter>();

    await RunScenarioAsync(scenario);

    Console.WriteLine();
    Console.WriteLine("Seeding complete.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Seeding failed: {ex.Message}");
    return 1;
}

static Task RunScenarioAsync(string scenario)
{
    switch (scenario)
    {
        case "clean":
            RunClean();
            break;
        case "compensation":
            RunCompensation();
            break;
        case "tenants":
            RunTenants();
            break;
        case "all":
            RunClean();
            RunCompensation();
            RunTenants();
            break;
    }

    return Task.CompletedTask;
}

static void RunClean()
    => Console.WriteLine("  clean: an order through to completion. Not yet implemented.");

static void RunCompensation()
    => Console.WriteLine("  compensation: an order whose reservation fails. Not yet implemented.");

static void RunTenants()
    => Console.WriteLine("  tenants: two tenants writing the same order id. Not yet implemented.");
