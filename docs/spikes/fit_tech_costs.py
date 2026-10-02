"""Exploratory fit for the invention point-buy budget (docs/spikes/AGENT_INVENTED_TECH.md).

Parses unity/Assets/Pez/Sim/Defs.cs, prices every armed mobile unit's cost in steel-equivalents (se) and fits
    log(cost_se) = log a + alpha*log(hp) + beta*log(edps) + g_range*range + g_speed*speed (+ flags)
by least squares. The C# validator (Invention.cs) hard-codes the rounded result; Tests.cs prints the residuals
against the live defs so drift shows up there. Run: python3 docs/spikes/fit_tech_costs.py
"""
import math, re, pathlib
import numpy as np

SRC = pathlib.Path(__file__).resolve().parents[2] / "unity/Assets/Pez/Sim/Defs.cs"
text = SRC.read_text()

# Material values in steel-equivalents: ore value ~ sqrt(scarcity), converters add 25%.
ORE = {"iron_ore": 1.0, "copper_ore": 1.5, "crystal": 3.0, "uranium": 5.0}
PREMIUM = 1.25
VALUE = dict(ORE, steel=1.0, copper=1.5)
VALUE["circuits"] = (2 * VALUE["copper"] + VALUE["steel"]) * PREMIUM
VALUE["lenses"] = 2 * ORE["crystal"] * PREMIUM
VALUE["plasma"] = 2 * ORE["uranium"] * PREMIUM
VALUE["composite"] = (2 * VALUE["steel"] + ORE["crystal"]) * PREMIUM

def num(s, key, default=0.0):
    m = re.search(rf"\b{key}\s*=\s*(-?[\d.]+)f?", s)
    return float(m.group(1)) if m else default

def flag(s, key):
    return re.search(rf"\b{key}\s*=\s*true", s) is not None

weapons = {}
for m in re.finditer(r"static readonly WeaponDef (\w+) = new WeaponDef \{(.*?)\};", text):
    body = m.group(2)
    w = dict(dmg=num(body, "Damage"), rng=num(body, "Range"), cd=num(body, "Cooldown"), splash=num(body, "SplashRadius"),
             ground=not re.search(r"HitsGround\s*=\s*false", body), air=flag(body, "HitsAir"),
             inf=num(body, "VsInfantry", 1), veh=num(body, "VsVehicle", 1), st=num(body, "VsStructure", 1), vair=num(body, "VsAir", 1))
    weapons[m.group(1)] = w

def edps(w):
    g = (0.35 * w["inf"] + 0.40 * w["veh"] + 0.25 * w["st"]) if w["ground"] else 0.0
    a = w["vair"] if w["air"] else 0.0
    mult = 0.8 * g + 0.2 * a if w["ground"] else 0.5 * a  # air-only weapons: half credit (one target class)
    return w["dmg"] / w["cd"] * mult * (1 + 0.4 * w["splash"])

rows = []
for m in re.finditer(r'Add\(new EntityDef \{ Key = "(\w+)"(.*?)\}\);', text, re.S):
    key, body = m.group(1), m.group(2)
    if "IsStructure = true" in body or "Weapon =" not in body:
        continue
    cost = dict((k, int(v)) for k, v in re.findall(r'"(\w+)", (\d+)', re.search(r"Cost = C\((.*?)\)", body).group(1)))
    w = weapons[re.search(r"Weapon = (\w+)", body).group(1)]
    rows.append(dict(key=key, se=sum(VALUE[k] * v for k, v in cost.items()), hp=num(body, "MaxHp"), speed=num(body, "Speed"),
                     edps=edps(w), rng=w["rng"], air=flag(body, "IsAir"), stealth=flag(body, "Stealth"),
                     cap=num(body, "Capacity"), selfrep=num(body, "SelfRepairTo")))

print("material values (se):", {k: round(v, 2) for k, v in VALUE.items()})
X = np.array([[1, math.log(r["hp"]), math.log(r["edps"]), r["rng"], r["speed"]] for r in rows])
y = np.array([math.log(r["se"]) for r in rows])
# Flags (air, stealth, capacity, self-repair) are rare: price them as fixed multipliers instead of fitting them.
adj = np.array([math.log((1.5 if r["air"] else 1) * (1.6 if r["stealth"] else 1) * (1 + 0.06 * r["cap"]) * (1.3 if r["selfrep"] else 1)) for r in rows])
coef, *_ = np.linalg.lstsq(X, y - adj, rcond=None)
pred = X @ coef + adj
print("coef: log a=%.3f (a=%.3f) alpha_hp=%.3f beta_dps=%.3f g_range=%.3f g_speed=%.3f" % (coef[0], math.exp(coef[0]), *coef[1:]))
print(f"{'unit':16} {'cost_se':>8} {'fit':>8} {'ratio':>6}  hp edps range speed")
for r, p in zip(rows, pred):
    print(f"{r['key']:16} {r['se']:8.0f} {math.exp(p):8.0f} {r['se'] / math.exp(p):6.2f}  {r['hp']:.0f} {r['edps']:.1f} {r['rng']} {r['speed']}")
print("rms log error %.3f" % math.sqrt(np.mean((pred - y) ** 2)))

# Lanchester square-law cost-efficiency (hp x edps / cost^2) of the standard roster, relative to the light tank.
lt = next(r for r in rows if r["key"] == "light_tank")
ref = lt["hp"] * lt["edps"] / lt["se"] ** 2
print("efficiency vs light_tank:", ", ".join(f"{r['key']} {r['hp'] * r['edps'] / r['se'] ** 2 / ref:.2f}" for r in rows))
