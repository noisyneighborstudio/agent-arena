using System;
using System.Collections.Generic;

namespace Pez.Sim
{
    /// <summary>8-directional A* over the tile grid. Structures and rough terrain block; units do not.</summary>
    public class Pathfinder
    {
        readonly Map map;
        readonly int[] g;
        readonly int[] parent;
        readonly int[] stamp;
        int curStamp;
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };
        // Costs are whole numbers (a step, and a diagonal step: the square root of 2 to a thousandth), so two routes of the same shape cost
        // exactly the same however they were added up, and equally good ones can be told apart by rules that don't depend on the scan.
        const int Straight = 1000, Diag = 1414;

        public Pathfinder(Map map)
        {
            this.map = map;
            g = new int[map.W * map.H];
            parent = new int[map.W * map.H];
            stamp = new int[map.W * map.H];
        }

        /// <summary>
        /// Nearest passable tile to target (spiral search). Equally near tiles: the one nearer the middle of the map, then the one on the
        /// right as the target faces it, not the first the scan meets (the lowest y, then x), which pushed every blocked spot toward the
        /// south-west corner, away from one seat's base and toward the middle for the other's.
        /// </summary>
        public Int2 NearestPassable(Int2 t, int maxR = 12)
        {
            if (map.Passable(t.X, t.Y)) return t;
            var centre = new Vec2(map.W / 2f, map.H / 2f);
            var face = (centre - t.Center).Normalized;
            var right = new Vec2(face.Y, -face.X);
            for (int r = 1; r <= maxR; r++)
            {
                Int2? best = null; float bd = float.MaxValue, bc = 0, br = 0;
                for (int y = t.Y - r; y <= t.Y + r; y++)
                    for (int x = t.X - r; x <= t.X + r; x++)
                    {
                        if (Math.Abs(x - t.X) != r && Math.Abs(y - t.Y) != r) continue;
                        if (!map.Passable(x, y)) continue;
                        float d = (x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y);
                        if (d > bd) continue;
                        var c = new Int2(x, y).Center;
                        float toCentre = Vec2.DistSq(c, centre), toRight = (c.X - t.Center.X) * right.X + (c.Y - t.Center.Y) * right.Y;
                        if (best == null || d < bd || toCentre < bc - 1e-3f || (toCentre <= bc + 1e-3f && toRight > br + 1e-3f)) { bd = d; bc = toCentre; br = toRight; best = new Int2(x, y); }
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
            // Equally good nodes are told apart by where they lie against the straight line from the start to the goal (see Node.Before),
            // which turns with the problem: a route turned half a circle is searched in the same order, and found the same, turned.
            int ux = goal.X - s.X, uy = goal.Y - s.Y;
            Node Make(int idx, int cost)
            {
                int x = idx % map.W, y = idx / map.W, h = H(new Int2(x, y), goal), cross = ux * (y - s.Y) - uy * (x - s.X), along = ux * (x - s.X) + uy * (y - s.Y);
                return new Node { item = idx, f = cost + h, h = h, absCross = Math.Abs(cross), cross = cross, along = along };
            }
            g[si] = 0; parent[si] = -1; stamp[si] = curStamp;
            open.Push(Make(si, 0));
            int expanded = 0, bestIdx = si, bestH = H(s, goal);
            var closed = new HashSet<int>();
            while (open.Count > 0 && expanded < maxExpand)
            {
                int cur = open.Pop();
                if (cur == gi) { bestIdx = gi; break; }
                if (!closed.Add(cur)) continue;
                expanded++;
                int cx = cur % map.W, cy = cur / map.W;
                int h = H(new Int2(cx, cy), goal);
                if (h < bestH) { bestH = h; bestIdx = cur; }
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DX[d], ny = cy + DY[d];
                    if (!map.Passable(nx, ny)) continue;
                    if (d >= 4 && (!map.Passable(cx + DX[d], cy) || !map.Passable(cx, cy + DY[d]))) continue; // no corner cutting
                    int ni = map.Idx(nx, ny);
                    int ng = g[cur] + (d >= 4 ? Diag : Straight);
                    if (stamp[ni] == curStamp && ng >= g[ni]) continue;
                    stamp[ni] = curStamp; g[ni] = ng; parent[ni] = cur;
                    open.Push(Make(ni, ng));
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

        static int H(Int2 a, Int2 b)
        {
            int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
            return Straight * Math.Max(dx, dy) + (Diag - Straight) * Math.Min(dx, dy);
        }

        /// <summary>An open node: its tile, its estimated cost f, and what orders equally costly ones (see Before).</summary>
        struct Node
        {
            public int item, f, h, absCross, cross, along;

            /// <summary>
            /// Cheaper first. Equal in cost: the one nearer the goal, then the one nearer the straight line from the start to the goal, then
            /// the one to the right of that line as the way goes (cross is negative there), then the one farther along it, then the lower
            /// tile number (which only a tile lying on the same spot could still tie on). All but the last are the same for a problem
            /// turned round, so the first routes found are too; the scan order (the tile number) alone isn't, and sent the seat at one end
            /// of the map down the left of a gap and the seat at the other down the right.
            /// </summary>
            public bool Before(Node o)
            {
                if (f != o.f) return f < o.f;
                if (h != o.h) return h < o.h;
                if (absCross != o.absCross) return absCross < o.absCross;
                if (cross != o.cross) return cross < o.cross;
                if (along != o.along) return along > o.along;
                return item < o.item;
            }
        }

        /// <summary>Binary min-heap of open nodes.</summary>
        class PriorityQueue
        {
            readonly List<Node> heap = new List<Node>();
            public int Count => heap.Count;
            public void Push(Node n)
            {
                heap.Add(n);
                int i = heap.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (!heap[i].Before(heap[p])) break;
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
                    if (l < heap.Count && heap[l].Before(heap[m])) m = l;
                    if (r < heap.Count && heap[r].Before(heap[m])) m = r;
                    if (m == i) break;
                    (heap[m], heap[i]) = (heap[i], heap[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
