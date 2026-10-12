// Rooms.reap: every room alike, none permanent. A waiting room has no game time, so its seats never idle: it closes after
// idleMinutes with no call from its players (the gateway stamps room.calledAt on every player call). A room whose match
// is over closes at once when no outside player is left for the next one; one with players stays (its next match waits).
//
//   node --test mcp/rooms.test.js
import { test, after } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Rooms, busiest } from "./rooms.js";

const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-rooms-test-"));
process.env.HOME = DIR; // close() removes a local room's save under ~/.config/pezz: keep it off the real one
// Stub engines: each answers /api/lobby with the state it was made with.
const engines = [];
async function engine(lobby) {
  const s = http.createServer((req, res) => {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify(req.url === "/api/lobby" ? lobby : { ok: true }));
  });
  await new Promise((r) => s.listen(0, "127.0.0.1", r));
  engines.push(s);
  return `http://127.0.0.1:${s.address().port}`;
}
after(() => { for (const s of engines) s.close(); fs.rmSync(DIR, { recursive: true, force: true }); });
const newRooms = () => new Rooms({ engine: "none", stateFile: path.join(DIR, "rooms.json"), logDir: DIR, idleMinutes: 10, localRooms: false, log: () => {} });
const seat = (o) => ({ house: false, status: "playing", idle_s: 0, ...o });

test("no permanent room: a gateway with nothing saved has no rooms, and saves none", async () => {
  const rooms = newRooms();
  await rooms.ready;
  assert.deepEqual(rooms.all(), []);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(DIR, "rooms.json"), "utf8")), { rooms: [] });
});

test("a waiting room closes after idleMinutes with no call from its players, and stays while they call", async () => {
  const game = await engine({ status: "waiting", teams: [seat()] }); // one outside player, not idle: no game time has passed
  const rooms = newRooms();
  await rooms.ready;
  const room = (id, calledAt) => rooms.rooms.set(id, { id, kind: "headless", game, port: 1, code: `pezz-test${id}`, calledAt });
  room(1, Date.now() - 11 * 60000); // abandoned
  room(2, Date.now() - 60000);      // its player called a minute ago
  room(3, undefined);               // never seen by this gateway (adopted after a restart): the clock starts now
  await rooms.reap();
  assert.equal(rooms.get(1), undefined, "the abandoned waiting room is closed");
  assert.ok(rooms.get(2), "a waiting room whose player is calling stays");
  assert.ok(rooms.get(3)?.calledAt > Date.now() - 5000, "a room first seen now starts its clock now, rather than closing at once");
});

test("after a match: closed at once with nobody left to play the next, kept while outside players are seated", async () => {
  const empty = await engine({ status: "results", teams: [seat({ house: true }), seat({ status: "left" }), seat({ idle_s: 3600 })] });
  const seated = await engine({ status: "results", teams: [seat({ house: true }), seat()] });
  const playing = await engine({ status: "playing", teams: [seat({ house: true })] });
  const rooms = newRooms();
  await rooms.ready;
  rooms.rooms.set(1, { id: 1, kind: "headless", game: empty, port: 1, code: "pezz-a" });
  rooms.rooms.set(2, { id: 2, kind: "headless", game: seated, port: 3, code: "pezz-b" });
  rooms.rooms.set(3, { id: 3, kind: "headless", game: playing, port: 5, code: "pezz-c" });
  await Promise.all([rooms.reap(), rooms.reap()]); // one pass at a time: the second call shares the first
  assert.equal(rooms.get(1), undefined, "results with no outside player (house, left or idle only): closed");
  assert.ok(rooms.get(2), "results with a player still seated: kept, for its next match");
  assert.ok(rooms.get(3)?.emptySince > 0, "a playing room with nobody outside only starts its idle clock");
});

test("a fixed room (PEZZ_GAME) is room 1, never reaped or saved", async () => {
  const game = await engine({ status: "results", teams: [] });
  const rooms = new Rooms({ fixed: { game, frames: null }, engine: "none", stateFile: path.join(DIR, "fixed.json"), logDir: DIR, localRooms: false, log: () => {} });
  await rooms.ready;
  assert.equal(rooms.get(1).kind, "fixed");
  await rooms.reap();
  assert.ok(rooms.get(1), "kept");
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(DIR, "fixed.json"), "utf8")).rooms, []);
});

test("busiest: public running rooms, most active outside players first, then most seated", () => {
  const t = (n, active) => [...Array(n)].map(() => seat({ active }));
  const pick = (entries) => busiest(entries)?.room ?? null;
  assert.equal(pick([]), null);
  assert.equal(pick([{ room: 1, status: "not running" }, { room: 2, private: true, status: "playing", teams: t(3, true) }]), null);
  assert.equal(pick([{ room: 1, status: "playing", teams: t(3, false) }, { room: 2, status: "waiting", teams: t(1, true) }]), 2);
  assert.equal(pick([{ room: 1, status: "playing", teams: t(1, false) }, { room: 2, status: "playing", teams: t(2, false) }]), 2);
});
