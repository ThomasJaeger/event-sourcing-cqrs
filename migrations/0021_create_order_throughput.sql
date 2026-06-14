-- 0021_create_order_throughput.sql
-- Phase 11 (P11.12b): the order-throughput read model for the admin metrics
-- dashboard. OrderThroughputProjection counts the eight Sales order events into
-- per-(tenant, occurrence-second) buckets and prunes buckets outside a retention
-- window on write, so the table stays bounded per tenant.
--
-- A net-new tenant-scoped read-model table: tenant_id is part of the composite
-- primary key, with no DEFAULT (unlike the 0017 retrofit, whose default only
-- backfilled pre-existing rows on an ALTER). The writer always stamps the tenant.
-- A read_models migration, so it does not touch the event_store assertions.

CREATE TABLE read_models.order_throughput (
    tenant_id  UUID        NOT NULL,
    second_utc TIMESTAMPTZ NOT NULL,
    count      BIGINT      NOT NULL,
    CONSTRAINT pk_order_throughput PRIMARY KEY (tenant_id, second_utc)
);
