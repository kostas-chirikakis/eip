-- =============================================================================
-- 0003 — Reusable invariant check
--
-- Called by SchemaInvariantTests in CI. A new table carrying org_id fails the build until
-- it has a policy, which is what turns "remember to add RLS" from a review habit into a
-- gate.
-- =============================================================================

create or replace function identity.assert_tenant_isolation_invariants()
returns table (check_name text, passed boolean, detail text)
language plpgsql
as $$
begin
    -- 1. No BYPASSRLS on the application role.
    return query
    select 'cube_app_no_bypassrls'::text,
           not coalesce((select rolbypassrls from pg_roles where rolname = 'cube_app'), false),
           coalesce((select 'rolbypassrls=' || rolbypassrls::text from pg_roles where rolname = 'cube_app'),
                    'role cube_app not found');

    -- 2. Every org_id table has RLS enabled and forced, in every schema, not just identity.
    return query
    select 'all_org_id_tables_rls_forced'::text,
           count(*) = 0,
           coalesce(string_agg(n.nspname || '.' || c.relname, ', '), 'none')
      from pg_class c
      join pg_namespace n on n.oid = c.relnamespace
     where n.nspname not in ('pg_catalog','information_schema')
       and c.relkind = 'r'
       and exists (
             select 1 from pg_attribute a
              where a.attrelid = c.oid and a.attname = 'org_id' and a.attnum > 0 and not a.attisdropped)
       and not (c.relrowsecurity and c.relforcerowsecurity);

    -- 3. Every RLS-enabled table actually has at least one policy. Enabled-but-policyless
    --    denies everything, which fails safe but breaks the application confusingly.
    return query
    select 'rls_tables_have_policies'::text,
           count(*) = 0,
           coalesce(string_agg(n.nspname || '.' || c.relname, ', '), 'none')
      from pg_class c
      join pg_namespace n on n.oid = c.relnamespace
     where n.nspname not in ('pg_catalog','information_schema')
       and c.relkind = 'r'
       and c.relrowsecurity
       and not exists (select 1 from pg_policy p where p.polrelid = c.oid);

    -- 4. The session variable resolves to NULL when unset, so unset context yields zero
    --    rows rather than unrestricted access.
    return query
    select 'unset_context_is_null'::text,
           (select identity.current_org_id() is null
              from (select set_config('app.current_org_id', '', true)) _),
           'current_org_id() must be NULL when the session variable is empty';
end $$;
