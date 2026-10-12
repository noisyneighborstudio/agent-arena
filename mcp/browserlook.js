// "Browser look": the agent's look tool asks the human's own browser (the Unity Web build on their /view/<v>/web page)
// to render the picture, so a room with no renderer and no GPU on the server still gets a real image. The broker is the
// rendezvous: a look request is queued per seat, the page long-polls for it, renders offscreen, and uploads a JPEG.
// Pure logic (no HTTP, no game calls) so the tests can drive it with a fake page; the gateway wires it to routes.

export const LOOK_WAIT_MS = 6000;        // how long an agent's look waits for the browser before falling back
export const LOOK_HOLD_MS = 25000;       // how long the page's long-poll is held open when nothing is asked
export const LOOK_PRESENCE_MS = 3000;    // a page counts as open this long after its last poll ended (it re-polls at once)
export const LOOK_COOL_MS = 20000;       // after a request nobody answered, skip the browser this long (a hidden tab, a broken page)
export const LOOK_MAX_BYTES = 1536 * 1024;
const MAX_PENDING = 8;

/** A JPEG: SOI marker up front and EOI at the end (encoders may pad after it, so look at the last few bytes). */
export function isJpeg(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 4 || buf[0] !== 0xff || buf[1] !== 0xd8 || buf[2] !== 0xff) return false;
  for (let i = buf.length - 1; i >= Math.max(1, buf.length - 16); i--) if (buf[i - 1] === 0xff && buf[i] === 0xd9) return true;
  return false;
}

export class LookBroker {
  constructor({ waitMs = LOOK_WAIT_MS, holdMs = LOOK_HOLD_MS, presenceMs = LOOK_PRESENCE_MS, coolMs = LOOK_COOL_MS, now = Date.now } = {}) {
    Object.assign(this, { waitMs, holdMs, presenceMs, coolMs, now });
    this.seats = new Map(); // seat key -> { pollers: Set, pending: Map(id -> req), lastPoll, coldUntil }
    this.nextId = 1;
  }

  seat(key) {
    let s = this.seats.get(key);
    if (!s) { s = { pollers: new Set(), pending: new Map(), lastPoll: 0, coldUntil: 0 }; this.seats.set(key, s); }
    return s;
  }

  /** Is a browser showing this seat's view right now (polling for looks, or just finished a poll)? */
  hasPage(key) {
    const s = this.seats.get(key);
    return !!s && (s.pollers.size > 0 || this.now() - s.lastPoll < this.presenceMs);
  }

  /** Will a look be sent to the browser at all: a page is there and it hasn't just failed to answer. */
  available(key) {
    const s = this.seats.get(key);
    return !!s && this.hasPage(key) && this.now() >= s.coldUntil;
  }

  /** Ask the seat's browser for a picture. Resolves to a JPEG Buffer, or null (no page, cooling off, timeout): the caller falls back. */
  request(key, x, y) {
    if (!this.available(key)) return Promise.resolve(null);
    const s = this.seat(key);
    if (s.pending.size >= MAX_PENDING) return Promise.resolve(null);
    return new Promise((resolve) => {
      const req = { id: this.nextId++, x: Number.isFinite(x) ? x : null, y: Number.isFinite(y) ? y : null, resolve, timer: null };
      req.timer = setTimeout(() => {
        s.pending.delete(req.id);
        s.coldUntil = this.now() + this.coolMs;
        resolve(null);
      }, this.waitMs);
      s.pending.set(req.id, req);
      for (const p of [...s.pollers]) p.wake();
    });
  }

  /** The page's long-poll: the oldest request with an id above `after`, or null when the hold runs out (or `signal` aborts). */
  next(key, after, signal) {
    const s = this.seat(key);
    const arrivedAfterGap = this.now() - s.lastPoll > this.presenceMs && s.pollers.size === 0;
    if (arrivedAfterGap) s.coldUntil = 0; // the page came back: try it again
    const find = () => {
      let best = null;
      for (const r of s.pending.values()) if (r.id > after && (!best || r.id < best.id)) best = r;
      return best && { id: best.id, x: best.x, y: best.y };
    };
    const first = find();
    if (first) { s.lastPoll = this.now(); return Promise.resolve(first); }
    return new Promise((resolve) => {
      const poller = { wake: () => { const r = find(); if (r) done(r); }, end: () => done(null) };
      const timer = setTimeout(() => done(null), this.holdMs);
      const done = (v) => {
        clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
        s.pollers.delete(poller);
        s.lastPoll = this.now();
        resolve(v);
      };
      const onAbort = () => done(null);
      s.pollers.add(poller);
      if (signal?.aborted) return done(null);
      signal?.addEventListener("abort", onAbort, { once: true });
    });
  }

  /** The page's upload. Returns { ok: true } or { ok: false, status, error }. First answer for an id wins. */
  submit(key, id, buf) {
    const s = this.seats.get(key);
    const req = s?.pending.get(id);
    if (!req) return { ok: false, status: 410, error: "no such look request (it timed out, or another tab answered first)" };
    if (!Buffer.isBuffer(buf) || buf.length === 0) return { ok: false, status: 400, error: "send the JPEG as the request body" };
    if (buf.length > LOOK_MAX_BYTES) return { ok: false, status: 413, error: `the image is over ${LOOK_MAX_BYTES} bytes` };
    if (!isJpeg(buf)) return { ok: false, status: 415, error: "the body isn't a JPEG" };
    s.pending.delete(id);
    clearTimeout(req.timer);
    s.coldUntil = 0;
    req.resolve(buf);
    return { ok: true };
  }

  /** The seat ended or changed hands: nobody is waiting for anything on it any more. */
  forget(key) {
    const s = this.seats.get(key);
    if (!s) return;
    for (const r of s.pending.values()) { clearTimeout(r.timer); r.resolve(null); }
    for (const p of [...s.pollers]) p.end();
    this.seats.delete(key);
  }

  /** Drop seats nobody has polled for a while, so a long-running gateway doesn't keep every seat it has seen. */
  sweep() {
    const cut = this.now() - 10 * 60 * 1000;
    for (const [k, s] of this.seats) if (!s.pollers.size && !s.pending.size && s.lastPoll < cut) this.seats.delete(k);
  }
}
