using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Access.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CurrentRoles;
using EventSourcingCqrs.TestInfrastructure;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// The current-user-roles cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// This family is the eighth and last, and it differs from the seven before it in the one way that
// matters most to a reader of a green run. Its table carries no tenant_id column, so almost nothing
// a reader would infer from the other seven holds here. What follows is what does hold.
//
// The isolation property passes for this family structurally rather than by observation. Its table
// set comes from the live schema, filtered to read_models tables carrying a tenant_id column, which
// against a database migrated to head is fourteen of the sixteen. read_models.current_user_roles is
// one of the two it excludes. So the property digests fourteen tables this family never writes,
// finds them identical across the act, and passes. It cannot fail here whatever these drives do.
// Its green is a statement about the other families' tables, not about this one.
//
// The retiring condition is on record in the two migrations that between them settle this table.
// Migration 0015 creates it keyed (user_id, role) with no discriminator, and migration 0017's
// header records why it stays out: current_user_roles "defers its tenant to the slice that resolves
// a per-user tenant on the principal." One refinement matters and is easy to get backwards. Because
// the property reads its table set from the live schema rather than from a list, the vacuity retires
// by itself the moment that slice adds the column: the table joins the digest set with no edit to
// the property. The family's coverage of a tenant does not retire by itself, because the adapter
// takes no current-tenant accessor and no statement here names a tenant, so covering one is a
// production change rather than a test change.
//
// Three of the five defect shapes session log 0065 names are inapplicable by construction here
// rather than absent, and two are applicable in mechanism and intended in effect.
//
//   Inapplicable by construction:
//     Untenanted mutation. All three mutations lack a tenant predicate, but "another tenant's row"
//       is undefined when no column partitions rows by tenant.
//     Conflict-target fold. The shape's first clause is unmet: AssignAsync names no tenant in its
//       column list, and its target (user_id, role) matches the primary key exactly.
//     Misattribution. AssignAsync names no tenant, but there is no discriminator column to take a
//       DEFAULT, so nothing is attributed to anyone.
//
//   Applicable in mechanism, intended in effect:
//     Key-forbidden state. The (user_id, role) key does forbid two tenants holding one role for one
//       user as separate rows; they collapse to one. That collapse is what 0015 intends.
//     Disclosure. GetRolesForUserAsync carries no tenant predicate. This is the shape that
//       most resembles a defect and is not one: the rows carry no tenant, so nothing is disclosed
//       across a boundary that exists.
//
// The isolation boundary this family does have is the authenticated actor id, and it is enforced at
// the two call sites rather than in the store. CurrentRolesPrincipalFactory.CreateAsync reads roles
// for the actor id it is handed and records the reason in its own comment, that loading the roles
// beats trusting a forwarded claim. AdminConsoleAccessHandler parses the actor id from the
// NameIdentifier claim and denies by default when it will not parse. Neither passes a tenant,
// because roles belong to a user rather than to a tenant.
//
// No complementary read-back is possible, which is the guard every DO NOTHING family before this one
// leans on. Reading back as the second tenant returns exactly the rows reading back as the owner
// returns, because the read carries no tenant predicate and the rows carry no tenant. A read-back
// here distinguishes nothing, so the facts beside this file assert what the rows are rather than who
// can see them.
//
// The aggregate identifier the harness hands these drives is the user id, and unlike the two
// families before it that is the row key. read_models.current_user_roles is keyed (user_id, role),
// user_id reaches these drives through RoleAssigned and RoleRevoked, and both tenants can present
// the same one. The role half of the key is chosen here as a constant.
//
// Two drives, one per handler. CurrentRolesProjection implements two handlers, each of which opens
// its own unit of work, checks the checkpoint, calls exactly one required write, and commits, so no
// single dispatched event reaches both. One act dispatching both events would satisfy the coverage
// property, and a coverage failure would then name the port and the unreached member without saying
// which handler stopped writing. Two drives make the failure identify the handler. That is the same
// ground the order-throughput family split on.
//
// Positions ascend within each drive because both handlers share one checkpoint, "current-roles".
// Each drive runs on its own migrated database, so a checkpoint does not leak between drives, but it
// does accumulate within one: an arrange at position 2 leaves an act at 2 or below silently skipped.
//
// No raw-SQL seed. Three of the seven families before this one carry one, and all three seed for the
// same reason: the act's handler path resolves through a tenant-predicated lookup read before it
// reaches the mutation under test. This handler path opens a unit of work, reads the checkpoint,
// makes one write, and commits, with no lookup between them. The Revoke drive's precondition row is
// created by its arrange through AssignAsync rather than by raw SQL.
//
// The assertions live in CurrentUserRolesCrossTenantWriteTests, where a reader debugging a failure
// will look. The drives live here so both that class and the harness run the same code.
internal static class CurrentUserRolesCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;

    // No shared At, which the six families before this one all carry. RoleAssigned and RoleRevoked
    // declare no timestamp, so the shared instant reaches no column and no statement here.

    // The role the owning tenant assigns first and keeps through both drives.
    internal const Role OwnerRole = Role.Admin;

    // The role the acting tenant assigns in the Assign drive, and revokes in the Revoke drive.
    internal const Role OtherRole = Role.Support;

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Assign", Bind(AssignOwnerRoleAsOwner), Bind(AssignSecondRoleAsOther)),
        new("Revoke", Bind(AssignBothRolesAsOwner), Bind(RevokeSecondRoleAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "CurrentUserRoles",
        typeof(ICurrentUserRolesUnitOfWork),
        BuildTarget,
        All);

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<CurrentRolesProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, userId, connStr)
            => drive((CurrentRolesProjection)target.Projection, target.Tenant, userId, connStr);

    // The real adapter, wrapped so the coverage property can see which members a run reached.
    //
    // Two departures from the seven Build methods before this one, both forced by the adapter. It
    // takes two constructor arguments where every other read-model store takes four: no notification
    // publisher, because the port declares no PublishOnCommit and the read model feeds the principal
    // factory rather than a dashboard, and no current-tenant accessor, because no statement here
    // names a tenant. The stub below is therefore inert. It is constructed and handed over because
    // CrossTenantTarget requires one, and the drives flip it between phases to keep the shape
    // uniform, but nothing reads it and flipping it changes no statement this family issues.
    //
    // PostgresCurrentUserRolesStore is internal, the only one of the nine read-model store adapters
    // that is. Constructing it here is possible because ReadModels.Postgres.csproj grants
    // InternalsVisibleTo to Projections.Tests, which is this assembly.
    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresCurrentUserRolesStore(factory, new PostgresCheckpointStore(factory));
        var store = RecordingPort.Wrap<ICurrentUserRolesStore>(
            real, invoked, typeof(ICurrentUserRolesUnitOfWork));
        var projection = new CurrentRolesProjection(store);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the user's roles ===

    // One RoleAssigned at position 1, leaving the checkpoint there, so the act's position 10 clears
    // the guard.
    private static async Task AssignOwnerRoleAsOwner(
        CurrentRolesProjection p, StubTenantAccessor stub, Guid userId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleAssigned(userId, OwnerRole), 1, Owner),
            CancellationToken.None);
    }

    // The Revoke drive needs a row for its act to remove, and it needs a second row to survive that
    // removal, so the fact beside this file can hold that a revoke takes the role it names and no
    // other. Both are assigned by the owner, at 1 and 2, leaving the checkpoint at 2.
    private static async Task AssignBothRolesAsOwner(
        CurrentRolesProjection p, StubTenantAccessor stub, Guid userId, string connStr)
    {
        await AssignOwnerRoleAsOwner(p, stub, userId, connStr);
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleAssigned(userId, OtherRole), 2, Owner),
            CancellationToken.None);
    }

    // === act: the second tenant changes the same user's roles ===

    // A second role for the user the owner already assigned. The insert names no tenant and conflicts
    // on (user_id, role), and the role differs, so the row lands beside the owner's rather than
    // collapsing into it. Both are then visible to either tenant through the tenant-free read, which
    // is the recorded decision this family exists to hold.
    private static async Task AssignSecondRoleAsOther(
        CurrentRolesProjection p, StubTenantAccessor stub, Guid userId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleAssigned(userId, OtherRole), 10, Other),
            CancellationToken.None);
    }

    // The acting tenant removes one of the two roles the owner assigned. Under the recorded decision
    // that is correct rather than a crossing: roles belong to the user, so a revoke is legitimate
    // from whichever tenant's event carries it. The delete is keyed on the pair, so the owner's other
    // role survives, and that is what the fact beside this file holds.
    private static async Task RevokeSecondRoleAsOther(
        CurrentRolesProjection p, StubTenantAccessor stub, Guid userId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleRevoked(userId, OtherRole), 10, Other),
            CancellationToken.None);
    }
}
