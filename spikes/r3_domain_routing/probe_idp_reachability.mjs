/**
 * R3 — can a user reach another customer's identity provider?
 *
 *   node spikes/r3_domain_routing/probe_idp_reachability.mjs
 *   node spikes/r3_domain_routing/probe_idp_reachability.mjs --headed   # watch it
 *
 * This is an ISOLATION test wearing UX clothing. If the External ID sign-in page presents a
 * list of configured identity providers, then in our shared tenant that list is potentially
 * every customer's IdP — and a user at org A can start an authentication against org B's
 * corporate directory.
 *
 * Prerequisites: two identity providers configured in the disposable CIAM tenant, named
 * `spike-org-a-*` and `spike-org-b-*`, both associated with the same user flow. That
 * mirrors production, where one app registration serves every customer.
 *
 * ## The four probes
 *
 *   P1  Baseline          — no hint. What does the page offer unprompted?
 *   P2  domain_hint       — does hinting org A's domain suppress org B's provider?
 *   P3  login_hint        — does an email address do it when domain_hint does not?
 *   P4  Direct navigation — can org B's IdP be reached by crafting the URL, even when
 *                           the picker is suppressed?
 *
 * P4 is the one that matters. Suppressing a button is presentation; if the underlying
 * authorize request still accepts an arbitrary provider, the control is cosmetic.
 *
 * ## Interpreting the result
 *
 * Finding that org B is reachable is **not** a project-ending result. Our design already
 * resolves the organisation from the pre-existing user record rather than from the IdP that
 * asserted the authentication, so a user who wanders into the wrong provider gets a token
 * with no `cube_org_id` — useless rather than mis-scoped.
 *
 * What this spike decides is whether that is our *only* defence or our *second* one. If
 * org B is reachable, the user-record rule stops being a belt-and-braces nicety and becomes
 * load-bearing, and it needs a test that runs on every build.
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SPIKES = path.resolve(HERE, '..');
const CONFIG = path.join(SPIKES, 'spike-config.json');

function loadConfig() {
  if (!fs.existsSync(CONFIG)) {
    console.error(`\nERROR: ${CONFIG} not found.`);
    console.error('Copy spike-config.example.json to spike-config.json and fill in the r3 block.\n');
    process.exit(1);
  }
  const cfg = JSON.parse(fs.readFileSync(CONFIG, 'utf8'));

  // Same guard as the Python harness: never point this at a tenant not marked disposable.
  const ciam = cfg.spike_tenants?.spike_ciam;
  if (!ciam || ciam.disposable !== true) {
    console.error('\nERROR: spike_ciam is not marked "disposable": true. Refusing to run.\n');
    process.exit(1);
  }
  if (!cfg.r3?.client_id || cfg.r3.client_id.startsWith('00000000-0000')) {
    console.error('\nERROR: r3.client_id is still the placeholder from the example config.\n');
    process.exit(1);
  }
  return cfg;
}

function authorizeUrl(r3, extra = {}) {
  const u = new URL(r3.sign_in_start_url);
  const params = {
    client_id: r3.client_id,
    response_type: 'code',
    redirect_uri: r3.redirect_uri,
    scope: 'openid profile email',
    state: 'r3probe',
    ...extra,
  };
  for (const [k, v] of Object.entries(params)) u.searchParams.set(k, v);
  return u.toString();
}

/** Everything on the page that could start an authentication with some provider. */
async function readProviders(page) {
  return page.evaluate(() => {
    const seen = new Set();
    const out = [];
    const candidates = document.querySelectorAll(
      'button, a[href], [role="button"], input[type="submit"], [data-provider], .idp, .identity-provider'
    );
    for (const el of candidates) {
      const text = (el.innerText || el.value || el.getAttribute('aria-label') || '').trim();
      if (!text || text.length > 120) continue;
      const key = text.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ text, tag: el.tagName.toLowerCase(), href: el.getAttribute('href') || null });
    }
    return out;
  });
}

function mentions(providers, needle) {
  const n = needle.toLowerCase();
  return providers.filter((p) => p.text.toLowerCase().includes(n) || (p.href || '').toLowerCase().includes(n));
}

