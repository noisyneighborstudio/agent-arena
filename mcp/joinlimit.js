// Who a request comes from, and how often that address may take a seat. Kept apart from the gateway so a test can run it
// without starting one.
//
//   node --test mcp/joinlimit.test.js
import { isIP } from "node:net";

/**
 * The client's address, for rate limits and session records. By default the address on the socket: a forwarding header is
 * only as honest as whoever sent it, and a client can send any header it likes. When the gateway sits behind a proxy
 * that sets a header itself (PEZZ_CLIENT_IP_HEADER: x-real-ip behind CapRover's nginx, cf-connecting-ip behind
 * Cloudflare), that header is read instead. It is the right-most address in it, the one the nearest proxy wrote, never the
 * left-most, which is whatever the client sent. A missing or malformed value falls back to the socket address.
 */
export function clientIpOf(headers, remoteAddress, headerName = "") {
  const socket = remoteAddress || "?";
  const name = String(headerName || "").trim().toLowerCase();
  if (!name) return socket;
  const raw = headers[name];
  const last = String(Array.isArray(raw) ? raw[raw.length - 1] : raw ?? "").split(",").pop().trim();
  return isIP(last) ? last : socket;
}

/**
 * At most `max` joins per `windowS` seconds for each address, for every way of taking a seat (POST /join, the MCP join
 * tool, switch_seat alike): each join spends one token, and tokens come back evenly over the window. Joins and leaves mint
 * salvage, so cycling seats is the thing this stops.
 */
export function joinLimiter({ max = 5, windowS = 600, now = () => Date.now() / 1000 } = {}) {
  const seen = new Map(); // address -> { tokens, at }
  const refilled = (b, t) => Math.min(max, b.tokens + (t - b.at) * (max / windowS));
  // Forget addresses that have been quiet for a whole window, so a long-running gateway doesn't keep every one it has seen.
  setInterval(() => { const cut = now() - windowS; for (const [k, b] of seen) if (b.at < cut) seen.delete(k); }, 60000).unref();
  return {
    /** Spends a join for this address. False when it has used them all. */
    take(ip) {
      const t = now();
      const b = seen.get(ip) ?? { tokens: max, at: t };
      b.tokens = refilled(b, t);
      b.at = t;
      seen.set(ip, b);
      if (b.tokens < 1) return false;
      b.tokens -= 1;
      return true;
    },
    /** Seconds until this address has a join again (0 if it has one now). */
    retryAfter(ip) {
      const b = seen.get(ip);
      if (!b) return 0;
      const tokens = refilled(b, now());
      return tokens >= 1 ? 0 : Math.ceil((1 - tokens) * (windowS / max));
    },
    max, windowS,
  };
}
