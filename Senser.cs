using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>Maps the ids shown to the AI (I1, P1, E1, H1, C1, R1) back to real objects.</summary>
    public class WorldIndex
    {
        public List<Item> Items = new List<Item>();
        public List<Body> Players = new List<Body>();
        public List<GameObject> Creatures = new List<GameObject>();
        public List<GameObject> Hazards = new List<GameObject>();
        public List<Climbable> Climbs = new List<Climbable>();
        public List<Recipe> Recipes = new List<Recipe>();
        public Vector2 Origin;
    }

    public class LimbSnap { public float Skin, Muscle; public bool Broken, Dislocated, Infected, Dismembered, Bleeding; }

    public class Snapshot
    {
        public float Blood, Brain, Hunger, Thirst, Oxygen;
        public bool Conscious, Standing;
        public Dictionary<string, LimbSnap> Limbs = new Dictionary<string, LimbSnap>();
    }

    public class Observation { public string Compact; public string Map; public Snapshot Snap; }

    /// <summary>Collects "what just hurt me" notes from the Harmony hooks (falls, traps, explosions, creature attacks).</summary>
    public static class DamageLog
    {
        static readonly List<string> pending = new List<string>();
        public static void Note(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            lock (pending) { if (!pending.Contains(s)) pending.Add(s); while (pending.Count > 8) pending.RemoveAt(0); }
        }
        public static List<string> Drain() { lock (pending) { var l = new List<string>(pending); pending.Clear(); return l; } }
    }

    /// <summary>Turns the game state into a short, non-overwhelming text description.</summary>
    public static class Senser
    {
        static readonly FieldInfo SlideL = AccessTools.Field(typeof(Body), "slidingLeft");
        static readonly FieldInfo SlideR = AccessTools.Field(typeof(Body), "slidingRight");

        public static readonly Dictionary<string, KeyValuePair<string, string>> HazardInfo = new Dictionary<string, KeyValuePair<string, string>>
        {
            ["BearTrap"] = new KeyValuePair<string, string>("bear trap", "snaps shut on a limb: broken bone, heavy bleeding, pins you in place. Walk around it or jump over it"),
            ["MineScript"] = new KeyValuePair<string, string>("landmine", "explodes about 1 second after anything touches it. Stay far away"),
            ["GunmineScript"] = new KeyValuePair<string, string>("gun mine", "fires at anyone standing in front of it (every ~6s)"),
            ["BarbedFence"] = new KeyValuePair<string, string>("barbed fence", "tears skin, bleeding, pain and a fall when touched"),
            ["CactusScript"] = new KeyValuePair<string, string>("cactus", "spikes: pain and bleeding when touched"),
            ["CoilScript"] = new KeyValuePair<string, string>("tesla coil", "huge electric shock, knocks you unconscious when touched"),
            ["XalorisScript"] = new KeyValuePair<string, string>("xaloris spores", "toxic cloud within ~5 blocks causes septic shock. Keep away"),
            ["SawbladeScript"] = new KeyValuePair<string, string>("sawblade", "cuts limbs. Keep away"),
            ["SpikeStabberScript"] = new KeyValuePair<string, string>("spike trap", "stabs when you pass. Avoid"),
            ["TurretScript"] = new KeyValuePair<string, string>("turret", "shoots at you. Break line of sight"),
            ["StalactiteDropper"] = new KeyValuePair<string, string>("loose stalactite", "falls when you walk under it. Do not stand under it"),
            ["GeyserScript"] = new KeyValuePair<string, string>("geyser", "scalding burst. Avoid"),
            ["GrabberPlant"] = new KeyValuePair<string, string>("grabber plant", "grabs and holds you. Stay away"),
            ["DamagingCrate"] = new KeyValuePair<string, string>("damaging crate", "hurts on contact")
        };

        static readonly string[] CreatureTypes = { "SpiderHandler", "ElderThornbackBehaviour", "CrystalEnemy", "TraderScript", "CaveTicks" };

        public static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, "<.*?>", "");
            s = Regex.Replace(s, "\\s+", " ");
            return s.Trim();
        }
        public static string Trunc(string s, int n) { return (s != null && s.Length > n) ? s.Substring(0, n) + "..." : s; }
        static int R(float v) { return Mathf.RoundToInt(v); }
        static string Sg(float v) { return (v >= 0 ? "+" : "") + v.ToString("0.0"); }

        // ------------------------------------------------------------------ item naming
        public static string ItemLabel(Item it)
        {
            try
            {
                var tip = PlayerCamera.ItemHoverDescription(it);   // respects "unknown item" rules of the game
                string n = Strip(tip.Item1);
                if (n.Length > 0) return n;
            }
            catch { }
            return it != null ? it.id : "?";
        }

        public static string ItemName(Item it)
        {
            string s = ItemLabel(it) + " " + R(it.condition * 100f) + "%";
            try
            {
                WaterContainerItem wc = it.GetComponent<WaterContainerItem>();
                if (wc != null)
                {
                    if (wc.CurrentTotal > 0.5f)
                    {
                        var parts = new List<string>();
                        foreach (var st in wc.stack) parts.Add(st.liquidId + " " + R(st.amount) + "mL");
                        s += " (contains " + string.Join(", ", parts.ToArray()) + ")";
                    }
                    else s += " (empty)";
                }
            }
            catch { }
            return s;
        }

        public static string Flags(Item it)
        {
            try
            {
                var st = it.Stats;
                if (st == null || !st.rec.recognizable) return "";
                var f = new List<string>();
                if (st.ActuallyUsableOnLimb(it)) f.Add("apply");
                if (st.usable && !st.usableWithLMB) f.Add("use");
                if (st.usable && st.usableWithLMB) f.Add("tool/weapon");
                if (st.wearable) f.Add("wear");
                if (st.onlyHoldInHands) f.Add("hands-only");
                return f.Count > 0 ? " [" + string.Join(",", f.ToArray()) + "]" : "";
            }
            catch { return ""; }
        }

        static string FlagsIfKnown(Item it, AiSettings cfg)
        {
            var mem = Plugin.Instance != null ? Plugin.Instance.Mem : null;
            if (cfg.ShowItemFlags || (mem != null && mem.Notes.ContainsKey(it.id))) return Flags(it);
            return "";
        }

        // ------------------------------------------------------------------ entity discovery
        static Type GameType(string name) { return typeof(Body).Assembly.GetType(name); }

        static List<Component> FindAll(string typeName)
        {
            var res = new List<Component>();
            Type t = GameType(typeName);
            if (t == null) return res;
            try { foreach (var o in UnityEngine.Object.FindObjectsOfType(t)) { var c = o as Component; if (c != null) res.Add(c); } } catch { }
            return res;
        }

        static string TrapState(Component c)
        {
            foreach (string f in new[] { "exploded", "activated", "pressed" })
            {
                try
                {
                    FieldInfo fi = c.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(bool) && (bool)fi.GetValue(c)) return f == "exploded" ? "ALREADY EXPLODED" : (f == "activated" ? "SPRUNG (already triggered)" : "TRIGGERED, about to explode");
                }
                catch { }
            }
            return "armed";
        }

        class Ent { public GameObject Go; public string Label; public string Note; }

        static List<Ent> FindHazards(Vector2 p, float radius)
        {
            var res = new List<Ent>();
            var seen = new HashSet<GameObject>();
            foreach (var kv in HazardInfo)
            {
                foreach (var c in FindAll(kv.Key))
                {
                    GameObject go = c.gameObject;
                    if (!go.activeInHierarchy || !seen.Add(go)) continue;
                    if (Vector2.Distance(p, go.transform.position) > radius) continue;
                    var cols = go.GetComponents<Collider2D>();
                    if (cols.Length > 0 && cols.All(x => !x.enabled)) continue;     // "backgroundified" = harmless scenery
                    res.Add(new Ent { Go = go, Label = kv.Value.Key, Note = TrapState(c) + ". " + kv.Value.Value });
                }
            }
            return res;
        }

        static List<Ent> FindCreatures(Body me, Vector2 p, float radius)
        {
            var res = new List<Ent>();
            var seen = new HashSet<GameObject>();
            Action<GameObject, string> add = (go, fallback) =>
            {
                if (go == null || !go.activeInHierarchy || !seen.Add(go)) return;
                if (go.GetComponentInParent<Body>() != null) return;            // players/humans are listed separately
                if (Vector2.Distance(p, go.transform.position) > radius) return;
                var be = go.GetComponent<BuildingEntity>();
                if (be != null && be.health < 0.5f) return;
                string n = (be != null && !string.IsNullOrEmpty(be.fullName)) ? Strip(be.fullName) : fallback;
                if (be != null) n += " (hp " + R(be.health) + ")";
                res.Add(new Ent { Go = go, Label = n });
            };
            foreach (string t in CreatureTypes)
                foreach (var c in FindAll(t))
                    add(c.gameObject, t == "TraderScript" ? "trader (NPC)" : t.Replace("Script", "").Replace("Behaviour", "").Replace("Handler", " creature"));
            try
            {
                foreach (var be in UnityEngine.Object.FindObjectsOfType<BuildingEntity>())
                    if (be.animal) add(be.gameObject, be.id);
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------------ crafting cache
        static float craftTime = -99f;
        static List<Recipe> craftRecipes = new List<Recipe>();
        static List<string> craftLines = new List<string>();

        static void RefreshCraftables(Body b)
        {
            if (Time.realtimeSinceStartup - craftTime < 4f) return;
            craftTime = Time.realtimeSinceStartup;
            craftRecipes = new List<Recipe>();
            craftLines = new List<string>();
            try
            {
                foreach (Recipe r in Recipes.GetVisibleRecipes(null))
                {
                    if (craftRecipes.Count >= 10) break;
                    List<Item> mats = null;
                    try { mats = r.GetItemsForRecipe(); } catch { }
                    if (mats == null) continue;
                    craftRecipes.Add(r);
                    string line = "R" + craftRecipes.Count + " " + Strip(r.simpleName) + "  <-  " + string.Join(", ", mats.Where(m => m != null).Select(m => ItemLabel(m)).Distinct().ToArray());
                    if (b.skills.INT < r.INT) line += "  (RISKY: needs intelligence " + r.INT + ", you have " + b.skills.INT + ")";
                    craftLines.Add(line);
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("craft scan: " + e.Message); }
        }

        // ------------------------------------------------------------------ helpers
        static float Ray(Vector2 o, Vector2 dir, float max, int mask)
        {
            RaycastHit2D h = Physics2D.Raycast(o, dir, max, mask);
            return h.collider != null ? h.distance : -1f;
        }

        static bool IsLiquid(Vector2 pos)
        {
            try
            {
                if (FluidManager.main == null || WorldGeneration.world == null) return false;
                return FluidManager.main.WaterInfo(WorldGeneration.world.WorldToBlockPos(pos)).Item1 > 0f;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ the observation
        public static Observation Build(Body b, AiSettings cfg, WorldIndex idx, Snapshot prev)
        {
            var s = cfg.Sense;
            var sb = new StringBuilder();
            var snap = new Snapshot();
            int ground = LayerMask.GetMask("Ground");
            Vector2 p = b.transform.position;
            idx.Origin = p;
            Bounds bb = b.col != null ? b.col.bounds : new Bounds(p, new Vector3(0.6f, 1.6f, 0f));
            var mem = Plugin.Instance != null ? Plugin.Instance.Mem : null;

            snap.Blood = b.bloodVolume; snap.Brain = b.brainHealth; snap.Hunger = b.hunger; snap.Thirst = b.thirst;
            snap.Oxygen = b.bloodOxygen; snap.Conscious = b.conscious; snap.Standing = b.standing;

            sb.AppendLine("TIME " + R(Time.time) + "s");
            if (s.Vitals)
            {
                sb.AppendLine("STATUS: " + (b.conscious ? "awake" : "UNCONSCIOUS") + ", " + (b.standing ? "standing" : "not standing")
                    + (b.sleeping ? ", sleeping" : "") + (b.inWater ? ", in liquid" : "")
                    + " | blood volume " + R(b.bloodVolume) + " | blood oxygen " + R(b.bloodOxygen) + (b.breathing ? "" : " (NOT BREATHING)")
                    + " | brain " + R(b.brainHealth) + " | consciousness " + R(b.consciousness) + " | shock " + R(b.shock)
                    + " | pain " + R(b.averagePain) + " | bleeding rate " + b.totalBleedSpeed.ToString("0.00")
                    + (b.internalBleeding > 1f ? " | INTERNAL BLEEDING " + R(b.internalBleeding) : "")
                    + (b.septicShock > 1f ? " | SEPTIC SHOCK " + R(b.septicShock) : "")
                    + " | heart rate " + R(b.heartRate) + " | blood pressure " + b.bloodPressureReadout);
            }
            if (s.Needs)
                sb.AppendLine("NEEDS: hunger " + R(b.hunger) + " | thirst " + R(b.thirst) + " | stamina " + R(b.stamina) + " | energy " + R(b.energy)
                    + " | body temp " + b.temperature.ToString("0.0") + "C (normal 36-37.5)");
            if (s.Position) sb.AppendLine("POSITION: x " + R(p.x) + ", y " + R(p.y) + " (bigger y = higher)");

            // ---- status icons shown at the bottom of the screen ----
            if (s.StatusIcons && MoodleManager.main != null && MoodleManager.main.moodles != null)
            {
                var lines = new List<string>();
                try
                {
                    foreach (Transform t in MoodleManager.main.moodles)
                    {
                        Moodle m = t.GetComponent<Moodle>();
                        UITooltip tip = t.GetComponent<UITooltip>();
                        if (m == null || tip == null) continue;
                        string nm = Strip(tip.tipName), ds = Strip(tip.tipDesc);
                        if (nm.Length == 0) continue;
                        string line = "  " + (m.doWarningFlash ? "[CRITICAL] " : "") + nm + (ds.Length > 0 ? " - " + Trunc(ds, 110) : "");
                        if (!lines.Contains(line)) lines.Add(line);
                        if (lines.Count >= 10) break;
                    }
                }
                catch { }
                if (lines.Count > 0) { sb.AppendLine("STATUS ICONS (what the icons at the bottom of the screen say):"); foreach (var l in lines) sb.AppendLine(l); }
            }

            // ---- limbs ----
            var okLimbs = new List<string>();
            var badLimbs = new List<string>();
            if (b.limbs != null)
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null) continue;
                    bool bleeding = l.bleedAmount > 0.01f;
                    snap.Limbs[l.name] = new LimbSnap { Skin = l.skinHealth, Muscle = l.muscleHealth, Broken = l.broken, Dislocated = l.dislocated, Infected = l.infected, Dismembered = l.dismembered, Bleeding = bleeding };
                    bool healthy = l.skinHealth >= 95f && l.muscleHealth >= 95f && !l.broken && !l.dislocated && !l.infected && !l.dismembered && !bleeding && l.pain < 5f && !l.hasShrapnel;
                    string label = "L" + i + " " + l.name;
                    if (healthy) { okLimbs.Add(label); continue; }
                    var flags = new List<string>();
                    if (l.dismembered) flags.Add("DISMEMBERED");
                    if (bleeding) flags.Add("BLEEDING " + l.bleedAmount.ToString("0.0"));
                    if (l.broken) flags.Add("BROKEN");
                    if (l.dislocated) flags.Add("DISLOCATED");
                    if (l.infected) flags.Add("INFECTED");
                    if (l.hasShrapnel) flags.Add("SHRAPNEL x" + l.shrapnel);
                    if (l.pain >= 5f) flags.Add("pain " + R(l.pain));
                    badLimbs.Add("  " + label + ": skin " + R(l.skinHealth) + ", muscle " + R(l.muscleHealth) + (flags.Count > 0 ? " - " + string.Join(", ", flags.ToArray()) : ""));
                }
            if (s.Limbs)
            {
                if (badLimbs.Count > 0) { sb.AppendLine("INJURED LIMBS (skin/muscle 100 = intact):"); foreach (var line in badLimbs) sb.AppendLine(line); }
                if (okLimbs.Count > 0) sb.AppendLine("Healthy limbs: " + string.Join(", ", okLimbs.ToArray()));
            }

            // ---- collision / touch ----
            if (s.Collision)
            {
                float ext = bb.extents.x, feetY = bb.min.y;
                float wl = Ray(p, Vector2.left, 8f, ground), wr = Ray(p, Vector2.right, 8f, ground);
                float ceil = Ray(new Vector2(p.x, bb.max.y), Vector2.up, 8f, ground);
                float fl = Ray(new Vector2(p.x, feetY + 0.2f), Vector2.down, 14f, ground);
                float ledgeL = Ray(new Vector2(p.x - ext - 0.8f, feetY + 0.3f), Vector2.down, 14f, ground);
                float ledgeR = Ray(new Vector2(p.x + ext + 0.8f, feetY + 0.3f), Vector2.down, 14f, ground);
                bool slideL = SlideL != null && (bool)SlideL.GetValue(b), slideR = SlideR != null && (bool)SlideR.GetValue(b);
                bool touchL = wl >= 0f && wl - ext < 0.45f, touchR = wr >= 0f && wr - ext < 0.45f;
                Func<float, string> wall = d => d < 0f ? "none within 8" : Math.Max(0f, d - ext).ToString("0.0") + " away";
                Func<float, string> ledge = d => d < 0f ? "DROP 14+ (dangerous)" : (d - 0.3f < 1.2f ? "flat/ok" : "step down " + (d - 0.3f).ToString("0.0"));
                sb.AppendLine("COLLISION: on ground " + (b.grounded ? "yes" : "NO (airborne)")
                    + " | touching wall: left " + (touchL ? "YES" : "no") + ", right " + (touchR ? "YES" : "no") + (slideL || slideR ? " (sliding on it)" : "")
                    + " | floor below feet " + (fl < 0f ? "none within 14" : Math.Max(0f, fl - 0.2f).ToString("0.0"))
                    + " | ceiling above head " + (ceil < 0f ? "none within 8" : ceil.ToString("0.0"))
                    + " | wall left " + wall(wl) + ", wall right " + wall(wr)
                    + " | ground ahead left: " + ledge(ledgeL) + ", right: " + ledge(ledgeR)
                    + (b.currentClimbable != null ? " | CLIMBING a rope/ladder" : ""));
            }

            // ---- items ----
            var noteIds = new HashSet<string>();
            if (s.Items)
            {
                var found = new List<Item>();
                var seen = new HashSet<Item>();
                int itemMask = LayerMask.GetMask("Item");
                Collider2D[] cols = itemMask != 0 ? Physics2D.OverlapCircleAll(p, cfg.ItemRadius, itemMask) : Physics2D.OverlapCircleAll(p, cfg.ItemRadius);
                foreach (var c in cols)
                {
                    Item it = c.GetComponent<Item>();
                    if (it == null) it = c.GetComponentInParent<Item>();
                    if (it == null || !seen.Add(it)) continue;
                    if (it.rb == null || !it.rb.simulated) continue;
                    if (it.transform.IsChildOf(b.transform)) continue;
                    found.Add(it);
                }
                found = found.OrderBy(it => Vector2.Distance(p, it.transform.position)).Take(12).ToList();
                idx.Items = found;
                if (found.Count > 0)
                {
                    sb.AppendLine("NEARBY ITEMS (dx right+, dy up+; you can grab within 10 blocks with a clear line):");
                    for (int i = 0; i < found.Count; i++)
                    {
                        Vector2 ip = found[i].transform.position;
                        bool clear = !Physics2D.Linecast(p, ip, ground);
                        float dist = Vector2.Distance(p, ip);
                        noteIds.Add(found[i].id);
                        sb.AppendLine("  I" + (i + 1) + " " + ItemName(found[i]) + FlagsIfKnown(found[i], cfg) + " dx=" + Sg(ip.x - p.x) + " dy=" + Sg(ip.y - p.y)
                            + (dist <= Body.interactionRange && clear ? " REACHABLE" : (clear ? " too far" : " blocked by wall")));
                    }
                }
                else sb.AppendLine("NEARBY ITEMS: none in range.");
            }

            // ---- other players ----
            if (s.Players || s.Map)
            {
                var plist = new List<PlayerInfo>();
                if (MP.Present) plist = MP.Players().Where(x => !x.IsLocal && x.Body != null && x.Body != b).ToList();
                else
                {
                    try { foreach (var ob in UnityEngine.Object.FindObjectsOfType<Body>()) if (ob != b) plist.Add(new PlayerInfo { Body = ob, Name = "unknown person" }); } catch { }
                }
                plist = plist.OrderBy(x => Vector2.Distance(p, x.Body.transform.position)).Take(6).ToList();
                idx.Players = plist.Select(x => x.Body).ToList();
                if (s.Players)
                {
                    if (plist.Count > 0)
                    {
                        sb.AppendLine("OTHER PLAYERS (real humans, NOT enemies; you can talk to them with the 'chat' field; walk_to/follow work on them):");
                        for (int i = 0; i < plist.Count; i++)
                        {
                            Body ob = plist[i].Body;
                            Vector2 op = ob.transform.position;
                            var st = new List<string>();
                            st.Add(!ob.alive ? "DEAD" : (ob.conscious ? "awake" : "UNCONSCIOUS"));
                            if (ob.alive && ob.totalBleedSpeed > 0.5f) st.Add("bleeding");
                            if (ob.alive && ob.bloodVolume < 70f) st.Add("low blood");
                            try { Item hi = ob.GetItem(ob.handSlot); if (hi != null) st.Add("holding " + ItemLabel(hi)); } catch { }
                            sb.AppendLine("  P" + (i + 1) + " \"" + plist[i].Name + "\" dx=" + Sg(op.x - p.x) + " dy=" + Sg(op.y - p.y) + " dist=" + Vector2.Distance(p, op).ToString("0.0") + " - " + string.Join(", ", st.ToArray()));
                        }
                    }
                    else if (MP.Present) sb.AppendLine("OTHER PLAYERS: none in the scene.");
                }
            }

            // ---- creatures / hazards ----
            List<Ent> creatures = new List<Ent>(), hazards = new List<Ent>();
            if (s.Creatures || s.Map) creatures = FindCreatures(b, p, cfg.CreatureRadius).OrderBy(e => Vector2.Distance(p, e.Go.transform.position)).Take(8).ToList();
            if (s.Hazards || s.Map) hazards = FindHazards(p, cfg.CreatureRadius).OrderBy(e => Vector2.Distance(p, e.Go.transform.position)).Take(10).ToList();
            idx.Creatures = creatures.Select(e => e.Go).ToList();
            idx.Hazards = hazards.Select(e => e.Go).ToList();

            if (s.Creatures)
            {
                if (creatures.Count > 0)
                {
                    sb.AppendLine("CREATURES (enemies/animals/NPCs):");
                    for (int i = 0; i < creatures.Count; i++)
                    {
                        Vector2 cp = creatures[i].Go.transform.position;
                        bool los = !Physics2D.Linecast(p, cp, ground);
                        sb.AppendLine("  E" + (i + 1) + " " + creatures[i].Label + " dx=" + Sg(cp.x - p.x) + " dy=" + Sg(cp.y - p.y) + " dist=" + Vector2.Distance(p, cp).ToString("0.0") + (los ? " in line of sight" : " behind a wall"));
                    }
                }
                else sb.AppendLine("CREATURES: none nearby.");
            }
            if (s.Hazards)
            {
                if (hazards.Count > 0)
                {
                    sb.AppendLine("HAZARDS / TRAPS (do not touch):");
                    for (int i = 0; i < hazards.Count; i++)
                    {
                        Vector2 hp = hazards[i].Go.transform.position;
                        sb.AppendLine("  H" + (i + 1) + " " + hazards[i].Label + " dx=" + Sg(hp.x - p.x) + " dy=" + Sg(hp.y - p.y) + " dist=" + Vector2.Distance(p, hp).ToString("0.0") + " - " + hazards[i].Note);
                    }
                }
                else sb.AppendLine("HAZARDS: none seen nearby.");
            }

            // ---- climbables ----
            if (s.Climbables && Climbable.allClimbables != null)
            {
                var cl = new List<KeyValuePair<Climbable, ClimbableGrabInfo>>();
                try
                {
                    foreach (var c in Climbable.allClimbables)
                    {
                        if (c == null || !c.gameObject.activeInHierarchy) continue;
                        ClimbableGrabInfo gi = c.GetGrabInfo(p);
                        if (gi.distanceToPlayer < 14f) cl.Add(new KeyValuePair<Climbable, ClimbableGrabInfo>(c, gi));
                    }
                }
                catch { }
                cl = cl.OrderBy(k => k.Value.distanceToPlayer).Take(3).ToList();
                idx.Climbs = cl.Select(k => k.Key).ToList();
                if (cl.Count > 0)
                {
                    sb.AppendLine("CLIMBABLES (ropes/ladders; stand at it and hold 'up' to grab it, 'climb' to move on it, 'jump' to let go):");
                    for (int i = 0; i < cl.Count; i++)
                        sb.AppendLine("  C" + (i + 1) + " nearest grab point dx=" + Sg(cl[i].Value.position.x - p.x) + " dy=" + Sg(cl[i].Value.position.y - p.y) + " (distance " + cl[i].Value.distanceToPlayer.ToString("0.0") + ", length " + R(cl[i].Key.totalLength) + ")");
                }
            }

            // ---- inventory ----
            if (s.Inventory && b.slots != null)
            {
                sb.AppendLine("INVENTORY (active hand = slot " + b.handSlot + "):");
                for (int i = 0; i < b.slots.Length; i++)
                {
                    string slotName = Strip(b.slots[i].gameObject.name).Replace("(Clone)", "").Trim();
                    if (b.HoldingItem(i))
                    {
                        Item it = b.GetItem(i);
                        noteIds.Add(it.id);
                        string extra = "";
                        Container cont = it.GetComponent<Container>();
                        if (cont != null && cont.itemCount > 0)
                        {
                            var names = new List<string>();
                            foreach (Transform t in cont.transform) { Item ci = t.GetComponent<Item>(); if (ci != null) names.Add(ItemLabel(ci)); }
                            extra = " (contains: " + string.Join(", ", names.ToArray()) + ")";
                        }
                        sb.AppendLine("  [" + i + "] " + slotName + (i == b.handSlot ? " (ACTIVE HAND)" : "") + ": " + ItemName(it) + FlagsIfKnown(it, cfg) + extra);
                    }
                    else if (b.slots[i].canPickUp)
                        sb.AppendLine("  [" + i + "] " + slotName + (i == b.handSlot ? " (ACTIVE HAND)" : "") + ": empty");
                }
            }
            if (s.Wearing)
            {
                try
                {
                    var worn = b.GetAllWearables();
                    if (worn != null && worn.Count > 0) sb.AppendLine("WEARING: " + string.Join(", ", worn.Select(w => ItemName(w)).ToArray()));
                }
                catch { }
            }
            if (mem != null && noteIds.Count > 0)
            {
                var notes = noteIds.Where(id => mem.Notes.ContainsKey(id)).Take(6).Select(id => "  " + id + ": " + mem.Notes[id]).ToList();
                if (notes.Count > 0) { sb.AppendLine("WHAT YOU LEARNED ABOUT THESE ITEMS (from inspecting):"); foreach (var n in notes) sb.AppendLine(n); }
            }

            // ---- crafting ----
            idx.Recipes = new List<Recipe>();
            if (s.Craftables && cfg.Allow.Craft)
            {
                RefreshCraftables(b);
                idx.Recipes = new List<Recipe>(craftRecipes);
                if (craftLines.Count > 0) { sb.AppendLine("CRAFTABLE RIGHT NOW (use craft with target R#):"); foreach (var l in craftLines) sb.AppendLine("  " + l); }
            }

            // ---- what hit me + events ----
            var hits = DamageLog.Drain();
            bool lostHealth = false;
            if (s.Events && prev != null)
            {
                var ev = new List<string>();
                if (prev.Conscious && !snap.Conscious) ev.Add("you lost consciousness");
                if (!prev.Conscious && snap.Conscious) ev.Add("you woke up");
                if (prev.Standing && !snap.Standing) ev.Add("you fell down / are no longer standing");
                if (snap.Blood < prev.Blood - 2f) ev.Add("blood volume " + R(prev.Blood) + " -> " + R(snap.Blood));
                if (snap.Brain < prev.Brain - 1f) { ev.Add("brain health " + R(prev.Brain) + " -> " + R(snap.Brain)); lostHealth = true; }
                if (snap.Oxygen < prev.Oxygen - 5f) ev.Add("blood oxygen " + R(prev.Oxygen) + " -> " + R(snap.Oxygen));
                if (snap.Hunger < 25f && prev.Hunger >= 25f) ev.Add("you are getting very hungry");
                if (snap.Thirst < 25f && prev.Thirst >= 25f) ev.Add("you are getting very thirsty");
                foreach (var kv in snap.Limbs)
                {
                    LimbSnap o; if (!prev.Limbs.TryGetValue(kv.Key, out o)) continue;
                    LimbSnap n = kv.Value;
                    if (n.Skin < o.Skin - 4f) { ev.Add(kv.Key + " skin " + R(o.Skin) + " -> " + R(n.Skin)); lostHealth = true; }
                    if (n.Muscle < o.Muscle - 4f) { ev.Add(kv.Key + " muscle " + R(o.Muscle) + " -> " + R(n.Muscle)); lostHealth = true; }
                    if (n.Broken && !o.Broken) { ev.Add(kv.Key + " is now BROKEN"); lostHealth = true; }
                    if (!n.Broken && o.Broken) ev.Add(kv.Key + " bone is no longer broken");
                    if (n.Dislocated && !o.Dislocated) { ev.Add(kv.Key + " is now DISLOCATED"); lostHealth = true; }
                    if (n.Infected && !o.Infected) ev.Add(kv.Key + " got INFECTED");
                    if (n.Dismembered && !o.Dismembered) { ev.Add(kv.Key + " was DISMEMBERED"); lostHealth = true; }
                    if (n.Bleeding && !o.Bleeding) ev.Add(kv.Key + " started BLEEDING");
                    if (!n.Bleeding && o.Bleeding) ev.Add(kv.Key + " stopped bleeding");
                }
                if (s.DamageLog && lostHealth && hits.Count == 0)
                {
                    // no hook fired: guess from what is close to the player
                    var near = hazards.Select(e => new { e.Label, d = Vector2.Distance(p, e.Go.transform.position) })
                        .Concat(creatures.Select(e => new { e.Label, d = Vector2.Distance(p, e.Go.transform.position) }))
                        .OrderBy(x => x.d).FirstOrDefault();
                    if (near != null && near.d < 4f) hits.Add("probably " + near.Label + " (it is " + near.d.ToString("0.0") + " blocks away)");
                    else hits.Add("no trap or creature nearby: probably a fall/impact, bleeding, infection or a status effect");
                }
                if (ev.Count > 0) { sb.AppendLine("NEW EVENTS SINCE LAST TURN:"); foreach (var e in ev) sb.AppendLine("  - " + e); }
            }
            if (s.DamageLog && hits.Count > 0) { sb.AppendLine("WHAT HIT YOU:"); foreach (var h in hits) sb.AppendLine("  - " + h); }

            // ---- map ----
            string map = "";
            if (s.Map)
            {
                int W = Mathf.Clamp(cfg.MapHalfWidth, 3, 25), H = Mathf.Clamp(cfg.MapHalfHeight, 2, 15);
                int px = Mathf.FloorToInt(p.x), py = Mathf.FloorToInt(p.y);
                var grid = new char[2 * H + 1, 2 * W + 1];
                for (int dy = H; dy >= -H; dy--)
                    for (int dx = -W; dx <= W; dx++)
                    {
                        Vector2 cell = new Vector2(px + dx + 0.5f, py + dy + 0.5f);
                        char ch = '.';
                        if (Physics2D.OverlapPoint(cell, ground) != null) ch = '#';
                        else if (IsLiquid(cell)) ch = '~';
                        grid[H - dy, dx + W] = ch;
                    }
                Action<Vector2, char> put = (pos, ch) =>
                {
                    int dx = Mathf.FloorToInt(pos.x) - px, dy = Mathf.FloorToInt(pos.y) - py;
                    if (dx < -W || dx > W || dy < -H || dy > H) return;
                    grid[H - dy, dx + W] = ch;
                };
                foreach (var c in idx.Climbs) { if (c != null) put(c.GetGrabInfo(p).position, '|'); }
                foreach (var it in idx.Items) if (it != null) put(it.transform.position, 'i');
                foreach (var e in hazards) if (e.Go != null) put(e.Go.transform.position, '!');
                foreach (var e in creatures) if (e.Go != null) put(e.Go.transform.position, 'E');
                foreach (var ob in idx.Players) if (ob != null) put(ob.transform.position, 'P');
                if (Pointer.Set && s.Pointer) put(Pointer.World, 'X');
                grid[H, W] = '@';
                var ms = new StringBuilder();
                ms.AppendLine("MAP (top = up, 1 char = 1 block; @ you, # solid, ~ liquid, . air, i item, E creature, ! hazard, P player, | rope/ladder, X human pointer):");
                for (int r = 0; r < grid.GetLength(0); r++)
                {
                    var row = new char[grid.GetLength(1)];
                    for (int c = 0; c < row.Length; c++) row[c] = grid[r, c];
                    ms.AppendLine("  " + new string(row));
                }
                map = ms.ToString();
            }

            return new Observation { Compact = sb.ToString(), Map = map, Snap = snap };
        }
    }
}
