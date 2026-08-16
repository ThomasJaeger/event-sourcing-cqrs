-- 0024_tenant_scoped_order_detail_keys.sql
-- Phase 10 (multi-tenancy): make the order-detail family's keys tenant-scoped under the
-- shared-schema isolation model. Migration 0017 added the tenant_id column to all five tables but
-- left every key global; this migration swaps four of them to tenant-leading composites, the same
-- order 0018 and 0019 used, so two tenants can each hold a row at the same caller-supplied id.
--
-- Aggregate ids are caller-supplied and tenants are not. POST /commands deserializes OrderId,
-- ShipmentId, and PaymentId straight out of the request body and reads the tenant from the loaded
-- principal, so two tenants can present the same id and neither can choose the other's tenant. The
-- write side keeps them apart because StreamId composes the tenant into the stream. A read-model
-- key on the aggregate id alone has no such composition, so the second tenant's row lands on the
-- first tenant's key. No predicate closes that: ON CONFLICT is decided by the key, and the row the
-- clause hits is the row the key selects.
--
-- Four keys move:
--
--   order_detail            pk (order_id)              -> (tenant_id, order_id)
--   order_detail_lines      pk (order_id, line_id)     -> (tenant_id, order_id, line_id)
--   order_detail_shipments  pk (shipment_id)           -> (tenant_id, shipment_id)
--   order_detail_payments   pk (payment_id)            -> (tenant_id, payment_id)
--
-- order_detail_timeline keeps pk (order_id, global_position) and is the one table in the family
-- that needs no swap. global_position is assigned by the event-store append and is unique across
-- the whole corpus rather than per tenant, so two tenants cannot present the same pair and the
-- fold the other four admit cannot occur. The column is a discriminator for reads, which it
-- already serves; it is not load-bearing in that key.
--
-- Safe on populated data with no rewrite and no empty-table guard: every pre-existing row carries
-- the default tenant from 0017's NOT NULL DEFAULT, so a tenant-leading composite is unique exactly
-- when the old key was unique. It assumes it runs before a second tenant is onboarded to these
-- tables, the condition 0018 states for the SKU keys. That condition holds here and was checked
-- rather than assumed: CurrentRolesPrincipalFactory returns WellKnownTenants.Default
-- unconditionally, and every other production construction of a TenantId is a round trip, two
-- delay-queue readers reading back a tenant they wrote, two JSON converters, and one hub
-- subscription echoing the tenant its authorize call returned. No production path originates a
-- non-default tenant until the per-user tenant lookup lands with tenant onboarding.
--
-- What the swap costs, in 0019's terms: each ALTER takes ACCESS EXCLUSIVE on its table and
-- rebuilds the backing index. Four small read-model tables on a single-tenant corpus make that
-- acceptable. CREATE UNIQUE INDEX CONCURRENTLY followed by a constraint swap is the path if table
-- size or projection availability later requires it, and it would run per table rather than in one
-- statement per table as written here.
--
-- The three ON CONFLICT clauses in PostgresOrderDetailUnitOfWork move with these keys and must:
-- PostgreSQL requires a conflict target to match a unique index or constraint, so
-- ON CONFLICT (order_id) against a (tenant_id, order_id) primary key raises 42P10 at execution
-- rather than degrading quietly. AppendTimelineAsync's target is unchanged because its key is.
--
-- A read_models-schema migration, so it does not touch the event_store constraint and index
-- assertions. It does change the applied count and the last-applied identity that the migration
-- runner's own tests pin, and those move with it. Stated as the obligation rather than as a file
-- name, because these bytes are checksummed and immutable once applied, so a reference in them
-- could never be corrected if the referent moved.

ALTER TABLE read_models.order_detail
    DROP CONSTRAINT pk_order_detail,
    ADD CONSTRAINT pk_order_detail PRIMARY KEY (tenant_id, order_id);

ALTER TABLE read_models.order_detail_lines
    DROP CONSTRAINT pk_order_detail_lines,
    ADD CONSTRAINT pk_order_detail_lines PRIMARY KEY (tenant_id, order_id, line_id);

ALTER TABLE read_models.order_detail_shipments
    DROP CONSTRAINT pk_order_detail_shipments,
    ADD CONSTRAINT pk_order_detail_shipments PRIMARY KEY (tenant_id, shipment_id);

ALTER TABLE read_models.order_detail_payments
    DROP CONSTRAINT pk_order_detail_payments,
    ADD CONSTRAINT pk_order_detail_payments PRIMARY KEY (tenant_id, payment_id);
