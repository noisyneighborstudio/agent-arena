// The central invention registry: every agent-invented unit design from every room and every game, kept so designs can
// be evaluated later for permanent inclusion in the standard roster.
//
//   ~/.config/pezz/inventions/registry.json   one record per design (game_id/key): the design, its price, its inventor,
//                                             and its record (built, kills, lost, how the inventor's game went)
//   ~/.config/pezz/inventions/proposals.jsonl every propose_tech call (dry runs and rejections too), with the verdict
//
// The gateway fills both (it polls each room's /api/admin/inventions and sees every command it forwards). Nothing here
// is served to players: an enemy's design is theirs until it meets you on the field.
//
//   node mcp/inventions.js            evaluation report: designs ranked, convergent designs, rejection reasons
//   node mcp/inventions.js --json     the registry as JSON
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";

export const DIR = process.env.PEZZ_INVENTIONS_DIR || path.join(os.homedir(), ".config", "pezz", "inventions");
const REGISTRY = path.join(DIR, "registry.json");
const PROPOSALS = path.join(DIR, "proposals.jsonl");
// The proposal fields worth keeping (the game rejects anything else, and we don't store free text beyond the name).
const FIELDS = ["name", "base", "weapon_from", "hp", "speed", "damage", "range", "cooldown", "sight", "fuel", "dry_run"];

export function load() {
  try { return JSON.parse(fs.readFileSync(REGISTRY, "utf8")); } catch { return {}; }
}

function save(reg) {
  fs.mkdirSync(DIR, { recursive: true, mode: 0o700 });
  const tmp = REGISTRY + ".tmp";
  fs.writeFileSync(tmp, JSON.stringify(reg, null, 1), { mode: 0o600 });
  fs.renameSync(tmp, REGISTRY);
}

/** Fold one room's /api/admin/inventions into the registry. Returns how many records changed. */
export function merge(reg, roomId, data, now = new Date().toISOString()) {
  let changed = 0;
  for (const inv of data?.inventions ?? []) {
    const id = `${data.game_id}/${inv.key}`;
    const prev = reg[id];
    const next = { ...prev, ...inv, id, game_id: data.game_id, room: roomId, game_time_s: data.time_s, game_over: !!data.game_over,
      first_seen: prev?.first_seen ?? now, last_seen: now };
    const { last_seen: _a, game_time_s: _b, ...cmpNext } = next;
    const { last_seen: _c, game_time_s: _d, ...cmpPrev } = prev ?? {};
    if (!prev || JSON.stringify(cmpNext) !== JSON.stringify(cmpPrev)) changed++;
    reg[id] = next;
  }
  return changed;
}

/** Poll every room and save the registry if anything changed. `fetchRoom(room)` returns the room's admin JSON. */
export async function collect(rooms, fetchRoom, games) {
  const reg = load();
  let changed = 0;
  for (const room of rooms) {
    let data;
    try { data = await fetchRoom(room); } catch { continue; }
    if (!data?.game_id) continue;
    games?.set(room.id, data.game_id);
    changed += merge(reg, room.id, data);
  }
  if (changed) try { save(reg); } catch {}
  return changed;
}

/** One propose_tech call, as the gateway forwarded it, and the game's verdict. */
export function logProposal({ room, gameId, seat, team, command, result }) {
  const proposal = Object.fromEntries(FIELDS.filter((k) => command?.[k] !== undefined).map((k) => [k, command[k]]));
  const extra = Object.keys(command ?? {}).filter((k) => k !== "type" && !FIELDS.includes(k));
  const line = { at: new Date().toISOString(), room, game_id: gameId ?? null, seat, team, proposal, extra_fields: extra.length ? extra : undefined,
    ok: result?.ok !== false, key: result?.key ?? result?.quote?.key ?? null, error: result?.error ?? null, quote: result?.quote ?? null };
  try {
    fs.mkdirSync(DIR, { recursive: true, mode: 0o700 });
    fs.appendFileSync(PROPOSALS, JSON.stringify(line) + "\n", { mode: 0o600 });
  } catch {}
}

function proposals() {
  try { return fs.readFileSync(PROPOSALS, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l)); } catch { return []; }
}

