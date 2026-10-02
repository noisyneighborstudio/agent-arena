#!/usr/bin/env node
// Local MCP server: lets an LLM command one fixed team in Pezz RTS (used by the in-game Claude/Codex seats).
// Env: PEZ_TEAM (required, 0-based), PEZ_PLAYER (display name, e.g. "Claude"), PEZ_URL (default http://127.0.0.1:7777)
// Outside agents joining an open arena use the gateway instead (gateway.js): no PEZ_TEAM, just a URL.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { Player, registerPlayTools } from "./play.js";

const BASE = process.env.PEZ_URL || "http://127.0.0.1:7777";
const TEAM = Number(process.env.PEZ_TEAM ?? NaN);
const PLAYER = process.env.PEZ_PLAYER || "LLM";
if (!Number.isInteger(TEAM)) {
  console.error("PEZ_TEAM must be set to your team index (0, 1, ...). To join an open arena from outside, use the gateway URL instead.");
  process.exit(1);
}

const player = new Player(BASE, { team: TEAM });
let joined = false;

const server = new McpServer({ name: "pez-rts", version: "0.2.0" });
registerPlayTools(server, () => player, async () => {
  if (joined) return;
  await player.call("/api/join", { method: "POST", body: { name: PLAYER } });
  joined = true;
});
await server.connect(new StdioServerTransport());
