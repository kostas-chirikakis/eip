# RLS isolation verification

Executable proof of the isolation claim in
[docs/identity/01-tenant-isolation-model.md §1.8](../../docs/identity/01-tenant-isolation-model.md#18-data-layer-enforcement-l4).

This is the artifact to attach to a customer security questionnaire. It is not a design
intention — it runs, and it either passes or fails.

## Running it

```bash
createdb cubeidentity
psql -d cubeidentity -f ../../db/migrations/0001_identity_core.sql
psql -d cubeidentity -f ../../db/migrations/0002_rls_policies.sql
psql -d cubeidentity -f ../../db/migrations/0003_schema_invariant_check.sql
psql -d cubeidentity -f rls_isolation_seed.sql

# MUST connect as cube_app. A superuser bypasses RLS entirely regardless of
# FORCE ROW LEVEL SECURITY, so running this as postgres proves nothing.
psql -U cube_app -d cubeidentity -f rls_isolation_test.sql
```

In CI this runs against a Testcontainers PostgreSQL instance as part of
`RlsIsolationTests`.

## What each case proves

| Case | Claim |
| --- | --- |
| T1 / T2 | Each organisation's context sees only its own rows across every tenant-owned table |
| T3 | An explicit cross-org query **by primary key** returns zero rows — not an error, not another customer's data |
| T4 | Unset context returns zero rows. Unset never means unrestricted. |
| T5 | `WITH CHECK` blocks a cross-org INSERT. Without it, a caller could write into an organisation it cannot read. |
| T6 | The multi-org consultant (§1.7) resolves to exactly one organisation per context |
| T7 | Schema invariants: no `BYPASSRLS` on `cube_app`; every `org_id` table has RLS enabled **and forced**; every RLS table has a policy |

## Verified result

Run against PostgreSQL 16.13 on 2026-09-16 — all seven cases pass.

```
 connected_as | has_bypassrls
--------------+---------------
 cube_app     | f

T3  northwind_rows_visible_from_acme      = 0
T4  visible_with_no_context               = 0
T5  ERROR: new row violates row-level security policy for table "org_domains"
T7  cube_app_no_bypassrls                 | t
    all_org_id_tables_rls_forced          | t
    rls_tables_have_policies              | t
    unset_context_is_null                 | t
```

The T5 error is the expected outcome, not a failure.
