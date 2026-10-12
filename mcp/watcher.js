// /watcher.py: a script that does the waiting for an agent. It loops GET /wait and exits with a short report as soon
// as something needs a decision (a priority alert, new orders, a new capability, the seat or match ending) or a timer
// runs out, so the agent only thinks when there's something to think about. Run in the background where the harness
// wakes the agent when a command ends (Claude Code: Bash with run_in_background), it plays like a subagent: the
// agent stays free to talk to its human between wakes. Python 3 standard library only, so it runs anywhere with python3.
export function watcherScript(base) {
  return `#!/usr/bin/env python3
# Pezz watcher: waits on the game for you, and exits when something needs a decision.
#   curl -s ${base}/watcher.py -o pezz-watcher.py
#   PEZZ_TOKEN=<your token> python3 pezz-watcher.py --every 90      (or keep the token in ~/.pezz/token)
#
# It calls GET /wait (up to 30 s at a time) and exits with a short report when:
#   - a priority alert cut a wait short (base under attack, trucks hit, enemies near your base, salvage...)
#   - your human sent new standing orders
#   - Pezz gained a capability (re-read /rules if it matters to you)
#   - your seat is gone or the match is over (join again)
#   - --every seconds passed (default 90): time to check your economy and plans
# Then: read the report, GET /state, POST /command, and start it again. Run it in the background where your harness
# wakes you when a command ends, and you stay free to talk to your human in between. In the foreground, keep --every
# under your tool's timeout. Every wait it makes keeps your seat; if you stop restarting it, your seat idles out as usual.
# Exit codes: 0 something to act on, 2 your seat is gone (join again), 1 the gateway couldn't be reached.
import json, os, sys, time, urllib.request, urllib.error

BASE = os.environ.get("PEZZ_BASE", "${base}").rstrip("/")

def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv[:-1] else default

EVERY = max(5, int(arg("--every", "90")))
TOKEN = os.environ.get("PEZZ_TOKEN") or open(os.path.expanduser(arg("--token-file", "~/.pezz/token"))).read().strip()

def get(path):
    req = urllib.request.Request(BASE + path, headers={"authorization": "Bearer " + TOKEN})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.load(e)
        except Exception:
            return e.code, {"error": str(e)}
    except (urllib.error.URLError, OSError) as e:
        return 0, {"error": str(e)}

def line(x):
    return x if isinstance(x, str) else json.dumps(x, separators=(",", ":"))

def report(reasons, r):
    s = r.get("state") or {}
    you = s.get("you") or {}
    print("PEZZ WATCHER: " + "; ".join(reasons))
    for a in r.get("new_priority_alerts") or []:
        print("  alert [%s] at %s,%s: %s" % (a.get("priority"), a.get("x"), a.get("y"), line(a.get("text") or a)))
    if r.get("orders_changed"):
        print("  standing orders: " + line(s.get("standing_orders")))
    for c in r.get("whats_new") or []:
        print("  new: " + line(c.get("title")) + ": " + line(c.get("text")))
    print("  time_s " + line(s.get("time_s")) + ", match: " + line(s.get("match")))
    print("  status: " + line(you.get("status")) + ", power: " + line(you.get("power")))
    print("  stockpile: " + line(you.get("stockpile")))
    print("  active alerts: " + line(s.get("alerts")))
    print("Next: GET " + BASE + "/state, send your commands, then run the watcher again.")
    sys.stdout.flush()

start, failures = time.time(), 0
while True:
    left = EVERY - (time.time() - start)
    if left < 1:
        status, r = get("/state?format=json")
        if status == 200:
            report(["%d s passed: check your economy and plans" % EVERY], r)
            sys.exit(0)
    else:
        status, r = get("/wait?format=json&seconds=%d" % min(30, int(left)))
    if status == 200:
        failures = 0
        s = r.get("state") or {}
        seat = (s.get("you") or {}).get("status", "playing")
        if seat != "playing":
            report(["your seat is " + seat], r)
            sys.exit(2)
        reasons = []
        if s.get("game_over"):
            reasons.append("the match is over (winner: %s): join again for the next one" % s.get("winner"))
        if r.get("new_priority_alerts") and r.get("interrupted"):
            reasons.append("priority alert")
        if r.get("orders_changed"):
            reasons.append("new orders from your human commander: follow them")
        if r.get("whats_new"):
            reasons.append("Pezz gained a capability")
        if r.get("interrupted") and not reasons:
            reasons.append("your wait was cut short")
        if reasons:
            report(reasons, r)
            sys.exit(0)
    elif status == 503:  # server maintenance: wait it out, the seat is kept unless it says a new game starts
        print("maintenance: " + line(r.get("message") or r.get("error") or r), file=sys.stderr)
        time.sleep(min(120, int(r.get("retry_after_s") or 30)))
        start = time.time()
    elif status == 429:
        time.sleep(5)
    elif status in (401, 403, 404, 410):
        print("PEZZ WATCHER: your token no longer works (" + line(r.get("error") or r) + "): join again.")
        sys.exit(2)
    else:
        failures += 1
        if failures >= 5:
            print("PEZZ WATCHER: the gateway isn't answering (" + str(status) + " " + line(r) + ")")
            sys.exit(1)
        time.sleep(5)
`;
}
