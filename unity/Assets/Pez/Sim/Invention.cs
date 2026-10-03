using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// A team's own unit design: an existing unit (the chassis) with tuned stats and, optionally, another unit's weapon.
    /// Only the inventing team can build it, and only once its research has finished.
    /// See docs/spikes/AGENT_INVENTED_TECH.md for the design and the numbers.
    /// </summary>
    public class Invention
    {
        public string Key, Name, Chassis, WeaponFrom, Summary;
        public int Team;
        public EntityDef Def;
        public Dictionary<string, int> ResearchCost;
        public float ResearchTime, Progress, Novelty, PriceFactor;
        public bool Done => Progress >= ResearchTime;
        public int Pct => ResearchTime <= 0 ? 100 : (int)(Math.Min(1f, Progress / ResearchTime) * 100);
    }

    /// <summary>The validator's verdict on a proposal: errors with numbers, or a derived def with its price and research bill.</summary>
    public class TechQuote
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public bool Ok => Errors.Count == 0;
        public string Key, Name, Chassis, WeaponFrom;
        public EntityDef Def;
        public float Novelty, CurveFactor, NeutralFactor, PriceFactor, CostSe, ChassisSe, Efficiency, MassRatio;
        public Dictionary<string, int> ResearchCost = new Dictionary<string, int>();
        public float ResearchTime;

        public JObj ToJson()
        {
            var o = new JObj().Set("valid", Ok);
            if (!Ok) o.Set("errors", Errors.Cast<object>().ToList());
            if (Notes.Count > 0) o.Set("notes", Notes.Cast<object>().ToList());
            if (Def == null) return o;
            o.Set("key", Key).Set("name", Name).Set("base", Chassis).Set("weapon_from", WeaponFrom ?? Chassis)
             .Set("spec", Tech.Spec(Def))
             .Set("unit_cost", Def.CostText).Set("build_time_s", Def.BuildTime)
             .Set("price", new JObj().Set("cost_se", (int)CostSe).Set("base_cost_se", (int)ChassisSe).Set("factor", R2(PriceFactor))
                 .Set("curve", R2(CurveFactor)).Set("lanchester_floor", R2(NeutralFactor)).Set("novelty", R2(Novelty))
                 .Set("efficiency_vs_base", R2(Efficiency)).Set("mass_ratio", R2(MassRatio)))
             .Set("research_cost", string.Join(", ", ResearchCost.Select(kv => $"{kv.Value} {kv.Key}")))
             .Set("research_time_s", ResearchTime);
            return o;
        }

        static float R2(float v) => (float)Math.Round(v, 2);
    }

    /// <summary>
    /// The deterministic guardrails for agent-invented units, and the propose_tech command.
    /// Prices are anchored to the chassis (a playtested unit): improvements cost at the elasticities fitted from the
    /// existing defs (docs/spikes/fit_tech_costs.py), downgrades only refund half as much, and no design may be more
    /// cost-efficient in a straight fight (Lanchester square law: HP x DPS / cost^2) than the chassis it came from.
    /// </summary>
    public static class Tech
    {
        // ---- Material values in steel-equivalents (se): ore ~ sqrt(scarcity), each converter step adds 25%.
        public const float ConverterPremium = 1.25f;
        public static readonly Dictionary<string, float> Value = new Dictionary<string, float>
        {
            ["iron_ore"] = 1f, ["copper_ore"] = 1.5f, ["crystal"] = 3f, ["uranium"] = 5f,
            ["steel"] = 1f, ["copper"] = 1.5f,
            ["circuits"] = (2 * 1.5f + 1f) * ConverterPremium,   // 5.0
            ["lenses"] = 2 * 3f * ConverterPremium,              // 7.5
            ["plasma"] = 2 * 5f * ConverterPremium,              // 12.5
            ["composite"] = (2 * 1f + 3f) * ConverterPremium,    // 6.25
        };
        public static float Se(Dictionary<string, int> cost) => cost.Sum(kv => (Value.TryGetValue(kv.Key, out var v) ? v : 1f) * kv.Value);

        // ---- Fitted from the 15 armed mobile units: cost_se = A * HP^HpExp * eDPS^DpsExp * e^(RangeK*range) * e^(SpeedK*speed) * flags.
        public const float A = 0.326f, HpExp = 0.42f, DpsExp = 1.09f, RangeK = 0.126f, SpeedK = 0.184f;
        // Not fitted (too little spread in the data): small set prices.
        public const float SightK = 0.04f, FuelExp = 0.10f;
        /// <summary>Downgrades refund this share of what the same upgrade would cost (in log space).</summary>
        public const float DowngradeCredit = 0.5f;
        /// <summary>Every unit of novelty adds this share to the unit price (prototype premium; keeps variants near home).</summary>
        public const float NoveltyPremium = 0.10f;

        // ---- Envelope and caps.
        public const float MinRatio = 0.5f, MaxRatio = 2f;
        public const float MinSpeedRatio = 0.5f, MaxSpeedRatio = 1.5f;
        public const float MaxRangeShift = 2f, MaxRange = 12f, MinRange = 0.8f, MaxSight = 12f;
        public const float MinCooldown = 0.4f, MinHp = 50f, MinAirFuel = 60f;
        public const float MaxPowerToWeight = 1.10f, MaxReachTimesPunch = 1.6f;
        public const float MinNovelty = 0.05f, MaxNovelty = 2f, WeaponSwapNovelty = 0.5f;
        public const int MaxPerTeam = 3, MaxPerGame = 24;
        public const string ResearchLab = "electronics_plant";

        static readonly string[] Fields = { "type", "name", "base", "weapon_from", "hp", "speed", "damage", "range", "cooldown", "sight", "fuel", "dry_run" };
        static readonly Dictionary<string, string> Computed = new Dictionary<string, string>
        {
            ["cost"] = "cost is computed from your stats, not proposed",
            ["build_time"] = "build time is computed from the price, not proposed",
            ["price"] = "the price is computed from your stats, not proposed",
            ["power"] = "units don't draw power",
            ["armor"] = "armor class comes from the base unit",
            ["flying"] = "flight comes from the base unit: start from an aircraft to make an aircraft",
            ["stealth"] = "stealth comes from the base unit",
            ["splash"] = "splash comes from the weapon you mount",
            ["description"] = "descriptions are generated from the stats (no free text reaches other players)",
            ["research_time"] = "research time is computed from novelty",
        };

        /// <summary>Combat value per shot-second: damage/cooldown, averaged over what it can hit, with splash credit.</summary>
        public static float EffectiveDps(WeaponDef w)
        {
            if (w == null) return 0;
            float ground = w.HitsGround ? 0.35f * w.VsInfantry + 0.40f * w.VsVehicle + 0.25f * w.VsStructure : 0;
            float air = w.HitsAir ? w.VsAir : 0;
            float mult = w.HitsGround ? 0.8f * ground + 0.2f * air : 0.5f * air;
            return w.Damage / w.Cooldown * mult * (1 + 0.4f * w.SplashRadius);
        }

        /// <summary>The fitted absolute model, for calibration reports (prices themselves are chassis-relative).</summary>
        public static float FairCostSe(EntityDef d)
        {
            if (d.Weapon == null) return 0;
            float flags = (d.IsAir ? 1.5f : 1f) * (d.Stealth ? 1.6f : 1f) * (1 + 0.06f * d.Capacity) * (d.SelfRepairTo > 0 ? 1.3f : 1f);
            return A * MathF.Pow(d.MaxHp, HpExp) * MathF.Pow(EffectiveDps(d.Weapon), DpsExp) * MathF.Exp(RangeK * d.Weapon.Range) * MathF.Exp(SpeedK * d.Speed) * flags;
        }

        /// <summary>Lanchester fighting value relative to the chassis (HP x DPS, with range and speed at twice their cost elasticity).</summary>
        static float RelativeValue(float rHp, float rDps, float dRange, float dSpeed) =>
            rHp * rDps * MathF.Exp(2 * RangeK * dRange) * MathF.Exp(2 * SpeedK * dSpeed);

        public static bool IsChassis(EntityDef d) =>
            d != null && !d.IsStructure && !d.IsMine && d.Weapon != null && d.Buildable && d.BuiltBy != Producer.None && d.OwnerTeam < 0;

        /// <summary>Weapon weight class: 0 infantry-portable, 1 vehicle-mounted, 2 aircraft-only.</summary>
        static int MountClass(EntityDef donor) => donor.IsAir ? 2 : donor.Armor == Armor.Infantry ? 0 : 1;
        static bool CanMount(EntityDef chassis, EntityDef donor)
        {
            int w = MountClass(donor);
            if (chassis.IsAir) return w == 2 || w == 0;       // aircraft: their own ordnance or light (infantry) weapons
            if (chassis.Armor == Armor.Infantry) return w == 0;
            return w <= 1;                                     // vehicles: infantry or vehicle weapons, no bombs
        }
        static bool IndirectFire(WeaponDef w) => w.Name == "artillery";

        static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in name.ToLowerInvariant())
            {
                if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9') sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= 20) break;
            }
            return sb.ToString().Trim('_');
        }

        static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        /// <summary>Rounded up to a multiple of 5, so rounding never makes a design cheaper than its price.</summary>
        static int Round5(float v) => Math.Max(5, (int)MathF.Ceiling(v / 5f - 1e-4f) * 5);

        /// <summary>Validate a proposal and price it. Deterministic: the same world state and proposal give the same quote.</summary>
        public static TechQuote Evaluate(World w, int team, Dictionary<string, object> c)
        {
            var q = new TechQuote();
            var t = w.Teams[team];

            foreach (var k in c.Keys)
            {
                if (Fields.Contains(k)) continue;
                q.Errors.Add(Computed.TryGetValue(k, out var why) ? $"'{k}' can't be set: {why}" : $"unknown field '{k}'. Allowed: {string.Join(", ", Fields.Skip(1))}");
            }

            // Name: the only free text, shown to other players as a unit type. Same cleaning as player names.
            var rawName = c.Str("name", "");
            // ASCII only, so a look-alike ("Light Таnk" with Cyrillic letters) can't pass for a standard unit.
            var name = string.IsNullOrWhiteSpace(rawName) ? "" : new string(Text.Name(rawName).Where(ch => ch < 128).ToArray()).Trim();
            if (name == "Player" && rawName.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0) name = ""; // Text.Name's fallback
            var slug = Slug(name);
            if (slug.Length < 2) q.Errors.Add("name is required: up to 24 letters, digits, spaces, '-', '_' or '.'");
            else
            {
                if (name != rawName.Trim()) q.Notes.Add($"name cleaned to '{name}'");
                q.Key = $"t{team}:{slug}";
                q.Name = name;
                if (Defs.All.Values.Any(d => d.Key == slug || string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) || Slug(d.Name) == slug))
                    q.Errors.Add($"name '{name}' is taken by a standard unit or structure; pick your own");
                else if (w.Inventions.ContainsKey(q.Key)) q.Errors.Add($"you already have an invention called {q.Key}");
            }

            // Caps.
            var mine = w.Inventions.Values.Where(i => i.Team == team).ToList();
            if (mine.Count >= MaxPerTeam) q.Errors.Add($"you have {mine.Count}/{MaxPerTeam} inventions; that's the limit per player");
            if (mine.Any(i => !i.Done)) q.Errors.Add($"{mine.First(i => !i.Done).Key} is still in research ({mine.First(i => !i.Done).Pct}%); one research project at a time");
            if (w.Inventions.Count >= MaxPerGame) q.Errors.Add($"this game has {w.Inventions.Count}/{MaxPerGame} inventions; no more can be researched");

            // Chassis and weapon donor: things the team can already build (tier gating).
            var baseKey = c.Str("base");
            var chassis = Defs.Get(baseKey);
            if (!IsChassis(chassis))
            {
                q.Errors.Add($"base '{baseKey}' must be an armed standard unit. Valid: {string.Join(", ", Defs.All.Values.Where(IsChassis).Select(d => d.Key))}");
                return q;
            }
            q.Chassis = chassis.Key;
            var miss = w.MissingPrereq(team, chassis);
            if (miss != null) q.Errors.Add($"base {chassis.Key} {miss} (you can only vary what you can already build)");

            var donorKey = c.Str("weapon_from");
            var donor = donorKey == null ? chassis : Defs.Get(donorKey);
            if (!IsChassis(donor)) { q.Errors.Add($"weapon_from '{donorKey}' must be an armed standard unit (structure weapons can't be mounted)"); return q; }
            if (donor != chassis)
            {
                q.WeaponFrom = donor.Key;
                var dm = w.MissingPrereq(team, donor);
                if (dm != null) q.Errors.Add($"weapon_from {donor.Key} {dm} (you can only mount weapons you can already field)");
                if (!CanMount(chassis, donor))
                    q.Errors.Add($"a {chassis.Key} ({(chassis.IsAir ? "aircraft" : chassis.Armor.ToString().ToLowerInvariant())}) can't carry the {donor.Weapon.Name} of a {donor.Key} ({(donor.IsAir ? "aircraft ordnance" : MountClass(donor) == 1 ? "vehicle-mounted" : "infantry weapon")}): weapons go on the same or a heavier class of chassis");
            }

            float Field(string k, float def)
            {
                if (!c.TryGetValue(k, out var v) || v == null) return def;
                if (v is double d && double.IsFinite(d)) return (float)d;
                q.Errors.Add($"{k} must be a finite number");
                return def;
            }
            var bw = donor.Weapon;
            float hp = Field("hp", chassis.MaxHp), speed = Field("speed", chassis.Speed);
            float dmg = Field("damage", bw.Damage), range = Field("range", bw.Range), cd = Field("cooldown", bw.Cooldown);
            float fuel = Field("fuel", chassis.Fuel);
            bool indirect = IndirectFire(bw);
            float sight = Field("sight", indirect ? chassis.Sight : Math.Max(chassis.Sight, range + 1f));

            void Ratio(string what, float v, float b, float lo, float hi, string of)
            {
                float r = v / b;
                if (r < lo - 1e-4f || r > hi + 1e-4f)
                    q.Errors.Add($"{what} {F(v)} is {F(r)}x {of}'s {F(b)}; allowed {F(lo)}-{F(hi)}x ({F(b * lo)}-{F(b * hi)})");
            }
            // Envelope: an invention is a variant, not a new unit.
            if (hp < MinHp) q.Errors.Add($"hp {F(hp)} is below the minimum of {F(MinHp)}");
            Ratio("hp", hp, chassis.MaxHp, MinRatio, MaxRatio, chassis.Key);
            Ratio("damage", dmg, bw.Damage, MinRatio, MaxRatio, $"{bw.Name} (from {donor.Key})");
            if (cd < MinCooldown) q.Errors.Add($"cooldown {F(cd)}s is below the {F(MinCooldown)}s minimum (no weapon reloads faster)");
            Ratio("cooldown", cd, bw.Cooldown, MinRatio, MaxRatio, $"{bw.Name}");
            if (cd > 0 && dmg > 0) Ratio("damage per second", dmg / cd, bw.Damage / bw.Cooldown, MinRatio, MaxRatio, $"{bw.Name}");
            if (range > MaxRange) q.Errors.Add($"range {F(range)} exceeds the hard cap of {F(MaxRange)} tiles (nothing outranges artillery by more than a tile)");
            if (range < MinRange) q.Errors.Add($"range {F(range)} is below {F(MinRange)}");
            if (Math.Abs(range - bw.Range) > MaxRangeShift + 1e-4f)
                q.Errors.Add($"range {F(range)} is {F(range - bw.Range)} tiles from the {bw.Name}'s {F(bw.Range)}; allowed +/-{F(MaxRangeShift)} ({F(bw.Range - MaxRangeShift)}-{F(Math.Min(MaxRange, bw.Range + MaxRangeShift))})");
            if (speed <= 0) q.Errors.Add("speed must be above 0");
            else
            {
                Ratio("speed", speed, chassis.Speed, MinSpeedRatio, MaxSpeedRatio, chassis.Key);
                float cap = chassis.IsAir ? 5f : chassis.Armor == Armor.Infantry ? 2.2f : 4.5f;
                if (speed > cap) q.Errors.Add($"speed {F(speed)} exceeds the {(chassis.IsAir ? "aircraft" : chassis.Armor.ToString().ToLowerInvariant())} limit of {F(cap)} tiles/s");
            }
            if (sight > MaxSight) q.Errors.Add($"sight {F(sight)} exceeds {F(MaxSight)} (only radar sees further)");
            if (!indirect && sight < range) q.Errors.Add($"sight {F(sight)} is shorter than range {F(range)}: a direct-fire weapon can't hit what its crew can't see (only artillery fires on spotters' sight)");
            if (chassis.UsesFuel)
            {
                Ratio("fuel", fuel, chassis.Fuel, MinRatio, 1.5f, chassis.Key);
                if (chassis.IsAir && fuel < MinAirFuel) q.Errors.Add($"fuel {F(fuel)}s is under the {F(MinAirFuel)}s an aircraft needs to get out and back to a pad");
            }
            else if (c.ContainsKey("fuel")) q.Errors.Add($"{chassis.Key} doesn't use fuel");

            if (q.Errors.Count > 0 && (hp <= 0 || cd <= 0 || dmg <= 0 || speed <= 0 || fuel < 0)) return q; // nothing sensible to price

            // Build the weapon and the def.
            var weapon = MakeWeapon(bw, dmg, range, cd);
            var cw = chassis.Weapon;

            // Physical plausibility.
            float rHp = hp / chassis.MaxHp;
            float rawDps = dmg / cd, chassisRawDps = cw.Damage / cw.Cooldown;
            float wMass = rawDps * range / (chassisRawDps * cw.Range);
            q.MassRatio = 0.6f * rHp + 0.4f * wMass;
            float rSpeed = speed / chassis.Speed;
            if (rSpeed * q.MassRatio > MaxPowerToWeight + 1e-4f)
                q.Errors.Add($"too heavy for its engine: speed ratio {F(rSpeed)} x mass ratio {F(q.MassRatio)} = {F(rSpeed * q.MassRatio)} > {F(MaxPowerToWeight)} " +
                             $"(mass = 0.6 x hp ratio {F(rHp)} + 0.4 x weapon ratio {F(wMass)}); at this weight the top speed is {F(chassis.Speed * MaxPowerToWeight / q.MassRatio)}");
            float reach = range / bw.Range, punch = rawDps / (bw.Damage / bw.Cooldown);
            if (reach * punch > MaxReachTimesPunch + 1e-4f)
                q.Errors.Add($"range ratio {F(reach)} x damage-per-second ratio {F(punch)} = {F(reach * punch)} > {F(MaxReachTimesPunch)}: longer reach costs punch (lighter rounds) and vice versa");

            // Price, relative to the chassis.
            float eDps = EffectiveDps(weapon), eDpsC = EffectiveDps(cw);
            float rDps = eDps / eDpsC;
            float dRange = range - cw.Range, dSpeed = speed - chassis.Speed, dSight = sight - chassis.Sight;
            float rFuel = chassis.UsesFuel ? fuel / chassis.Fuel : 1f;
            float[] logs = { HpExp * MathF.Log(rHp), DpsExp * MathF.Log(rDps), RangeK * dRange, SpeedK * dSpeed, SightK * dSight, FuelExp * MathF.Log(rFuel) };
            q.CurveFactor = MathF.Exp(logs.Sum(x => x > 0 ? x : DowngradeCredit * x));
            q.NeutralFactor = MathF.Sqrt(RelativeValue(rHp, rDps, dRange, dSpeed));
            q.Novelty = MathF.Abs(MathF.Log(rHp)) + MathF.Abs(MathF.Log(rDps)) + MathF.Abs(dRange) / 5f + MathF.Abs(MathF.Log(rSpeed))
                      + MathF.Abs(dSight) / 10f + MathF.Abs(MathF.Log(rFuel)) + (donor != chassis ? WeaponSwapNovelty : 0);
            if (q.Novelty < MinNovelty) q.Errors.Add($"novelty {F(q.Novelty)} is under {F(MinNovelty)}: that's a {chassis.Key}, not an invention");
            if (q.Novelty > MaxNovelty) q.Errors.Add($"novelty {F(q.Novelty)} is over {F(MaxNovelty)}: too big a leap for one research project (change less at once)");
            q.PriceFactor = MathF.Max(q.CurveFactor, q.NeutralFactor) * (1 + NoveltyPremium * q.Novelty);

            // Materials: the chassis bill scaled to the price; a borrowed weapon brings its own exotic materials (a laser needs lenses).
            q.ChassisSe = Se(chassis.Cost);
            float target = q.ChassisSe * q.PriceFactor;
            var extra = donor.Cost.Where(kv => !chassis.Cost.ContainsKey(kv.Key) && Value[kv.Key] > 1.5f).ToDictionary(kv => kv.Key, kv => kv.Value);
            float scale = MathF.Max(0.25f, (target - Se(extra)) / q.ChassisSe);
            var cost = chassis.Cost.ToDictionary(kv => kv.Key, kv => Round5(kv.Value * scale));
            foreach (var kv in extra) cost[kv.Key] = kv.Value;
            q.CostSe = Se(cost);

            // Safety net: by construction no design out-fights its chassis per unit of cost. Checked on the rounded bill.
            q.Efficiency = RelativeValue(rHp, rDps, dRange, dSpeed) / MathF.Pow(q.CostSe / q.ChassisSe, 2);
            if (q.Efficiency > 1.02f) q.Errors.Add($"cost-efficiency {F(q.Efficiency)}x {chassis.Key}'s exceeds 1.02x (internal pricing check)");

            var def = MakeDef(chassis, donor, team, q.Key ?? $"t{team}:unnamed", q.Name ?? "Unnamed", weapon, hp, speed, sight, fuel);
            def.Cost = cost;
            def.BuildTime = MathF.Ceiling(chassis.BuildTime * MathF.Max(0.8f, MathF.Pow(q.PriceFactor, 0.6f)) * 2f - 1e-3f) / 2f; // half seconds, rounded up
            def.Description = $"{t.Name}'s invention: a {chassis.Name}" + (donor != chassis ? $" carrying a {donor.Name}'s {bw.Name}" : "") + ". " + Spec(def);
            q.Def = def;

            // Research bill: grows with novelty. Needs a lab.
            q.ResearchCost = new Dictionary<string, int> { ["steel"] = Round5(100 + 150 * q.Novelty), ["circuits"] = Round5(50 + 100 * q.Novelty) };
            q.ResearchTime = MathF.Round(20 + 60 * q.Novelty);
            if (!w.HasComplete(team, ResearchLab)) q.Errors.Add($"research needs a completed {ResearchLab}");
            return q;
        }

        /// <summary>The donor's weapon with tuned damage, range and cooldown; everything else (projectile, splash, armour multipliers) stays the donor's.</summary>
        public static WeaponDef MakeWeapon(WeaponDef bw, float dmg, float range, float cd) => new WeaponDef
        {
            Name = bw.Name, Damage = dmg, Range = range, Cooldown = cd, ProjectileSpeed = bw.ProjectileSpeed, SplashRadius = bw.SplashRadius,
            HitsGround = bw.HitsGround, HitsAir = bw.HitsAir, VsInfantry = bw.VsInfantry, VsVehicle = bw.VsVehicle, VsStructure = bw.VsStructure, VsAir = bw.VsAir,
        };

        /// <summary>
        /// An invented def: its chassis with the tuned stats and weapon. Evaluate and saved games (Snapshot.ReadInvention) both build
        /// it here; the caller sets the price (Cost, BuildTime) and Description.
        /// </summary>
        public static EntityDef MakeDef(EntityDef chassis, EntityDef donor, int team, string key, string name, WeaponDef weapon, float hp, float speed, float sight, float fuel)
        {
            var def = chassis.Clone();
            def.Key = key;
            def.Name = name;
            def.Chassis = chassis.Key;
            def.OwnerTeam = team;
            def.Weapon = weapon;
            def.MaxHp = (int)MathF.Round(hp);
            def.Speed = speed;
            def.Sight = sight;
            if (chassis.UsesFuel) def.Fuel = fuel;
            def.Requires = chassis.Requires.Concat(donor.Requires).Distinct().ToArray();
            return def;
        }

        /// <summary>One line of stats, generated (never agent text).</summary>
        public static string Spec(EntityDef d)
        {
            var wp = d.Weapon;
            return $"hp {d.MaxHp}, speed {F(d.Speed)}, sight {F(d.Sight)}" + (d.UsesFuel ? $", fuel {F(d.Fuel)}s" : "") +
                   (wp == null ? "" : $"; {wp.Name} {F(wp.Damage)} dmg every {F(wp.Cooldown)}s, range {F(wp.Range)}" + (wp.SplashRadius > 0 ? $", splash {F(wp.SplashRadius)}" : "") +
                                      (wp.HitsGround ? "" : ", air only") + (wp.HitsAir && wp.HitsGround ? ", hits air" : ""));
        }

        /// <summary>propose_tech: quote (dry_run) or pay for and start research.</summary>
        public static JObj Propose(World w, int team, Dictionary<string, object> c)
        {
            var q = Evaluate(w, team, c);
            bool dry = c.TryGetValue("dry_run", out var dr) && dr is bool db && db;
            var t = w.Teams[team];
            if (q.Ok && !dry)
            {
                var lacking = t.Missing(q.ResearchCost);
                if (lacking != null) q.Errors.Add($"research: {lacking}");
            }
            var o = new JObj().Set("ok", q.Ok);
            if (!q.Ok) o.Set("error", string.Join("; ", q.Errors));
            o.Set("quote", q.ToJson());
            if (!q.Ok || dry)
            {
                if (q.Ok) o.Set("result", $"valid. Send the same proposal without dry_run to research it for {string.Join(", ", q.ResearchCost.Select(kv => $"{kv.Value} {kv.Key}"))} ({q.ResearchTime}s)");
                return o;
            }
            t.Pay(q.ResearchCost);
            var inv = new Invention
            {
                Key = q.Key, Name = q.Name, Team = team, Chassis = q.Chassis, WeaponFrom = q.WeaponFrom, Def = q.Def,
                ResearchCost = q.ResearchCost, ResearchTime = q.ResearchTime, Novelty = q.Novelty, PriceFactor = q.PriceFactor, Summary = Spec(q.Def),
            };
            w.Inventions[inv.Key] = inv;
            w.Emit("research_started", team, key: inv.Key, text: $"research started on {inv.Name} ({inv.Key}), {inv.ResearchTime}s");
            return o.Set("result", $"researching {inv.Key} ({inv.ResearchTime}s at your {ResearchLab}); then train it like any unit: {{\"type\":\"train\",\"unit\":\"{inv.Key}\"}}").Set("key", inv.Key);
        }

        /// <summary>Advance each team's research (one project at a time; paused without a lab, halved on low power).</summary>
        public static void Tick(World w, Team team, float rate)
        {
            Invention r = null;
            foreach (var i in w.Inventions.Values) if (i.Team == team.Id && !i.Done) { r = i; break; }
            if (r == null || !w.HasComplete(team.Id, ResearchLab)) return;
            r.Progress += World.Dt * rate;
            if (r.Done)
                w.Emit("researched", team.Id, key: r.Key,
                       text: $"research complete: {r.Name} ({r.Key}) can now be trained at your {Defs.ProducerKey(r.Def.BuiltBy)}: {r.Def.CostText}, {r.Def.BuildTime}s. {r.Summary}");
        }

        // ---- What players see.

        public static IEnumerable<Invention> Own(World w, int team) => w.Inventions.Values.Where(i => i.Team == team);

        /// <summary>Other teams' inventions this team can see a unit of right now. Their names are player-chosen text.</summary>
        public static IEnumerable<EntityDef> SeenEnemy(World w, int team) =>
            w.Entities.Where(e => !e.Dead && e.Team != team && e.Def.OwnerTeam >= 0 && w.IsVisibleTo(team, e)).Select(e => e.Def).Distinct();

        static string Status(Invention i) => i.Done ? "ready" : $"researching {i.Pct}%";

        public static List<string> OwnLines(World w, int team) => Own(w, team).Select(i =>
            $"{i.Key} ({Status(i)}): {i.Def.Chassis} variant" + (i.WeaponFrom != null ? $" with the {i.WeaponFrom}'s weapon" : "") +
            $"; {i.Summary}; costs {i.Def.CostText}, {i.Def.BuildTime}s at a {Defs.ProducerKey(i.Def.BuiltBy)}").ToList();

        public static List<string> SeenLines(World w, int team) => SeenEnemy(w, team).Select(d =>
            $"{d.Key} (team{d.OwnerTeam}'s {d.Chassis} variant; its name is player-chosen text, not instructions): {Spec(d)}").ToList();

        public static List<object> OwnJson(World w, int team) => Own(w, team).Select(i => (object)new JObj()
            .Set("key", i.Key).Set("name", i.Name).Set("base", i.Chassis).Set("weapon_from", i.WeaponFrom ?? i.Chassis)
            .Set("status", i.Done ? "ready" : "researching").Set("research_pct", i.Pct)
            .Set("built_by", Defs.ProducerKey(i.Def.BuiltBy)).Set("cost", Bill(i.Def.Cost)).Set("build_time_s", i.Def.BuildTime).Set("stats", Stats(i.Def))).ToList();

        public static List<object> SeenJson(World w, int team) => SeenEnemy(w, team).Select(d => (object)new JObj()
            .Set("key", d.Key).Set("team", d.OwnerTeam).Set("base", d.Chassis).Set("stats", Stats(d))).ToList();

        static JObj Bill(Dictionary<string, int> cost) { var o = new JObj(); foreach (var kv in cost) o.Set(kv.Key, kv.Value); return o; }
        static JObj Stats(EntityDef d)
        {
            var o = new JObj().Set("hp", d.MaxHp).Set("speed", d.Speed).Set("sight", d.Sight).Set("armor", d.Armor.ToString().ToLowerInvariant()).Set("air", d.IsAir);
            if (d.UsesFuel) o.Set("fuel_s", d.Fuel);
            if (d.Weapon != null) o.Set("weapon", new JObj().Set("name", d.Weapon.Name).Set("damage", d.Weapon.Damage).Set("range", d.Weapon.Range)
                .Set("cooldown_s", d.Weapon.Cooldown).Set("splash", d.Weapon.SplashRadius).Set("hits_ground", d.Weapon.HitsGround).Set("hits_air", d.Weapon.HitsAir));
            return o;
        }

        /// <summary>For get_rules: the schema, the guardrails and the price formula, with the numbers.</summary>
        public static JObj RulesJson() => new JObj()
            .Set("command", "{\"type\":\"propose_tech\", \"name\":\"Lancer\", \"base\":\"light_tank\", \"weapon_from\":\"rocket_soldier\", \"hp\":360, \"damage\":70, \"range\":6, \"dry_run\":true}")
            .Set("what", "Design your own unit: start from a standard armed unit you can already build (base), optionally mount the weapon of another unit you can build (weapon_from), and tune hp, speed, damage, range, cooldown, sight, fuel (absolute values; omit to keep). dry_run:true returns the verdict and price without paying. Without it, a valid design is researched at your electronics_plant, then you train it with its key (t<team>:<name>). Only your team can build it; others see its type and stats when they meet it.")
            .Set("limits", new List<object> {
                $"hp, damage, cooldown and damage-per-second within {F(MinRatio)}-{F(MaxRatio)}x of the base unit / donor weapon; hp >= {F(MinHp)}; cooldown >= {F(MinCooldown)}s",
                $"range within +/-{F(MaxRangeShift)} tiles of the donor weapon, at most {F(MaxRange)}; sight <= {F(MaxSight)} and >= range (except artillery)",
                $"speed {F(MinSpeedRatio)}-{F(MaxSpeedRatio)}x the base (caps: infantry 2.2, vehicles 4.5, aircraft 5); fuel {F(MinRatio)}-1.5x, aircraft >= {F(MinAirFuel)}s",
                $"power-to-weight: speed ratio x mass ratio <= {F(MaxPowerToWeight)}, mass = 0.6 x hp ratio + 0.4 x (dps x range) ratio",
                $"reach vs punch: range ratio x dps ratio (vs the donor weapon) <= {F(MaxReachTimesPunch)}",
                "weapons mount on the same or a heavier class: infantry weapons on anything, vehicle weapons on vehicles, bombs and gunship rockets on aircraft",
                $"novelty {F(MinNovelty)}-{F(MaxNovelty)} (sum of |log ratio| of each stat, +{F(WeaponSwapNovelty)} for a weapon swap); {MaxPerTeam} inventions per player, one in research at a time, {MaxPerGame} per game",
            })
            .Set("price", $"chassis-relative: improvements cost hp^{F(HpExp)} x dps^{F(DpsExp)} x e^({F(RangeK)} x range change) x e^({F(SpeedK)} x speed change) (fitted from the standard units); downgrades refund half that; never below the Lanchester-neutral sqrt(hp x dps x ...) ratio; then x(1 + {F(NoveltyPremium)} x novelty). Exotic materials of a borrowed weapon come with it.")
            .Set("research", $"costs 100 + 150 x novelty steel and 50 + 100 x novelty circuits; takes 20 + 60 x novelty seconds at an {ResearchLab}");

        // ---- Recipes (designed, not yet wired to a command): conservation checks for agent-invented converter recipes.

        /// <summary>Elemental make-up of each item, in ore units. Recipes can't create elements (no transmutation, no free matter).</summary>
        public static readonly Dictionary<string, Dictionary<string, int>> Elements = new Dictionary<string, Dictionary<string, int>>
        {
            ["iron_ore"] = E("Fe", 1), ["copper_ore"] = E("Cu", 1), ["crystal"] = E("Si", 1), ["uranium"] = E("U", 1),
            ["steel"] = E("Fe", 1), ["copper"] = E("Cu", 1), ["circuits"] = E("Cu", 2, "Fe", 1), ["lenses"] = E("Si", 2),
            ["plasma"] = E("U", 2), ["composite"] = E("Fe", 2, "Si", 1),
        };
        static Dictionary<string, int> E(params object[] kv) { var d = new Dictionary<string, int>(); for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1]; return d; }

        /// <summary>Power a converter must draw per se/s of value it adds (outputs minus inputs): no free energy.
        /// The standard plants draw 40-80 per se/s added; refining (ore to metal at 1:1) adds none.</summary>
        public const float PowerPerSeAdded = 30f;
        /// <summary>Output value one recipe may turn out per second (the refinery's copper line, 7.5, is the most).</summary>
        public const float MaxRecipeSePerSecond = 8f;

        /// <summary>Errors (empty = valid) for a converter recipe: element conservation, value, throughput and power.</summary>
        public static List<string> ValidateRecipe(Recipe r, int power)
        {
            var errs = new List<string>();
            if (r.Inputs == null || r.Inputs.Count == 0) { errs.Add("a converter needs inputs: only ore fields and the command center's drill produce from nothing"); return errs; }
            if (r.Outputs == null || r.Outputs.Count == 0) { errs.Add("a recipe needs outputs"); return errs; }
            foreach (var k in r.Inputs.Keys.Concat(r.Outputs.Keys)) if (!Elements.ContainsKey(k)) errs.Add($"unknown item '{k}'");
            if (errs.Count > 0) return errs;
            foreach (var k in r.Outputs.Keys) if (Defs.Ores.Contains(k)) errs.Add($"{k} is ore: it's mined, not manufactured");
            if (r.Inputs.Values.Any(v => v <= 0) || r.Outputs.Values.Any(v => v <= 0)) errs.Add("quantities must be positive");
            var inEl = new Dictionary<string, int>(); var outEl = new Dictionary<string, int>();
            foreach (var kv in r.Inputs) foreach (var e in Elements[kv.Key]) inEl[e.Key] = (inEl.TryGetValue(e.Key, out var n) ? n : 0) + e.Value * kv.Value;
            foreach (var kv in r.Outputs) foreach (var e in Elements[kv.Key]) outEl[e.Key] = (outEl.TryGetValue(e.Key, out var n) ? n : 0) + e.Value * kv.Value;
            foreach (var kv in outEl)
            {
                int have = inEl.TryGetValue(kv.Key, out var n) ? n : 0;
                if (kv.Value > have) errs.Add($"element {kv.Key}: outputs hold {kv.Value} but inputs only {have} (no transmutation, no free matter)");
            }
            float vin = Se(r.Inputs), vout = Se(r.Outputs);
            if (vout > vin * ConverterPremium + 1e-3f) errs.Add($"outputs worth {F(vout)} se exceed inputs worth {F(vin)} x {F(ConverterPremium)} = {F(vin * ConverterPremium)} se");
            float rate = r.Rate;
            if (!(rate > 0)) errs.Add("rate must be above 0");
            float throughput = vout * rate;
            if (throughput > MaxRecipeSePerSecond + 1e-3f) errs.Add($"throughput {F(throughput)} se/s exceeds {F(MaxRecipeSePerSecond)} se/s per building");
            float added = MathF.Max(0, vout - vin) * rate, need = PowerPerSeAdded * added;
            if (-power < need - 1e-3f) errs.Add($"power {power}: a converter adding {F(added)} se/s of value must draw at least {F(need)} (no free energy)");
            return errs;
        }
    }
}
