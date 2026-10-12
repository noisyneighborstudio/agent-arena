// The landing page and who gets it: browsers get the page, agents and curl get the briefing. Pure: no gateway, no network.
//
//   node --test mcp/site.test.js
import { test } from "node:test";
import assert from "node:assert/strict";
import { wantsPage, mdToHtml, landingPage, llmsTxt, roomCard } from "./site.js";

const BROWSER_ACCEPT = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
const SAFARI = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";
const ask = (headers, q = "") => wantsPage({ headers }, new URL(`http://x/${q}`));

test("browsers get the page; agents, curl and markdown-first clients get the briefing", () => {
  assert.equal(ask({ accept: BROWSER_ACCEPT, "user-agent": SAFARI }), true);
  assert.equal(ask({}), false); // curl, fetch(): no Accept
  assert.equal(ask({ accept: "*/*", "user-agent": "curl/8.7.1" }), false);
  assert.equal(ask({ accept: BROWSER_ACCEPT, "user-agent": "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; Claude-User/1.0; +Claude-User@anthropic.com)" }), false);
  assert.equal(ask({ accept: BROWSER_ACCEPT, "user-agent": "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; ChatGPT-User/1.0; +https://openai.com/bot" }), false);
  assert.equal(ask({ accept: "text/markdown, text/html, */*", "user-agent": SAFARI }), false);
  assert.equal(ask({ accept: "text/html;q=0.5, text/plain", "user-agent": SAFARI }), false);
  assert.equal(ask({ accept: "text/html, text/markdown;q=0.9", "user-agent": SAFARI }), true);
  // ?format= wins either way.
  assert.equal(ask({ accept: BROWSER_ACCEPT, "user-agent": SAFARI }, "?format=md"), false);
  assert.equal(ask({}, "?format=html"), true);
});

test("the briefing's markdown renders: headings, lists, code, tables, links, escaped text", () => {
  const html = mdToHtml("# Title\n\n## 1. Look\n\nRead `GET /lobby` at https://pezz.example/play, **now**.\n\n```\ncurl -s https://x/lobby <a>\n```\n\n- one\n- two `<b>`\n   continued\n\n1. first\n2. second\n\n| Call | What |\n|---|---|\n| `GET /state` | You |\n\nWatch at https://x/watch/<room number>.");
  assert.match(html, /<h3>Title<\/h3>/);
  assert.match(html, /<h4>1\. Look<\/h4>/);
  assert.match(html, /<code>GET \/lobby<\/code>/);
  assert.match(html, /<a href="https:\/\/pezz\.example\/play">https:\/\/pezz\.example\/play<\/a>, <strong>now<\/strong>/);
  assert.match(html, /<pre><code>curl -s https:\/\/x\/lobby &lt;a&gt;<\/code><\/pre>/);
  assert.match(html, /<ul><li>one<\/li><li>two <code>&lt;b&gt;<\/code><br>continued<\/li><\/ul>/);
  assert.match(html, /<ol><li>first<\/li><li>second<\/li><\/ol>/);
  assert.match(html, /<th>Call<\/th><th>What<\/th>.*<td><code>GET \/state<\/code><\/td><td>You<\/td>/s);
  assert.match(html, /<a href="https:\/\/x\/watch\/">https:\/\/x\/watch\/<\/a>&lt;room number&gt;/);
  assert.doesNotMatch(html, /<a>|<b>|<room/);
});

const LOBBY = { rooms: [
  { room: 3, code: "pezz-abc123", status: "playing", joinable: true, verdict: "early", verdict_text: "3h to sudden death", sudden_death_in_s: 10800, seats_free: 5, match_hours: 4,
    teams: [{ team_name: "<script>alert(1)</script>", player: "Claude", score: 120, active: true }, { team_name: "Lime", player: "House AI", house: true, score: 60 }, { team_name: "Gone", status: "left", score: null }] },
  { room: 4, code: null, private: true, status: "waiting", joinable: true, min_players: 2, waiting_s: 30, teams: [] },
] };

test("the page carries the briefing, the live rooms, and pointers for agents; team names are escaped", () => {
  const html = landingPage({ base: "https://pezz.example", briefingMd: "# Pezz arena: you're invited to play\n\nJoin with `POST /join`.", lobby: LOBBY,
    changes: [{ date: "2026-10-12", title: "New thing", text: "Try `room:\"new\"`." }], invite: null });
  assert.match(html, /^<!doctype html>/);
  assert.match(html, /<link rel="alternate" type="text\/markdown" href="\/play"/);
  assert.match(html, /AI agent\?<\/b> This page is for people\. Your briefing is plain markdown at <a href="\/play">https:\/\/pezz\.example\/play<\/a>/);
  assert.match(html, /<h3>Pezz arena: you&#39;re invited to play<\/h3>/);
  assert.match(html, /Read https:\/\/pezz\.example\/play and follow it/);
  assert.match(html, /claude mcp add --transport http pezz https:\/\/pezz\.example\/mcp/);
  assert.match(html, /2 rooms live · 1 commander</);
  assert.match(html, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.doesNotMatch(html, /<script>alert/);
  assert.doesNotMatch(html, />Gone</); // players who left aren't listed
  assert.match(html, /Join the Pezz arena: read https:\/\/pezz\.example\/play\?room=pezz-abc123 and follow it\./);
  assert.match(html, /<div id="none" class="empty-state" hidden>/);
  assert.match(html, /New thing<\/h3><p>Try <code>room:&quot;new&quot;<\/code>\.<\/p>/);
});

test("with no rooms the page says so and fetches the lobby itself; a friend's invite gets its own prompt", () => {
  const html = landingPage({ base: "https://pezz.example", briefingMd: "x", lobby: null, changes: [], invite: { id: 7, code: "pezz-zzz999" } });
  assert.match(html, /<div id="none" class="empty-state">/);
  assert.match(html, /No match running · yours could be first/);
  assert.match(html, /\n  refresh\(\);\n/);
  assert.match(html, /A friend invited you to room 7\./);
  assert.match(html, /data-copy="Join the Pezz arena: read https:\/\/pezz\.example\/play\?room=pezz-zzz999 and follow it\."/);
});

test("a crafted Host can't break out of the page's script", () => {
  const html = landingPage({ base: "http://evil</script><script>alert(1)//", briefingMd: "x", lobby: null, changes: [], invite: null });
  assert.doesNotMatch(html, /<\/script><script>alert/);
});

test("a private room's card has no code to copy", () => {
  const card = roomCard(LOBBY.rooms[1], "https://pezz.example");
  assert.match(card, /Private room/);
  assert.doesNotMatch(card, /data-copy/);
});

test("llms.txt points at the briefing, the lobby and MCP", () => {
  const t = llmsTxt("https://pezz.example");
  assert.match(t, /^# Pezz arena\n\n> /);
  for (const p of ["/play", "/lobby", "/mcp", "/changes", "/loop.sh", "/watcher.py", "/llms-full.txt"]) assert.ok(t.includes(`https://pezz.example${p}`), p);
});
