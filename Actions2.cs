using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>v2.1 additions to the Executor: path finding, ledge mounting, ropes, auto-crouch, bags, defibrillators, interaction...</summary>
    public partial class Executor
    {
        float stdH = 1.8f, crouchHold, ropeIntent;
        NavGrid navGrid;
        float navGridT;
        Vector2 navGridC;
        List<NavStep> navPath;
        float navAirDir;

        static readonly FieldInfo CliProg = AccessTools.Field(typeof(Body), "climbableProgress");
        static readonly FieldInfo AedState = AccessTools.Field(typeof(AEDMinigame), "state");
        static readonly FieldInfo AedPads = AccessTools.Field(typeof(AEDMinigame), "pads");
        static readonly FieldInfo AedSpots = AccessTools.Field(typeof(AEDMinigame), "desiredSpots");
        static readonly FieldInfo MdCur = AccessTools.Field(typeof(ManualDefibMinigame), "currentCharge");
        static readonly FieldInfo MdDes = AccessTools.Field(typeof(ManualDefibMinigame), "desiredCharge");
        static readonly FieldInfo MdChg = AccessTools.Field(typeof(ManualDefibMinigame), "charging");
        static readonly FieldInfo ShObjects = AccessTools.Field(typeof(ShrapnelMinigame), "objects");

        static readonly HashSet<string> ExtraVerbs = new HashSet<string>
        {
            "climb", "mount", "interact", "remove", "store", "take", "combine", "pull_shrapnel", "piggyback", "dismount", "carry", "drop_carried"
        };

        static string DeniedExtra(string verb, PermissionSettings A)
        {
            bool ok;
            switch (verb)
            {
                case "mount": ok = A.Move && A.Jump; break;
                case "interact": ok = A.Interact; break;
                case "remove": ok = A.Wear || A.ApplyToLimbs; break;
                case "store": case "take": case "combine": ok = A.Storage; break;
                case "piggyback": case "dismount": case "carry": case "drop_carried": ok = A.Carry; break;
                default: return "unknown action '" + verb + "'";
            }
            return ok ? null : "NOT ALLOWED by the human: " + verb;
        }

        void DoJumpHigh() { if (!P.Cfg.Allow.Jump) return; jumpPending = true; jumpHold = 0.65f; jumpLock = 0.5f; }

        static float RayDist(Vector2 o, Vector2 d, float max)
        {
            RaycastHit2D h = Physics2D.Raycast(o, d, max, LayerMask.GetMask("Ground"));
            return h.collider != null ? h.distance : -1f;
        }

        // ---------------------------------------------------------------- per-frame helpers called from Apply()
        bool AutoCrouchNeeded(Body b)
        {
            try
            {
                if (!P.Cfg.AutoCrouch || !P.Agent.Active) return false;
                if (!b.crouching) stdH = Mathf.Max(stdH, b.col.size.y);
                if (Mathf.Abs(WantMove.x) > 0.1f && b.grounded)
                {
                    Bounds bb = b.col.bounds;
                    float dir = Mathf.Sign(WantMove.x);
                    float reach = bb.extents.x + 0.9f;
                    Vector2 hi = new Vector2(bb.center.x, bb.min.y + stdH - 0.15f);
                    Vector2 lo = new Vector2(bb.center.x, bb.min.y + 0.45f);
                    int mask = LayerMask.GetMask("Ground");
                    bool hiBlocked = Physics2D.Raycast(hi, new Vector2(dir, 0f), reach, mask).collider != null;
                    bool loBlocked = Physics2D.Raycast(lo, new Vector2(dir, 0f), reach, mask).collider != null;
                    if (hiBlocked && !loBlocked) crouchHold = 0.7f;
                }
                crouchHold -= Time.deltaTime;
                return crouchHold > 0f;
            }
            catch { return false; }
        }

        Vector2 RopeAssist(Body b, Vector2 mv)
        {
            try
            {
                if (!P.Cfg.AutoGrabRope || !P.Agent.Active || b.currentClimbable != null || !P.Cfg.Allow.Move) return mv;
                Climbable c = Climbable.GetClosestClimbable(b.transform.position);
                if (c == null) return mv;
                ClimbableGrabInfo gi = c.GetGrabInfo(b.transform.position);
                if (gi.distanceToPlayer > 0.95f) return mv;
                bool falling = !b.grounded && b.rb.velocity.y < -3f;
                if (ropeIntent > 0f || falling) mv.y = 1f;     // the game grabs the rope as soon as moveDir.y > 0.5 and it is within reach
            }
            catch { }
            return mv;
        }

        // ---------------------------------------------------------------- dispatch
        bool UseExtra(AiAction a, Body b, WorldIndex idx)
        {
            if (ExtraVerbs.Contains(a.Do)) return true;
            if (a.Do == "walk_to" || a.Do == "follow") return NeedsNav(a, b, idx);
            return false;
        }

        bool StepExtra(AiAction a, Body b, WorldIndex idx, float dt, out string res)
        {
            res = null;
            switch (a.Do)
            {
                case "walk_to": case "follow": return StepNav(a, b, idx, dt, out res);
                case "climb": return StepClimb(a, b, idx, dt, out res);
                case "mount": return StepMount(a, b, dt, out res);
                case "pull_shrapnel": return StepShrapnel(a, b, dt, out res);
                case "interact": res = DoInteract(a, b, idx); return true;
                case "remove": res = DoRemove(a, b, idx); return true;
                case "store": res = DoStore(a, b, idx); return true;
                case "take": res = DoTake(a, b, idx); return true;
                case "combine": res = DoCombine(a, b, idx); return true;
                case "piggyback": res = MPX.Ride(b, PlayerBody(a.Target, idx)); return true;
                case "carry": res = MPX.Carry(b, PlayerBody(a.Target, idx)); return true;
                case "dismount": res = MPX.Dismount(b); return true;
                case "drop_carried": res = MPX.DropCarried(b); return true;
            }
            res = "unknown action '" + a.Do + "'";
            return true;
        }

        static Body PlayerBody(string id, WorldIndex idx)
        {
            int n = ParseIndex(id, 'P');
            return (n >= 1 && n <= idx.Players.Count) ? idx.Players[n - 1] : null;
        }

        static Item AnyItem(int slot, string target, WorldIndex idx, Body b)
        {
            if (slot >= 0) return SlotItem(b, slot);
            if (string.IsNullOrEmpty(target)) return null;
            char c = target[0];
            int n = ParseIndex(target, c);
            if (n < 1) return null;
            if (c == 'I' && n <= idx.Items.Count) return idx.Items[n - 1];
            if (c == 'W' && n <= idx.Worn.Count) return idx.Worn[n - 1];
            return null;
        }

        // ---------------------------------------------------------------- bandage / minigame helpers
        static bool BandageDone(AiAction a, Item item, Limb lb, float maxSec)
        {
            if (item == null || item.condition <= 0.001f) return true;       // used up completely
            if (a.Sec > 0f) return a.T > maxSec;
            if (a.T > maxSec + 6f) return true;
            bool bleedDone = lb.bleedAmount < 0.05f && a.T > 2f;
            if (bleedDone) return item.condition >= 0.12f;                    // do not leave a useless 1-10% scrap behind: use it up
            return a.T > maxSec;
        }

        bool DriveOther(Minigame mg, AiAction a, Body b, float dt, out string res)
        {
            res = null;
            try
            {
                if (a.T > 40f) { MinigameBase.main.EndMinigame(); res = "treatment timed out (" + mg.GetType().Name + ")"; return true; }

                if (mg is AEDMinigame)
                {
                    int st = (int)AedState.GetValue(mg);
                    if (st == 0)
                    {
                        var pads = AedPads.GetValue(mg) as List<RectTransform>;
                        var spots = AedSpots.GetValue(null) as Vector2[];
                        if (pads != null && spots != null)
                            for (int i = 0; i < pads.Count && i < spots.Length; i++)
                                pads[i].localPosition = new Vector3(spots[i].x, spots[i].y, pads[i].localPosition.z);
                    }
                    else if (st == 2) AedState.SetValue(mg, 3);      // "press the shock button"
                    else if (st == 4)
                    {
                        if (a.Extra2 <= 0f) a.Extra2 = a.T;
                        if (a.T - a.Extra2 > 0.7f)
                        {
                            MinigameBase.main.EndMinigame();
                            res = "AED: shock delivered. Heart rhythm now: " + (b.fibrillationProgress > 1f ? "still irregular (fibrillation " + Mathf.RoundToInt(b.fibrillationProgress) + ")" : "normal");
                            return true;
                        }
                    }
                    else if (st == 5)
                    {
                        MinigameBase.main.EndMinigame();
                        res = "AED analysed the heart: NO shock advised (the rhythm is not fibrillating - or the pads were not on the chest limb)";
                        return true;
                    }
                    return false;
                }

                if (mg is ManualDefibMinigame)
                {
                    var md = (ManualDefibMinigame)mg;
                    Item item = MinigameBase.main.currentItem;
                    if (item == null || item.battery == null || !item.battery.hasCharge) { MinigameBase.main.EndMinigame(); res = "defibrillator battery is empty"; return true; }
                    float fib = b.fibrillationProgress;
                    float desired = Mathf.Clamp(fib * 2f, 10f, 200f);
                    switch (a.Count)
                    {
                        case 0:
                            MdDes.SetValue(md, desired); MdChg.SetValue(md, true); a.Count = 1; a.Cool = 0f;
                            break;
                        case 1:
                        {
                            float cur = (float)MdCur.GetValue(md);
                            a.Cool += dt;
                            if (cur >= desired - 1f || a.Cool > 12f) a.Count = 2; else MdChg.SetValue(md, true);
                            break;
                        }
                        case 2:
                        {
                            float cur = (float)MdCur.GetValue(md);
                            item.Defibrillate(new Item.DefibInfo { chance = md.ChanceToWork(), limb = md.limb });
                            item.battery.DrainCharge(cur / 4000f);
                            MdCur.SetValue(md, 0f); MdChg.SetValue(md, false);
                            a.Fail++; a.Count = 3; a.Cool = 1.6f;
                            break;
                        }
                        case 3:
                            a.Cool -= dt;
                            if (a.Cool <= 0f)
                            {
                                if (b.fibrillationProgress > 1f && a.Fail < 4) { a.Count = 0; }
                                else
                                {
                                    MinigameBase.main.EndMinigame();
                                    res = "manual defibrillator: " + a.Fail + " shock(s) given; heart rhythm now " + (b.fibrillationProgress > 1f ? "still irregular (fibrillation " + Mathf.RoundToInt(b.fibrillationProgress) + ")" : "normal");
                                    return true;
                                }
                            }
                            break;
                    }
                    return false;
                }

                if (mg is ShrapnelMinigame) return DriveShrapnel((ShrapnelMinigame)mg, a, b, dt, a.LimbRef, out res);

                MinigameBase.main.EndMinigame();
                res = "this treatment starts a hand-operated minigame (" + mg.GetType().Name + ") that I cannot play; it was closed";
                return true;
            }
            catch (Exception e)
            {
                try { MinigameBase.main.EndMinigame(); } catch { }
                res = "treatment failed: " + e.Message;
                return true;
            }
        }

        bool DriveShrapnel(ShrapnelMinigame sm, AiAction a, Body b, float dt, Limb limb, out string res)
        {
            res = null;
            var objs = ShObjects.GetValue(sm) as List<RectTransform>;
            a.Cool -= dt;
            if (a.Cool <= 0f && objs != null && limb != null)
            {
                foreach (var o in objs)
                {
                    if (o == null || o.anchoredPosition.y >= 35f) continue;
                    o.anchoredPosition = new Vector2(o.anchoredPosition.x, 70f);    // pulled out of the wound
                    if (!sm.hasTweezers)
                    {
                        limb.skinHealth -= UnityEngine.Random.Range(4f, 6f);
                        limb.bleedAmount += UnityEngine.Random.Range(0.4f, 1f);
                        limb.pain += UnityEngine.Random.Range(9f, 16f);
                        try { b.DoGoreSound(); } catch { }
                    }
                    a.Cool = 0.4f;
                    break;
                }
            }
            if (a.T > 14f) { MinigameBase.main.EndMinigame(); res = "gave up pulling shrapnel (took too long)"; return true; }
            return false;
        }

        bool StepShrapnel(AiAction a, Body b, float dt, out string res)
        {
            res = null;
            if (!a.Flag)
            {
                a.Flag = true;
                Limb l = ResolveLimb(b, a.Limb);
                if (l == null) { res = "pull_shrapnel: limb '" + a.Limb + "' not found"; return true; }
                if (!l.hasShrapnel) { res = l.name + " has no shrapnel"; return true; }
                if (!b.conscious) { res = "cannot do that while unconscious"; return true; }
                if (MinigameBase.main.currentMinigame != null) { res = "another treatment is in progress"; return true; }
                a.LimbRef = l; a.Count = l.shrapnel; a.Cool = 0.5f;
                a.Pending = l.name;
                MinigameBase.main.StartMinigame(new ShrapnelMinigame(l), null);
                return false;
            }
            Minigame mg = MinigameBase.main.currentMinigame;
            if (mg == null)
            {
                if (a.T < 0.5f) return false;
                res = "pulled the shrapnel out of the " + a.Pending + " with bare hands (painful, it bleeds more now); remaining pieces: " + (a.LimbRef != null ? a.LimbRef.shrapnel : 0);
                return true;
            }
            ShrapnelMinigame sm = mg as ShrapnelMinigame;
            if (sm == null) { res = "a different minigame is open"; return true; }
            return DriveShrapnel(sm, a, b, dt, a.LimbRef, out res);
        }

        // ---------------------------------------------------------------- object interaction
        string DoInteract(AiAction a, Body b, WorldIndex idx)
        {
            int n = ParseIndex(a.Target, 'O');
            if (n < 1 || n > idx.Objects.Count || idx.Objects[n - 1] == null) return "interact: object '" + a.Target + "' not found (see WORLD OBJECTS; ids change every turn)";
            Component c = idx.Objects[n - 1];
            UsableObject uo = c as UsableObject;
            if (uo == null) uo = c.GetComponent<UsableObject>();
            if (uo == null) return "interact: that object cannot be used. If it is a plant/rock/tree, attack it instead (aim at it, then attack)";
            float d = Vector2.Distance(b.transform.position, uo.transform.position);
            if (d >= 10f * uo.rangeMultiplier) return "interact: too far (" + d.ToString("0.0") + " >= " + (10f * uo.rangeMultiplier).ToString("0") + "). walk_to " + a.Target + " first";
            if (!b.conscious) return "cannot interact while unconscious";
            BuildingEntity be = uo.GetComponent<BuildingEntity>();
            string nm = be != null && !string.IsNullOrEmpty(be.fullName) ? Senser.Strip(be.fullName) : uo.gameObject.name.Replace("(Clone)", "");
            uo.gameObject.SendMessage("OnUse", SendMessageOptions.DontRequireReceiver);
            return "used " + nm + " (" + Senser.Strip(uo.toggleString) + ")";
        }

        string DoRemove(AiAction a, Body b, WorldIndex idx)
        {
            if (!string.IsNullOrEmpty(a.Limb))
            {
                Limb l = ResolveLimb(b, a.Limb);
                if (l == null) return "remove: limb '" + a.Limb + "' not found";
                TourniquetScript tq = l.GetComponent<TourniquetScript>();
                if (tq != null) { tq.TakeOff(); return "took the tourniquet off the " + l.name; }
                SplintLimb sp = l.GetComponent<SplintLimb>();
                if (sp != null) { sp.TakeOff(); return "took the splint off the " + l.name + " (it is back in your inventory or at your feet)"; }
                return "nothing attached to the " + l.name + " can be removed by hand";
            }
            int n = ParseIndex(a.Target, 'W');
            if (n < 1 || n > idx.Worn.Count || idx.Worn[n - 1] == null) return "remove: worn item '" + a.Target + "' not found (see WEARING)";
            Item it = idx.Worn[n - 1];
            string nm = Senser.ItemName(it);
            b.DropWearable(it);
            try { b.AutoPickUpItem(it); } catch { }
            if (b.GetAllWearables().Contains(it)) return "could not take off " + nm;
            return b.HoldingItem(it) ? "took off " + nm + " and put it in your inventory" : "took off " + nm + "; it is on the ground next to you (no free slot)";
        }

        static Container ContainerOf(Item bag) { return bag != null ? bag.GetComponent<Container>() : null; }

        string DoStore(AiAction a, Body b, WorldIndex idx)
        {
            Item it = AnyItem(a.Slot, a.Target, idx, b);
            Item bag = AnyItem(a.Slot2, a.Target2, idx, b);
            if (it == null) return "store: item to store not found (slot or target I#)";
            if (bag == null) return "store: container not found (slot2 or target2 = W#/I#)";
            Container cont = ContainerOf(bag);
            if (cont == null) return Senser.ItemLabel(bag) + " is not a container";
            if (it == bag) return "cannot store a container inside itself";
            if (b.GetAllWearables().Contains(it)) return "take " + Senser.ItemLabel(it) + " off first (remove), then store it";
            string nm = Senser.ItemName(it);
            if (!cont.CanHoldItem(it)) return "cannot store " + nm + " in " + Senser.ItemLabel(bag) + ": too heavy for it, or it only accepts certain item types";
            if (Vector2.Distance(it.transform.position, bag.transform.position) >= 10f) return "store: too far from the container";
            cont.UnloadItem(it);
            cont.LoadItem(it);
            try { PlayerCamera.main.PlayBackpackSound(); } catch { }
            return it.transform.parent == cont.transform ? "stored " + nm + " in " + Senser.ItemLabel(bag) : "could not store " + nm + " (containers cannot be nested inside other containers)";
        }

        string DoTake(AiAction a, Body b, WorldIndex idx)
        {
            Item bag = AnyItem(a.Slot, a.Target, idx, b);
            if (bag == null) return "take: container not found (slot = held container, or target W#/I#)";
            Container cont = ContainerOf(bag);
            if (cont == null) return Senser.ItemLabel(bag) + " is not a container";
            var contents = new List<Item>();
            foreach (Transform t in cont.transform) { Item ci = t.GetComponent<Item>(); if (ci != null) contents.Add(ci); }
            int k = a.Slot2 >= 0 ? a.Slot2 : 0;
            if (contents.Count == 0) return Senser.ItemLabel(bag) + " is empty";
            if (k >= contents.Count) return "take: the container only has " + contents.Count + " item(s) (#0-#" + (contents.Count - 1) + ")";
            Item it = contents[k];
            string nm = Senser.ItemName(it);
            cont.UnloadItem(it, b);
            try { b.AutoPickUpItem(it); } catch { }
            try { PlayerCamera.main.PlayBackpackSound(); } catch { }
            return b.HoldingItem(it) ? "took " + nm + " out of the container" : "took " + nm + " out; it is on the ground next to you (no free slot)";
        }

        string DoCombine(AiAction a, Body b, WorldIndex idx)
        {
            Item A = AnyItem(a.Slot, a.Target, idx, b);
            Item B = AnyItem(a.Slot2, a.Target2, idx, b);
            if (A == null || B == null) return "combine: needs two items (slot/target = the item you use, slot2/target2 = the item it is used on)";
            if (A == B) return "combine: choose two different items";
            string an = Senser.ItemLabel(A), bn = Senser.ItemLabel(B);
            try
            {
                if (B.battery != null && A.Stats.HasTag("tool")) { B.battery.UnloadBattery(); return "used " + an + " on " + bn + ": its battery was taken out"; }
                if (B.battery != null && A.Stats.HasTag("battery")) { B.battery.LoadBattery(A); return "put " + an + " into " + bn + " (check the item to confirm it has power now)"; }
                Container cont = ContainerOf(B);
                if (cont != null && ContainerOf(A) == null)
                {
                    AiAction sub = new AiAction { Slot = a.Slot, Target = a.Target, Slot2 = a.Slot2, Target2 = a.Target2 };
                    return DoStore(sub, b, idx);
                }
                if (b.CanCombine(B, A)) { b.CombineItems(B, A); return "combined " + an + " into " + bn + " (now " + Mathf.RoundToInt(B.condition * 100f) + "%)"; }
            }
            catch (Exception e) { return "combine failed: " + e.Message; }
            return "nothing happens when you use " + an + " on " + bn + " (not a tool+battery, battery+device, container or stackable pair)";
        }

        // ---------------------------------------------------------------- ropes / ladders
        bool StepClimb(AiAction a, Body b, WorldIndex idx, float dt, out string res)
        {
            res = null;
            Vector2 pos = b.transform.position;
            ropeIntent = 0.6f;
            if (!a.Flag) { a.Flag = true; a.Extra = 0f; }

            Climbable rope = null;
            if (!string.IsNullOrEmpty(a.Target) && a.Target[0] == 'C')
            {
                int n = ParseIndex(a.Target, 'C');
                if (n >= 1 && n <= idx.Climbs.Count) rope = idx.Climbs[n - 1];
            }
            Vector2 goal = default(Vector2); Transform gt = null;
            bool hasGoal = !string.IsNullOrEmpty(a.Target) && a.Target[0] != 'C' && Resolve(a.Target, idx, b, out goal, out gt);
            if (rope == null) rope = b.currentClimbable != null ? b.currentClimbable : Climbable.GetClosestClimbable(pos);

            if (a.Extra > 0f)   // already jumped off at the end: wait for the landing
            {
                a.Extra += dt;
                if (a.Extra > 1.0f) { WantMove = Vector2.zero; res = "left the rope, " + Moved(a, b); return true; }
                return false;
            }

            if (b.currentClimbable == null)
            {
                if (rope == null) { res = "climb: no rope/ladder known nearby"; return true; }
                ClimbableGrabInfo gi = rope.GetGrabInfo(pos);
                if (gi.distanceToPlayer < 0.9f) { WantMove = new Vector2(0f, 1f); }       // grab it right now
                else
                {
                    float dx = gi.position.x - pos.x, dy = gi.position.y - pos.y;
                    WantMove = new Vector2(Mathf.Abs(dx) > 0.25f ? Mathf.Sign(dx) : 0f, 0f);
                    if (dx != 0f) FacingDir = Mathf.Sign(dx);
                    if (dy > 0.8f && b.grounded && Mathf.Abs(dx) < 2.6f && jumpLock <= 0f) DoJumpHigh();   // jump TO the rope
                }
                if (a.T > 7f) { WantMove = Vector2.zero; res = "climb: could not reach the rope in time (it is at dx=" + (rope.GetGrabInfo(pos).position.x - pos.x).ToString("+0.0;-0.0") + ", dy=" + (rope.GetGrabInfo(pos).position.y - pos.y).ToString("+0.0;-0.0") + ")"; return true; }
                return false;
            }

            // on the rope
            float sec = Mathf.Clamp(a.Sec <= 0f ? 10f : a.Sec, 0.5f, 30f);
            if (hasGoal)
            {
                float dyg = goal.y - pos.y;
                if (Mathf.Abs(dyg) < 0.8f)
                {
                    float side = Mathf.Sign(goal.x - pos.x);
                    if (Mathf.Abs(goal.x - pos.x) < 0.6f) { WantMove = Vector2.zero; res = "reached the height of " + a.Target; return true; }
                    WantMove = new Vector2(side, 0f); FacingDir = side;
                    DoJumpHigh();                    // leap off the rope toward the target
                    a.Extra = 0.01f;
                    return false;
                }
                WantMove = new Vector2(0f, Mathf.Sign(dyg));
            }
            else
            {
                float dirY = a.Dir == "down" ? -1f : 1f;
                WantMove = new Vector2(0f, dirY);
                try
                {
                    float prog = CliProg != null ? (float)CliProg.GetValue(b) : 0f;
                    if (dirY > 0f && prog >= b.currentClimbable.totalLength - 0.4f)
                    {
                        if (a.Exit == "left" || a.Exit == "right")
                        {
                            float side = a.Exit == "left" ? -1f : 1f;
                            WantMove = new Vector2(side, 0f); FacingDir = side; DoJumpHigh(); a.Extra = 0.01f;
                            return false;
                        }
                        WantMove = Vector2.zero;
                        res = "at the top of the rope/ladder (use jump with dir left/right, or climb with exit left/right, to get off)";
                        return true;
                    }
                }
                catch { }
            }
            if (a.T >= sec) { WantMove = Vector2.zero; res = "climbed for " + sec.ToString("0.#") + "s, " + Moved(a, b); return true; }
            return false;
        }

        // ---------------------------------------------------------------- get on top of a ledge / wall
        bool StepMount(AiAction a, Body b, float dt, out string res)
        {
            res = null;
            Bounds bb = b.col.bounds;
            float feet = bb.min.y;
            int ground = LayerMask.GetMask("Ground");
            if (!a.Flag)
            {
                a.Flag = true;
                float side = a.Dir == "left" ? -1f : (a.Dir == "right" ? 1f : 0f);
                if (side == 0f)
                {
                    float l = RayDist(bb.center, Vector2.left, 6f), r = RayDist(bb.center, Vector2.right, 6f);
                    side = (l >= 0f && (r < 0f || l <= r)) ? -1f : 1f;
                }
                RaycastHit2D hit = Physics2D.Raycast(bb.center, new Vector2(side, 0f), 7f, ground);
                if (hit.collider == null) { res = "mount: no wall or obstacle within 7 blocks on the " + (side < 0f ? "left" : "right"); return true; }
                float probeX = hit.point.x + side * 0.4f;
                float y = feet + 0.15f;
                while (Physics2D.OverlapPoint(new Vector2(probeX, y), ground) != null && y < feet + 12f) y += 0.25f;
                a.Extra = side; a.Extra2 = y;
                float h = y - feet;
                a.Pending = h.ToString("0.0");
                if (h > 9f) { res = "mount: the obstacle is " + h.ToString("0.0") + " blocks high - too high. Use a rope/ladder, another route, or help from another player"; return true; }
                if (h < 0.4f) { res = "mount: there is no obstacle to climb on that side"; return true; }
            }
            float s = a.Extra, topY = a.Extra2;
            WantMove = new Vector2(s, 0f); FacingDir = s;
            a.Cool -= dt;
            float hRem = topY - feet;
            if (b.grounded && hRem <= 0.35f && a.T > 0.3f) { WantMove = Vector2.zero; res = "reached the top (height " + a.Pending + "), " + Moved(a, b); return true; }
            if (a.T > 13f || a.Count > 9) { WantMove = Vector2.zero; res = "mount failed after " + a.Count + " jumps (height " + a.Pending + "): cannot get on top. Try a rope/ladder, a lower spot, or another route"; return true; }

            bool touching = Touching(b, s) || Sliding(b, s < 0f);
            if (a.Cool <= 0f && jumpLock <= 0f)
            {
                if (b.grounded)
                {
                    float wd = RayDist(bb.center, new Vector2(s, 0f), 3f);
                    if (wd >= 0f && wd - bb.extents.x < 0.9f) { DoJumpHigh(); a.Count++; a.Cool = 0.4f; }
                }
                else if (touching && feet < topY - 0.2f && b.rb.velocity.y < 4f)
                {
                    DoJumpHigh();                 // kick off the wall; WantMove keeps pushing back toward it so we land on top
                    a.Count++; a.Cool = 0.3f;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- path finding for walk_to / follow
        bool NeedsNav(AiAction a, Body b, WorldIndex idx)
        {
            if (a.Pending == "nav") return true;
            if (a.Pending == "simple") return false;
            Vector2 t; Transform tt;
            if (!Resolve(a.Target, idx, b, out t, out tt)) return false;
            Vector2 pos = b.transform.position;
            float dx = t.x - pos.x, dy = t.y - pos.y;
            if (Mathf.Abs(dx) < 1.6f && Mathf.Abs(dy) < 1.6f) { a.Pending = "simple"; return false; }
            bool blocked = Physics2D.Linecast(pos + Vector2.up * 0.3f, t, LayerMask.GetMask("Ground"));
            float drop;
            if (dy > 1.3f || dy < -2.5f || blocked || (Mathf.Abs(dx) > 0.5f && LedgeAhead(b, Mathf.Sign(dx), out drop))) { a.Pending = "nav"; navPath = null; navGrid = null; navAirDir = Mathf.Sign(dx); return true; }
            a.Pending = "simple";
            return false;
        }

        bool StepNav(AiAction a, Body b, WorldIndex idx, float dt, out string res)
        {
            res = null;
            bool follow = a.Do == "follow";
            float sec = Mathf.Clamp(a.Sec <= 0f ? (follow ? 12f : 18f) : a.Sec, 1f, follow ? 40f : 35f);
            Vector2 t; Transform tt;
            if (!Resolve(a.Target, idx, b, out t, out tt)) { WantMove = Vector2.zero; res = a.Do + ": target '" + a.Target + "' not found (gone, picked up, or the id is from an old turn)"; return true; }
            Vector2 pos = b.transform.position;
            char kind = a.Target.Length > 0 ? a.Target[0] : 'X';
            float arrive = kind == 'I' ? 1.5f : (kind == 'C' ? 0.6f : 2.4f);
            float dx = t.x - pos.x, dy = t.y - pos.y;
            string where = "(dx=" + dx.ToString("+0.0;-0.0;0.0") + " dy=" + dy.ToString("+0.0;-0.0;0.0") + ")";

            if (Mathf.Abs(dx) <= arrive && Mathf.Abs(dy) <= 2.5f)
            {
                WantMove = Vector2.zero; WantCrouch = false;
                if (!follow || a.T >= sec) { res = (follow ? "followed " : "arrived next to ") + a.Target + " " + where; return true; }
                return false;
            }
            if (a.T >= sec) { WantMove = Vector2.zero; res = a.Do + " " + a.Target + " not finished after " + sec.ToString("0") + "s: still " + where + (navPath == null ? " (no route found)" : ""); return true; }

            if (a.Count == 0) { a.Count = 1; navPath = null; navGrid = null; a.Cool = 0f; }
            if (!b.grounded && b.currentClimbable == null) { WantMove = new Vector2(navAirDir, 0f); return false; }

            // stuck detection
            if (b.grounded && Mathf.Abs(WantMove.x) > 0.1f && Mathf.Abs(b.rb.velocity.x) < 0.25f) a.Extra += dt; else a.Extra = 0f;
            if (a.Extra > 1.1f) { a.Extra = 0f; navGrid = null; a.Cool = 0f; a.Fail++; if (jumpLock <= 0f) DoJumpHigh(); }

            a.Cool -= dt;
            if (navPath == null || a.Cool <= 0f)
            {
                a.Cool = 0.45f;
                if (navGrid == null || Time.time - navGridT > 1.5f || Vector2.Distance(navGridC, pos) > 5f) { navGrid = NavGrid.Build(pos, b); navGridT = Time.time; navGridC = pos; }
                int sx, sy;
                if (navGrid.FindStart(pos.x, b.col.bounds.min.y, out sx, out sy))
                {
                    float tx = t.x, ty = t.y, ar = arrive + 0.3f;
                    NavResult r = navGrid.Search(sx, sy, (x, y) => Mathf.Abs(x + 0.5f - tx) <= ar && Mathf.Abs(y + 0.5f - ty) <= 1.8f, tx, ty, 170f);
                    if (r.Found || r.Partial) { navPath = r.Path; if (r.Found) a.Fail = 0; }
                    else { navPath = null; a.Fail++; }
                }
                else { navPath = null; navGrid = null; a.Fail++; }
                if (a.Fail >= 4) { a.Pending = "simple"; WantMove = Vector2.zero; return false; }    // give up planning; the plain walker takes over
            }
            if (navPath == null || navPath.Count < 2) { WantMove = new Vector2(Mathf.Sign(dx), 0f); return false; }

            NavStep from = navPath[0], to = navPath[1];
            float cx = from.X + 0.5f;
            float dir = to.Dir != 0 ? to.Dir : Mathf.Sign(to.X + 0.5f - pos.x);
            navAirDir = dir;
            if (dir != 0f) FacingDir = dir;
            WantCrouch = to.Crouch || from.Crouch;
            switch (to.Kind)
            {
                case 0:
                {
                    float ddx = to.X + 0.5f - pos.x;
                    WantMove = new Vector2(Mathf.Abs(ddx) > 0.1f ? Mathf.Sign(ddx) : dir, 0f);
                    break;
                }
                case 1:
                case 3:
                {
                    bool atEdge = to.Kind == 3 ? Mathf.Abs(pos.x - cx) < 0.3f : (dir > 0f ? pos.x >= cx - 0.05f : pos.x <= cx + 0.05f);
                    if (!atEdge) WantMove = new Vector2(Mathf.Sign(cx - pos.x), 0f);
                    else
                    {
                        WantMove = new Vector2(to.Kind == 3 ? 0f : dir, 0f);
                        if (b.grounded && jumpLock <= 0f) { DoJumpHigh(); if (to.Kind == 3) navAirDir = Mathf.Sign(to.X + 0.5f - pos.x); }
                    }
                    break;
                }
                default:
                    WantMove = new Vector2(dir, 0f);
                    break;
            }
            return false;
        }
    }
}
