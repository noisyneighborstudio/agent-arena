using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>Advisory weight of an observation. Nothing in the game acts on it: consumers filter or wake on it if they like.</summary>
    public enum Sev { Info, Notice, Warning, Critical }

    /// <summary>
    /// One line of a team's observation feed: something that team's commander could have noticed, when, and how they
    /// know it (Src). Body holds the structured fields (ids, positions, before/after); Text is the readable line.
    /// </summary>
    public class Observation
    {
        public long Seq;
        public int Tick;
        public long Ms;          // server wall clock (unix ms) when it was logged: event-to-agent latency is receipt minus this
        public string Cat, Kind;
        public Sev Sev;
        /// <summary>
        /// Where the knowledge comes from: own (your own forces), sight (an enemy you can see now), memory (what you saw
        /// before), radar, server (match, clock, restarts), human (your authenticated commander), chat (untrusted player
        /// chat), command (the reply to a command you sent), game (an alert the game raised for you).
        /// </summary>
        public string Src;
        /// <summary>Delivered at once, past any batching: human orders, command results and failures, controller changes.</summary>
        public bool Now;
        public long Cmd;
        public JObj Body;
        public string Text;

        public JObj ToJson(bool full)
        {
            var o = new JObj().Set("seq", Seq).Set("t", (float)Math.Round(Tick * World.Dt, 2)).Set("tick", Tick).Set("ms", Ms)
                .Set("cat", Cat).Set("kind", Kind).Set("sev", SevName(Sev)).Set("src", Src);
            if (Now) o.Set("now", true);
            if (Cmd != 0) o.Set("cmd", Cmd);
            if (Body != null) foreach (System.Collections.DictionaryEntry kv in Body) o[kv.Key] = kv.Value;
            if (full && Text != null) o.Set("text", Text);
            return o;
        }

        public static string SevName(Sev s) => s.ToString().ToLowerInvariant();
        public static Sev ParseSev(string s, Sev def) => s switch { "info" => Sev.Info, "notice" => Sev.Notice, "warning" => Sev.Warning, "critical" => Sev.Critical, _ => def };
    }

    /// <summary>What one consumer wants from the feed: categories, kinds, a severity floor, detail, and what ends a long-poll early.</summary>
    public class ObsFilter
    {
        public HashSet<string> Cats, Exclude, Kinds, WakeCats, WakeKinds;
        public Sev Min = Sev.Info, WakeMin = Sev.Info;
        public bool Full;

        static HashSet<string> Set(string s) => string.IsNullOrWhiteSpace(s) ? null
            : new HashSet<string>(s.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));

        public static ObsFilter Parse(Func<string, string> q) => new ObsFilter
        {
            Cats = Set(q("cats")), Exclude = Set(q("exclude")), Kinds = Set(q("kinds")),
            WakeCats = Set(q("wake_cats")), WakeKinds = Set(q("wake_kinds")),
            Min = Observation.ParseSev(q("min"), Sev.Info), WakeMin = Observation.ParseSev(q("wake"), Sev.Info),
            Full = q("detail") == "full",
        };

        /// <summary>Delivered to this consumer. Immediate observations skip the severity floor, never an explicit exclusion.</summary>
        public bool Pass(Observation o) =>
            (Cats == null || Cats.Contains(o.Cat)) && (Kinds == null || Kinds.Contains(o.Kind)) &&
            (Exclude == null || !(Exclude.Contains(o.Cat) || Exclude.Contains(o.Kind))) && (o.Now || o.Sev >= Min);

        /// <summary>Ends a long-poll: anything delivered that is immediate, or meets the wake conditions.</summary>
        public bool Wakes(Observation o) => Pass(o) && (o.Now ||
            (o.Sev >= WakeMin && (WakeCats == null || WakeCats.Contains(o.Cat)) && (WakeKinds == null || WakeKinds.Contains(o.Kind))));
    }

    /// <summary>
    /// The battlefield observation feed: for every team, a log of what that team's commander could notice, sampled
    /// from the team's own fogged view every IntervalS game-seconds (with human orders and command results logged the
    /// moment they happen). It is never omniscient: enemies appear only while the team can see them (or as timestamped
    /// memories), a destruction is confirmed only if the team saw it, and nothing about a rival's base, economy or plans
    /// is ever logged. Readers keep their own cursors, so reading never consumes anything.
    /// </summary>
    public class ObservationFeed
    {
        public float IntervalS = 1f;
        public float HeartbeatS = 5f;
        public int Retention = 4000;
        /// <summary>Changes every time a feed is created (a process start or a resumed save): part of each stream id.</summary>
        public readonly string Nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
        public readonly bool Resumed;
        /// <summary>Wall clock in unix ms (tests may replace it).</summary>
        public static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        readonly World w;
        readonly Dictionary<int, TeamLog> logs = new Dictionary<int, TeamLog>();
        long scannedEvent;
        long scannedAlert;
        int lastSampleTick = -1;

        public ObservationFeed(World w)
        {
            this.w = w;
            Resumed = w.Tick > 0;
            scannedEvent = w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq : 0;
            scannedAlert = w.Alerts.LastSeq;
        }

        int SampleTicks => Math.Max(1, (int)Math.Round(IntervalS * World.TickRate));

        /// <summary>The team's log; a new seat (a new occupant) starts a new stream and never sees the old one's.</summary>
        public TeamLog Log(int team)
        {
            if (team < 0 || team >= w.Teams.Count) return null;
            var t = w.Teams[team];
            if (!logs.TryGetValue(team, out var log) || log.Seat != t.Seat)
            {
                string reason = log != null ? "new_seat" : Resumed && lastSampleTick < 0 ? "resumed" : w.Tick == 0 ? "new_game" : "joined";
                log = new TeamLog(this, w, team, t.Seat, $"{w.GameId}.{team}.{t.Seat}.{Nonce}", reason);
                logs[team] = log;
            }
            return log;
        }

        /// <summary>Called by World.Step every tick.</summary>
        public void Tick()
        {
            // Every tick, cheaply: note the exact tick a tracked command's units start to move (reported at the next sample).
            foreach (var log in logs.Values) log.WatchMoves();
            if (w.Tick % SampleTicks != 0 || w.Tick == lastSampleTick) return;
            Sample();
        }

        public void Sample()
        {
            lastSampleTick = w.Tick;
            var evs = new List<GameEvent>();
            for (int i = w.Events.Count - 1; i >= 0 && w.Events[i].Seq > scannedEvent; i--) evs.Add(w.Events[i]);
            evs.Reverse();
            if (evs.Count > 0) scannedEvent = evs[evs.Count - 1].Seq;
            var alerts = w.Alerts.All.Where(a => a.Seq > scannedAlert).ToList();
            scannedAlert = w.Alerts.LastSeq;
            for (int t = 0; t < w.Teams.Count; t++) Log(t).Sample(evs, alerts);
        }

        /// <summary>Event types logged the moment they're emitted, not at the next sample.</summary>
        public static bool Immediate(string type) => type == "orders" || type == "drill_failed" || type == "survey_failed" || type == "capture_stopped" || type == "out_of_range";

        public void OnEmit(GameEvent ev)
        {
            if (!Immediate(ev.Type) || ev.Team < 0 || ev.Team >= w.Teams.Count) return;
            var log = Log(ev.Team);
            if (ev.Type == "orders")
            {
                var t = w.Teams[ev.Team];
                log.Add("human", "human_orders", Sev.Critical, "human", new JObj().Set("version", t.OrdersVersion).Set("orders", t.StandingOrders)
                    .Set("authenticated", true), t.StandingOrders.Length == 0 ? "Your human commander cleared their standing orders." : "NEW ORDERS FROM YOUR HUMAN COMMANDER: " + t.StandingOrders, now: true);
                return;
            }
            var e = w.Get(ev.A);
            log.Add("orders", "order_failed", Sev.Warning, "own", new JObj().Set("unit", ev.A).Set("key", ev.Key).Set("failure", ev.Type).Set("pos", TeamLog.P(ev.Pos)),
                ev.Text, now: true, cmd: e?.CmdId ?? 0);
        }

        /// <summary>A note from outside the sim (the API's controller leases): logged at once for that team.</summary>
        public void Note(int team, string cat, string kind, Sev sev, string src, JObj body, string text) => Log(team)?.Add(cat, kind, sev, src, body, text, now: true);

        /// <summary>
        /// Run one command for a team and make it observable: give it an id (the seq of its command_accepted or
        /// command_rejected observation), stamp it on the units, queue items and structures it touched, and track it until
        /// they finish. The result gains cmd, units (what it affected) and not_affected (ids asked for that it didn't touch).
        /// Accepted is not proof of movement: movement_started, units_finished and command_completed follow.
        /// </summary>
        public JObj Track(int team, Dictionary<string, object> c, Func<JObj> exec)
        {
            var log = Log(team);
            if (log == null) return exec();
            var type = (c.Str("type", "") ?? "").ToLowerInvariant();
            var requested = Squads.Selector(c, out _) || c.TryGetValue("units", out var ur) && ur is List<object>
                ? Commands.RequestedUnits(w, team, c) : new List<int>();
            int entsBefore = w.Entities.Count;
            var t = w.Teams[team];
            var queued = new HashSet<ProdItem>(t.StructureQueue.Concat(t.UnitQueues.Values.SelectMany(q => q)));
            var retreat = type == "set_retreat" ? w.Owned(team).Where(e => !e.IsStructure).ToDictionary(e => e.Id, e => e.RetreatBelow) : null;
            var touched = new List<Entity>();
            w.Touched = touched;
            JObj r;
            try { r = exec(); }
            finally { w.Touched = null; }
            long id = log.Head + 1; // the seq of the observation about to be logged
            bool ok = r["ok"] is bool b && b;
            var cmdJson = new JObj().Set("type", type);
            foreach (var k in new[] { "x", "y", "target", "unit", "structure", "count", "structure_id" })
                if (c.TryGetValue(k, out var v) && v != null) cmdJson.Set(k, v);
            if (!ok)
            {
                log.Add("orders", "command_rejected", Sev.Warning, "command", new JObj().Set("command", cmdJson).Set("error", r["error"]).Set("units", requested),
                    $"command {type} rejected: {r["error"]}", now: true, cmd: id);
                r.Set("cmd", id);
                return r;
            }
            var units = touched.Where(e => e.Team == team && !e.IsStructure && !e.Dead).Select(e => e).Distinct().ToList();
            if (retreat != null) units.AddRange(w.Owned(team).Where(e => !e.IsStructure && retreat.TryGetValue(e.Id, out var was) && was != e.RetreatBelow));
            units = units.Distinct().ToList();
            var created = w.Entities.Skip(entsBefore).Where(e => e.Team == team && !e.Dead).ToList();
            var items = t.StructureQueue.Concat(t.UnitQueues.Values.SelectMany(q => q)).Where(p => !queued.Contains(p)).ToList();
            var track = new CmdTrack { Id = id, Type = type, IssuedTick = w.Tick };
            foreach (var p in items) p.CmdId = id;
            foreach (var e in created) e.CmdId = id;
            bool unitOrder = retreat == null && type != "rally" && type != "deploy";
            if (unitOrder)
                foreach (var e in units)
                {
                    e.CmdId = id;
                    track.Units.Add(e.Id);
                    track.Start[e.Id] = e.Pos;
                }
            string tracking = "none";
            if (track.Units.Count > 0) tracking = "units";
            else if (type == "train" && items.Count > 0) { tracking = "production"; track.TrainLeft = items.Count; }
            else if (type == "build" && created.Count > 0) { tracking = "construction"; track.Structure = created[0].Id; }
            if (tracking != "none")
            {
                float x = c.Num("x"), y = c.Num("y");
                var wps = c.TryGetValue("waypoints", out var wr) && wr is List<object> wl ? wl : null;
                if (wps != null && wps.Count > 0 && (type == "move" || type == "attack_move"))
                {
                    var last = wps[wps.Count - 1];
                    if (last is List<object> pl && pl.Count >= 2 && pl[0] is double lx && pl[1] is double ly) { x = (float)lx; y = (float)ly; }
                    else if (last is Dictionary<string, object> pd) { x = pd.Num("x"); y = pd.Num("y"); }
                }
                if (!float.IsNaN(x) && !float.IsNaN(y)) track.Dest = new Vec2(x, y);
                track.Target = (int)c.Num("target", 0);
                track.Loop = c.TryGetValue("loop", out var lp) && lp is bool lb && lb;
                float spread = c.TryGetValue("spread", out var sp) ? (sp is double sd ? (float)sd : sp is bool sb && sb ? 3f : 1f) : 1f;
                track.Tolerance = 2.5f + 0.7f * MathF.Sqrt(Math.Max(1, track.Units.Count)) * Math.Max(1f, spread);
                log.Open(track);
            }
            var unitIds = units.Select(e => e.Id).ToList();
            var body = new JObj().Set("command", cmdJson).Set("units", unitIds).Set("tracking", tracking);
            var missed = requested.Where(i => !unitIds.Contains(i)).ToList();
            if (missed.Count > 0) body.Set("not_affected", missed);
            if (created.Count > 0) body.Set("created", created.Select(e => (object)new JObj().Set("id", e.Id).Set("key", e.Def.Key)).ToList());
            if (r["result"] is string res) body.Set("result", res);
            log.Add("orders", "command_accepted", Sev.Info, "command", body, $"command {type} accepted" + (unitIds.Count > 0 ? $" for {unitIds.Count} unit(s)" : ""), now: true, cmd: id);
            r.Set("cmd", id).Set("units", unitIds).Set("tracking", tracking);
            if (missed.Count > 0) r.Set("not_affected", missed);
            return r;
        }
    }

    class CmdTrack
    {
        public long Id;
        public string Type;
        public int IssuedTick;
        public readonly List<int> Units = new List<int>();
        public readonly Dictionary<int, Vec2> Start = new Dictionary<int, Vec2>();
        public readonly Dictionary<int, string> Outcome = new Dictionary<int, string>();
        public Vec2? Dest;
        public int Target, Structure, TrainLeft, Trained;
        public bool Loop, Moved, Split;
        public int MovedTick = -1;
        public float Tolerance = 3f;
    }

    class UnitMem
    {
        public Order Order;
        public Vec2 Pos, OrderPos;
        public float Hp;
        public long Cmd;
        public int FuelBand, Waypoints;
        public bool Retreating, Landed, Carried, NoProgress, InCoverage;
        public float ProgDist, ProgAt;
    }

    class StructMem { public float Hp; public bool Complete, Firing, Exposed; public float LastFired; }

    class Contact
    {
        public int Id, Team;
        public string Key;
        public bool Visible, InRange;
        public Vec2 LastPos, Reported;
        public float LastSeen;
    }

    class Engagement
    {
        public int Id;
        public Vec2 Center;
        public float Start, Last;
        public int OwnLost, EnemyDestroyed;
        public readonly HashSet<int> Own = new HashSet<int>(), Enemies = new HashSet<int>();
    }

    /// <summary>One team's observation log and the memory it diffs against: a ring of observations with contiguous seqs.</summary>
    public class TeamLog
    {
        public readonly int Team, Seat;
        public readonly string Stream;
        readonly ObservationFeed feed;
        readonly World w;
        readonly List<Observation> ring = new List<Observation>();
        long nextSeq = 1;
        public long Head => nextSeq - 1;
        public long First => ring.Count > 0 ? ring[0].Seq : nextSeq;

        readonly Dictionary<int, UnitMem> units = new Dictionary<int, UnitMem>();
        readonly Dictionary<int, StructMem> structs = new Dictionary<int, StructMem>();
        readonly Dictionary<int, Contact> contacts = new Dictionary<int, Contact>();
        readonly Dictionary<int, (string key, int team, Int2 origin, float seen)> known = new Dictionary<int, (string, int, Int2, float)>();
        readonly HashSet<int> radar = new HashSet<int>();
        readonly HashSet<int> confirmedDead = new HashSet<int>();
        readonly Dictionary<long, CmdTrack> cmds = new Dictionary<long, CmdTrack>();
        readonly List<Engagement> fights = new List<Engagement>();
        readonly List<(int x, int y, string type)> fields = new List<(int, int, string)>();
        readonly HashSet<int> zones = new HashSet<int>();
        readonly Dictionary<string, float> rates = new Dictionary<string, float>();
        readonly Dictionary<string, ProdItem> heads = new Dictionary<string, ProdItem>();
        readonly Dictionary<ProdItem, int> milestones = new Dictionary<ProdItem, int>();
        /// <summary>Units being trained: the building each was last announced at (training_started), so every unit is announced once.</summary>
        readonly Dictionary<ProdItem, int> working = new Dictionary<ProdItem, int>();
        readonly HashSet<string> blocked = new HashSet<string>();
        readonly HashSet<int> congested = new HashSet<int>();
        int power0, power1, nextFight = 1, samples;
        float lastHeartbeat = -999, lastRates = -999;
        bool hadPad, ended, primed;

        public TeamLog(ObservationFeed feed, World w, int team, int seat, string stream, string reason)
        {
            this.feed = feed; this.w = w; Team = team; Seat = seat; Stream = stream;
            Add("match", "stream_started", Sev.Notice, "server", new JObj().Set("reason", reason).Set("stream", stream).Set("rules_version", StateView.RulesVersion)
                .Set("interval_s", feed.IntervalS).Set("heartbeat_s", feed.HeartbeatS),
                reason == "resumed" ? "The server restarted and resumed this game: anything before now is in the snapshot, not the feed." : $"Observation feed started ({reason}).");
        }

        public static List<object> P(Vec2 p) => new List<object> { (float)Math.Round(p.X, 1), (float)Math.Round(p.Y, 1) };
        float Now => w.Time;
        Team T => w.Teams[Team];

        public Observation Add(string cat, string kind, Sev sev, string src, JObj body, string text = null, bool now = false, long cmd = 0)
        {
            var o = new Observation { Seq = nextSeq++, Tick = w.Tick, Ms = ObservationFeed.Clock(), Cat = cat, Kind = kind, Sev = sev, Src = src, Now = now, Cmd = cmd, Body = body, Text = text };
            ring.Add(o);
            if (ring.Count > feed.Retention) ring.RemoveRange(0, ring.Count - feed.Retention);
            return o;
        }

        internal void WatchMoves()
        {
            foreach (var c in cmds.Values)
            {
                if (c.MovedTick >= 0 || c.Units.Count == 0) continue;
                foreach (var u in c.Units)
                    if (w.Get(u) is Entity e && e.CmdId == c.Id && (e.Moving || (c.Start.TryGetValue(u, out var s0) && Vec2.Dist(s0, e.Pos) > 0.05f))) { c.MovedTick = w.Tick; break; }
            }
        }

        internal void Open(CmdTrack c)
        {
            cmds[c.Id] = c;
            if (cmds.Count > 200)
                foreach (var old in cmds.Values.OrderBy(x => x.Id).Take(cmds.Count - 200).ToList()) cmds.Remove(old.Id);
        }

        /// <summary>Observations after a cursor (seqs are contiguous, so this is an index).</summary>
        public IEnumerable<Observation> After(long cursor)
        {
            if (ring.Count == 0) yield break;
            long start = Math.Max(0, cursor + 1 - ring[0].Seq);
            for (long i = start; i < ring.Count; i++) yield return ring[(int)i];
        }

        /// <summary>Whether anything after the cursor would end this consumer's long-poll, and whether it's immediate.</summary>
        public (bool wake, bool now) Wakes(long cursor, ObsFilter f)
        {
            bool any = false;
            foreach (var o in After(cursor))
                if (f.Wakes(o)) { if (o.Now) return (true, true); any = true; }
            return (any, false);
        }

        public static string GapReason(string mine, string theirs)
        {
            var a = mine.Split('.'); var b = (theirs ?? "").Split('.');
            if (b.Length != 4) return "unknown_stream";
            if (a[0] != b[0]) return "new_game";
            if (a[1] != b[1] || a[2] != b[2]) return "new_seat";
            if (a[3] != b[3]) return "server_restarted";
            return "unknown_stream";
        }

        /// <summary>
        /// A read from a consumer-owned cursor. A missing cursor starts fresh (a snapshot and the head as the cursor); a
        /// cursor from another stream (restart, new game, new seat), ahead of the head, or older than what's retained is a
        /// gap: it's said so explicitly, with a fresh snapshot. Filtered-out observations still advance next_cursor.
        /// </summary>
        public JObj Read(string stream, long cursor, ObsFilter f, int limit, bool snapshotOnGap = true)
        {
            var o = new JObj().Set("ok", true).Set("stream", Stream).Set("team", Team).Set("t", (float)Math.Round(w.Time, 2)).Set("tick", w.Tick)
                .Set("server_ms", ObservationFeed.Clock()).Set("interval_s", feed.IntervalS).Set("heartbeat_s", feed.HeartbeatS)
                .Set("first", First).Set("head", Head);
            JObj gap = null;
            long from = cursor;
            bool snap = false;
            if (cursor < 0) { from = Head; snap = true; o.Set("fresh", true); }
            else if (stream != null && stream != Stream) { gap = new JObj().Set("reason", GapReason(Stream, stream)).Set("your_stream", stream).Set("your_cursor", cursor); from = Head; snap = true; }
            else if (cursor > Head) { gap = new JObj().Set("reason", "cursor_ahead").Set("your_cursor", cursor); from = Head; snap = true; }
            else if (cursor < First - 1) { gap = new JObj().Set("reason", "retention").Set("missed_from", cursor + 1).Set("missed_to", First - 1); from = First - 1; snap = true; }
            var list = new List<object>();
            long scanned = from;
            foreach (var ob in After(from))
            {
                if (list.Count >= limit) break;
                scanned = ob.Seq;
                if (f.Pass(ob)) list.Add(ob.ToJson(f.Full));
            }
            if (list.Count < limit) scanned = Math.Max(scanned, Head);
            o.Set("gap", gap).Set("events", list).Set("next_cursor", scanned).Set("more", scanned < Head);
            if (snap && (snapshotOnGap || cursor < 0)) o.Set("snapshot", Snapshot());
            return o;
        }

        // ------------------------------------------------------------------ sampling

        internal void Sample(List<GameEvent> evs, List<Alert> alerts)
        {
            var t = T;
            if (t.Left || t.Defeated)
            {
                if (!ended) { ended = true; Add("match", "seat_ended", Sev.Critical, "server", new JObj().Set("status", t.Resigned ? "resigned" : t.Left ? "left" : "eliminated"), "Your team is out of this game."); }
                return;
            }
            samples++;
            var vis = w.Entities.Where(e => !e.Dead && e.Team != Team && e.Team >= 0 && !e.IsStructure && w.IsVisibleTo(Team, e)).ToList();
            var own = w.Entities.Where(e => !e.Dead && e.Team == Team).ToList();
            if (!primed) { Prime(own, vis); primed = true; }
            var lost = new Dictionary<int, Vec2>();
            var seenDead = new HashSet<int>();
            MapEvents(evs, lost, seenDead);
            MapAlerts(alerts);
            var damaged = new List<object>();
            var activity = new List<(Vec2 pos, int id)>();
            Units(own, damaged, activity);
            Structures(own, vis, damaged, activity);
            if (damaged.Count > 0)
            {
                var top = damaged.Cast<JObj>().OrderByDescending(d => (float)d["lost"]).ToList();
                Add("combat", "damage", top.Any(d => d["structure"] is bool sb && sb) ? Sev.Warning : Sev.Notice, "own",
                    new JObj().Set("hits", top.Take(20).Cast<object>().ToList()).Set("count", top.Count),
                    $"{top.Count} of yours took damage: " + string.Join(", ", top.Take(6).Select(d => $"{d["key"]} #{d["id"]} {((List<object>)d["hp"])[0]}->{((List<object>)d["hp"])[1]}")));
            }
            Contacts(own, vis, seenDead);
            Known(seenDead);
            Fights(activity, lost, seenDead, vis);
            Commands(lost, seenDead);
            Production();
            Economy(own);
            if (samples % 10 == 1) Resources();
            if (Now - lastHeartbeat >= feed.HeartbeatS - 1e-3f) Heartbeat(own, vis);
        }

        /// <summary>The first sample: remember what's there without announcing it (the snapshot already says it).</summary>
        void Prime(List<Entity> own, List<Entity> vis)
        {
            foreach (var e in own) { if (e.IsStructure) structs[e.Id] = SMem(e); else if (!e.IsMine) units[e.Id] = UMem(e); }
            foreach (var e in vis) contacts[e.Id] = new Contact { Id = e.Id, Team = e.Team, Key = e.Def.Key, Visible = true, LastPos = e.Pos, Reported = e.Pos, LastSeen = Now };
            foreach (var kv in T.KnownEnemyStructures) known[kv.Key] = (kv.Value.key, kv.Value.team, kv.Value.origin, Now);
            foreach (var z in T.Zones.Keys) zones.Add(z);
            foreach (var f in StateView.OreFields(w, T.Explored)) fields.Add((f.cx, f.cy, f.type));
            foreach (var item in Defs.Items) rates[item] = T.Rates.TryGetValue(item, out var r) ? r : 0;
            power0 = T.PowerUsed; power1 = T.PowerProduced;
            hadPad = own.Any(e => e.IsStructure && e.IsComplete && e.Def.Key == "airfield");
            foreach (var e in w.RadarContacts(Team)) radar.Add(e.Id);
        }

        UnitMem UMem(Entity e) => new UnitMem
        {
            Order = e.Order, Pos = e.Pos, OrderPos = e.OrderPos, Hp = e.Hp, Cmd = e.CmdId, FuelBand = FuelBand(e), Waypoints = e.Waypoints.Count,
            Retreating = e.Retreating, Landed = e.Landed, Carried = e.IsCarried, ProgDist = Vec2.Dist(e.Pos, e.OrderPos), ProgAt = w.Time,
        };
        static StructMem SMem(Entity e) => new StructMem { Hp = e.Hp, Complete = e.IsComplete, LastFired = e.LastFiredAt };
        static int FuelBand(Entity e) => !e.Def.UsesFuel ? 0 : e.FuelFraction <= 0.1f ? 3 : e.FuelFraction <= 0.25f ? 2 : e.FuelFraction <= 0.5f ? 1 : 0;
        static JObj U(Entity e) => new JObj().Set("id", e.Id).Set("key", e.Def.Key).Set("pos", P(e.IsStructure ? e.Center : e.Pos));
        static string Ids(IEnumerable<JObj> l, int max = 6)
        {
            var a = l.ToList();
            return string.Join(", ", a.Take(max).Select(x => $"{x["key"]} #{x["id"]}")) + (a.Count > max ? $" +{a.Count - max} more" : "");
        }
        Sev Weight(Priority p) => p == Priority.Critical ? Sev.Critical : p == Priority.High ? Sev.Warning : Sev.Notice;

        bool SeenBy(GameEvent ev) => (ev.SeenBy & (1 << Team)) != 0;

        bool Sees(Vec2 p)
        {
            var m = w.Map;
            int x = Math.Clamp((int)p.X, 0, m.W - 1), y = Math.Clamp((int)p.Y, 0, m.H - 1);
            return T.Visible[m.Idx(x, y)];
        }

        /// <summary>Who did it, only if this team can see them now: never a hidden attacker's identity or whereabouts.</summary>
        JObj Visible(int id)
        {
            var e = w.Get(id);
            if (e == null || e.Team == Team || !w.IsVisibleTo(Team, e)) return null;
            return new JObj().Set("id", e.Id).Set("key", e.Def.Key).Set("team", e.Team).Set("pos", P(e.IsStructure ? e.Center : e.Pos));
        }

        void MapEvents(List<GameEvent> evs, Dictionary<int, Vec2> lost, HashSet<int> seenDead)
        {
            var losses = new List<JObj>();
            var killed = new List<JObj>();
            var sold = new HashSet<int>(evs.Where(e => e.Type == "sold" && e.Team == Team).Select(e => e.A));
            foreach (var ev in evs)
            {
                if (ObservationFeed.Immediate(ev.Type)) continue;
                bool mine = ev.Team == Team;
                var body = new JObj();
                switch (ev.Type)
                {
                    case "destroyed":
                        if (mine)
                        {
                            if (sold.Contains(ev.A)) break;
                            lost[ev.A] = ev.Pos;
                            var l = new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)).Set("at_t", (float)Math.Round(ev.Tick * World.Dt, 2)).Set("structure", w.Def(ev.Key)?.IsStructure ?? false);
                            var killer = Visible(ev.B);
                            if (killer != null) l.Set("killer", killer); else l.Set("killer", "not visible");
                            losses.Add(l);
                        }
                        else if (SeenBy(ev))
                        {
                            seenDead.Add(ev.A);
                            confirmedDead.Add(ev.A);
                            if (confirmedDead.Count > 5000) confirmedDead.Clear();
                            var k = w.ById.TryGetValue(ev.B, out var ke) ? ke : null;
                            killed.Add(new JObj().Set("id", ev.A).Set("key", ev.Key).Set("team", ev.Team).Set("pos", P(ev.Pos)).Set("at_t", (float)Math.Round(ev.Tick * World.Dt, 2)).Set("by_you", k != null && k.Team == Team));
                        }
                        break;
                    case "sold": if (mine) Add("economy", "structure_sold", Sev.Info, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), $"sold {ev.Key} #{ev.A}"); break;
                    case "placed":
                        if (mine) Add("production", "construction_placed", Sev.Info, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), $"{ev.Key} #{ev.A} placed", cmd: w.ById.TryGetValue(ev.A, out var pe) ? pe.CmdId : 0);
                        break;
                    case "built":
                        if (mine)
                        {
                            var s = w.ById.TryGetValue(ev.A, out var se) ? se : null;
                            Add("production", "construction_completed", Sev.Notice, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), $"{ev.Key} #{ev.A} is complete", cmd: s?.CmdId ?? 0);
                        }
                        break;
                    case "trained":
                        if (mine)
                        {
                            var u = w.ById.TryGetValue(ev.A, out var ue) ? ue : null;
                            body.Set("id", ev.A).Set("key", ev.Key).Set("spawn", P(ev.Pos));
                            if (u != null && u.Order == Order.Move) body.Set("rally", P(u.OrderPos));
                            long cmd = 0;
                            if (u != null)
                            {
                                // A unit inherits the train command's id only for the record; it isn't on any unit order.
                                foreach (var c in cmds.Values) if (c.Type == "train" && c.TrainLeft > 0 && u.CmdId == c.Id) { c.Trained++; cmd = c.Id; break; }
                                u.CmdId = 0;
                            }
                            Add("production", "unit_trained", Sev.Notice, "own", body, $"{ev.Key} #{ev.A} is ready" + (body.Contains("rally") ? " and heading for its rally point" : ""), cmd: cmd);
                        }
                        break;
                    case "research_started": case "researched":
                        if (mine) Add("production", ev.Type, ev.Type == "researched" ? Sev.Notice : Sev.Info, "own", new JObj().Set("key", ev.Key), ev.Text);
                        break;
                    case "retreating":
                        if (mine) Add("orders", "retreat_started", Sev.Warning, "own", new JObj().Set("unit", ev.A).Set("key", ev.Key).Set("to", ev.B).Set("pos", P(ev.Pos)), ev.Text, cmd: CmdOf(ev.A));
                        break;
                    case "low_fuel": case "refuelled": case "stranded": case "crashed":
                        if (mine) Add("logistics", ev.Type == "low_fuel" ? "refuel_trip" : ev.Type, ev.Type == "refuelled" ? Sev.Info : ev.Type == "low_fuel" ? Sev.Notice : Sev.Warning, "own",
                            new JObj().Set("unit", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), ev.Text ?? $"{ev.Key} #{ev.A} {ev.Type}", cmd: CmdOf(ev.A));
                        break;
                    case "boarded": case "unloaded": case "mine_laid": case "derrick_skipped": case "salvaged":
                        if (mine) Add("orders", ev.Type, Sev.Info, "own", new JObj().Set("unit", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), ev.Text, cmd: CmdOf(ev.A));
                        else if (ev.Type == "salvaged" && SeenBy(ev))
                        {
                            // Another team's thing dismantled in plain sight (it left, or was knocked out): gone for sure, so no
                            // contact or remembered structure lingers for it. Nobody killed it, so it isn't an enemy_destroyed.
                            seenDead.Add(ev.A);
                            confirmedDead.Add(ev.A);
                            if (confirmedDead.Count > 5000) confirmedDead.Clear();
                        }
                        break;
                    case "captured":
                        if (mine) Add("economy", "captured", Sev.Notice, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), ev.Text ?? $"captured {ev.Key} #{ev.A}");
                        break;
                    case "burning": case "fire_out":
                        if (mine) Add("economy", ev.Type == "burning" ? "fire" : "fire_out", ev.Type == "burning" ? Sev.Warning : Sev.Info, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), ev.Text);
                        break;
                    case "surveyed": case "drilled": case "depleted":
                        if (mine) Add("economy", ev.Type, ev.Type == "depleted" ? Sev.Warning : Sev.Notice, "own", new JObj().Set("id", ev.A).Set("key", ev.Key).Set("pos", P(ev.Pos)), ev.Text);
                        break;
                    case "low_power": case "power_restored": case "defences_offline": case "defences_online": case "unstalled":
                        if (mine) Add("economy", ev.Type == "low_power" ? "power_shortage" : ev.Type, ev.Type is "low_power" or "defences_offline" ? Sev.Warning : Sev.Notice, "own",
                            new JObj().Set("power", new List<object> { T.PowerUsed, T.PowerProduced }), ev.Text);
                        break;
                    case "derrick": case "derrick_captured": case "derrick_released":
                        if (mine || Sees(ev.Pos))
                            Add("recon", ev.Type, Sev.Info, mine ? "own" : "sight", new JObj().Set("id", ev.A).Set("pos", P(ev.Pos)).Set("owner", ev.Type == "derrick_captured" ? (object)ev.Team : null),
                                ev.Type == "derrick" ? "a neutral derrick rose" : ev.Type == "derrick_released" ? "a derrick went back to neutral" : "a derrick changed hands");
                        break;
                    case "chat":
                        if (ev.Team >= 0 && ev.Team < w.Teams.Count)
                            Add("chat", "chat", Sev.Info, "chat", new JObj().Set("from_team", ev.Team).Set("from", w.Teams[ev.Team].Name).Set("message", ev.Text).Set("trusted", false), null);
                        else Add("match", "server_message", Sev.Info, "server", new JObj().Set("message", ev.Text), null);
                        break;
                    case "joined": case "defeated": case "game_over": case "match": case "arena_cleared": case "left":
                        // Public news: who, never where (no positions: a rival's base is for scouting).
                        Add("match", ev.Type, ev.Type is "game_over" || (ev.Type == "defeated" && mine) ? Sev.Critical : Sev.Notice, "server",
                            new JObj().Set("team", ev.Team >= 0 ? (object)ev.Team : null).Set("message", ev.Text), ev.Text);
                        break;
                }
            }
            if (losses.Count > 0)
                Add("combat", "losses", losses.Any(l => l["structure"] is bool b && b) ? Sev.Critical : Sev.Warning, "own", new JObj().Set("lost", losses.Cast<object>().ToList()),
                    "lost " + Ids(losses));
            if (killed.Count > 0)
                Add("combat", "enemy_destroyed", Sev.Notice, "sight", new JObj().Set("destroyed", killed.Cast<object>().ToList()), "seen destroyed: " + Ids(killed));
        }

        long CmdOf(int id) => w.ById.TryGetValue(id, out var e) ? e.CmdId : 0;

        void MapAlerts(List<Alert> alerts)
        {
            foreach (var a in alerts)
                if (a.Team == Team)
                    Add("alert", a.Kind, Weight(a.Priority), "game", new JObj().Set("alert_seq", a.Seq).Set("label", AlertLog.Label(a.Kind)).Set("priority", a.Priority.ToString().ToLowerInvariant()).Set("pos", P(a.Pos)),
                        AlertLog.Describe(w, a));
        }

        static bool Travelling(Entity e) => e.Order is Order.Move or Order.AttackMove or Order.Refuel or Order.Drill or Order.Board or Order.Capture or Order.Survey
            || (e.Order == Order.Harvest && e.Dock == DockStep.None && Vec2.Dist(e.Pos, e.OrderPos) > 2f);

        void Units(List<Entity> own, List<object> damaged, List<(Vec2, int)> activity)
        {
            var idle = new List<JObj>(); var arrived = new List<JObj>(); var auto = new List<JObj>(); var landed = new List<JObj>(); var fuel = new List<JObj>(); var wps = new List<JObj>();
            var stuck = new List<JObj>(); var moving = new List<JObj>(); var healed = new List<JObj>(); var retreatEnd = new List<JObj>(); var covered = new List<JObj>();
            var seen = new HashSet<int>();
            var defences = known.Where(k => k.Value.team >= 0 && w.Def(k.Value.key)?.Weapon != null).Select(k => (id: k.Key, k.Value.key, at: new Vec2(k.Value.origin.X + (w.Def(k.Value.key).SizeX) / 2f, k.Value.origin.Y + w.Def(k.Value.key).SizeY / 2f), wd: w.Def(k.Value.key).Weapon)).ToList();
            var docks = new Dictionary<int, List<int>>();
            float now = Now;
            foreach (var e in own)
            {
                if (e.IsStructure || e.IsMine) continue;
                seen.Add(e.Id);
                if (!units.TryGetValue(e.Id, out var m)) { units[e.Id] = UMem(e); continue; }
                bool onCmd = e.CmdId != 0 && cmds.ContainsKey(e.CmdId);
                if (now - e.LastFiredAt <= feed.IntervalS + 0.05f) activity.Add((e.Pos, e.Id));
                if (m.Order != Order.Idle && e.Order == Order.Idle && !onCmd)
                {
                    // Reaching the end of a move it didn't get from you (a new unit's rally, stepping aside) is arriving, not stopping.
                    if (m.Order == Order.Move && !e.IsHarvester && Vec2.Dist(e.Pos, m.OrderPos) <= 2.5f) arrived.Add(U(e));
                    else idle.Add(U(e).Set("was", OrderName(m.Order)).Set("miner", e.IsHarvester));
                }
                if (m.Order == Order.Idle && e.Order != Order.Idle && !onCmd && !e.GivingWay && e.CmdId == m.Cmd)
                    auto.Add(U(e).Set("order", e.OrderName).Set("reason", e.Responding ? "responding to an attack" : e.Retreating ? "retreating" : e.Order == Order.Refuel ? "refuelling" : "automatic"));
                if (m.Retreating && !e.Retreating) retreatEnd.Add(U(e).Set("order", e.OrderName));
                if (m.Landed != e.Landed) landed.Add(U(e).Set("landed", e.Landed));
                int band = FuelBand(e);
                if (band > m.FuelBand) fuel.Add(U(e).Set("fuel_pct", StateView.Pct(e.FuelFraction)));
                m.FuelBand = band;
                if (e.Order == m.Order && (e.Order == Order.Move || e.Order == Order.AttackMove) && m.Waypoints > 0 && e.CmdId == m.Cmd && Vec2.Dist(e.OrderPos, m.OrderPos) > 0.5f)
                    wps.Add(U(e).Set("reached", P(m.OrderPos)).Set("next", P(e.OrderPos)).Set("left", e.Waypoints.Count));
                if (e.Hp < m.Hp - 0.5f)
                {
                    var d = U(e).Set("hp", new List<object> { (int)m.Hp, (int)e.Hp }).Set("max", e.Def.MaxHp).Set("lost", (float)(m.Hp - e.Hp)).Set("structure", false);
                    var by = Visible(e.LastAttackerId);
                    d.Set("by", by ?? (object)"not visible");
                    damaged.Add(d);
                    activity.Add((e.Pos, e.Id));
                }
                else if (e.Hp > m.Hp + 0.5f) healed.Add(U(e).Set("hp", new List<object> { (int)m.Hp, (int)e.Hp }).Set("max", e.Def.MaxHp));
                // Lack of progress: on the way somewhere and no closer for 6 s, not because it's fighting.
                float dist = Vec2.Dist(e.Pos, e.OrderPos);
                if (!Travelling(e) || e.Stranded || e.Landed || e.IsCarried || m.Order != e.Order || Vec2.Dist(m.OrderPos, e.OrderPos) > 0.5f)
                { m.ProgDist = dist; m.ProgAt = now; if (m.NoProgress && !Travelling(e)) m.NoProgress = false; }
                else if (dist < m.ProgDist - 1f || dist < 1.5f)
                {
                    if (m.NoProgress) moving.Add(U(e));
                    m.NoProgress = false; m.ProgDist = dist; m.ProgAt = now;
                }
                else if (!m.NoProgress && now - m.ProgAt >= 6f && now - e.LastFiredAt > 3f)
                {
                    m.NoProgress = true;
                    stuck.Add(U(e).Set("order", e.OrderName).Set("dest", P(e.OrderPos)).Set("dist", (float)Math.Round(dist, 1)).Set("for_s", (int)(now - m.ProgAt))
                        .Set("waiting_for_group", e.Group != 0 && !e.Moving));
                }
                // Into the reach of an enemy defence this team knows of (seen now, or remembered).
                bool inCov = defences.Any(dd => dd.wd.CanHit(e.Def) && Vec2.Dist(dd.at, e.Pos) <= dd.wd.Range + 1.5f);
                if (inCov && !m.InCoverage)
                {
                    var dd = defences.Where(x => x.wd.CanHit(e.Def)).OrderBy(x => Vec2.Dist(x.at, e.Pos)).First();
                    covered.Add(U(e).Set("defence", new JObj().Set("id", dd.id).Set("key", dd.key).Set("pos", P(dd.at)).Set("seen_now", w.Get(dd.id) is Entity de && w.IsVisibleTo(Team, de))));
                }
                m.InCoverage = inCov;
                if (e.Dock == DockStep.Queue) { if (!docks.TryGetValue(e.DockAt, out var l)) docks[e.DockAt] = l = new List<int>(); l.Add(e.Id); }
                m.Order = e.Order; m.OrderPos = e.OrderPos; m.Pos = e.Pos; m.Hp = e.Hp; m.Cmd = e.CmdId; m.Waypoints = e.Waypoints.Count;
                m.Retreating = e.Retreating; m.Landed = e.Landed; m.Carried = e.IsCarried;
            }
            foreach (var id in units.Keys.Where(k => !seen.Contains(k)).ToList()) units.Remove(id);
            var idleMiners = idle.Where(x => x["miner"] is bool b && b).ToList();
            var idleOther = idle.Except(idleMiners).ToList();
            if (idleOther.Count > 0) Add("orders", "unit_idle", Sev.Notice, "own", new JObj().Set("units", idleOther.Cast<object>().ToList()), "now idle (no order of yours finished): " + Ids(idleOther));
            if (idleMiners.Count > 0) Add("economy", "miner_idle", Sev.Notice, "own", new JObj().Set("units", idleMiners.Cast<object>().ToList()), "mining trucks stopped: " + Ids(idleMiners));
            if (arrived.Count > 0) Add("orders", "unit_arrived", Sev.Info, "own", new JObj().Set("units", arrived.Cast<object>().ToList()), "reached a rally point or a spot it moved to on its own, now idle: " + Ids(arrived));
            if (auto.Count > 0) Add("orders", "auto_order", Sev.Info, "own", new JObj().Set("units", auto.Cast<object>().ToList()), "acting on their own: " + string.Join(", ", auto.Take(6).Select(a => $"#{a["id"]} {a["reason"]}")));
            if (retreatEnd.Count > 0) Add("orders", "retreat_ended", Sev.Info, "own", new JObj().Set("units", retreatEnd.Cast<object>().ToList()), "back from retreat: " + Ids(retreatEnd));
            if (landed.Count > 0) Add("logistics", "landing", Sev.Info, "own", new JObj().Set("units", landed.Cast<object>().ToList()), string.Join(", ", landed.Select(x => $"#{x["id"]} {((bool)x["landed"] ? "landed" : "took off")}")));
            if (fuel.Count > 0) Add("logistics", "fuel_low", fuel.Any(f => (int)f["fuel_pct"] <= 10) ? Sev.Warning : Sev.Notice, "own", new JObj().Set("units", fuel.Cast<object>().ToList()), "fuel getting low: " + string.Join(", ", fuel.Select(f => $"#{f["id"]} {f["fuel_pct"]}%")));
            if (wps.Count > 0) Add("orders", "waypoint_reached", Sev.Info, "own", new JObj().Set("units", wps.Cast<object>().ToList()), "waypoint reached: " + Ids(wps));
            if (stuck.Count > 0) Add("orders", "no_progress", Sev.Warning, "own", new JObj().Set("units", stuck.Cast<object>().ToList()), "not getting any closer for 6s+: " + Ids(stuck));
            if (moving.Count > 0) Add("orders", "progress_resumed", Sev.Info, "own", new JObj().Set("units", moving.Cast<object>().ToList()), "moving again: " + Ids(moving));
            if (healed.Count > 0) Add("combat", "repaired", Sev.Info, "own", new JObj().Set("units", healed.Cast<object>().ToList()), "repaired/healed: " + Ids(healed));
            if (covered.Count > 0) Add("combat", "entered_defence_range", Sev.Warning, "own", new JObj().Set("units", covered.Cast<object>().ToList()), "inside the range of a known enemy defence: " + Ids(covered));
            // Delivery congestion: two or more trucks waiting for one bay.
            var jam = docks.Where(kv => kv.Value.Count >= 2).ToList();
            foreach (var kv in jam)
                if (congested.Add(kv.Key))
                    Add("economy", "delivery_congestion", Sev.Notice, "own", new JObj().Set("dropoff", kv.Key).Set("waiting", kv.Value), $"{kv.Value.Count} trucks queueing at drop-off #{kv.Key}");
            foreach (var id in congested.Where(id => !jam.Any(kv => kv.Key == id)).ToList())
            {
                congested.Remove(id);
                Add("economy", "delivery_clear", Sev.Info, "own", new JObj().Set("dropoff", id), $"no queue at drop-off #{id} any more");
            }
            bool hasPad = own.Any(e => e.IsStructure && e.IsComplete && e.Def.Key == "airfield");
            bool aircraft = own.Any(e => !e.IsStructure && e.IsAir);
            if (hadPad && !hasPad && aircraft) Add("logistics", "no_landing_pad", Sev.Warning, "own", new JObj(), "you have aircraft and no airfield to land on");
            if (!hadPad && hasPad && aircraft) Add("logistics", "landing_available", Sev.Info, "own", new JObj(), "an airfield is available for your aircraft");
            hadPad = hasPad;
        }

        static string OrderName(Order o) => o switch { Order.AttackMove => "attack_move", Order.ReturnOre => "return_ore", Order.LayMines => "lay_mines", _ => o.ToString().ToLowerInvariant() };

        void Structures(List<Entity> own, List<Entity> vis, List<object> damaged, List<(Vec2, int)> activity)
        {
            var seen = new HashSet<int>();
            var firing = new List<JObj>(); var quiet = new List<JObj>(); var repaired = new List<JObj>(); var exposed = new List<JObj>(); var safe = new List<JObj>();
            var armedFoes = vis.Where(e => e.IsArmed).ToList();
            var guards = own.Where(e => e.IsArmed && (!e.IsStructure || (e.IsComplete && !e.Offline))).ToList();
            foreach (var s in own)
            {
                if (!s.IsStructure) continue;
                seen.Add(s.Id);
                if (!structs.TryGetValue(s.Id, out var m)) { structs[s.Id] = SMem(s); continue; }
                if (s.IsComplete && m.Complete && s.Hp < m.Hp - 0.5f)
                {
                    var d = U(s).Set("hp", new List<object> { (int)m.Hp, (int)s.Hp }).Set("max", s.Def.MaxHp).Set("lost", (float)(m.Hp - s.Hp)).Set("structure", true);
                    d.Set("by", s.Burning && w.Get(s.LastAttackerId) == null ? (object)"fire" : Visible(s.LastAttackerId) ?? (object)"not visible");
                    damaged.Add(d);
                    activity.Add((s.Center, s.Id));
                }
                else if (s.IsComplete && m.Complete && s.Hp > m.Hp + 0.5f) repaired.Add(U(s).Set("hp", new List<object> { (int)m.Hp, (int)s.Hp }).Set("max", s.Def.MaxHp));
                if (s.IsArmed)
                {
                    bool fires = Now - s.LastFiredAt <= feed.IntervalS + 0.05f;
                    if (fires) activity.Add((s.Center, s.Id));
                    if (fires && !m.Firing) { m.Firing = true; firing.Add(U(s)); }
                    else if (!fires && m.Firing && Now - s.LastFiredAt > 5f) { m.Firing = false; quiet.Add(U(s)); }
                }
                // Exposed: an armed enemy in sight within 10 tiles, and nothing of yours armed within 10 tiles to answer it.
                if (s.IsComplete)
                {
                    var threat = armedFoes.FirstOrDefault(f => s.DistFrom(f.Pos) <= 10f);
                    bool isExposed = threat != null && !guards.Any(g => g != s && (g.IsStructure ? Vec2.Dist(g.Center, s.Center) : s.DistFrom(g.Pos)) <= 10f) && !(s.IsArmed && !s.Offline);
                    if (isExposed && !m.Exposed) exposed.Add(U(s).Set("threat", new JObj().Set("id", threat.Id).Set("key", threat.Def.Key).Set("pos", P(threat.Pos))));
                    else if (!isExposed && m.Exposed) safe.Add(U(s));
                    m.Exposed = isExposed;
                }
                m.Hp = s.Hp; m.Complete = s.IsComplete;
            }
            foreach (var id in structs.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                structs.Remove(id);
                var e = w.Get(id);
                if (e != null && e.Team != Team) // still standing, but not ours: captured
                    Add("economy", "structure_captured", Sev.Critical, "own", U(e).Set("by_team", e.Team), $"your {e.Def.Key} #{e.Id} was captured");
            }
            if (firing.Count > 0) Add("combat", "defence_firing", Sev.Notice, "own", new JObj().Set("defences", firing.Cast<object>().ToList()), "your defences opened fire: " + Ids(firing));
            if (quiet.Count > 0) Add("combat", "defence_quiet", Sev.Info, "own", new JObj().Set("defences", quiet.Cast<object>().ToList()), "your defences stopped firing: " + Ids(quiet));
            if (repaired.Count > 0) Add("economy", "structure_repaired", Sev.Info, "own", new JObj().Set("structures", repaired.Cast<object>().ToList()), "repairs: " + Ids(repaired));
            if (exposed.Count > 0) Add("economy", "infrastructure_exposed", Sev.Warning, "own", new JObj().Set("structures", exposed.Cast<object>().ToList()), "armed enemies in sight and nothing armed of yours nearby: " + Ids(exposed));
            if (safe.Count > 0) Add("economy", "infrastructure_covered", Sev.Info, "own", new JObj().Set("structures", safe.Cast<object>().ToList()), "no longer exposed: " + Ids(safe));
        }

        void Contacts(List<Entity> own, List<Entity> vis, HashSet<int> seenDead)
        {
            var appeared = new List<JObj>(); var moved = new List<JObj>(); var gone = new List<JObj>(); var inRange = new List<JObj>();
            var shooters = own.Where(e => e.IsArmed && (!e.IsStructure || (e.IsComplete && !e.Offline))).ToList();
            var now = new HashSet<int>();
            foreach (var e in vis)
            {
                now.Add(e.Id);
                JObj Row() => U(e).Set("team", e.Team).Set("hp_pct", StateView.Pct(e.Hp / e.Def.MaxHp)).Set("air", e.IsAir);
                if (!contacts.TryGetValue(e.Id, out var c))
                {
                    contacts[e.Id] = c = new Contact { Id = e.Id, Team = e.Team, Key = e.Def.Key, Reported = e.Pos };
                    appeared.Add(Row());
                }
                else if (!c.Visible) { appeared.Add(Row().Set("last_seen_t", (float)Math.Round(c.LastSeen, 1)).Set("last_pos", P(c.LastPos))); c.Reported = e.Pos; }
                else if (Vec2.Dist(e.Pos, c.Reported) >= 5f) { moved.Add(Row().Set("from", P(c.Reported))); c.Reported = e.Pos; }
                c.Visible = true; c.LastPos = e.Pos; c.LastSeen = Now;
                bool r = shooters.Any(s => s.Def.Weapon.CanHit(e.Def) && e.DistFrom(s.IsStructure ? s.Center : s.Pos) <= s.Def.Weapon.Range + (s.IsStructure ? s.Def.SizeX / 2f : 0));
                if (r && !c.InRange) inRange.Add(Row());
                c.InRange = r;
            }
            foreach (var c in contacts.Values.Where(c => c.Visible && !now.Contains(c.Id)).ToList())
            {
                c.Visible = false; c.InRange = false;
                if (seenDead.Contains(c.Id) || confirmedDead.Contains(c.Id)) { contacts.Remove(c.Id); continue; }
                gone.Add(new JObj().Set("id", c.Id).Set("key", c.Key).Set("team", c.Team).Set("last_pos", P(c.LastPos)).Set("last_seen_t", (float)Math.Round(c.LastSeen, 1)));
            }
            foreach (var c in contacts.Values.Where(c => !c.Visible && Now - c.LastSeen > 600f).ToList()) contacts.Remove(c.Id);
            if (appeared.Count > 0) Add("recon", "contacts_appeared", Sev.Notice, "sight", new JObj().Set("contacts", appeared.Cast<object>().ToList()), "enemies in sight: " + Ids(appeared));
            if (moved.Count > 0) Add("recon", "contacts_moved", Sev.Info, "sight", new JObj().Set("contacts", moved.Cast<object>().ToList()), "enemies moving: " + Ids(moved));
            if (gone.Count > 0) Add("recon", "contacts_lost", Sev.Info, "memory", new JObj().Set("contacts", gone.Cast<object>().ToList()), "out of sight (not seen destroyed): " + Ids(gone));
            if (inRange.Count > 0) Add("combat", "enemy_in_range", Sev.Notice, "sight", new JObj().Set("contacts", inRange.Cast<object>().ToList()), "enemies inside your weapons' range: " + Ids(inRange));
            // Radar: aircraft the dome reports but nobody can see. Where they are is what the radar shows; never what they are.
            var blips = w.RadarContacts(Team).Where(e => !w.IsVisibleTo(Team, e)).ToList();
            var newBlips = blips.Where(e => !radar.Contains(e.Id)).ToList();
            int lostBlips = radar.Count(id => !blips.Any(b => b.Id == id));
            if (newBlips.Count > 0 || lostBlips > 0)
                Add("recon", "radar_contacts", newBlips.Count > 0 ? Sev.Notice : Sev.Info, "radar", new JObj().Set("appeared", newBlips.Select(b => (object)new List<object> { (int)b.Pos.X, (int)b.Pos.Y }).ToList())
                    .Set("faded", lostBlips).Set("on_radar", blips.Count), $"radar: {newBlips.Count} new aircraft contact(s), {lostBlips} faded, {blips.Count} on radar");
            radar.Clear(); foreach (var b in blips) radar.Add(b.Id);
        }

        void Known(HashSet<int> seenDead)
        {
            var found = new List<JObj>(); var missing = new List<JObj>(); var changed = new List<JObj>();
            foreach (var kv in T.KnownEnemyStructures)
            {
                var (key, origin, team) = kv.Value;
                var row = new JObj().Set("id", kv.Key).Set("key", key).Set("team", team).Set("origin", new List<object> { origin.X, origin.Y });
                if (!known.TryGetValue(kv.Key, out var k))
                {
                    if (key is "command_center" or "outpost" or "mining_refinery" or "deep_mine" or "derrick") row.Set("expansion", true);
                    found.Add(row);
                }
                else if (k.team != team) changed.Add(row.Set("was_team", k.team));
                known[kv.Key] = (key, team, origin, Now);
            }
            foreach (var id in known.Keys.Where(id => !T.KnownEnemyStructures.ContainsKey(id)).ToList())
            {
                var k = known[id];
                known.Remove(id);
                if (seenDead.Contains(id) || confirmedDead.Contains(id)) continue; // already reported as seen destroyed
                missing.Add(new JObj().Set("id", id).Set("key", k.key).Set("team", k.team).Set("origin", new List<object> { k.origin.X, k.origin.Y }).Set("last_seen_t", (float)Math.Round(k.seen, 1)));
            }
            if (found.Count > 0) Add("recon", "structures_discovered", Sev.Notice, "sight", new JObj().Set("structures", found.Cast<object>().ToList()), "enemy structures found: " + Ids(found));
            if (missing.Count > 0) Add("recon", "structures_missing", Sev.Notice, "sight", new JObj().Set("structures", missing.Cast<object>().ToList()), "remembered structures no longer there (not seen destroyed): " + Ids(missing));
            if (changed.Count > 0) Add("recon", "structures_changed_hands", Sev.Notice, "sight", new JObj().Set("structures", changed.Cast<object>().ToList()), "changed hands: " + Ids(changed));
        }

        void Fights(List<(Vec2 pos, int id)> activity, Dictionary<int, Vec2> lost, HashSet<int> seenDead, List<Entity> vis)
        {
            foreach (var (pos, id) in activity)
            {
                var f = fights.Where(x => Vec2.Dist(x.Center, pos) <= 15f).OrderBy(x => Vec2.Dist(x.Center, pos)).FirstOrDefault();
                if (f == null)
                {
                    f = new Engagement { Id = nextFight++, Center = pos, Start = Now };
                    fights.Add(f);
                }
                f.Last = Now;
                f.Own.Add(id);
                f.Center = f.Center + (pos - f.Center) * (1f / Math.Max(2, f.Own.Count));
            }
            foreach (var f in fights)
            {
                foreach (var e in vis) if (Vec2.Dist(e.Pos, f.Center) <= 15f) f.Enemies.Add(e.Id);
                f.EnemyDestroyed += seenDead.Count(id => f.Enemies.Contains(id));
                if (f.Start == Now && f.Last == Now)
                {
                    var foes = vis.Where(e => Vec2.Dist(e.Pos, f.Center) <= 15f).ToList();
                    Add("combat", "engagement_started", Sev.Warning, "own", new JObj().Set("engagement", f.Id).Set("pos", P(f.Center)).Set("sector", StateView.Sector(f.Center))
                        .Set("yours", f.Own.Take(20).Cast<object>().ToList()).Set("enemies_seen", foes.Take(20).Select(e => (object)new JObj().Set("id", e.Id).Set("key", e.Def.Key).Set("team", e.Team)).ToList()),
                        $"fighting at {(int)f.Center.X},{(int)f.Center.Y} ({StateView.Sector(f.Center)}): {f.Own.Count} of yours, {foes.Count} enemies in sight");
                }
            }
            foreach (var kv in lost)
            {
                var near = fights.Where(x => Vec2.Dist(x.Center, kv.Value) <= 15f).OrderBy(x => Vec2.Dist(x.Center, kv.Value)).FirstOrDefault();
                if (near != null) near.OwnLost++;
            }
            foreach (var f in fights.Where(x => Now - x.Last > 8f).ToList())
            {
                fights.Remove(f);
                Add("combat", "engagement_ended", Sev.Notice, "own", new JObj().Set("engagement", f.Id).Set("pos", P(f.Center)).Set("duration_s", (int)(f.Last - f.Start + 1))
                    .Set("yours_involved", f.Own.Count).Set("yours_lost", f.OwnLost).Set("enemies_seen", f.Enemies.Count).Set("enemies_seen_destroyed", f.EnemyDestroyed),
                    $"the fight at {(int)f.Center.X},{(int)f.Center.Y} has gone quiet: lost {f.OwnLost}, saw {f.EnemyDestroyed} enemies destroyed");
            }
        }

        bool StillOn(CmdTrack c, Entity e)
        {
            if (e.Order == Order.Refuel && c.Type != "refuel") return true; // a fuel stop, then it resumes
            switch (c.Type)
            {
                case "move": return e.Order == Order.Move && !(c.Loop && c.Moved);
                case "attack_move": return (e.Order == Order.AttackMove || e.Order == Order.Attack) && !(c.Loop && c.Moved);
                case "attack": return e.Order == Order.Attack || e.Order == Order.AttackMove;
                case "refuel": return e.Order == Order.Refuel;
                case "load": return e.Order == Order.Board && !e.IsCarried;
                case "capture": return e.Order == Order.Capture;
                case "repair": case "heal": return e.Order == Order.Repair;
                case "survey": case "prospect": return e.Order == Order.Survey;
                case "drill": return e.Order == Order.Drill;
                case "lay_mines": return e.Order == Order.LayMines;
                case "harvest":
                    return (e.Order == Order.Harvest || e.Order == Order.ReturnOre) && !(e.Order == Order.Harvest && e.HarvestTile.HasValue && Vec2.Dist(e.Pos, e.HarvestTile.Value.Center) < 1.6f);
                case "stop": return false;
                default: return e.Order != Order.Idle;
            }
        }

        string Outcome(CmdTrack c, Entity e, out float shortBy)
        {
            shortBy = 0;
            if (e.IsCarried) return "boarded";
            if (c.Loop) return "patrolling";
            if (e.Retreating) return "retreated";
            if (e.Order != Order.Idle)
                return c.Type == "harvest" && e.Order == Order.Harvest ? "mining" : "diverted:" + e.OrderName;
            switch (c.Type)
            {
                case "move":
                case "attack_move":
                    if (!c.Dest.HasValue) return "arrived";
                    shortBy = Vec2.Dist(e.Pos, c.Dest.Value);
                    return shortBy <= c.Tolerance ? "arrived" : "stopped_short";
                case "attack":
                    {
                        var t = w.Get(c.Target);
                        if (t == null) return confirmedDead.Contains(c.Target) ? "target_destroyed" : "target_lost";
                        return w.IsVisibleTo(Team, t) ? "stopped" : "target_lost";
                    }
                case "stop": return "stopped";
                case "harvest": return "idle_no_ore";
                default: return "done";
            }
        }

        void Commands(Dictionary<int, Vec2> lost, HashSet<int> seenDead)
        {
            foreach (var c in cmds.Values.ToList())
            {
                bool done = false;
                if (c.Units.Count > 0)
                {
                    var finished = new List<JObj>();
                    foreach (var u in c.Units)
                    {
                        if (c.Outcome.ContainsKey(u)) continue;
                        var e = w.Get(u);
                        string outcome = null; float shortBy = 0;
                        if (e == null) outcome = lost.ContainsKey(u) || !w.ById.ContainsKey(u) ? "destroyed" : "gone";
                        else if (e.CmdId != c.Id) outcome = "replaced_by_cmd_" + e.CmdId;
                        else if (!StillOn(c, e)) outcome = Outcome(c, e, out shortBy);
                        if (outcome == null) continue;
                        c.Outcome[u] = outcome;
                        var row = new JObj().Set("id", u).Set("key", e?.Def.Key ?? (w.ById.TryGetValue(u, out var de) ? de.Def.Key : null)).Set("outcome", outcome);
                        if (e != null) row.Set("pos", P(e.Pos));
                        if (outcome == "stopped_short") row.Set("short_by", (float)Math.Round(shortBy, 1));
                        finished.Add(row);
                    }
                    // Movement actually started (accepted isn't proof of it).
                    if (!c.Moved)
                    {
                        var movers = c.Units.Select(w.Get).Where(e => e != null && e.CmdId == c.Id && (e.Moving || (c.Start.TryGetValue(e.Id, out var s0) && Vec2.Dist(s0, e.Pos) > 0.3f))).ToList();
                        if (movers.Count > 0)
                        {
                            c.Moved = true;
                            int at = c.MovedTick >= 0 ? c.MovedTick : w.Tick;
                            Add("orders", "movement_started", Sev.Info, "own", new JObj().Set("units", movers.Select(e => (object)e.Id).ToList()).Set("at_t", (float)Math.Round(at * World.Dt, 2)).Set("after_s", (float)Math.Round((at - c.IssuedTick) * World.Dt, 2)),
                                $"cmd {c.Id}: {movers.Count} unit(s) on the move", cmd: c.Id);
                        }
                    }
                    if (finished.Count > 0)
                    {
                        bool bad = finished.Any(f => f["outcome"] is string s && (s == "stopped_short" || s == "destroyed" || s == "target_lost" || s == "idle_no_ore" || s.StartsWith("diverted")));
                        Add("orders", "units_finished", bad ? Sev.Warning : Sev.Notice, "own", new JObj().Set("units", finished.Cast<object>().ToList()).Set("remaining", c.Units.Count - c.Outcome.Count),
                            $"cmd {c.Id} ({c.Type}): " + string.Join(", ", finished.Take(8).Select(f => $"#{f["id"]} {f["outcome"]}")), cmd: c.Id);
                    }
                    if (c.Type is "move" or "attack_move") Formation(c);
                    done = c.Outcome.Count >= c.Units.Count;
                    if (done)
                    {
                        var counts = new JObj();
                        foreach (var g in c.Outcome.Values.GroupBy(v => v.StartsWith("replaced_by") ? "replaced" : v)) counts.Set(g.Key, g.Count());
                        var idle = c.Units.Select(w.Get).Where(e => e != null && e.Order == Order.Idle).Select(e => (object)e.Id).ToList();
                        Add("orders", "command_completed", Sev.Notice, "own", new JObj().Set("type", c.Type).Set("duration_s", (float)Math.Round((w.Tick - c.IssuedTick) * World.Dt, 1))
                            .Set("outcomes", counts).Set("units", c.Units.Cast<object>().ToList()).Set("now_idle", idle),
                            $"cmd {c.Id} ({c.Type}) finished: " + string.Join(", ", counts.Keys.Cast<string>().Select(k => $"{counts[k]} {k}")) + (idle.Count > 0 ? $"; {idle.Count} now idle" : ""), cmd: c.Id);
                    }
                }
                else if (c.Type == "train")
                {
                    int left = T.UnitQueues.Values.SelectMany(q => q).Count(p => p.CmdId == c.Id);
                    if (left == 0)
                    {
                        done = true;
                        Add("production", "command_completed", Sev.Notice, "own", new JObj().Set("type", c.Type).Set("duration_s", (float)Math.Round((w.Tick - c.IssuedTick) * World.Dt, 1))
                            .Set("outcomes", new JObj().Set("trained", c.Trained).Set("cancelled", Math.Max(0, c.TrainLeft - c.Trained))), $"cmd {c.Id}: trained {c.Trained} of {c.TrainLeft}", cmd: c.Id);
                    }
                }
                else if (c.Type == "build")
                {
                    var s = w.Get(c.Structure);
                    if (s == null || s.IsComplete)
                    {
                        done = true;
                        Add("production", "command_completed", s == null ? Sev.Warning : Sev.Notice, "own", new JObj().Set("type", c.Type).Set("duration_s", (float)Math.Round((w.Tick - c.IssuedTick) * World.Dt, 1))
                            .Set("outcomes", new JObj().Set(s == null ? "lost_or_cancelled" : "built", 1)).Set("structure", c.Structure), $"cmd {c.Id}: {(s == null ? "the foundation is gone" : "built")}", cmd: c.Id);
                    }
                }
                else done = true;
                if (done) cmds.Remove(c.Id);
            }
        }

        /// <summary>A group moving on one command coming apart, and support (repair trucks, medics) falling behind it.</summary>
        void Formation(CmdTrack c)
        {
            var g = c.Units.Select(w.Get).Where(e => e != null && e.CmdId == c.Id && !e.IsAir && !e.IsCarried && !c.Outcome.ContainsKey(e.Id)).ToList();
            if (g.Count < 2) return;
            var mid = new Vec2(g.Average(e => e.Pos.X), g.Average(e => e.Pos.Y));
            float spread = g.Max(e => Vec2.Dist(e.Pos, mid));
            float limit = 10f + 2f * MathF.Sqrt(g.Count);
            if (!c.Split && spread > limit)
            {
                c.Split = true;
                var lag = g.OrderByDescending(e => Vec2.Dist(e.Pos, mid)).Where(e => Vec2.Dist(e.Pos, mid) > limit * 0.6f).Take(10)
                    .Select(e => (object)U(e).Set("behind_by", (float)Math.Round(Vec2.Dist(e.Pos, mid), 1)).Set("support", e.Def.RepairRate > 0)).ToList();
                bool support = lag.Cast<JObj>().Any(x => x["support"] is bool b && b);
                Add("orders", support ? "support_behind" : "formation_split", Sev.Warning, "own", new JObj().Set("spread", (float)Math.Round(spread, 1)).Set("centre", P(mid)).Set("stragglers", lag),
                    $"cmd {c.Id}: the group is strung out over {spread:0} tiles" + (support ? " and its support is behind" : ""), cmd: c.Id);
            }
            else if (c.Split && spread < limit * 0.6f)
            {
                c.Split = false;
                Add("orders", "formation_regrouped", Sev.Info, "own", new JObj().Set("spread", (float)Math.Round(spread, 1)), $"cmd {c.Id}: the group is together again", cmd: c.Id);
            }
        }

        static string ProducerName(Producer p) => p switch { Producer.CommandCenter => "command_center", Producer.Barracks => "barracks", Producer.Factory => "factory", Producer.Airfield => "airfield", _ => "none" };

        void Production()
        {
            var t = T;
            var queues = new List<(string name, List<ProdItem> q, bool units, string blockedBy)>();
            bool hq = w.HasComplete(Team, "command_center");
            queues.Add(("construction", t.StructureQueue, false, hq ? null : "needs a completed command_center"));
            // A unit queue is stuck only when no building of its kind is left to work on it.
            foreach (var kv in t.UnitQueues)
                queues.Add((ProducerName(kv.Key), kv.Value, true, kv.Value.Count > 0 && w.FirstProducer(Team, kv.Key) == null ? $"needs a completed {ProducerName(kv.Key)}" : null));
            var progress = new List<JObj>();
            var active = new HashSet<ProdItem>();
            float rate = t.LowPower ? 0.5f : 1f;
            foreach (var (name, q, units, why) in queues)
            {
                var head = q.Count > 0 ? q[0] : null;
                heads.TryGetValue(name, out var was);
                if (head != was)
                {
                    // Units announce themselves below, each as a building takes it up; structures go one at a time, so the head is the one.
                    if (head != null && !units)
                    {
                        var def = w.Def(head.Key);
                        float eta = def == null ? 0 : (def.BuildTime - (w.Get(head.StructureId)?.BuildProgress ?? 0) * def.BuildTime) / rate;
                        Add("production", "construction_started", Sev.Info, "own",
                            new JObj().Set("producer", name).Set("key", head.Key).Set("eta_s", (int)Math.Ceiling(eta)).Set("queued", q.Count), $"{name}: started {head.Key} (~{eta:0}s, {q.Count} queued)", cmd: head.CmdId);
                    }
                    else if (head == null) Add("production", "queue_empty", Sev.Notice, "own", new JObj().Set("producer", name), $"{name} queue is empty");
                    heads[name] = head;
                }
                if (why != null && head != null) { if (blocked.Add(name)) Add("production", "queue_blocked", Sev.Warning, "own", new JObj().Set("producer", name).Set("key", head.Key).Set("reason", why), $"{name} queue is stuck: {why}"); }
                else if (blocked.Remove(name)) Add("production", "queue_unblocked", Sev.Info, "own", new JObj().Set("producer", name), $"{name} queue is moving again");
                // Everything being worked on: each completed building of a kind trains one unit at once.
                foreach (var item in units ? q.Where(p => p.WorkerId != 0 && w.Get(p.WorkerId) != null) : q.Take(1))
                {
                    active.Add(item);
                    var def = w.Def(item.Key);
                    if (units && (!working.TryGetValue(item, out var by) || by != item.WorkerId))
                    {
                        working[item] = item.WorkerId; // a building took it up (a different one, if the first was lost)
                        float eta = def == null ? 0 : (def.BuildTime - item.Progress) / rate;
                        Add("production", "training_started", Sev.Info, "own",
                            new JObj().Set("producer", name).Set("building", item.WorkerId).Set("key", item.Key).Set("eta_s", (int)Math.Ceiling(eta)).Set("queued", q.Count),
                            $"{name} #{item.WorkerId}: started {item.Key} (~{eta:0}s, {q.Count} queued)", cmd: item.CmdId);
                    }
                    if (def != null && def.BuildTime >= 20)
                    {
                        float pct = units ? item.Progress / def.BuildTime : (w.Get(item.StructureId)?.BuildProgress ?? 0);
                        int quarter = Math.Min(3, (int)(pct * 4));
                        milestones.TryGetValue(item, out var had);
                        if (quarter > had) { milestones[item] = quarter; progress.Add(new JObj().Set("producer", name).Set("key", item.Key).Set("pct", quarter * 25)); }
                    }
                }
            }
            foreach (var p in working.Keys.Where(p => !active.Contains(p)).ToList()) working.Remove(p);
            foreach (var p in milestones.Keys.Where(p => !queues.Any(q => q.q.Contains(p))).ToList()) milestones.Remove(p);
            if (progress.Count > 0) Add("production", "production_progress", Sev.Info, "own", new JObj().Set("items", progress.Cast<object>().ToList()), string.Join(", ", progress.Select(p => $"{p["key"]} {p["pct"]}%")));
        }

        void Economy(List<Entity> own)
        {
            var t = T;
            if (t.PowerUsed != power0 || t.PowerProduced != power1)
            {
                Add("economy", "power_changed", t.LowPower ? Sev.Warning : Sev.Info, "own", new JObj().Set("before", new List<object> { power0, power1 }).Set("after", new List<object> { t.PowerUsed, t.PowerProduced }),
                    $"power {power0}/{power1} -> {t.PowerUsed}/{t.PowerProduced} (used/produced)");
                power0 = t.PowerUsed; power1 = t.PowerProduced;
            }
            if (Now - lastRates < 5f) return;
            lastRates = Now;
            var changed = new JObj();
            foreach (var item in Defs.Items)
            {
                float r = t.Rates.TryGetValue(item, out var v) ? v : 0, was = rates.TryGetValue(item, out var p) ? p : 0;
                bool flip = Math.Sign(r) != Math.Sign(was) && (Math.Abs(r) > 0.1f || Math.Abs(was) > 0.1f);
                if (!flip && Math.Abs(r - was) < Math.Max(0.5f, 0.3f * Math.Abs(was))) continue;
                changed.Set(item, new JObj().Set("rate", new List<object> { (float)Math.Round(was, 2), (float)Math.Round(r, 2) }).Set("stock", t.Amount(item)));
                rates[item] = r;
            }
            if (changed.Count > 0)
                Add("economy", "rates_changed", Sev.Info, "own", new JObj().Set("items", changed),
                    "rates: " + string.Join(", ", changed.Keys.Cast<string>().Select(k => { var l = (List<object>)((JObj)changed[k])["rate"]; return $"{k} {l[0]}->{l[1]}/s"; })));
        }

        /// <summary>Resources this team found: ore fields in ground it has explored, and mining zones its surveyors flagged.</summary>
        void Resources()
        {
            var found = new List<JObj>();
            foreach (var f in StateView.OreFields(w, T.Explored))
            {
                if (fields.Any(k => k.type == f.type && Math.Abs(k.x - f.cx) + Math.Abs(k.y - f.cy) <= 8)) continue;
                fields.Add((f.cx, f.cy, f.type));
                found.Add(new JObj().Set("ore", f.type).Set("pos", new List<object> { f.cx, f.cy }).Set("tiles", f.tiles).Set("amount", f.total));
            }
            if (found.Count > 0) Add("recon", "ore_fields_discovered", Sev.Notice, "sight", new JObj().Set("fields", found.Cast<object>().ToList()), "ore found: " + string.Join(", ", found.Select(f => $"{f["ore"]} at {((List<object>)f["pos"])[0]},{((List<object>)f["pos"])[1]}")));
            var zs = T.Zones.Keys.Where(z => zones.Add(z)).ToList();
            if (zs.Count > 0)
                Add("recon", "zones_flagged", Sev.Notice, "own", new JObj().Set("zones", zs.Select(z => { var d = w.Map.DepositById(z); return (object)new JObj().Set("zone", z).Set("ore", d != null ? Defs.Ores[d.Type] : null).Set("pos", d != null ? P(d.Pos) : null); }).ToList()),
                    $"{zs.Count} new mining zone(s) flagged");
        }

        void Heartbeat(List<Entity> own, List<Entity> vis)
        {
            lastHeartbeat = Now;
            var t = T;
            var us = own.Where(e => !e.IsStructure && !e.IsMine).ToList();
            var idle = us.Where(e => e.Order == Order.Idle && !e.IsCarried).Select(e => e.Id).ToList();
            var stock = new JObj();
            foreach (var item in Defs.Items) { int n = t.Amount(item); if (n > 0) stock.Set(item, n); }
            var rateObj = new JObj();
            foreach (var item in Defs.Items) if (t.Rates.TryGetValue(item, out var r) && Math.Abs(r) >= 0.05f) rateObj.Set(item, (float)Math.Round(r, 2));
            var building = t.StructureQueue.Take(3).Select(p => (object)new JObj().Set("key", p.Key).Set("pct", StateView.Pct(w.Get(p.StructureId)?.BuildProgress ?? 0))).ToList();
            // One entry per unit being worked on (each completed building trains one), or the head of a queue nothing is working on.
            var training = t.UnitQueues.Where(kv => kv.Value.Count > 0).SelectMany(kv =>
            {
                var now = t.Working(kv.Key);
                if (now.Count == 0) now.Add(kv.Value[0]);
                return now.Select(p =>
                {
                    var d = w.Def(p.Key);
                    return (object)new JObj().Set("producer", ProducerName(kv.Key)).Set("building", p.WorkerId != 0 ? (object)p.WorkerId : null).Set("key", p.Key)
                        .Set("pct", d != null && d.BuildTime > 0 ? StateView.Pct(p.Progress / d.BuildTime) : 0).Set("queued", kv.Value.Count);
                });
            }).ToList();
            Add("heartbeat", "heartbeat", Sev.Info, "own", new JObj()
                .Set("units", us.Count).Set("idle", idle.Take(15).Cast<object>().ToList()).Set("idle_n", idle.Count)
                .Set("moving_n", us.Count(e => e.Moving)).Set("firing_n", us.Count(e => Now - e.LastFiredAt < 3f))
                .Set("structures", own.Count(e => e.IsStructure)).Set("building", building).Set("training", training)
                .Set("stock", stock).Set("rates", rateObj).Set("power", new List<object> { t.PowerUsed, t.PowerProduced })
                .Set("contacts", vis.Count).Set("remembered", contacts.Values.Count(c => !c.Visible)).Set("engagements", fights.Count)
                .Set("open_cmds", cmds.Keys.OrderBy(k => k).Take(10).Cast<object>().ToList()).Set("stranded", us.Where(e => e.Stranded).Select(e => (object)e.Id).ToList())
                .Set("orders_version", t.OrdersVersion).Set("plan_version", t.PlanVersion).Set("rules_version", StateView.RulesVersion), null);
        }

        /// <summary>
        /// A fresh picture to resume from after a gap: your own forces in full, enemies you see now, what you remember
        /// (with when you last saw it), queues, stockpile, power, your human's orders and the commands still being tracked.
        /// </summary>
        public JObj Snapshot()
        {
            var t = T;
            var own = w.Entities.Where(e => !e.Dead && e.Team == Team && !e.IsMine).ToList();
            var vis = w.Entities.Where(e => !e.Dead && e.Team != Team && e.Team >= 0 && !e.IsStructure && w.IsVisibleTo(Team, e)).ToList();
            return new JObj().Set("cursor", Head).Set("stream", Stream).Set("t", (float)Math.Round(w.Time, 2))
                .Set("units", own.Where(e => !e.IsStructure).Select(e => (object)U(e).Set("order", e.OrderName).Set("moving", e.Moving).Set("hp_pct", StateView.Pct(e.Hp / e.Def.MaxHp))
                    .Set("cmd", e.CmdId != 0 && cmds.ContainsKey(e.CmdId) ? (object)e.CmdId : null).Set("fuel_pct", e.Def.UsesFuel ? (object)StateView.Pct(e.FuelFraction) : null)).ToList())
                .Set("structures", own.Where(e => e.IsStructure).Select(e => (object)U(e).Set("hp_pct", StateView.Pct(e.Hp / e.Def.MaxHp)).Set("complete_pct", StateView.Pct(e.BuildProgress))).ToList())
                .Set("contacts", vis.Select(e => (object)U(e).Set("team", e.Team).Set("hp_pct", StateView.Pct(e.Hp / e.Def.MaxHp))).ToList())
                .Set("remembered_contacts", contacts.Values.Where(c => !c.Visible).Select(c => (object)new JObj().Set("id", c.Id).Set("key", c.Key).Set("team", c.Team)
                    .Set("last_pos", P(c.LastPos)).Set("last_seen_t", (float)Math.Round(c.LastSeen, 1))).ToList())
                .Set("known_structures", t.KnownEnemyStructures.Select(kv => (object)new JObj().Set("id", kv.Key).Set("key", kv.Value.key).Set("team", kv.Value.team)
                    .Set("origin", new List<object> { kv.Value.origin.X, kv.Value.origin.Y }).Set("in_sight", w.Get(kv.Key) is Entity ks && w.IsVisibleTo(Team, ks))).ToList())
                .Set("queues", new JObj().Set("construction", t.StructureQueue.Select(p => (object)p.Key).ToList())
                    .Set("units", t.UnitQueues.Where(kv => kv.Value.Count > 0).ToDictionary(kv => ProducerName(kv.Key), kv => (object)kv.Value.Select(p => (object)p.Key).ToList())))
                .Set("stockpile", StateView.Stockpile(t)).Set("power", new List<object> { t.PowerUsed, t.PowerProduced })
                .Set("policies", new JObj().Set("miners", t.Miners.View()))
                .Set("squads", Squads.View(w, Team)).Set("objectives", Objectives.View(w, Team))
                .Set("standing_orders", new JObj().Set("version", t.OrdersVersion).Set("text", t.StandingOrders).Set("src", "human"))
                .Set("strategy", StrategyRecords.View(t)).Set("plan_version", t.PlanVersion)
                .Set("open_cmds", cmds.Values.Select(c => (object)new JObj().Set("cmd", c.Id).Set("type", c.Type).Set("units", c.Units.Where(u => !c.Outcome.ContainsKey(u)).Cast<object>().ToList())).ToList())
                .Set("match", StateView.MatchJson(w));
        }
    }
}
