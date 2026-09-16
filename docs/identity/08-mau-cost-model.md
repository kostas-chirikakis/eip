# 8. MAU Cost Exposure

Where monthly-active-user counts grow faster than customer count, and what to do about it
*in the design* rather than in a budget review.

---

## 8.1 The billing shape

Entra External ID bills on **monthly active users** — a user who authenticates at least
once in a calendar month. There is a free allowance, then per-MAU pricing, and premium
features (Conditional Access, Identity Protection) are a **separate per-MAU charge on top**.

Two consequences shape the design:

1. **A user who signs in once costs the same as one who signs in a thousand times.** So
   cost scales with *distinct humans*, not with traffic. Good.
2. **Anything that authenticates and is modelled as a user is a MAU.** Including things
   that are not humans. This is where the surprises live.

> **Verify current pricing and free-tier thresholds before committing a budget.** Pricing
> and tier boundaries change; this document models the *shape* of the exposure, not the
> rates.

---

## 8.2 The naive estimate, and why it is wrong

A reasonable first estimate for 25 customers at ~200 users each: **5,000 MAU**.

Here is what actually lands on the bill if the design does not prevent it:

| Source | Naive | Realistic if unmanaged | Why |
| --- | --- | --- | --- |
| Customer end users | 5,000 | 5,000 | The number people estimate |
| Customer org admins | — | +75 | 2–4 per customer |
| **Service / integration identities** | — | **+150** | A customer's nightly batch job authenticating as a "user" |
| **Cube RM support accounts** | — | **+60** | One per support engineer, per tenant |
| **Test / QA accounts in prod tenant** | — | **+200** | Accumulate silently, nobody deletes them |
| **Load-test synthetic users** | — | **+5,000** | One load test against the prod tenant |
| Canary / monitoring accounts | — | +30 | One per org, signing in every 5 minutes |
| | **5,000** | **~10,515** | **2.1×** |

The load-test row is the one that turns a budget conversation into an incident. A single
performance test run against the production tenant with 5,000 synthetic users doubles that
month's MAU, permanently, because MAU is a count of distinct authenticating users in the
month and cannot be un-counted.

---

## 8.3 Design rules that prevent this

These are architectural constraints, not policies. Each one closes a growth vector.

### Rule 1 — Machines are never users

> **Service-to-service authentication uses app registrations and client credentials.
> An app registration authenticating with client credentials is not a MAU.**

Any integration, batch job, webhook consumer or system account that needs to call Cube RM
APIs gets an app registration with a client credential and an org binding in
`identity.service_principals` — **never** a user account.

This is the single highest-value rule. It is also the one most likely to be violated under
delivery pressure, because creating a user account is the path of least resistance when a
customer says "our integration needs to log in." The mitigation is to make the supported
path easy: the Tenant Admin API exposes "create integration credential" as a first-class
self-service operation for org admins.

**Detection:** alert on any user account whose sign-in pattern is machine-like — more than
20 sign-ins/day, or sign-ins at a fixed interval, or a user-agent that is not a browser.

### Rule 2 — Cube RM staff never hold external-tenant accounts

