using System;
using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesOllama
{
    public class NavStep
    {
        public int X, Y;
        public int Kind;      // 0 walk, 1 jump, 2 drop (walk off an edge), 3 vertical jump
        public int Dir;       // -1 / 0 / +1
        public bool Crouch;   // the cell can only be entered crouching
    }

    public class NavEdge
    {
        public int To, Kind, Dir;
        public float Cost;
        public bool Crouch;
    }

    public class NavResult
    {
        public List<NavStep> Path = new List<NavStep>();
        public bool Found, Partial;
        public int Expanded;
        public float[] Cost;
        public int[] Prev;
        public NavEdge[] PrevEdge;
        public NavGrid Grid;
    }

    /// <summary>
    /// A small platformer path finder over a local grid of solid cells: walking, stepping up, jumping over gaps / onto ledges,
    /// dropping off edges and crawling through 1-block tunnels. Jump arcs are simulated with the real jump speed and gravity.
    /// </summary>
    public class NavGrid
    {
        public int W, H, OX, OY;
        bool[,] solid;
        public int BodyH = 2;
        public float JumpV = 10f, Speed = 7f, G = 25f, HalfW = 0.32f, Height = 1.8f;

        public static NavGrid Build(Vector2 center, Body b, int rx = 28, int ry = 16)
        {
            var g = new NavGrid();
            g.OX = Mathf.FloorToInt(center.x) - rx;
            g.OY = Mathf.FloorToInt(center.y) - ry;
            g.W = 2 * rx + 1; g.H = 2 * ry + 1;
            g.solid = new bool[g.W, g.H];
            int mask = LayerMask.GetMask("Ground");
            for (int x = 0; x < g.W; x++)
                for (int y = 0; y < g.H; y++)
                    g.solid[x, y] = Physics2D.OverlapPoint(new Vector2(g.OX + x + 0.5f, g.OY + y + 0.5f), mask) != null;
            try
            {
                g.JumpV = Mathf.Max(4f, b.actualJumpSpeed);
                g.Speed = Mathf.Max(3f, b.maxSpeed);
                float grav = Mathf.Abs(Physics2D.gravity.y) * (b.rb != null ? Mathf.Max(0.5f, b.rb.gravityScale) : 1f);
                if (grav > 1f) g.G = grav;
                if (b.col != null)
                {
                    g.Height = Mathf.Clamp(b.col.size.y, 1.2f, 2.6f);
                    g.HalfW = Mathf.Clamp(b.col.size.x * 0.5f, 0.2f, 0.6f);
                }
            }
            catch { }
            g.BodyH = Mathf.Max(1, Mathf.CeilToInt(g.Height - 0.05f));
            return g;
        }

        public float MaxJumpHeight { get { return JumpV * JumpV / (2f * G); } }

        public int Id(int x, int y)
        {
            int cx = x - OX, cy = y - OY;
            if (cx < 0 || cy < 0 || cx >= W || cy >= H) return -1;
            return cx + cy * W;
        }

        public bool Solid(int x, int y)
        {
            int cx = x - OX, cy = y - OY;
            if (cx < 0 || cx >= W || cy < 0 || cy >= H) return true;
            return solid[cx, cy];
        }

        public bool Fits(int x, int y, int h)
        {
            for (int i = 0; i < h; i++) if (Solid(x, y + i)) return false;
            return true;
        }

        /// <summary>A cell the body can stand (or crouch) in.</summary>
        public bool Stand(int x, int y, out bool crouchOnly)
        {
            crouchOnly = false;
            if (!Solid(x, y - 1)) return false;
            if (Fits(x, y, BodyH)) return true;
            if (Fits(x, y, 1)) { crouchOnly = true; return true; }
            return false;
        }

        public bool Stand(int x, int y) { bool c; return Stand(x, y, out c); }

        bool Hit(float px, float feet)
        {
            int x0 = Mathf.FloorToInt(px - HalfW), x1 = Mathf.FloorToInt(px + HalfW);
            int y0 = Mathf.FloorToInt(feet + 0.02f), y1 = Mathf.FloorToInt(feet + Height - 0.02f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (Solid(x, y)) return true;
            return false;
        }

        bool Sim(float startX, int sy, int dir, float vx, float vy0, out int lx, out int ly, out float t)
        {
            lx = ly = 0; t = 0f;
            float px = startX, py = sy, vy = vy0;
            const float dt = 0.05f;
            for (int i = 0; i < 70; i++)
            {
                t += dt;
                float nx = px + dir * vx * dt;
                if (!Hit(nx, py)) px = nx;
                vy -= G * dt;
                float ny = py + vy * dt;
                if (vy > 0f)
                {
                    if (Hit(px, ny)) vy = 0f; else py = ny;
                }
                else
                {
                    if (Hit(px, ny))
                    {
                        float snapped = Mathf.Floor(ny) + 1f;
                        if (Hit(px, snapped)) return false;
                        py = snapped;
                        int ly0 = Mathf.FloorToInt(py + 0.01f);
                        int[] cols = { Mathf.FloorToInt(px), Mathf.FloorToInt(px - HalfW), Mathf.FloorToInt(px + HalfW) };
                        foreach (int c in cols) { if (Stand(c, ly0)) { lx = c; ly = ly0; return true; } }
                        return false;
                    }
                    py = ny;
                }
                if (py < OY + 1 || px < OX + 1 || px > OX + W - 1) return false;
            }
            return false;
        }

        void AddEdge(List<NavEdge> list, int x, int y, int kind, int dir, float cost, bool crouch)
        {
            int to = Id(x, y);
            if (to < 0) return;
            list.Add(new NavEdge { To = to, Kind = kind, Dir = dir, Cost = cost, Crouch = crouch });
        }

        List<NavEdge> Edges(int x, int y)
        {
            var list = new List<NavEdge>();
            bool curCrouch;
            if (!Stand(x, y, out curCrouch)) return list;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int nx = x + dir;
                bool c;
                if (Stand(nx, y, out c)) AddEdge(list, nx, y, 0, dir, c ? 1.6f : 1f, c);
                else if (!curCrouch && Stand(nx, y + 1, out c) && !c && Fits(x, y + BodyH, 1)) AddEdge(list, nx, y + 1, 1, dir, 2.3f, false);
                else if (Stand(nx, y - 1, out c) && Fits(nx, y, BodyH)) AddEdge(list, nx, y - 1, 0, dir, 1.3f, c);
            }
            if (curCrouch) return list;

            // jumps (full speed and slow variants) and a vertical jump
            float[] speeds = { Speed * 0.85f, Speed * 0.45f };
            for (int dir = -1; dir <= 1; dir += 2)
                foreach (float vx in speeds)
                {
                    int lx, ly; float t;
                    if (Sim(x + 0.5f, y, dir, vx, JumpV, out lx, out ly, out t) && (lx != x || ly != y))
                        AddEdge(list, lx, ly, 1, dir, 2.2f + t * 2.5f, false);
                }
            {
                int lx, ly; float t;
                if (Sim(x + 0.5f, y, 0, 0f, JumpV, out lx, out ly, out t) && (lx != x || ly != y))
                    AddEdge(list, lx, ly, 3, 0, 2.2f + t * 2.5f, false);
            }
            // drop off an edge
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int nx = x + dir;
                if (!Solid(nx, y - 1) && Fits(nx, y, BodyH))
                {
                    int lx, ly; float t;
                    if (Sim(x + 0.5f + dir * 0.9f, y, dir, Speed * 0.5f, 0f, out lx, out ly, out t))
                        AddEdge(list, lx, ly, 2, dir, 1.5f + t * 1.2f + Mathf.Max(0, y - ly) * 0.35f, false);
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ search
        class Heap
        {
            readonly List<KeyValuePair<float, int>> a = new List<KeyValuePair<float, int>>();
            public int Count { get { return a.Count; } }
            public void Push(float k, int v)
            {
                a.Add(new KeyValuePair<float, int>(k, v));
                int i = a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (a[p].Key <= a[i].Key) break;
                    var tmp = a[p]; a[p] = a[i]; a[i] = tmp; i = p;
                }
            }
            public KeyValuePair<float, int> Pop()
            {
                var top = a[0];
                var last = a[a.Count - 1]; a.RemoveAt(a.Count - 1);
                if (a.Count > 0)
                {
                    a[0] = last; int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < a.Count && a[l].Key < a[m].Key) m = l;
                        if (r < a.Count && a[r].Key < a[m].Key) m = r;
                        if (m == i) break;
                        var tmp = a[m]; a[m] = a[i]; a[i] = tmp; i = m;
                    }
                }
                return top;
            }
        }

        public int NodeX(int id) { return id % W + OX; }
        public int NodeY(int id) { return id / W + OY; }

        /// <summary>Finds the standing cell at/below a position (the start of a search), or false.</summary>
        public bool FindStart(float x, float feetY, out int sx, out int sy)
        {
            sx = Mathf.FloorToInt(x); sy = Mathf.FloorToInt(feetY + 0.15f);
            int[] offs = { 0, -1, 1 };
            foreach (int ox in offs)
                for (int d = 0; d <= 12; d++)
                    if (Stand(sx + ox, sy - d)) { sx = sx + ox; sy = sy - d; return true; }
            return false;
        }

        /// <summary>A* (goal given) or Dijkstra (goal null) from a standing cell.</summary>
        public NavResult Search(int sx, int sy, Func<int, int, bool> goal, float gx, float gy, float maxCost)
        {
            var res = new NavResult { Grid = this };
            int n = W * H;
            var cost = new float[n]; var prev = new int[n]; var pe = new NavEdge[n]; var closed = new bool[n];
            for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; prev[i] = -1; }
            res.Cost = cost; res.Prev = prev; res.PrevEdge = pe;
            int s = Id(sx, sy);
            if (s < 0) return res;
            cost[s] = 0f;
            var heap = new Heap();
            heap.Push(0f, s);
            int best = s;
            float bestH = goal != null ? Heur(sx, sy, gx, gy) : 0f;
            int goalNode = -1;
            while (heap.Count > 0 && res.Expanded < 3500)
            {
                var top = heap.Pop();
                int u = top.Value;
                if (closed[u]) continue;
                closed[u] = true; res.Expanded++;
                int ux = NodeX(u), uy = NodeY(u);
                if (goal != null)
                {
                    if (goal(ux, uy)) { goalNode = u; break; }
                    float hh = Heur(ux, uy, gx, gy);
                    if (hh < bestH) { bestH = hh; best = u; }
                }
                foreach (var e in Edges(ux, uy))
                {
                    float nc = cost[u] + e.Cost;
                    if (nc > maxCost || nc >= cost[e.To]) continue;
                    cost[e.To] = nc; prev[e.To] = u; pe[e.To] = e;
                    heap.Push(nc + (goal != null ? Heur(NodeX(e.To), NodeY(e.To), gx, gy) : 0f), e.To);
                }
            }
            if (goal != null)
            {
                if (goalNode >= 0) { res.Found = true; res.Path = BuildPath(res, goalNode); }
                else if (best != s) { res.Partial = true; res.Path = BuildPath(res, best); }
            }
            return res;
        }

        static float Heur(int x, int y, float gx, float gy) { return (Mathf.Abs(x + 0.5f - gx) + Mathf.Abs(y - gy) * 1.4f) * 0.9f; }

        public List<NavStep> BuildPath(NavResult r, int node)
        {
            var rev = new List<NavStep>();
            int cur = node;
            while (cur >= 0)
            {
                var e = r.PrevEdge[cur];
                rev.Add(new NavStep { X = NodeX(cur), Y = NodeY(cur), Kind = e != null ? e.Kind : 0, Dir = e != null ? e.Dir : 0, Crouch = e != null && e.Crouch });
                cur = r.Prev[cur];
            }
            rev.Reverse();
            return rev;
        }
    }
}
