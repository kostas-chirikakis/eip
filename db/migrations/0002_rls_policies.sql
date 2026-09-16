-- =============================================================================
-- 0002 — Row-level security
--
-- L4 of the isolation model, and the layer the isolation claim actually rests on.
-- Everything above it (directory config, APIM policy, MediatR behaviour, EF query filters)
-- is code and configuration we wrote, and is therefore fallible. This is enforced by the
-- database engine against a role that cannot bypass it.
--
-- Three properties follow, all of which are worth stating to a customer's security
-- reviewer verbatim:
--   1. A query missing its org_id predicate returns ZERO rows, not another customer's.
--   2. IgnoreQueryFilters() cannot escape isolation — RLS is below EF Core entirely.
--   3. A SQL injection that reaches the database still executes under RLS.
-- =============================================================================

-- The session variable the application sets per transaction via
-- OrgScopedConnectionInterceptor. Registered so current_setting() does not error when
-- unset — the 'true' missing_ok argument in each policy handles that.
--
-- current_setting(..., true) returns NULL when unset, and `org_id = NULL` is NULL, which
-- is not true, so no rows match. Unset context therefore means zero rows, which is the
-- fail-closed behaviour we want.

create or replace function identity.current_org_id()
returns uuid
language sql
stable
parallel safe
as $$
    select nullif(current_setting('app.current_org_id', true), '')::uuid
$$;

comment on function identity.current_org_id() is
    'Returns NULL when unset or empty, so every policy below yields zero rows rather than '
    'erroring or defaulting. Unset context must never mean unrestricted access.';

-- Helper: apply the standard policy to a tenant-owned table -------------------
create or replace function identity.apply_org_isolation(target_schema text, target_table text)
returns void
language plpgsql
as $$
declare
    policy_name text := target_table || '_org_isolation';
begin
    execute format('alter table %I.%I enable row level security', target_schema, target_table);

    -- FORCE matters: without it the table owner bypasses the policy, and migrations
    -- commonly run as owner. This is why cube_migrator and cube_app are separate roles.
    execute format('alter table %I.%I force row level security', target_schema, target_table);

    execute format('drop policy if exists %I on %I.%I', policy_name, target_schema, target_table);

    execute format(
        'create policy %I on %I.%I using (org_id = identity.current_org_id()) '
        'with check (org_id = identity.current_org_id())',
        policy_name, target_schema, target_table);
end $$;

comment on function identity.apply_org_isolation(text, text) is
    'WITH CHECK is as important as USING: without it a caller could INSERT or UPDATE a row '
    'into another organisation even though they cannot read it.';

-- Apply to tenant-owned identity tables ---------------------------------------
select identity.apply_org_isolation('identity', 'org_domains');
select identity.apply_org_isolation('identity', 'org_identity_providers');
select identity.apply_org_isolation('identity', 'org_memberships');
select identity.apply_org_isolation('identity', 'trusted_issuers');
select identity.apply_org_isolation('identity', 'service_principals');
select identity.apply_org_isolation('identity', 'support_sessions');
select identity.apply_org_isolation('identity', 'mau_daily_active');
select identity.apply_org_isolation('identity', 'onboarding_audit');

-- identity.organizations is deliberately NOT org-isolated on org_id (its key IS org_id).
-- It gets a policy matching on the primary key instead.
alter table identity.organizations enable row level security;
alter table identity.organizations force row level security;

drop policy if exists organizations_self_isolation on identity.organizations;
create policy organizations_self_isolation on identity.organizations
    using (id = identity.current_org_id());

-- identity.users is cross-organisational by design: one human, N memberships (§1.7).
-- Visibility is therefore membership-derived rather than column-derived.
alter table identity.users enable row level security;
alter table identity.users force row level security;

drop policy if exists users_membership_isolation on identity.users;
create policy users_membership_isolation on identity.users
    using (
        exists (
            select 1
            from identity.org_memberships m
            where m.user_id = identity.users.id
              and m.org_id  = identity.current_org_id()
              and m.status  = 'active'
        )
    );

comment on policy users_membership_isolation on identity.users is
    'A user is visible to an organisation only through an active membership. A consultant '
    'working for two customers is one row that each customer can see, and neither can '
    'learn about the other membership.';

-- Platform-operator bypass ------------------------------------------------------
-- A narrow role for the reconciler and platform operations, which legitimately work across
-- organisations. Granted to the provisioning service principal and to break-glass only —
-- never to cube_app, and never to anything serving customer requests.
do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'cube_platform_operator') then
        create role cube_platform_operator;
    end if;
end $$;

grant usage on schema identity to cube_platform_operator;
grant select, insert, update on all tables in schema identity to cube_platform_operator;

create policy organizations_platform_operator on identity.organizations
    to cube_platform_operator using (true) with check (true);

-- Invariant assertions ----------------------------------------------------------
-- Run as part of migration and again in CI. These are the guarantees, so they are checked
-- rather than assumed.
do $$
declare
    offender text;
begin
    -- 1. cube_app must never be able to bypass RLS.
    if exists (select 1 from pg_roles where rolname = 'cube_app' and rolbypassrls) then
        raise exception 'cube_app has BYPASSRLS. Tenant isolation is not enforced.';
    end if;

    -- 2. Every identity table carrying org_id must have RLS enabled AND forced.
    select string_agg(c.relname, ', ')
      into offender
      from pg_class c
      join pg_namespace n on n.oid = c.relnamespace
     where n.nspname = 'identity'
       and c.relkind = 'r'
       and exists (
             select 1 from pg_attribute a
              where a.attrelid = c.oid and a.attname = 'org_id' and a.attnum > 0 and not a.attisdropped)
       and not (c.relrowsecurity and c.relforcerowsecurity);

    if offender is not null then
        raise exception 'Tables with org_id lacking enabled+forced RLS: %', offender;
    end if;
end $$;
