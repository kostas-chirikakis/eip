-- =============================================================================
-- 0001 — Identity core schema
--
-- The authorization authority for Cube RM (ADR-0002). Entra External ID proves who a
-- human is; everything about what they may do lives here.
-- =============================================================================

create schema if not exists identity;

-- Roles ----------------------------------------------------------------------
-- Separated so that migrations run as owner while the application runs as a role that
-- cannot bypass row-level security. FORCE ROW LEVEL SECURITY (0002) means even the owner
-- is subject to policies, so this split is what keeps migrations working at all.
do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'cube_app') then
        create role cube_app login;      -- password/managed identity set out of band
    end if;
    if not exists (select 1 from pg_roles where rolname = 'cube_migrator') then
        create role cube_migrator login;
    end if;
end $$;

-- cube_app must never hold BYPASSRLS. Asserted in 0002 and re-asserted in CI.
alter role cube_app nobypassrls;

-- Organisations --------------------------------------------------------------
create table identity.organizations (
    id                  uuid primary key,
    slug                text        not null unique
                        constraint organizations_slug_format
                        check (slug ~ '^[a-z][a-z0-9-]{2,30}$'),
    display_name        text        not null,
    status              text        not null default 'active'
                        check (status in ('active','suspended','offboarding','offboarded')),
    authentication_mode text        not null
                        check (authentication_mode in ('local','federated','trusted_issuer')),
    tier                text        not null default 'standard',
    created_at          timestamptz not null default now(),
    suspended_at        timestamptz,
    suspension_reason   text,

    -- A suspended organisation must always carry a reason, for the audit trail.
    constraint organizations_suspension_reason
        check (status <> 'suspended' or suspension_reason is not null)
);

comment on column identity.organizations.slug is
    'Immutable. Embedded in Entra object names; renaming would require directory churn '
    'across the shared tenant. A rebrand changes display_name only.';

create index organizations_status_idx on identity.organizations (status)
    where status <> 'active';   -- the deny-list query; most rows are active

-- Email domain routing -------------------------------------------------------
-- Backing table for home-realm discovery (ADR-0003).
create table identity.org_domains (
    domain           text        primary key,   -- normalised: lowercase, punycode, no trailing dot
    org_id           uuid        not null references identity.organizations (id),
    mode             text        not null check (mode in ('local','federated','trusted_issuer')),
    idp_ref          text,                      -- e.g. idp-acme-pharma-saml. Null for local.
    verified         boolean     not null default false,
    verification_ref text,
    verified_at      timestamptz,
    created_at       timestamptz not null default now(),

    -- Federation on an unverified domain would let one customer claim another's domain
    -- and harvest their sign-ins. Enforced here as well as in the onboarding schema,
    -- because this is the constraint that actually holds at runtime.
    constraint org_domains_federated_requires_verification
        check (mode <> 'federated' or (verified = true and idp_ref is not null))
);

comment on table identity.org_domains is
    'PRIMARY KEY on domain is the cross-customer collision guard: a domain can belong to '
    'exactly one organisation, enforced by the database rather than by onboarding process.';

create index org_domains_org_id_idx on identity.org_domains (org_id);

-- Identity providers configured per organisation ------------------------------
create table identity.org_identity_providers (
    id            uuid        primary key,
    org_id        uuid        not null references identity.organizations (id),
    idp_ref       text        not null unique,   -- Entra identityProvider display name
    protocol      text        not null check (protocol in ('saml','oidc')),
    display_name  text        not null,
    metadata_url  text,
    status        text        not null default 'active'
                  check (status in ('active','disabled','pending')),
    created_at    timestamptz not null default now()
);

create index org_identity_providers_org_id_idx on identity.org_identity_providers (org_id);

-- Users ----------------------------------------------------------------------
create table identity.users (
    id             uuid        primary key,
    issuer         text        not null,
    subject_oid    text        not null,
    email          text        not null,
    display_name   text,
    status         text        not null default 'active'
                   check (status in ('active','disabled','deleted')),
    created_at     timestamptz not null default now(),
    last_sign_in_at timestamptz,

    -- Composite key, not subject_oid alone: an oid from a customer's own Entra tenant is
    -- unique only within that tenant, so oid alone would collide across issuers.
    constraint users_issuer_subject_unique unique (issuer, subject_oid)
);

