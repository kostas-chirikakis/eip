\set ON_ERROR_STOP on
\pset format aligned
\echo '=============================================================='
\echo ' RLS ISOLATION TEST  (connected as cube_app, NOT superuser)'
\echo '=============================================================='
select current_user as connected_as,
       (select rolbypassrls from pg_roles where rolname = current_user) as has_bypassrls;

\echo ''
\echo '--- T1: Acme context sees only Acme -------------------------'
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000a1', false);
select 'organizations' as tbl, count(*) as visible from identity.organizations
union all select 'org_domains',  count(*) from identity.org_domains
union all select 'users',        count(*) from identity.users
union all select 'memberships',  count(*) from identity.org_memberships;
select slug from identity.organizations;
select domain from identity.org_domains order by domain;

\echo ''
\echo '--- T2: Northwind context sees only Northwind ---------------'
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000b2', false);
select slug from identity.organizations;
select domain from identity.org_domains order by domain;
select email from identity.users order by email;

\echo ''
\echo '--- T3: Explicit cross-org query returns ZERO rows ----------'
\echo '        (Acme context, querying Northwind by primary key)'
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000a1', false);
select count(*) as northwind_rows_visible_from_acme
  from identity.organizations
 where id = '00000000-0000-0000-0000-0000000000b2';
select count(*) as northwind_domains_visible_from_acme
  from identity.org_domains
 where domain = 'northwind-devices.com';

\echo ''
\echo '--- T4: UNSET context returns ZERO rows (fail closed) -------'
select set_config('app.current_org_id', '', false);
select count(*) as visible_with_no_context from identity.organizations;
select count(*) as domains_with_no_context from identity.org_domains;

\echo ''
\echo '--- T5: WITH CHECK blocks cross-org INSERT ------------------'
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000a1', false);
\echo '        Acme trying to insert a domain owned by Northwind:'
\set ON_ERROR_STOP off
insert into identity.org_domains (domain, org_id, mode, verified)
values ('evil.example', '00000000-0000-0000-0000-0000000000b2', 'local', false);
\set ON_ERROR_STOP on

\echo ''
\echo '--- T6: Multi-org consultant (section 1.7) ------------------'
\echo '        Same human, two memberships, one org visible per context.'
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000a1', false);
select 'acme sees'      as ctx, count(*) as memberships_for_consultant
  from identity.org_memberships where user_id = '00000000-0000-0000-0000-0000000000f3';
select set_config('app.current_org_id', '00000000-0000-0000-0000-0000000000b2', false);
select 'northwind sees' as ctx, count(*) as memberships_for_consultant
  from identity.org_memberships where user_id = '00000000-0000-0000-0000-0000000000f3';

\echo ''
\echo '--- T7: Schema invariant assertions -------------------------'
select check_name, passed, detail from identity.assert_tenant_isolation_invariants();
