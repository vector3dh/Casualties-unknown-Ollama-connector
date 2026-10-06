using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>Extra senses for v2.1: world objects, bags, worn items, liquids, line of sight, compact terrain vision.</summary>
    public static class SenserExtra
    {
        public static bool Los(Vector2 a, Vector2 b)
        {
            return !Physics2D.Linecast(a, b, LayerMask.GetMask("Ground"));
        }

        static float Ray(Vector2 o, Vector2 d, float max)
        {
            RaycastHit2D h = Physics2D.Raycast(o, d, max, LayerMask.GetMask("Ground"));
            return h.collider != null ? h.distance : -1f;
        }

        // ------------------------------------------------------------------ liquids
        public static string Liquid(Body b)
        {
            try
            {
                var pos = WorldGeneration.world.WorldToBlockPos(b.limbs[0].transform.position);
                var n = FluidManager.main.LiquidName(pos);
                string name = Senser.Strip(n.Item1);
                if (name.Length > 0) return name + " (liquid)";
            }
            catch { }
            return "liquid";
        }

        static string LiquidAt(Vector2Int pos)
        {
            try
            {
                var n = FluidManager.main.LiquidName(pos);
                return Senser.Strip(n.Item1);
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ vitals extras
        public static string VitalsExtra(Body b)
        {
            var sb = new StringBuilder();
            try
            {
                if (b.fibrillationProgress > 1f) sb.Append(" | HEART RHYTHM IRREGULAR (fibrillation " + Mathf.RoundToInt(b.fibrillationProgress) + ") - needs a defibrillator on the chest limb");
                if (b.inCardiacArrest) sb.Append(" | CARDIAC ARREST");
            }
            catch { }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ worn items and things attached to limbs
        public static void Worn(StringBuilder sb, Body b, WorldIndex idx)
        {
            idx.Worn = new List<Item>();
            try
            {
                var worn = b.GetAllWearables();
                if (worn != null) foreach (var it in worn) if (it != null) idx.Worn.Add(it);
            }
            catch { }
            if (idx.Worn.Count > 0)
            {
                var parts = new List<string>();
                for (int i = 0; i < idx.Worn.Count; i++)
                {
                    Item it = idx.Worn[i];
                    Limb l = it.transform.parent != null ? it.transform.parent.GetComponent<Limb>() : null;
                    parts.Add("W" + (i + 1) + " " + Senser.ItemName(it) + (l != null ? " on " + l.name : ""));
                }
                sb.AppendLine("WEARING (take off with remove target W#): " + string.Join("; ", parts.ToArray()));
            }
            var att = new List<string>();
            if (b.limbs != null)
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null) continue;
                    try
                    {
                        SplintLimb sp = l.GetComponent<SplintLimb>();
                        if (sp != null) att.Add("splint on L" + i + " " + l.name + " (" + Mathf.RoundToInt(sp.condition * 100f) + "%)");
                        TourniquetScript tq = l.GetComponent<TourniquetScript>();
                        if (tq != null) att.Add("tourniquet on L" + i + " " + l.name);
                    }
                    catch { }
                }
            if (att.Count > 0) sb.AppendLine("ATTACHED TO YOUR LIMBS (take off with remove limb L#): " + string.Join("; ", att.ToArray()));
        }

        // ------------------------------------------------------------------ bags
        public static void Bags(StringBuilder sb, Body b, WorldIndex idx, SenseSettings s)
        {
            if (!s.Inventory) return;
            var lines = new List<string>();
            Action<string, Item> add = (label, it) =>
            {
                if (it == null) return;
                Container c = it.GetComponent<Container>();
                if (c == null) return;
                var names = new List<string>();
                int k = 0;
                foreach (Transform t in c.transform)
                {
                    Item ci = t.GetComponent<Item>();
                    if (ci == null) continue;
                    names.Add("#" + k + " " + Senser.ItemName(ci));
                    k++;
                }
                lines.Add("  " + label + " " + Senser.ItemLabel(it) + " (weight " + c.GetHoldingWeight().ToString("0.0") + "/" + c.maxWeight.ToString("0.0") + "): " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "empty"));
            };
            try
            {
                if (b.slots != null) for (int i = 0; i < b.slots.Length; i++) if (b.HoldingItem(i)) add("slot " + i, b.GetItem(i));
                for (int i = 0; i < idx.Worn.Count; i++) add("W" + (i + 1), idx.Worn[i]);
                for (int i = 0; i < idx.Items.Count; i++) add("I" + (i + 1), idx.Items[i]);
            }
            catch { }
            if (lines.Count > 0)
            {
                sb.AppendLine("BAGS / CONTAINERS (store: item slot + container slot2 or target; take: container slot or target, slot2 = content #):");
                foreach (var l in lines) sb.AppendLine(l);
            }
        }

        // ------------------------------------------------------------------ world objects (crates, buttons, plants, trees...)
        public static void WorldObjects(StringBuilder sb, Body b, AiSettings cfg, WorldIndex idx)
        {
            idx.Objects = new List<Component>();
            if (!cfg.Sense.Objects) return;
            try
            {
                Vector2 p = b.transform.position;
                float R = Mathf.Min(cfg.ItemRadius + 4f, 22f);
                var excl = new HashSet<GameObject>();
                foreach (var g in idx.Creatures) if (g != null) excl.Add(g);
                foreach (var g in idx.Hazards) if (g != null) excl.Add(g);
                var seen = new HashSet<GameObject>();
                var found = new List<KeyValuePair<float, Component>>();

                foreach (var uo in UnityEngine.Object.FindObjectsOfType<UsableObject>())
                {
                    if (uo == null) continue;
                    GameObject go = uo.gameObject;
                    if (!go.activeInHierarchy || excl.Contains(go) || go.GetComponent<Item>() != null || !seen.Add(go)) continue;
                    float d = Vector2.Distance(p, go.transform.position);
                    if (d <= R) found.Add(new KeyValuePair<float, Component>(d, uo));
                }
                int extras = 0;
                var bes = UnityEngine.Object.FindObjectsOfType<BuildingEntity>().Where(x => x != null).OrderBy(x => Vector2.Distance(p, x.transform.position));
                foreach (var be in bes)
                {
                    if (extras >= 5) break;
                    GameObject go = be.gameObject;
                    if (be.animal || !go.activeInHierarchy || excl.Contains(go) || seen.Contains(go) || go.GetComponent<Item>() != null) continue;
                    if (string.IsNullOrEmpty(be.fullName)) continue;
                    float d = Vector2.Distance(p, go.transform.position);
                    if (d > 10f) break;
                    seen.Add(go);
                    found.Add(new KeyValuePair<float, Component>(d, be));
                    extras++;
                }
                var top = found.OrderBy(x => x.Key).Take(10).ToList();
                foreach (var kv in top) idx.Objects.Add(kv.Value);
                if (top.Count == 0) { sb.AppendLine("WORLD OBJECTS: none nearby."); return; }

                sb.AppendLine("WORLD OBJECTS (plants, crates, buttons, trees...; interact = use it, attack = damage/harvest it):");
                for (int i = 0; i < top.Count; i++)
                {
                    Component c = top[i].Value;
                    GameObject go = c.gameObject;
                    BuildingEntity be = go.GetComponent<BuildingEntity>();
                    UsableObject uo = go.GetComponent<UsableObject>();
                    string name = (be != null && !string.IsNullOrEmpty(be.fullName)) ? Senser.Strip(be.fullName) : go.name.Replace("(Clone)", "").Trim();
                    Vector2 op = go.transform.position;
                    string line = "  O" + (i + 1) + " " + name + (be != null ? " (hp " + Mathf.RoundToInt(be.health) + ")" : "")
                        + " dx=" + (op.x - p.x).ToString("+0.0;-0.0;0.0") + " dy=" + (op.y - p.y).ToString("+0.0;-0.0;0.0") + " dist=" + top[i].Key.ToString("0.0");
                    if (uo != null)
                    {
                        string tg = Senser.Strip(uo.toggleString);
                        line += " USABLE" + (tg.Length > 0 ? " (" + tg + ")" : "") + (top[i].Key < 10f * uo.rangeMultiplier ? " in range" : " too far");
                    }
                    else line += " not usable (attack it if you want to damage/harvest it)";
                    line += Los(p + Vector2.up * 0.5f, op) ? " LOS clear" : " LOS blocked";
                    sb.AppendLine(line);
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("WorldObjects: " + e.Message); }
        }

        public static void Carry(StringBuilder sb, Body b)
        {
            if (!MP.Present) return;
            string s = MPX.Info(b);
            if (s.Length > 0) sb.Append(s);
        }

        public static string PlayerNoteText(string name)
        {
            try
            {
                var mem = Plugin.Instance != null ? Plugin.Instance.Mem : null;
                if (mem == null) return "";
                var notes = mem.NotesFor(name);
                if (notes.Count == 0) return "";
                return "\n      your notes about " + name + ": " + string.Join("; ", notes.Take(4).ToArray());
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ compact terrain vision
        static string ScanFloor(NavGrid g, int sx, int sy, int dir)
        {
            int flat = 0;
            bool low = false;
            for (int i = 1; i <= 24; i++)
            {
                int x = sx + dir * i;
                bool crouch;
                if (g.Stand(x, sy, out crouch)) { flat = i; if (crouch) low = true; continue; }
                if (g.Solid(x, sy))
                {
                    int h = 0;
                    while (g.Solid(x, sy + h) && h < 14) h++;
                    string how = h <= 1 ? "a 1-block step" : (h <= Mathf.FloorToInt(g.MaxJumpHeight) ? "a wall " + h + " high (jumpable)" : "a wall " + h + " high (too high to jump: use mount / wall-jump / go around)");
                    return "flat " + flat + " blocks" + (low ? " (low tunnel, crouch)" : "") + ", then " + how;
                }
                if (g.Stand(x, sy - 1)) { flat = i; sy = sy - 1; continue; }
                int d = 0;
                while (!g.Solid(x, sy - 1 - d) && d < 16) d++;
                int w = 0;
                for (; w <= 14; w++) if (g.Stand(x + dir * w, sy)) break;
                string gap = w <= 14 ? "gap " + w + " wide (leap across)" : "no ground on the other side";
                return "flat " + flat + " blocks" + (low ? " (low tunnel, crouch)" : "") + ", then " + (d >= 16 ? "a very deep drop" : "a drop of " + d + " blocks") + ", " + gap;
            }
            return "open ground for 24+ blocks" + (low ? " (low tunnel, crouch)" : "");
        }

        public static void CompactVision(StringBuilder sb, Body b, AiSettings cfg, WorldIndex idx, List<GameObject> hz, List<GameObject> cr)
        {
            try
            {
                Vector2 p = b.transform.position;
                Bounds bb = b.col != null ? b.col.bounds : new Bounds(p, new Vector3(0.6f, 1.6f, 0f));
                sb.AppendLine("VISION (compact terrain; 1 block = 1 unit):");

                string[] names = { "right", "up-right", "up", "up-left", "left", "down-left", "down", "down-right" };
                var parts = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * 45f * Mathf.Deg2Rad;
                    float dist = Ray(bb.center, new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)), 12f);
                    parts.Add(names[i] + " " + (dist < 0f ? "clear" : dist.ToString("0.0")));
                }
                sb.AppendLine("  RAYS to the first solid (max 12): " + string.Join(", ", parts.ToArray()));

                NavGrid g = NavGrid.Build(p, b, 22, 13);
                int sx, sy;
                if (g.FindStart(p.x, bb.min.y, out sx, out sy))
                {
                    sb.AppendLine("  LEFT: " + ScanFloor(g, sx, sy, -1));
                    sb.AppendLine("  RIGHT: " + ScanFloor(g, sx, sy, 1));
                    var res = g.Search(sx, sy, null, 0f, 0f, 42f);
                    var buckets = new Dictionary<string, int>();
                    for (int id = 0; id < res.Cost.Length; id++)
                    {
                        if (res.Cost[id] >= float.MaxValue || id == g.Id(sx, sy)) continue;
                        int dx = g.NodeX(id) - sx, dy = g.NodeY(id) - sy;
                        if (Mathf.Abs(dy) < 2 && Mathf.Abs(dx) < 6) continue;
                        string key = (dx / 3) + ":" + (dy / 2);
                        int cur;
                        if (!buckets.TryGetValue(key, out cur) || res.Cost[id] < res.Cost[cur]) buckets[key] = id;
                    }
                    var spots = buckets.Values.OrderBy(id => res.Cost[id]).Take(7).ToList();
                    if (spots.Count > 0)
                    {
                        var txt = new List<string>();
                        foreach (int id in spots)
                        {
                            int dx = g.NodeX(id) - sx, dy = g.NodeY(id) - sy;
                            var e = res.PrevEdge[id];
                            string how = e == null ? "walk" : (e.Kind == 0 ? (e.Crouch ? "crawl" : "walk") : (e.Kind == 2 ? "drop" : "jump"));
                            txt.Add("dx" + (dx >= 0 ? "+" : "") + dx + " dy" + (dy >= 0 ? "+" : "") + dy + " (" + how + ")");
                        }
                        sb.AppendLine("  YOU CAN REACH (by walking/jumping/dropping): " + string.Join("; ", txt.ToArray()));
                    }
                    int reach = 0;
                    for (int id = 0; id < res.Cost.Length; id++) if (res.Cost[id] < float.MaxValue) reach++;
                    if (reach < 12) sb.AppendLine("  NOTE: you are boxed in (only " + reach + " standing spots reachable): look for a wall-jump (mount), rope, or a hole to crawl through");
                }
                else sb.AppendLine("  (you are not standing on solid ground right now)");

                // nearby liquids
                try
                {
                    if (FluidManager.main != null && WorldGeneration.world != null)
                    {
                        Vector2Int bp = WorldGeneration.world.WorldToBlockPos(p);
                        var seen = new Dictionary<int, string>();
                        for (int dx = -6; dx <= 6; dx++)
                            for (int dy = -4; dy <= 2; dy++)
                            {
                                var info = FluidManager.main.WaterInfo(new Vector2Int(bp.x + dx, bp.y + dy));
                                if (info.type > 0 && !seen.ContainsKey(info.type))
                                {
                                    string nm = LiquidAt(new Vector2Int(bp.x + dx, bp.y + dy));
                                    seen[info.type] = (nm.Length > 0 ? nm : "liquid#" + info.type) + " at dx" + (dx >= 0 ? "+" : "") + dx + " dy" + (dy >= 0 ? "+" : "") + dy;
                                }
                            }
                        if (seen.Count > 0) sb.AppendLine("  LIQUIDS nearby: " + string.Join("; ", seen.Values.ToArray()));
                    }
                }
                catch { }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("CompactVision: " + e.Message); sb.AppendLine("  (vision error: " + e.Message + ")"); }
        }
    }
}
