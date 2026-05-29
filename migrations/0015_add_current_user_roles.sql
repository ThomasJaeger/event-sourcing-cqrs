-- 0015_add_current_user_roles.sql
-- Phase 9 (RBAC, ADR 0028): the current-roles-per-user read model. CurrentRolesProjection projects
-- the Access events (RoleAssigned, RoleRevoked) into one row per (user_id, role), and the
-- identity-establishment commit reads a user's roles from it to build the principal. Role is stored
-- as its enum-name text; the store parses it back. The (user_id, role) primary key makes a
-- redelivered RoleAssigned a no-op via ON CONFLICT DO NOTHING and serves the by-user read on its
-- leading column, so no separate index is needed. A read_models migration, so it does not touch the
-- event_store constraint and index assertions.

CREATE TABLE read_models.current_user_roles (
    user_id UUID NOT NULL,
    role    TEXT NOT NULL,
    CONSTRAINT pk_current_user_roles PRIMARY KEY (user_id, role)
);
