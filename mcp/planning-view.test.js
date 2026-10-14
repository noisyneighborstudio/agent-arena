import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

// Exercise the actual page's pure renderer, including authored text in every exposed planning field.
const page = fs.readFileSync(new URL("./viewer.html", import.meta.url), "utf8");
const renderer = page.slice(page.indexOf("function planningHtml(p) {"), page.indexOf("\nlet sidePlan ="));
const context = vm.createContext({
  esc: (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]),
  pretty: (s) => String(s).replaceAll("_", " "),
  clock: (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, "0")}`,
});
vm.runInContext(renderer, context);
const render = context.planningHtml;

test("old, empty, and full plan pictures have explicit readable states", () => {
  assert.match(render(null), /No plan record/);
  const empty = render({ plan_version: 0, strategy: {}, objectives: [], interventions: [] });
  for (const state of ["Plan 0", "No strategy recorded", "No objectives configured", "No guard interventions yet"]) assert.ok(empty.includes(state));
  const full = render({ plan_version: 3, strategy: { text: "Expand east", priorities: ["Keep support close"] }, objectives: [{
    id: "west-exp", squad: "west", verb: "hold", at: [12, 16], radius: 6, phase: "paused", paused: true, reason: "budget_exhausted",
    reinforce: { budget: { steel: 800 }, spent: { steel: 800 } },
  }], interventions: [{ t: 90, squad: "west", rule: "hold_for_support", action: "hold", reason: "support_behind" }] });
  for (const state of ["Plan 3", "Expand east", "Keep support close", "west-exp", "Paused:", "budget exhausted", "steel 800/800", "1:30", "hold for support"]) assert.ok(full.includes(state), state);
  assert.match(full, /class="paused"/);
});

test("long or malicious authored text remains escaped across strategy, objectives, and guards", () => {
  const malicious = '<img src=x onerror="globalThis.pwned=1"><size=100> &';
  const long = malicious + " long-word".repeat(200);
  const html = render({ plan_version: 7, strategy: { text: long, priorities: [malicious] }, objectives: [{
    id: malicious, squad: malicious, verb: "escort", target_squad: malicious, phase: malicious, paused: true, reason: malicious,
  }], interventions: [{ t: 1, squad: malicious, rule: malicious, action: malicious, reason: malicious }] });
  assert.ok(!html.includes("<img") && !html.includes("<size"));
  assert.ok(html.includes("&lt;img") && html.includes("&amp;"));
  assert.equal((html.match(/long-word/g) ?? []).length, 200, "full text remains available to scroll");
  assert.equal(context.pwned, undefined);
});

test("interventions are bounded and explicit escort and assembly targets are shown", () => {
  const html = render({ strategy: {}, objectives: [{ id: "escort", squad: "north", verb: "escort", target_id: 12, assemble: { min: 8, at: [4, 5], released: false } }],
    interventions: Array.from({ length: 24 }, (_, i) => ({ t: i, squad: "west", action: `note-${i}` })) });
  assert.match(html, /unit #12 · radius 0/);
  assert.match(html, /Assembling 8 at \(4, 5\)/);
  assert.equal((html.match(/class="intervention"/g) ?? []).length, 8);
});

test("a released patrol shows its current waypoint rather than its original destination", () => {
  const o = { id: "route", squad: "west", verb: "patrol", at: [4, 5], anchor: [12, 13], radius: 3, patrol_index: 1, waypoints: [[4, 5], [12, 13]] };
  const current = render({ objectives: [o] });
  assert.match(current, /Waypoint 2\/2 · \(12, 13\) · radius 3/);
  assert.ok(!current.includes("(4, 5)"));
  const assembling = render({ objectives: [{ ...o, assemble: { released: false, min: 8, at: [1, 2] } }] });
  assert.match(assembling, /Route starts · \(4, 5\)/);
});
