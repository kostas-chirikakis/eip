# 2. Authentication Flows

All diagrams are Mermaid and render in GitHub, Azure DevOps wiki and VS Code.

Participants used throughout:

| Short | Full |
| --- | --- |
| `SPA` | React/Angular SPA using MSAL.js (`cube-tenderhub-spa`) |
| `BFF` | Server-rendered .NET app acting as backend-for-frontend (`cube-portal-bff`) |
| `HRD` | Cube RM home-realm-discovery service, `/auth/discover` |
| `CIAM` | Entra External ID external tenant, `cuberm.ciamlogin.com` |
| `CAE` | Cube RM claims-enrichment endpoint (`OnTokenIssuanceStart` handler) |
| `APIM` | Azure API Management |
| `API` | A Cube RM .NET 8 API |
| `PG` | PostgreSQL `identity` schema |
| `CIdP` | Customer's corporate IdP (SAML or OIDC) |

---

## 2.1 Flow (a) — Internal user SSO across Cube RM applications

A user already signed in to the Tender Hub SPA opens the Portal (BFF) and is not
re-prompted. The shared artifact is the **External ID session cookie** on
`cuberm.ciamlogin.com`, which both applications' authorize requests hit.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant SPA as SPA (app A)
    participant BFF as BFF (app B)
    participant CIAM as Entra External ID
    participant CAE as Claims Enrichment
    participant APIM
    participant API

    Note over U,SPA: User is already authenticated in app A
    U->>SPA: Uses Tender Hub
    SPA->>APIM: GET /tenders (Bearer access token for api://cube-tenderhub)
    APIM->>API: forward, token validated at both layers
    API-->>U: data

    Note over U,BFF: User navigates to app B. No credential prompt expected.
    U->>BFF: GET /portal
    BFF->>CIAM: 302 /authorize (client_id=cube-portal-bff, prompt=none, scope=openid ...)
    Note over CIAM: Session cookie for this browser<br/>on cuberm.ciamlogin.com is present
    CIAM->>CAE: OnTokenIssuanceStart (oid, tid, authenticating IdP)
    CAE-->>CIAM: cube_org_id, cube_org_slug, cube_idp
    CIAM-->>BFF: 302 back with authorization code (no user interaction)
    BFF->>CIAM: POST /token (code + client secret or federated credential)
    CIAM-->>BFF: id_token + access_token + refresh_token
    BFF-->>U: Set-Cookie (HttpOnly, SameSite=Lax) and render page
    U->>BFF: action requiring API
    BFF->>APIM: GET /contracts (Bearer token for api://cube-contracts)
    APIM->>API: forward
    API-->>U: data
```

### Why this works, and where it is fragile

| Aspect | Note |
| --- | --- |
| **Shared SSO domain** | All apps use the same `ciamlogin.com` authority. SSO is a property of that shared session cookie, not of anything we build. |
| **Separate app registrations** | One per Cube RM application, each with its own `aud`. A token for `api://cube-tenderhub` is rejected by the contracts API. This is intra-Cube-RM blast radius containment. |
| **BFF holds tokens server-side** | Browser gets only an HttpOnly cookie. No token in `localStorage`. This is the posture we quote in customer security reviews. |
| **⚠ SPA silent SSO is the fragile path** | MSAL.js `ssoSilent` uses a hidden iframe against the authorize endpoint. That is a **third-party cookie** in every modern browser's default configuration, and it is increasingly blocked. |

**Recommendation for the mixed topology:** do not rely on hidden-iframe `ssoSilent` for
cross-app SSO. Use one of:

1. **Full-page redirect with `prompt=none`** — visually a flash, but uses a first-party
   cookie and is not affected by third-party cookie policy. Acceptable default.
2. **Move SPAs behind a BFF** (the recommended long-term shape). The SPA keeps its
   client-side rendering; token handling moves server-side. This also unifies the session
   model across both app styles, which matters for the offboarding story in
   [§6](06-offboarding-incident.md) — you can kill a server-side session immediately, but
   you cannot recall a token already in a browser.

Option 2 is what we should converge on. Option 1 is the migration-safe interim.

---

## 2.2 Flow (d) — Email-domain routing (the entry point for both b and c)

This is shown before (b) and (c) because it is what selects between them. **We own this
step**; it is not delegated to External ID's native routing.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant App as SPA or BFF
    participant HRD as Cube RM /auth/discover
    participant PG as PostgreSQL identity schema
    participant CIAM as Entra External ID

    U->>App: Enters email j.smith@acme-pharma.com
    App->>HRD: POST /auth/discover { email }
    HRD->>HRD: Normalise, extract domain "acme-pharma.com"
    HRD->>PG: SELECT org, routing_mode, idp_ref FROM identity.org_domains WHERE domain = $1

    alt Domain unknown
        PG-->>HRD: no row
        HRD-->>App: { mode: "local" } (generic response, no enumeration signal)
        Note over HRD: Identical shape and timing to a known local domain.<br/>Rate limited per source IP.
    else Org suspended or offboarded
        PG-->>HRD: org.status in (suspended, offboarded)
        HRD-->>App: { mode: "denied", message: "Contact your administrator" }
        Note over HRD: Kill switch takes effect here, before Entra is ever contacted.
    else Federated customer
        PG-->>HRD: mode=federated, idp_ref=idp-acme-pharma-saml
        HRD-->>App: { mode: "federated", authorizeUrl: "...&domain_hint=acme-pharma.com" }
    else Local-account customer
        PG-->>HRD: mode=local
        HRD-->>App: { mode: "local", authorizeUrl: "..." }
    end

    App->>CIAM: 302 to the returned authorize URL
```

### Design notes

- **Unknown domain returns `local`, not an error.** Returning "no such organisation" is a
  user-enumeration oracle that tells an attacker which pharma companies are Cube RM
  customers — commercially sensitive independent of the security concern. The response
  shape, size and timing are identical for known-local and unknown domains.
- **The suspended branch is the kill switch.** It is evaluated from PostgreSQL on every
  discovery call, so revoking a customer's access takes effect on the next sign-in
  attempt with no Entra change required. Combined with the edge deny-list
  ([§4.4](04-apim-configuration.md)) for tokens already issued, this gives
  seconds-to-effect offboarding.
- **`idp_ref` pins exactly one IdP.** The user is never shown a list of identity providers
  (see [§1.5.2](01-tenant-isolation-model.md)).
- **Rate limited and monitored.** `/auth/discover` is an unauthenticated endpoint that
  reads the org registry. It gets aggressive per-IP rate limiting at APIM and an alert on
  high-cardinality domain probing.
- **⚠ Spike dependency.** Whether `domain_hint` reliably suppresses the IdP picker in an
  external tenant is risk register **R3**. If it does not, the fallback is a dedicated
  per-org authorize path using the IdP-specific parameter that testing shows does work.
  The HRD contract does not change either way — this is exactly why we own this layer.

---

## 2.3 Flow (b) — Enterprise customer federated SSO

Acme Pharmaceuticals federates their corporate SAML IdP. Their staff use corporate
credentials and never hold a Cube RM password.

```mermaid
sequenceDiagram
    autonumber
    actor U as Acme employee
    participant App as SPA or BFF
    participant HRD
    participant CIAM as Entra External ID
    participant CIdP as Acme corporate IdP (SAML)
    participant CAE as Claims Enrichment
    participant PG as PostgreSQL

    U->>App: Enters j.smith@acme-pharma.com
    App->>HRD: POST /auth/discover
    HRD-->>App: { mode: "federated", authorizeUrl with IdP pinned }
    App->>CIAM: GET /authorize (IdP = idp-acme-pharma-saml)
    CIAM->>CIdP: SAML AuthnRequest
    CIdP->>U: Corporate login and MFA
    U->>CIdP: Credentials
    CIdP-->>CIAM: SAML Response (assertion, NameID, email)

    CIAM->>CAE: OnTokenIssuanceStart
    CAE->>PG: SELECT u.org_id FROM identity.users u WHERE issuer=$1 AND subject_oid=$2

    alt User record exists and org is active
        PG-->>CAE: org_id, slug
        CAE->>PG: assert idp matches identity.org_identity_providers for that org
        CAE-->>CIAM: { cube_org_id, cube_org_slug, cube_idp: "saml:acme-pharma" }
        CIAM-->>App: code, exchanged for tokens
    else No user record (JIT disabled — default)
        PG-->>CAE: no row
        CAE-->>CIAM: no cube_org_id returned
        Note over CIAM,App: Token lacks cube_org_id.<br/>APIM and API both reject it. Fails closed.
    else IdP does not match the resolved org
        CAE-->>CAE: Raise P1 security alert
        CAE-->>CIAM: no claims
        Note over CAE: This is the cross-org routing detector.<br/>It should never fire. If it does, treat as incident.
    end
```

**The critical property:** `cube_org_id` is resolved from the **pre-existing user record**,
not from which IdP performed the authentication. An unexpected or manipulated IdP path
therefore produces a token with no org claim — useless — rather than a token scoped to the
wrong organisation.

---

## 2.4 Flow (c) — Local email + password customer

Northwind Medical Devices declines SSO. Their users get accounts in our tenant.

```mermaid
sequenceDiagram
    autonumber
    actor U as Northwind user
    participant App as SPA or BFF
    participant HRD
    participant CIAM as Entra External ID
    participant CAE as Claims Enrichment
    participant PG as PostgreSQL

    U->>App: Enters a.jones@northwind-devices.com
    App->>HRD: POST /auth/discover
    HRD-->>App: { mode: "local", authorizeUrl }
    App->>CIAM: GET /authorize (sign-in user flow, local accounts)
    CIAM->>U: Cube RM branded sign-in page
    U->>CIAM: Email + password
    CIAM->>CIAM: Validate credential, apply MFA policy (email OTP)
    CIAM->>CAE: OnTokenIssuanceStart
    CAE->>PG: resolve org from (issuer, oid)
    PG-->>CAE: org_id = northwind
    CAE-->>CIAM: { cube_org_id, cube_idp: "local" }
    CIAM-->>App: code, exchanged for tokens
```

### Local-account specifics

| Concern | Approach |
| --- | --- |
| **Account creation** | Invitation-only. Self-service sign-up is **disabled** — in a shared tenant, open sign-up lets anyone create an account with an unverified domain. Org admins invite via the Tenant Admin API. |
| **Password policy** | Tenant-wide (one tenant, one policy). Set to the strictest any customer requires. Per-org password policy is **not available** — flag this in contracts. |
| **MFA** | Email OTP as baseline. Stronger factors require the P1/P2 add-on, which is **per-MAU** — see [MAU model](08-mau-cost-model.md). |
| **Password reset** | Self-service via the user flow. Rate limited. |
| **Coexistence with (b)** | Both flows are the same user flow and the same app registration. Only the pinned IdP differs. No per-customer app registration. |

The password-policy constraint is worth surfacing early in customer conversations: a
single tenant means a single password policy, so the most security-conscious customer
effectively sets it for everyone. That is usually acceptable (strictest wins is a safe
direction) but it is a commitment, not a default.

---

## 2.5 Flow (e) — Entra-to-Entra fallback: customer tenant as a trusted issuer

Used when a customer runs their own Entra workforce tenant and built-in federation is
blocked or unreliable. See [ADR-0004](adr/0004-multi-issuer-fallback.md) and risk
register **R1**.

```mermaid
sequenceDiagram
    autonumber
    actor U as Customer employee
    participant SPA as Cube RM SPA
    participant CT as Customer Entra tenant
    participant APIM
    participant API as Cube RM API
    participant PG as PostgreSQL

    Note over CT: Cube RM multi-tenant app registration<br/>consented by the customer's Entra admin
    U->>SPA: Sign in
    SPA->>CT: /authorize against login.microsoftonline.com/{customer-tid}
    CT->>U: Customer's own CA policy, MFA, device compliance
    U->>CT: Corporate credentials
    CT-->>SPA: id_token + access_token issued by the CUSTOMER tenant

    SPA->>APIM: GET /tenders (Bearer, iss = customer tenant)
    APIM->>APIM: choose on iss, validate-jwt against that tenant's JWKS
    APIM->>API: forward
    API->>API: Multi-issuer JwtBearer validates signature, iss, aud
    API->>PG: SELECT org_id FROM identity.trusted_issuers WHERE issuer_url = $1
    PG-->>API: org_id (one-to-one, enforced by unique constraint)
    API->>API: Build ICubeTenantContext from registry, not from token claims
    API-->>SPA: data
```

### Trade-offs, stated plainly

| Gain | Cost |
| --- | --- |
| Bypasses External ID federation entirely — no dependency on the uncertain path | Tokens are **not** issued by our tenant, so `OnTokenIssuanceStart` never fires. No `cube_org_id` claim. |
| Customer's own CA, MFA and device compliance apply natively — a strong story for security-conscious pharma | Org is resolved server-side from `identity.trusted_issuers`, adding a lookup (cached) to the request path |
| No MAU cost — these users never authenticate against our external tenant | SSO with our *other* apps is not shared through the CIAM session cookie; each app authenticates against the customer tenant separately |
| Customer revokes access instantly by removing our app's consent | We must monitor their tenant's continued consent, and handle the `aud`/`scp` model differing from our CIAM tokens |

The MAU line is significant enough to call out: **this path materially reduces MAU cost**
for large federated customers, which may make it attractive beyond its role as a fallback.
That should be modelled before Phase 2 — see [MAU model §8.5](08-mau-cost-model.md).

---

## 2.6 Flow (f) — Claims enrichment internals

The `OnTokenIssuanceStart` handler is on the critical authentication path. Its failure
behaviour is a design decision, not an accident.

```mermaid
flowchart TD
    A[OnTokenIssuanceStart from Entra] --> B{Caller token valid?<br/>iss = our tenant<br/>aud = CAE app id}
    B -- no --> Z1[401. Log as potential spoof attempt]
    B -- yes --> C[Extract oid, tid, authenticating IdP]
    C --> D[(SELECT user + org<br/>FROM identity.users<br/>JOIN identity.organizations)]
    D --> E{User record found?}
    E -- no --> Z2[Return no claims<br/>Token issued WITHOUT cube_org_id<br/>Rejected downstream]
    E -- yes --> F{org.status = active?}
    F -- no --> Z2
    F -- yes --> G{IdP matches<br/>org_identity_providers?}
    G -- no --> Z3[Return no claims<br/>+ P1 security alert<br/>Cross-org routing detector]
    G -- yes --> H[Return cube_org_id,<br/>cube_org_slug, cube_idp, cube_ver]
    H --> I[Entra mints token with claims]

    style Z1 fill:#7f1d1d,color:#fff
    style Z2 fill:#78350f,color:#fff
    style Z3 fill:#7f1d1d,color:#fff
    style H fill:#14532d,color:#fff
```

### Availability coupling — the honest version

This endpoint being down means **no user of any customer can obtain a usable token**. That
is a self-inflicted single point of failure and it must be treated as such:

| Control | Requirement |
| --- | --- |
| Latency budget | p99 under 500 ms. Entra's timeout for the extension is short; exceeding it fails issuance. Validate the exact budget in spike **R2**. |
| Hosting | Multi-instance Container Apps or App Service with zone redundancy. Not the same deployment unit as product APIs. |
| Data access | Read-only replica or in-process cache of `(issuer, oid) → org` with short TTL. The lookup must not depend on the primary write database. |
| Deployment | Separate release cadence from product code. Changes here are identity-critical. |
| Degraded mode | If we cannot meet the SLO, fall back to directory extension attributes (`cubeOrgId`) as the claim source, which removes the runtime dependency at the cost of the fail-closed-on-suspension property. Decision gated on **R2**. |
| Monitoring | Synthetic sign-in canary per org archetype, every 5 minutes, alerting within one interval. |

The degraded mode is the important escape hatch. If R2 shows the latency or availability
envelope is not achievable, we move the org binding to directory attributes and accept
that suspension propagates via a Graph write rather than instantly — still acceptable,
because the edge deny-list ([§4.4](04-apim-configuration.md)) remains the fast path for
revocation.