create index users_email_idx on identity.users (lower(email));

-- Memberships ----------------------------------------------------------------
-- A consultant contracted to two customers is one user with two memberships. The token
-- carries exactly one org; switching requires a new token (§1.7).
create table identity.org_memberships (
    user_id    uuid        not null references identity.users (id),
    org_id     uuid        not null references identity.organizations (id),
    user_type  text        not null default 'member'
               check (user_type in ('member','org_admin')),
    status     text        not null default 'active'
               check (status in ('active','suspended','removed')),
    created_at timestamptz not null default now(),
    primary key (user_id, org_id)
);

create index org_memberships_org_id_idx on identity.org_memberships (org_id) where status = 'active';

-- Trusted issuers (ADR-0004) --------------------------------------------------
create table identity.trusted_issuers (
    issuer_url        text        primary key,
    org_id            uuid        not null references identity.organizations (id),
    entra_tenant_id   uuid        not null,
    metadata_address  text        not null,
    allowed_audiences text[]      not null,
    status            text        not null default 'active'
                      check (status in ('active','suspended')),
    created_at        timestamptz not null default now(),

    -- Load-bearing. One issuer serves exactly one organisation, so a token from a customer
    -- tenant can only ever resolve to that customer — a database constraint rather than a
    -- claims-mapping decision. This is what makes the trusted-issuer path as isolated as
    -- the federated one.
    constraint trusted_issuers_one_org_per_issuer unique (issuer_url, org_id)
);

create unique index trusted_issuers_entra_tenant_unique on identity.trusted_issuers (entra_tenant_id);

-- Service principals ----------------------------------------------------------
-- Machines are never users (MAU rule 1). Integrations get an app registration and a
-- client credential, recorded here, and never a directory user account.
create table identity.service_principals (
    id             uuid        primary key,
    org_id         uuid        not null references identity.organizations (id),
    client_id      uuid        not null unique,
    display_name   text        not null,
    status         text        not null default 'active'
                   check (status in ('active','disabled')),
    created_at     timestamptz not null default now(),
    last_used_at   timestamptz
);

-- Support impersonation -------------------------------------------------------
-- Cube RM support holds no external-tenant accounts (§1.4.4). Org scope comes from an
-- explicit, audited, time-boxed grant rather than from a standing credential.
create table identity.support_sessions (
    id              uuid        primary key,
    support_user_id uuid        not null,
    org_id          uuid        not null references identity.organizations (id),
    ticket_ref      text        not null,
    justification   text        not null,
    granted_at      timestamptz not null default now(),
    expires_at      timestamptz not null,
    revoked_at      timestamptz,

    constraint support_sessions_bounded
        check (expires_at > granted_at and expires_at <= granted_at + interval '8 hours')
);

create index support_sessions_active_idx on identity.support_sessions (org_id, expires_at)
    where revoked_at is null;

-- MAU telemetry ---------------------------------------------------------------
-- We see every token, so we can attribute MAU per organisation before Azure invoices it.
create table identity.mau_daily_active (
    org_id      uuid    not null references identity.organizations (id),
    user_id     uuid    not null references identity.users (id),
    active_date date    not null,
    issuer      text    not null,
    -- False for trusted-issuer and workforce paths: those users authenticate against
    -- someone else's tenant and are not our MAU (§8.5).
    is_billable boolean not null,
    primary key (org_id, user_id, active_date)
);

create index mau_daily_active_date_idx on identity.mau_daily_active (active_date);

-- Onboarding audit -------------------------------------------------------------
create table identity.onboarding_audit (
    id              uuid        primary key,
    org_id          uuid        not null references identity.organizations (id),
    action          text        not null,
    pull_request_url text,
    plan_hash       text,
    performed_by    text        not null,
    evidence        jsonb,       -- canary sign-in result, cross-tenant probe output
    performed_at    timestamptz not null default now()
);

create index onboarding_audit_org_id_idx on identity.onboarding_audit (org_id, performed_at desc);

grant usage on schema identity to cube_app;
grant select, insert, update on all tables in schema identity to cube_app;
-- No DELETE. Removal is a status change; hard deletion is a separate, explicitly
-- authorised operation (§6.4), not something application code can do.
