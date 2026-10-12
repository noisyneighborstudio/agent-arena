// Rooms.reap for waiting rooms: no game time passes while a room waits, so its seats never idle; it closes after
// idleMinutes with no call from its players (the gateway stamps room.calledAt on every player call).
//
//   node --test mcp/rooms.test.js
import { test, after } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Rooms } from "./rooms.js";

const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-rooms-test-"));
process.env.HOME = DIR; // close() removes a local room's save under ~/.config/pezz: keep it off the real one
// A stub engine whose lobby says it's waiting, with one outside player who hasn't idled (no game time has passed).
const engine = http.createServer((req, res) => {
  res.writeHead(200, { "content-type": "application/json" });
  res.end(JSON.stringify(req.url === "/api/lobby" ? { status: "waiting", teams: [{ house: false, status: "playing", idle_s: 0 }] } : { ok: true }));
});
await new Promise((r) => engine.listen(0, "127.0.0.1", r));
after(() => { engine.close(); fs.rmSync(DIR, { recursive: true, force: true }); });

test("a waiting room closes after idleMinutes with no call from its players, and stays while they call", async () => {
  const game = `http://127.0.0.1:${engine.address().port}`;
  const rooms = new Rooms({ hostGame: game, hostFrames: null, engine: "none", stateFile: path.join(DIR, "rooms.json"), logDir: DIR, idleMinutes: 10, localRooms: false, log: () => {} });
  await rooms.ready;
  const room = (id, calledAt) => rooms.rooms.set(id, { id, kind: "headless", game, port: 1, code: `pezz-test${id}`, calledAt });
  room(2, Date.now() - 11 * 60000); // abandoned
  room(3, Date.now() - 60000);      // its player called a minute ago
  room(4, undefined);               // never seen by this gateway (adopted after a restart): the clock starts now
  await rooms.reap();
  assert.equal(rooms.get(2), undefined, "the abandoned waiting room is closed");
  assert.ok(rooms.get(3), "a waiting room whose player is calling stays");
  assert.ok(rooms.get(4)?.calledAt > Date.now() - 5000, "a room first seen now starts its clock now, rather than closing at once");
});
