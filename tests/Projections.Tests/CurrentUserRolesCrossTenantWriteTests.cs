using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The current-user-roles family's cross-tenant write facts (ADR 0031's discriminator model).
//
// One table, read_models.current_user_roles, created by migration 0015 keyed (user_id, role) and
// never altered since. It carries no tenant_id column, which migration 0017's header records as
// deliberate, so this family sits outside the discriminator model rather than inside it defectively.
// The drive file beside this one carries the full reading; two consequences land here.
//
// There is no complementary read-back to write. GetRolesForUserAsync carries no tenant predicate and
// the rows carry no tenant, so reading back as the second tenant returns exactly what reading back
// as the owner returns. The assertion that carries every DO NOTHING family before this one cannot be
// reproduced, and writing one would assert nothing while looking like coverage. The facts below
// therefore hold what the rows are rather than who can see them, and neither flips the accessor
// before reading, because the accessor reaches no statement this family issues.
//
// There is no fail-closed fact to write either. Neither adapter mentions a tenant anywhere: the
// store takes no ICurrentTenantAccessor and no statement calls ReadModelTenant.ResolveOrThrow, so
// there is no resolve to omit and nothing that could fail closed. A fact asserting a throw here
// would assert a throw no code can produce.
//
// What is left is the recorded decision itself, which is worth pinning at the family because it is
// the thing a later reader is most likely to mistake for a defect: two tenants' assignments for one
// user coexist, and either tenant's read sees both.
public sealed class CurrentUserRolesCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CurrentUserRolesCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // The recorded decision. The owner assigns one role, a second tenant assigns another to the same
    // user, and the tenant-free read returns both. Roles ride on the user rather than on a tenant, so
    // this is the intended behaviour rather than a leak, and the read that returns both is the same
    // read the principal factory builds a principal from.
    [Fact]
    public async Task Both_tenants_assignments_for_one_user_are_visible_through_the_tenant_free_read()
    {
        var run = await RunAsync("Assign");

        var roles = await run.Store.GetRolesForUserAsync(run.UserId, CancellationToken.None);

        roles.Should().BeEquivalentTo(
            new[] { CurrentUserRolesCrossTenantDrive.OwnerRole, CurrentUserRolesCrossTenantDrive.OtherRole },
            "roles are keyed on the user rather than partitioned by tenant, so an assignment made "
            + "under either tenant's event must be visible to the read the principal factory uses; "
            + "a run returning one role has folded two assignments the key is meant to keep apart");
    }

    // The delete's key precision. The acting tenant revokes one of the two roles the owner assigned,
    // and the other survives. This is the only assertion here that fails when a revoke reaches wider
    // than the pair it names, and it is the only guard this family has against that, because the
    // isolation property cannot see this table at all.
    [Fact]
    public async Task A_revoke_removes_only_the_role_it_names_and_leaves_the_users_other_roles()
    {
        var run = await RunAsync("Revoke");

        var roles = await run.Store.GetRolesForUserAsync(run.UserId, CancellationToken.None);

        roles.Should().BeEquivalentTo(
            new[] { CurrentUserRolesCrossTenantDrive.OwnerRole },
            "the delete is keyed on (user_id, role), so revoking one role must leave the user's "
            + "other roles standing; a run that ends with fewer has removed an assignment no event "
            + "revoked, and nothing else in the harness would catch that");
    }

    // === arrangement ===

    // Stub is carried for uniformity with the seven families before this one and is inert here: the
    // store takes no current-tenant accessor, so nothing reads it.
    private sealed record Run(
        ICurrentUserRolesStore Store,
        StubTenantAccessor Stub,
        Guid UserId,
        NpgsqlDataSource Source);

    // Runs one named drive's two phases against a fresh migrated database, through the same Build the
    // harness uses, and hands back the store so a fact can read the user's roles.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = CurrentUserRolesCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var userId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, userId, connStr);
        await drive.ActAsOther(target, userId, connStr);
        return new Run((ICurrentUserRolesStore)target.Store, target.Tenant, userId, ds);
    }
}
