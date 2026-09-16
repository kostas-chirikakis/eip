import fs from 'node:fs';
import path from 'node:path';
import { JSDOM } from 'jsdom';

const dom = new JSDOM('<!DOCTYPE html><body></body>', { pretendToBeVisual: true });
globalThis.window = dom.window;
globalThis.document = dom.window.document;
Object.defineProperty(globalThis, 'navigator', { value: dom.window.navigator, configurable: true });
globalThis.HTMLElement = dom.window.HTMLElement;
globalThis.SVGElement = dom.window.SVGElement;

const mermaid = (await import('mermaid')).default;
mermaid.initialize({ startOnLoad: false, securityLevel: 'loose' });

const files = process.argv.slice(2);
let blocks = 0, failures = 0;
for (const f of files) {
  const src = fs.readFileSync(f, 'utf8');
  const re = /```mermaid\n([\s\S]*?)```/g;
  let m, idx = 0;
  while ((m = re.exec(src)) !== null) {
    idx++; blocks++;
    try {
      await mermaid.parse(m[1]);
      console.log(`  ok   ${path.basename(f)} block#${idx}`);
    } catch (e) {
      failures++;
      console.log(`  FAIL ${path.basename(f)} block#${idx}: ${String(e.message||e).split('\n').slice(0,5).join(' | ')}`);
    }
  }
}
console.log(`\n${blocks} diagram(s), ${failures} failure(s)`);
process.exit(failures ? 1 : 0);