// Which guardrail a rejection hit, from the validator's message.
const RULES = [
  [/requires a completed|can only vary|can only mount/, "tech tier"], [/too heavy for its engine/, "power-to-weight"], [/longer reach costs punch/, "reach vs punch"], [/novelty/, "novelty"],
  [/can't be set|unknown field/, "locked or unknown field"], [/name/, "name"], [/hard cap|range .* is below|tiles from the/, "range"],
  [/^hp|hp .* is below/, "hp"], [/^damage|damage per second/, "damage"], [/cooldown/, "cooldown"], [/^speed|speed .* exceeds/, "speed"],
  [/sight/, "sight"], [/fuel/, "fuel"], [/^base|must be an armed/, "base unit"], [/weapon_from|can't carry/, "mount"],
  [/inventions; that's the limit|no more can be researched|one research project/, "caps"], [/research needs|research:/, "research lab or bill"],
];
function ruleOf(err) { for (const [re, name] of RULES) if (re.test(err)) return name; return "other"; }

/** A plain-text evaluation report. */
export function report(reg = load(), props = proposals()) {
  const all = Object.values(reg);
  const done = all.filter((d) => d.researched);
  const out = [];
  out.push(`Pezz invention registry: ${all.length} designs (${done.length} researched) from ${new Set(all.map((d) => d.game_id)).size} games; ${props.length} proposals logged.`);
  out.push(`Data: ${DIR}`);

  // Convergent designs: the same base and weapon invented by different players is the strongest sign of a gap in the roster.
  const groups = new Map();
  for (const d of done) {
    const k = `${d.base}${d.weapon_from ? " + " + d.weapon_from + " weapon" : ""}`;
    const g = groups.get(k) ?? { k, designs: [], inventors: new Set(), built: 0, kills: 0, lost: 0, won: 0 };
    g.designs.push(d); g.inventors.add(`${d.player}`); g.built += d.built; g.kills += d.kills; g.lost += d.lost; if (d.team_status === "won") g.won++;
    groups.set(k, g);
  }
  out.push("\nConvergent designs (base + weapon), most inventors first:");
  for (const g of [...groups.values()].sort((a, b) => b.inventors.size - a.inventors.size || b.built - a.built).slice(0, 15))
    out.push(`  ${g.k.padEnd(40)} ${String(g.designs.length).padStart(3)} designs, ${String(g.inventors.size).padStart(2)} inventors, built ${g.built}, kills ${g.kills}, lost ${g.lost}, inventor won ${g.won}`);

  // Individual designs that were actually used, by combat record.
  out.push("\nMost used designs (built >= 1), by kills per unit built:");
  const used = done.filter((d) => d.built > 0).sort((a, b) => b.kills / b.built - a.kills / a.built || b.built - a.built);
  for (const d of used.slice(0, 20))
    out.push(`  ${String(d.name).padEnd(24)} ${String(d.base).padEnd(14)} built ${String(d.built).padStart(3)}, kills ${String(d.kills).padStart(3)}, lost ${String(d.lost).padStart(3)}, ` +
             `x${d.price_factor} price, by ${d.player} (${d.team_status}), room ${d.room}: ${d.spec}`);
  if (!used.length) out.push("  (none yet)");

  const rejected = props.filter((p) => !p.ok);
  const byRule = new Map();
  for (const p of rejected) for (const e of p.quote?.errors ?? [p.error ?? ""]) byRule.set(ruleOf(e), (byRule.get(ruleOf(e)) ?? 0) + 1);
  out.push(`\nProposals: ${props.length} (${props.filter((p) => p.proposal?.dry_run).length} dry runs), ${rejected.length} rejected. Rejections by rule:`);
  for (const [rule, n] of [...byRule.entries()].sort((a, b) => b[1] - a[1])) out.push(`  ${rule.padEnd(24)} ${n}`);
  out.push("\nPermanent inclusion: see docs/TECH_TREE.md (Inventions) for the criteria.");
  return out.join("\n");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(process.argv.includes("--json") ? JSON.stringify(load(), null, 1) : report());
}
