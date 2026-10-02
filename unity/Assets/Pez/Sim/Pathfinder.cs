using System;
using System.Collections.Generic;

namespace Pez.Sim
{
    /// <summary>8-directional A* over the tile grid. Structures and rough terrain block; units do not.</summary>
    public class Pathfinder
    {
        readonly Map map;
        readonly float[] g;
        readonly int[] parent;
        readonly int[] stamp;
        int curStamp;
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };
        const float Diag = 1.41421356f;

        public Pathfinder(Map map)
        {
            this.map = map;
            g = new float[map.W * map.H];
            parent = new int[map.W * map.H];
            stamp = new int[map.W * map.H];
        }

        /// <summary>Nearest passable tile to target (spiral search).</summary>
        public Int2 NearestPassable(Int2 t, int maxR = 12)
        {
            if (map.Passable(t.X, t.Y)) return t;
            for (int r = 1; r <= maxR; r++)
            {
                Int2? best = null; float bd = float.MaxValue;
                for (int y = t.Y - r; y <= t.Y + r; y++)
                    for (int x = t.X - r; x <= t.X + r; x++)
                    {
                        if (Math.Abs(x - t.X) != r && Math.Abs(y - t.Y) != r) continue;
                        if (!map.Passable(x, y)) continue;
                        float d = (x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y);
                        if (d < bd) { bd = d; best = new Int2(x, y); }
                    }
                if (best.HasValue) return best.Value;
            }
            return t;
        }

        public List<Vec2> Find(Vec2 from, Vec2 to, int maxExpand = 8000)
        {
            var s = Int2.Of(from);
            var goal = NearestPassable(Int2.Of(to));
            if (!map.InBounds(s.X, s.Y)) return null;
            curStamp++;
            int si = map.Idx(s.X, s.Y), gi = map.Idx(goal.X, goal.Y);
            var open = new PriorityQueue();
            g[si] = 0; parent[si] = -1; stamp[si] = curStamp;
            open.Push(si, H(s, goal));
            int expanded = 0, bestIdx = si; float bestH = H(s, goal);
            var closed = new HashSet<int>();
            while (open.Count > 0 && expanded < maxExpand)
            {
                int cur = open.Pop();
                if (cur == gi) { bestIdx = gi; break; }
                if (!closed.Add(cur)) continue;
                expanded++;
                int cx = cur % map.W, cy = cur / map.W;
                float h = H(new Int2(cx, cy), goal);
                if (h < bestH) { bestH = h; bestIdx = cur; }
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DX[d], ny = cy + DY[d];
                    if (!map.Passable(nx, ny)) continue;
                    if (d >= 4 && (!map.Passable(cx + DX[d], cy) || !map.Passable(cx, cy + DY[d]))) continue; // no corner cutting
                    int ni = map.Idx(nx, ny);
                    float ng = g[cur] + (d >= 4 ? Diag : 1f);
                    if (stamp[ni] == curStamp && ng >= g[ni]) continue;
                    stamp[ni] = curStamp; g[ni] = ng; parent[ni] = cur;
                    open.Push(ni, ng + H(new Int2(nx, ny), goal));
                }
            }
            // Reconstruct to goal, or to the closest point reached if unreachable.
            var tiles = new List<int>();
            for (int i = bestIdx; i != -1; i = parent[i]) tiles.Add(i);
            tiles.Reverse();
            var path = Smooth(tiles);
            if (bestIdx == gi && Int2.Of(to).Equals(goal)) path[path.Count - 1] = to;
            return path;
        }

        List<Vec2> Smooth(List<int> tiles)
        {
            var pts = new List<Vec2>();
            if (tiles.Count == 0) return pts;
            int anchor = 0;
            for (int i = 2; i < tiles.Count; i++)
            {
                if (!LineClear(tiles[anchor], tiles[i]))
                {
                    pts.Add(Center(tiles[i - 1]));
                    anchor = i - 1;
                }
            }
            pts.Add(Center(tiles[tiles.Count - 1]));
            return pts;
        }

        Vec2 Center(int i) => new Vec2(i % map.W + 0.5f, i / map.W + 0.5f);

        bool LineClear(int a, int b)
        {
            var pa = Center(a); var pb = Center(b);
            float len = Vec2.Dist(pa, pb);
            int steps = (int)(len * 3) + 1;
            for (int k = 1; k < steps; k++)
            {
                var p = Vec2.Lerp(pa, pb, k / (float)steps);
                // Check a small cross so units don't clip structure corners.
                for (int o = 0; o < 4; o++)
                {
                    float ox = o == 0 ? 0.3f : o == 1 ? -0.3f : 0, oy = o == 2 ? 0.3f : o == 3 ? -0.3f : 0;
                    var t = Int2.Of(new Vec2(p.X + ox, p.Y + oy));
                    if (!map.Passable(t.X, t.Y)) return false;
                }
            }
            return true;
        }

        static float H(Int2 a, Int2 b)
        {
            int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
            return Math.Max(dx, dy) + (Diag - 1f) * Math.Min(dx, dy);
        }

        /// <summary>Binary min-heap keyed by float priority.</summary>
        class PriorityQueue
        {
            readonly List<(int item, float pri)> heap = new List<(int, float)>();
            public int Count => heap.Count;
            public void Push(int item, float pri)
            {
                heap.Add((item, pri));
                int i = heap.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (heap[p].pri <= heap[i].pri) break;
                    (heap[p], heap[i]) = (heap[i], heap[p]);
                    i = p;
                }
            }
            public int Pop()
            {
                var top = heap[0].item;
                heap[0] = heap[heap.Count - 1];
                heap.RemoveAt(heap.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < heap.Count && heap[l].pri < heap[m].pri) m = l;
                    if (r < heap.Count && heap[r].pri < heap[m].pri) m = r;
                    if (m == i) break;
                    (heap[m], heap[i]) = (heap[i], heap[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
