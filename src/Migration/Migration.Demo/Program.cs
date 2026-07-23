// Chapter 18: the migration-patterns demo, standalone from the reference system's own hosts.
//
// This entry point is composition only. It names the scenarios the demo will run so the project,
// the solution, and the compose topology are verifiable before any scenario exists. The CDC,
// outbox-on-legacy, strangler, and shadow-mode implementations land in later slices.
Console.WriteLine(
    "Usage: Migration.Demo <scenario>, where <scenario> is one of: cdc, outbox, strangler, shadow, all");
return 0;
