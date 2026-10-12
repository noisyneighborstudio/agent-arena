// The operator's dashboard (pezz-ops.sethwebster.com): every room on this server, its match, every seat, and a way into
// each viewer, served by the gateway itself when a request comes in on the ops hostname.
//
// https://<ops host>/k/<key> sets an HttpOnly cookie and lands on the dashboard; anything without the cookie is a bare 404,
// so the hostname gives nothing away. The key is PEZZ_OPS_KEY (deploy/caprover/deploy.mjs sets it from the operator's
// ~/.config/pezz/ops-key); change it and redeploy to revoke every browser. Look only: it sends no orders. The per-seat
// links it shows are private view tokens.
import { timingSafeEqual } from "node:crypto";

const COOKIE = "pezz_ops";
const quiet = { "x-robots-tag": "noindex, nofollow", "referrer-policy": "no-referrer", "cache-control": "no-store", "x-content-type-options": "nosniff" };

/** opsSite({ host, key, rooms, gameAt, publicUrl }) -> handler(req, res, url) that returns true when it answered (the ops host). */
export function opsSite({ host, key, rooms, gameAt, publicUrl }) {
  host = String(host || "").toLowerCase();
  if (!host || !key || key.length < 24) return () => false;
  const same = (a) => { const x = Buffer.from(String(a ?? "")), y = Buffer.from(key); return x.length === y.length && timingSafeEqual(x, y); };
  const cookieKey = (req) => (req.headers.cookie || "").split(/;\s*/).map((c) => c.split("=")).find(([n]) => n === COOKIE)?.[1];
  const reply = (res, status, body, type) => { res.writeHead(status, { "content-type": `${type}; charset=utf-8`, ...quiet }); res.end(body); };

  async function session(room) {
    let st;
    try { st = await gameAt(room, "/api/status"); } catch { return { id: room.id, title: title(room), down: true }; }
    const scores = Object.fromEntries((st.match?.scores ?? []).map((s) => [s.team, s.score]));
    const seats = [];
    for (const t of st.teams ?? []) {
      // The engine mints a view token for a seat that never had one (host-only, which the gateway is).
      const vt = await gameAt(room, `/api/admin/viewlink?team=${t.team}`).then((v) => v.view_token).catch(() => null);
      seats.push({ team: t.team, name: t.name, player: t.player, controller: t.controller, out: !!t.defeated, score: scores[t.team],
        structures: t.structures, units: t.units, kills: t.kills, view: vt ? `${publicUrl}/view/r${room.id}-${vt}` : null });
    }
    return { id: room.id, title: title(room), code: room.code, time_s: st.time_s, speed: st.speed, paused: st.paused, map: st.map_size,
      errors: st.sim_errors, game_over: st.game_over, phase: st.match?.phase, ends_in_s: st.match?.ends_in_s, seats,
      viewers: [["spectator /watch", `${publicUrl}/watch${room.id === 1 ? "" : `/${room.id}`}`], ...(room.id === 1 ? [["site", `${publicUrl}/`]] : [])] };
  }
  const title = (room) => room.id === 1 ? "Room 1 · live arena" : `Room ${room.id} · ${room.code ?? ""}${room.kind === "remote" ? ` · on ${room.host}` : ""}`;

  return async function handle(req, res, url) {
    if (String(req.headers.host || "").toLowerCase().replace(/:\d+$/, "") !== host) return false;
    const m = url.pathname.match(/^\/k\/([A-Za-z0-9_-]+)\/?$/);
    if (m && same(m[1])) {
      // A month, on this hostname only; the redirect drops the key from the address bar and history.
      res.writeHead(302, { "set-cookie": `${COOKIE}=${key}; Path=/; Max-Age=2592000; HttpOnly; Secure; SameSite=Lax`, location: "/", ...quiet });
      res.end();
      return true;
    }
    if (!same(cookieKey(req))) { reply(res, 404, "Not found\n", "text/plain"); return true; }
    if (req.method !== "GET") { reply(res, 403, "look, don't touch: the dashboard can't send orders\n", "text/plain"); return true; }
    if (url.pathname === "/") { reply(res, 200, PAGE, "text/html"); return true; }
    if (url.pathname === "/api/sessions") {
      await rooms.ready;
      const sessions = await Promise.all(rooms.all().map(session));
      reply(res, 200, JSON.stringify({ build: process.env.PEZZ_BUILD || null, up_s: Math.round(process.uptime()), sessions }), "application/json");
      return true;
    }
    reply(res, 404, "Not found\n", "text/plain");
    return true;
  };
}

