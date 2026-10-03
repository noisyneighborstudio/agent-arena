using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The public spectator camera's director: where to look, how close, and how to get there.
    ///
    /// It reads the sim's combat events (shots, muzzle fire, hits, kills) as "heat": each is a weighted point that cools
    /// with a half-life of a few seconds (kills weigh most, buildings by footprint). Every half second it clusters the
    /// heat (the densest spot within a fight-sized radius) and frames the hottest cluster, zoomed to its spread. A shot is
    /// held for a few seconds before a hotter fight elsewhere can take over, and a fight that drifts is followed rather
    /// than cut away from. When nothing is happening it tours the bases (each team's command center) for about 17 s each.
    ///
    /// Moves are eased (critically damped pans and zooms); a target far across the map is reached with a soft cut (a
    /// quick dip to dark and back) instead of a long blurry pan. Fog never applies: this camera sees the whole map, and
    /// the gateway serves its frames 45 s late.
    /// </summary>
    public class SpectatorDirector
    {
        struct Heat { public Vector2 P; public float T, W; }

        const float HalfLife = 6f;        // seconds of game time for heat to halve
        const float MaxAge = 30f;         // heat older than this is forgotten
        const float ClusterRadius = 11f;  // tiles: what counts as one fight
        const float ActionScore = 5f;     // decayed heat a spot needs to be worth showing
        const float MinHold = 5f;         // seconds a shot is held before a hotter one may take over
        const float TakeOver = 1.6f;      // how much hotter that one must be
        const float TourDwell = 17f;      // seconds on each base when nothing is happening
        const float CutDistance = 42f;    // tiles: farther than this, cut (softly) instead of panning
        public const float MinSize = 7f, MaxSize = 22f;

        readonly List<Heat> heat = new List<Heat>(1024);
        long lastSeq = -1;
        float nextThink;

        // Where the camera is (smoothed) and where it's heading.
        public Vector3 Focus;
        public float Size = RtsCamera.BoardOrthoSize, Yaw = 45f;
        Vector3 target, focusVel;
        float targetSize = RtsCamera.BoardOrthoSize, sizeVel, yawPhase;
        bool placed;

        // The current shot.
        public enum Shot { None, Action, Tour }
        public Shot Mode { get; private set; } = Shot.None;
        float shotSince, shotScore;
        int tourIndex = -1, tourEntity;
        public string Describe => Mode == Shot.Action ? $"action score={shotScore:F1}" : Mode == Shot.Tour ? $"tour base #{tourEntity}" : "none";

        // Soft cut: 0 = clear picture, 1 = fully dipped. The camera jumps at the bottom of the dip.
        public float Dip { get; private set; }
        int cutPhase; // 0 none, 1 dipping out, 2 coming back
        Vector3 cutTo;
        float cutSize;

        /// <summary>Read combat events since the last call into the heat list.</summary>
        public void Ingest(World w)
        {
            var evs = w.Events;
            float now = w.Time;
            int start = evs.Count - 1;
            while (start >= 0 && evs[start].Seq > lastSeq) start--;
            for (int i = start + 1; i < evs.Count; i++)
            {
                var ev = evs[i];
                lastSeq = ev.Seq;
                float t = ev.Tick * World.Dt;
                if (now - t > MaxAge) continue;
                float wgt;
                switch (ev.Type)
                {
                    case "shot": wgt = ev.Key == "artillery" || ev.Key == "heavy_cannon" || ev.Key == "beam" || ev.Key == "c4" ? 2f : 1f; break;
                    case "fire": wgt = 1f; break;
                    case "hit": wgt = 1.5f; break;
                    case "destroyed":
                        {
                            // Only a kill counts: a sale, or a defeated team's units being cleared away, happens with no
                            // fighting nearby and leaves nothing to look at.
                            var at = new Vector2(ev.Pos.X, ev.Pos.Y);
                            bool fought = false;
                            for (int j = heat.Count - 1; j >= 0 && t - heat[j].T < 6f; j--)
                                if ((heat[j].P - at).sqrMagnitude < 64f) { fought = true; break; }
                            if (!fought) continue;
                            var d = Defs.Get(ev.Key ?? "");
                            wgt = d != null && d.IsStructure ? 12f + 4f * d.SizeX * d.SizeY : ev.Key == "mine" ? 0f : 6f;
                            break;
                        }
                    default: continue;
                }
                if (wgt <= 0f) continue;
                // A shot frames the exchange: halfway between the shooter and its target.
                var p = ev.Type == "shot" && (ev.Pos2.X != 0f || ev.Pos2.Y != 0f) ? new Vector2((ev.Pos.X + ev.Pos2.X) / 2f, (ev.Pos.Y + ev.Pos2.Y) / 2f) : new Vector2(ev.Pos.X, ev.Pos.Y);
                heat.Add(new Heat { P = p, T = t, W = wgt });
            }
            // Forget the cold; keep the list bounded in a long battle (the oldest go first).
            int drop = 0;
            while (drop < heat.Count && now - heat[drop].T > MaxAge) drop++;
            if (heat.Count - drop > 3000) drop = heat.Count - 3000;
            if (drop > 0) heat.RemoveRange(0, drop);
        }

        float Weight(in Heat h, float now) => h.W * Mathf.Pow(0.5f, Mathf.Max(0f, now - h.T) / HalfLife);

        /// <summary>The decayed heat within the cluster radius of p, its weighted centre, and how spread out it is.</summary>
        (float score, Vector2 centre, float spread) Around(Vector2 p, float now)
        {
            float sum = 0; Vector2 c = Vector2.zero;
            float r2 = ClusterRadius * ClusterRadius;
            foreach (var h in heat)
            {
                if ((h.P - p).sqrMagnitude > r2) continue;
                float k = Weight(h, now); sum += k; c += h.P * k;
            }
            if (sum <= 0f) return (0f, p, 0f);
            c /= sum;
            float v = 0;
            foreach (var h in heat)
            {
                if ((h.P - p).sqrMagnitude > r2) continue;
                v += Weight(h, now) * (h.P - c).sqrMagnitude;
            }
            return (sum, c, Mathf.Sqrt(v / sum));
        }

        /// <summary>The hottest cluster on the map: heat binned to 4-tile cells, then the densest cell's neighbourhood.</summary>
        (float score, Vector2 centre, float spread) Hottest(float now)
        {
            var cells = new Dictionary<(int, int), (float w, Vector2 sum)>();
            foreach (var h in heat)
            {
                float k = Weight(h, now);
                if (k < 0.05f) continue;
                var key = (Mathf.FloorToInt(h.P.x / 4f), Mathf.FloorToInt(h.P.y / 4f));
                cells.TryGetValue(key, out var c);
                cells[key] = (c.w + k, c.sum + h.P * k);
            }
            var best = (score: 0f, centre: Vector2.zero, spread: 0f);
            // Score every warm cell by the heat around it (cells are few: a handful of fights at most).
            foreach (var kv in cells)
            {
                if (kv.Value.w < 0.5f) continue;
                var r = Around(kv.Value.sum / kv.Value.w, now);
                if (r.score > best.score) best = r;
            }
            return best;
        }

        /// <summary>The bases to tour: each live team's command center (or, failing that, any of its buildings).</summary>
        static List<Entity> Bases(World w)
        {
            var list = new List<Entity>();
            for (int t = 0; t < w.Teams.Count; t++)
            {
                if (w.Teams[t].Left) continue;
                Entity cc = null, any = null;
                foreach (var e in w.Entities)
                {
                    if (e.Dead || e.Team != t || !e.IsStructure) continue;
                    if (e.Def.Key == "command_center") { cc = e; break; }
                    any ??= e;
                }
                if (cc != null || any != null) list.Add(cc ?? any);
            }
            return list;
        }

        /// <summary>Pick the shot (a few times a second) and move the camera toward it by dt seconds of real time.</summary>
        public void Step(World w, float dt, float aspect, float pitch)
        {
            float now = w.Time, real = Time.unscaledTime;
            if (real >= nextThink)
            {
                nextThink = real + 0.5f;
                Ingest(w);
                Think(w, now, real);
            }
            Move(w, dt, aspect, pitch);
        }

        void Think(World w, float now, float real)
        {
            var hot = Hottest(now);
            float held = real - shotSince;
            if (Mode == Shot.Action)
            {
                // Follow the fight we're on as it drifts; how hot is it still?
                var here = Around(new Vector2(target.x, target.z), now);
                shotScore = here.score;
                bool sameFight = hot.score > 0f && (hot.centre - new Vector2(target.x, target.z)).magnitude < ClusterRadius;
                if (sameFight) { Aim(hot.centre, hot.spread); shotScore = hot.score; return; }
                if (here.score >= ActionScore * 0.4f) Aim(here.centre, here.spread);
                if (held < MinHold) return;
                if (hot.score >= ActionScore && hot.score > TakeOver * here.score) { StartAction(hot, real); return; }
                if (here.score < ActionScore * 0.4f) { if (hot.score >= ActionScore) StartAction(hot, real); else StartTour(w, real, false); }
                return;
            }
            if (hot.score >= ActionScore && (Mode != Shot.Tour || held >= 2f)) { StartAction(hot, real); return; }
            if (Mode != Shot.Tour || held >= TourDwell) { StartTour(w, real, Mode == Shot.Tour); return; }
            // Touring: keep framing the same base (it may have been destroyed: move on).
            var e = w.Get(tourEntity);
            if (e == null || e.Dead) StartTour(w, real, true);
        }

        void StartAction((float score, Vector2 centre, float spread) hot, float real)
        {
            Mode = Shot.Action; shotSince = real; shotScore = hot.score;
            Aim(hot.centre, hot.spread);
            Debug.Log($"PEZSPECTATOR action at ({hot.centre.x:F0},{hot.centre.y:F0}) score={hot.score:F1} spread={hot.spread:F1} size={targetSize:F1}");
        }

        void Aim(Vector2 centre, float spread)
        {
            target = new Vector3(centre.x, 0f, centre.y);
            // Wide enough for the fight's spread, close enough to read it.
            targetSize = Mathf.Clamp(5.5f + spread * 1.25f, MinSize, MaxSize - 6f);
        }

        void StartTour(World w, float real, bool next)
        {
            var bases = Bases(w);
            Mode = Shot.Tour; shotSince = real; shotScore = 0f;
            if (bases.Count == 0)
            {
                // Nobody has a base yet: the whole map, from above its middle.
                target = new Vector3(w.Map.W / 2f, 0, w.Map.H / 2f); targetSize = MaxSize; tourEntity = 0;
                return;
            }
            // Next base in order (or the one we were on, if it's still there and we're just resuming the tour).
            int cur = bases.FindIndex(b => b.Id == tourEntity);
            tourIndex = next || cur < 0 ? (System.Math.Max(cur, tourIndex) + 1) % bases.Count : cur;
            var e = bases[tourIndex];
            tourEntity = e.Id;
            target = WorldView.W(e.Center);
            targetSize = RtsCamera.BoardOrthoSize * 1.25f;
            Debug.Log($"PEZSPECTATOR tour team {e.Team} {e.Def.Key} #{e.Id} at ({e.Center.X:F0},{e.Center.Y:F0})");
        }

        void Move(World w, float dt, float aspect, float pitch)
        {
            dt = Mathf.Clamp(dt, 0.001f, 0.5f);
            var bounds = new Vector2(w.Map.W, w.Map.H);
            // A slow sway of the view's heading keeps a held shot alive without spinning anyone round.
            yawPhase += dt;
            float yaw = 45f + 10f * Mathf.Sin(yawPhase * 2f * Mathf.PI / 70f);
            var goal = RtsCamera.KeepOverMap(target, targetSize, aspect, yaw, pitch, bounds);
            if (!placed) { Focus = goal; Size = targetSize; Yaw = yaw; placed = true; return; }

            // A far target: dip out, jump, come back, rather than smear across the map.
            if (cutPhase == 0 && (goal - Focus).magnitude > CutDistance) { cutPhase = 1; }
            if (cutPhase == 1)
            {
                Dip = Mathf.MoveTowards(Dip, 1f, dt / 0.35f);
                if (Dip >= 1f)
                {
                    Focus = goal; Size = targetSize; focusVel = Vector3.zero; sizeVel = 0f;
                    cutPhase = 2;
                }
                Yaw = yaw;
                return;
            }
            if (cutPhase == 2) { Dip = Mathf.MoveTowards(Dip, 0f, dt / 0.6f); if (Dip <= 0f) cutPhase = 0; }

            // Eased moves: critically damped, a little quicker on action than on the tour.
            float smooth = Mode == Shot.Action ? 1.4f : 2.4f;
            Focus = Vector3.SmoothDamp(Focus, goal, ref focusVel, smooth, Mathf.Infinity, dt);
            Size = Mathf.SmoothDamp(Size, targetSize, ref sizeVel, smooth * 1.3f, Mathf.Infinity, dt);
            Yaw = yaw;
            Focus = RtsCamera.KeepOverMap(Focus, Size, aspect, Yaw, pitch, bounds);
        }
    }
}