Support engineers authenticate against the **workforce** tenant and reach customer context
through the audited impersonation grant in
[§1.4.4](01-tenant-isolation-model.md#144-cube-rm-support-impersonation).

Saves ~60 MAU and, more importantly, removes a population of standing credentials that can
reach every customer — a security benefit that happens to also be a cost benefit.

### Rule 3 — Non-production is a separate tenant. Always.

A separate External ID tenant for dev/test/load. **Load tests never touch the production
tenant.** This is a hard rule with no exceptions, enforced by the load-test harness
refusing to run against the production authority.

### Rule 4 — Canary accounts are pooled, not per-org

One canary *per authentication archetype* (local, SAML-federated, OIDC-federated,
trusted-issuer), not one per organisation. Four MAU instead of thirty, and it tests the
same code paths.

The exception: per-org canaries are genuinely valuable for detecting a single customer's
IdP breaking ([RT-2](01-tenant-isolation-model.md#19-residual-risks-of-the-single-tenant-model)).
The resolution is to run per-org canaries against the **non-prod** tenant using a mirrored
IdP config where the customer permits it, and accept 4 production canaries. Where a
customer's IdP cannot be mirrored, a production canary for that org is justified — that is
a deliberate, costed exception, not a default.

### Rule 5 — Federation does not reduce MAU

**Federating a customer's IdP does not remove their users from MAU billing.** Each
federated user who authenticates is still a MAU in our tenant. This is a common and
expensive misconception — worth stating explicitly to anyone modelling costs, because the
intuition that "their IdP does the work, so they're not our users" is wrong.

The exception is §8.5.

---

## 8.4 Premium feature multiplier

If Conditional Access or Identity Protection is required — and a security-conscious pharma
customer may well demand it — the premium per-MAU charge applies to the MAU population
using those features.

**The trap:** applying a Conditional Access policy tenant-wide makes *every* MAU premium,
including the 29 customers who did not ask for it. In a single shared tenant, "scope the
premium feature to one customer" is not straightforward — CA policies target users, groups
or apps, and the billing follows the users in scope.

Actions:

1. Confirm with Microsoft licensing exactly how premium MAU is counted when a CA policy is
   scoped to a group rather than the tenant. **Do this before promising CA to a customer.**
2. If scoping works: apply CA to `org.{slug}.members` groups only — this is one of the few
   legitimate uses of those operational groups.
3. If it does not: premium features become a platform-wide cost, and must be priced into
   the base tier rather than sold as an enterprise add-on.
4. Either way, make CA an explicitly priced line item in enterprise contracts.

This is a genuine commercial risk in the single-tenant model and it should be resolved
before the first customer contract that mentions Conditional Access.

---

## 8.5 The trusted-issuer cost lever

Customers on the [multi-issuer path](02-auth-flows.md#25-flow-e--entra-to-entra-fallback-customer-tenant-as-a-trusted-issuer)
authenticate against **their own** Entra tenant. They never authenticate against ours.

**They are not our MAU.**

For a large customer — say 800 users — that is a material difference, and it compounds with
the premium multiplier since their CA is their own cost, not ours.

This reframes ADR-0004 considerably. The multi-issuer path was designed as a fallback for
R1 being blocked. On cost grounds it may be the **preferred** path for large Entra-based
customers regardless of whether federation works.

**Recommendation:** model this properly during Phase 0, alongside R1. If the numbers are
as they appear, the customer archetype decision in
[§5.6](05-onboarding-runbook.md#56-the-entra-customer-decision-gate) should route large
Entra customers to `trusted-issuer` by preference, not by fallback. That is a design
decision with commercial consequences and it deserves an explicit answer rather than
falling out of R1's outcome by accident.

Countervailing factors to weigh, not ignore:

- Cross-app SSO is not shared via the CIAM session cookie on that path — each app
  authenticates against the customer tenant separately. Acceptable, but it is a real UX
  difference.
- We depend on the customer maintaining consent for our app; consent removal is an outage
  we did not cause and cannot fix.
- Operationally it is a second integration pattern to support forever.

---

## 8.6 Instrumentation — know the number before the bill

We see every token. So we can compute MAU ourselves, per organisation, and attribute cost
before Azure invoices it.

```sql
-- identity.mau_daily_active — one row per (org, user, day)
create table identity.mau_daily_active (
    org_id       uuid not null,
    user_id      uuid not null,
    active_date  date not null,
    issuer       text not null,
    is_billable  boolean not null,   -- false for trusted-issuer and workforce paths
    primary key (org_id, user_id, active_date)
);

-- Monthly billable MAU per org
select org_id,
       date_trunc('month', active_date) as month,
       count(distinct user_id) filter (where is_billable) as billable_mau,
       count(distinct user_id)                            as total_mau
from identity.mau_daily_active
group by 1, 2;
```

Written asynchronously (Service Bus, not on the request path) on first token validation per
user per day.

| Alert | Threshold | Why |
| --- | --- | --- |
| Org MAU growth | >20% month over month | Early signal of an integration modelled as users |
| Platform MAU vs forecast | >10% over | Budget protection |
| Machine-like user accounts | Any detected | Rule 1 violation |
| Non-prod authority in a prod config | Any | Rule 3 violation |
| New user accounts created outside the reconciler | Any | Drift, and a MAU source |

The `is_billable` flag matters: it makes the trusted-issuer cost benefit visible per
customer, which is what turns §8.5 from an assertion into a decision someone can act on.