async function probe(browser, name, url, cfg, evidence) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const record = { probe: name, url, ts: new Date().toISOString() };

  try {
    await page.goto(url, { waitUntil: 'networkidle', timeout: 45000 });
    await page.waitForTimeout(1200);

    const providers = await readProviders(page);
    const orgA = cfg.r3.org_a_domain.split('.')[0];
    const orgB = cfg.r3.org_b_domain.split('.')[0];

    record.final_url = page.url();
    record.providers = providers;
    record.org_a_visible = mentions(providers, orgA).length > 0 || mentions(providers, 'spike-org-a').length > 0;
    record.org_b_visible = mentions(providers, orgB).length > 0 || mentions(providers, 'spike-org-b').length > 0;

    const shot = path.join(evidence, `${name}.png`);
    await page.screenshot({ path: shot, fullPage: true });
    record.screenshot = shot;

    console.log(`\n── ${name} ${'─'.repeat(Math.max(0, 56 - name.length))}`);
    console.log(`   url      : ${url.slice(0, 110)}${url.length > 110 ? '…' : ''}`);
    console.log(`   landed on: ${record.final_url.slice(0, 110)}`);
    console.log(`   offers   : ${providers.length ? providers.map((p) => p.text).join(' | ').slice(0, 160) : '(none detected)'}`);
    console.log(`   org A visible: ${record.org_a_visible}   org B visible: ${record.org_b_visible}`);
    if (record.org_b_visible) {
      console.log('   ⚠ ANOTHER CUSTOMER\'S IDENTITY PROVIDER IS REACHABLE FROM THIS ENTRY POINT.');
    }
  } catch (err) {
    record.error = String(err);
    console.log(`\n── ${name}: ERROR ${err}`);
  } finally {
    await ctx.close();
  }
  return record;
}

async function main() {
  const cfg = loadConfig();
  const headed = process.argv.includes('--headed');

  const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, 'Z');
  const evidence = path.join(SPIKES, 'evidence', 'r3_domain_routing', stamp);
  fs.mkdirSync(evidence, { recursive: true });

  console.log('='.repeat(72));
  console.log('  R3 — cross-organisation identity provider reachability');
  console.log('='.repeat(72));
  console.log(`  org A domain: ${cfg.r3.org_a_domain}`);
  console.log(`  org B domain: ${cfg.r3.org_b_domain}`);
  console.log(`  evidence    : ${evidence}`);

  const browser = await chromium.launch({ headless: !headed });
  const records = [];

  records.push(await probe(browser, 'P1-baseline', authorizeUrl(cfg.r3), cfg, evidence));
  records.push(await probe(browser, 'P2-domain-hint', authorizeUrl(cfg.r3, { domain_hint: cfg.r3.org_a_domain }), cfg, evidence));
  records.push(await probe(browser, 'P3-login-hint', authorizeUrl(cfg.r3, { login_hint: `probe@${cfg.r3.org_a_domain}` }), cfg, evidence));
  records.push(await probe(browser, 'P4-cross-org-direct', authorizeUrl(cfg.r3, { domain_hint: cfg.r3.org_b_domain }), cfg, evidence));

  await browser.close();

  fs.writeFileSync(path.join(evidence, 'transcript.json'), JSON.stringify(records, null, 2));

  // ---- verdict ------------------------------------------------------------
  const p2 = records.find((r) => r.probe === 'P2-domain-hint') || {};
  const p4 = records.find((r) => r.probe === 'P4-cross-org-direct') || {};
  const hintSuppresses = p2.org_b_visible === false;
  const crossOrgReachable = p4.error === undefined && p4.org_b_visible !== false;

  const verdict = [
    '# R3 — cross-organisation IdP reachability',
    '',
    '| Probe | Org A offered | Org B offered |',
    '| --- | --- | --- |',
    ...records.map((r) => `| \`${r.probe}\` | ${r.org_a_visible ?? '—'} | ${r.org_b_visible ?? '—'} |`),
    '',
    '## Verdict',
    '',
    hintSuppresses
      ? '- `domain_hint` **does** suppress the other organisation\'s provider on the picker.'
      : '- ⚠ `domain_hint` does **not** suppress the other organisation\'s provider. Our own HRD layer is the only thing preventing a user from seeing every customer\'s IdP.',
    crossOrgReachable
      ? '- ⚠ Org B\'s provider is **reachable by crafting the authorize URL**. Suppressing the button is presentation only.'
      : '- Org B\'s provider was not reachable by direct navigation in this configuration.',
    '',
    '## What this changes',
    '',
    crossOrgReachable || !hintSuppresses
      ? [
          'The rule that **org is resolved from the pre-existing user record, never from the IdP**',
          'is now load-bearing rather than defence in depth. It needs:',
          '',
          '- a test on every build asserting an unexpected IdP yields a token with no `cube_org_id`;',
          '- the IdP-mismatch detector wired to a P1 alert, since it is the only thing that will',
          '  tell you this is being exercised;',
          '- JIT provisioning kept off by default, with no per-customer exception that derives org',
          '  from anything but a verified email domain.',
        ].join('\n')
      : 'Pinning works as designed. The user-record rule stays as second-line defence.',
    '',
    `_Screenshots and full transcript: \`${path.relative(process.cwd(), evidence)}\`_`,
  ].join('\n');

  fs.writeFileSync(path.join(evidence, 'SUMMARY.md'), verdict + '\n');
  console.log('\n' + '='.repeat(72));
  console.log(verdict);
  console.log(`\nEvidence: ${evidence}`);
  console.log('Fill in the R3 row of spikes/RESULTS.md from this.');
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