const PAGE = String.raw`<!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Pezz ops</title><style>
:root{--bg:#121013;--card:#1c191d;--ink:#ece4d2;--dim:#9a9184;--acc:#e8a33d;--bad:#e0605a;--ok:#7fc27a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.4 ui-sans-serif,system-ui,-apple-system}
header{display:flex;align-items:baseline;gap:12px;padding:14px 18px;border-bottom:1px solid #2a262b;flex-wrap:wrap}
h1{font-size:18px;margin:0}header small{color:var(--dim)}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(380px,1fr));gap:14px;padding:14px 18px}
.card{background:var(--card);border:1px solid #2a262b;border-radius:10px;overflow:hidden}
.body{padding:10px 12px}.t{display:flex;justify-content:space-between;gap:8px}.t b{font-size:15px}
.meta{color:var(--dim);font-size:12px;margin:2px 0 8px}
.links a{display:inline-block;margin:0 6px 6px 0;padding:4px 9px;border:1px solid #3a343b;border-radius:6px;color:var(--ink);text-decoration:none;font-size:12px}
.links a:hover{border-color:var(--acc)}
table{width:100%;border-collapse:collapse;font-size:12px}td{padding:3px 4px;border-top:1px solid #2a262b}
td.n{text-align:right;color:var(--dim)}td a{color:var(--acc)}.out{opacity:.45}.err{color:var(--bad)}.okc{color:var(--ok)}
#viewer{position:fixed;inset:0;background:rgba(0,0,0,.92);display:none;flex-direction:column}
#viewer.on{display:flex}body:has(#viewer.on){overflow:hidden;overscroll-behavior:none}#viewer .bar{display:flex;gap:10px;align-items:center;padding:8px 12px;color:var(--ink)}
#viewer iframe{flex:1;border:0;width:100%;min-height:0;background:#000;touch-action:none}#viewer button{background:#2a262b;color:var(--ink);border:0;border-radius:6px;padding:5px 10px;cursor:pointer}
</style></head><body>
<header><h1>Pezz ops</h1><small id=upd>loading…</small><small style="margin-left:auto">look only · per-player links are private</small></header>
<main id=grid></main>
<div id=viewer><div class=bar><b id=vt></b><a id=vo target=_blank style="color:var(--acc)">open in a new tab</a><span style="flex:1"></span><button onclick="closeV()">close (Esc)</button></div><iframe id=vf allow="fullscreen"></iframe></div>
<script>
const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const hms=s=>s==null?'':Math.floor(s/3600)+':'+String(Math.floor(s%3600/60)).padStart(2,'0')+':'+String(Math.floor(s%60)).padStart(2,'0');
function openV(title,url){vt.textContent=title;vo.href=url;vf.src=url;viewer.classList.add('on')}
function closeV(){viewer.classList.remove('on');vf.src='about:blank'}
addEventListener('keydown',e=>{if(e.key==='Escape')closeV()});
const a=(title,label,url)=>'<a href="'+esc(url)+'" onclick="openV('+esc(JSON.stringify(title))+',\''+esc(url)+'\');return false">'+esc(label)+'</a>';
function card(s){
  if(s.down)return '<div class=card><div class=body><div class=t><b>'+esc(s.title)+'</b><span class=err>not answering</span></div></div></div>';
  const status=s.game_over?'<span class=err>game over</span>':s.paused?'paused':'<span class=okc>running</span> ×'+s.speed;
  const rows=s.seats.map(t=>'<tr class="'+(t.out?'out':'')+'"><td><b>'+esc(t.name)+'</b> '+esc(t.player||t.controller||'')+(t.out?' · out':'')+'</td><td class=n>'+(t.score??'')+'</td><td class=n>'+(t.structures??'')+'s '+(t.units??'')+'u '+(t.kills??'')+'k</td><td>'+(t.view?a(s.title+' · '+t.name,'watch',t.view):'')+'</td></tr>').join('');
  return '<div class=card><div class=body><div class=t><b>'+esc(s.title)+'</b><span>'+status+'</span></div>'+
    '<div class=meta>game '+hms(s.time_s)+' · map '+esc(s.map)+(s.phase?' · '+esc(s.phase)+(s.ends_in_s!=null?' · ends in '+hms(s.ends_in_s):''):'')+(s.code?' · '+esc(s.code):'')+(s.errors?' · <span class=err>'+s.errors+' sim errors</span>':'')+'</div>'+
    '<div class=links>'+s.viewers.map(([n,u])=>a(s.title+' · '+n,n,u)).join('')+'</div><table>'+rows+'</table></div></div>';
}
async function load(){
  try{const d=await (await fetch('/api/sessions')).json();
    grid.innerHTML=d.sessions.length?d.sessions.map(card).join(''):'<p style="color:var(--dim)">No rooms.</p>';
    upd.textContent=d.sessions.length+' room(s) · build '+(d.build||'?')+' · up '+hms(d.up_s)+' · updated '+new Date().toLocaleTimeString();
  }catch(e){upd.textContent='refresh failed: '+e}
}
load();setInterval(load,10000);
</script></body></html>`;
