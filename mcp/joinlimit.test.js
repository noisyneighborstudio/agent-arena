// The client address and the join limit (#47), in isolation: no gateway needed.
//
//   node --test mcp/joinlimit.test.js
import { test } from "node:test";
import assert from "node:assert/strict";
import { clientIpOf, joinLimiter } from "./joinlimit.js";

test("with no header configured, the socket address is the client whatever the headers say", () => {
  const h = { "x-forwarded-for": "1.2.3.4", "cf-connecting-ip": "5.6.7.8", "x-real-ip": "9.9.9.9" };
  assert.equal(clientIpOf(h, "10.0.0.5"), "10.0.0.5");
  assert.equal(clientIpOf(h, "10.0.0.5", ""), "10.0.0.5");
  assert.equal(clientIpOf({}, undefined), "?");
});

test("a configured header is read, and only that one", () => {
  const h = { "x-forwarded-for": "1.2.3.4", "cf-connecting-ip": "5.6.7.8", "x-real-ip": "9.9.9.9" };
  assert.equal(clientIpOf(h, "10.0.0.5", "x-real-ip"), "9.9.9.9");
  assert.equal(clientIpOf(h, "10.0.0.5", "CF-Connecting-IP"), "5.6.7.8");
});

test("x-forwarded-for: the right-most address, the one the nearest proxy appended, not the client's left-most", () => {
  assert.equal(clientIpOf({ "x-forwarded-for": "6.6.6.6, 203.0.113.9" }, "10.0.0.5", "x-forwarded-for"), "203.0.113.9");
  assert.equal(clientIpOf({ "x-forwarded-for": "6.6.6.6,7.7.7.7 , 2001:db8::1" }, "10.0.0.5", "x-forwarded-for"), "2001:db8::1");
  assert.equal(clientIpOf({ "x-forwarded-for": ["6.6.6.6", "203.0.113.9"] }, "10.0.0.5", "x-forwarded-for"), "203.0.113.9");
});

test("a missing or malformed value falls back to the socket address", () => {
  assert.equal(clientIpOf({}, "10.0.0.5", "x-real-ip"), "10.0.0.5");
  assert.equal(clientIpOf({ "x-real-ip": "not-an-ip" }, "10.0.0.5", "x-real-ip"), "10.0.0.5");
  assert.equal(clientIpOf({ "x-real-ip": "1.2.3.4, " }, "10.0.0.5", "x-real-ip"), "10.0.0.5");
});

test("the join limiter: 5 joins, then none until tokens come back, each address on its own", () => {
  let t = 1000;
  const lim = joinLimiter({ max: 5, windowS: 600, now: () => t });
  for (let i = 0; i < 5; i++) assert.equal(lim.take("1.1.1.1"), true, `join ${i + 1}`);
  assert.equal(lim.take("1.1.1.1"), false);
  assert.equal(lim.take("2.2.2.2"), true, "another address has its own");
  assert.equal(lim.retryAfter("1.1.1.1"), 120);
  t += 119;
  assert.equal(lim.take("1.1.1.1"), false);
  t += 2; // 121 s: one token back
  assert.equal(lim.take("1.1.1.1"), true);
  assert.equal(lim.take("1.1.1.1"), false);
  t += 100000; // a long quiet spell refills to 5, never more
  for (let i = 0; i < 5; i++) assert.equal(lim.take("1.1.1.1"), true);
  assert.equal(lim.take("1.1.1.1"), false);
});

test("refused joins don't spend anything", () => {
  let t = 0;
  const lim = joinLimiter({ max: 2, windowS: 100, now: () => t });
  lim.take("a"); lim.take("a");
  for (let i = 0; i < 20; i++) assert.equal(lim.take("a"), false);
  t += 51; // one token (50 s each), however many were refused meanwhile
  assert.equal(lim.take("a"), true);
  assert.equal(lim.take("a"), false);
});
