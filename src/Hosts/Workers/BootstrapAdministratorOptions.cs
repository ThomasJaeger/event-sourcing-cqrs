namespace EventSourcingCqrs.Hosts.Workers;

// Configuration for the bootstrap administrator seed. AdministratorUserId is the user the Workers
// host grants the Admin role on startup, so the system has an assigner before any other assignment
// can be made. An empty id (the default when nothing is configured) disables the seed, which a
// deployment that provisions its administrator another way can rely on.
public sealed class BootstrapAdministratorOptions
{
    public Guid AdministratorUserId { get; init; }
}
