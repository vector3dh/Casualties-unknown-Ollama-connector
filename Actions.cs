using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesOllama
{
    public class AiAction
    {
        public string Do = "", Dir = "", Target = "", Limb = "", Text = "", Target2 = "", Exit = "";
        public float Sec, Dx, Dy;
        public bool HasDxDy;
        public int Slot = -1, Slot2 = -1, Times = 1;

        // runtime state
        public float T, Cool, Extra, Extra2;
        public bool Started, Flag, Flag2;
        public int Count, Fail;
        public Vector2 StartPos;
        public string Pending = "";
        public Item ItemRef;
        public Limb LimbRef;

        public string Describe()
        {
            var parts = new List<string> { Do };
            if (!string.IsNullOrEmpty(Dir)) parts.Add(Dir);
            if (!string.IsNullOrEmpty(Target)) parts.Add(Target);
            if (Slot >= 0) parts.Add("slot " + Slot);
            if (Slot2 >= 0) parts.Add("slot2 " + Slot2);
            if (!string.IsNullOrEmpty(Limb)) parts.Add("limb " + Limb);
            if (Sec > 0) parts.Add(Sec.ToString("0.#") + "s");
            if (HasDxDy) parts.Add("dx " + Dx.ToString("0.#") + " dy " + Dy.ToString("0.#"));
            if (Times > 1) parts.Add("x" + Times);
            if (!string.IsNullOrEmpty(Text)) parts.Add("\"" + (Text.Length > 30 ? Text.Substring(0, 30) + "..." : Text) + "\"");
            return string.Join(" ", parts.ToArray());
        }

        static float F(JToken t, float def)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (float)t;
            float v;
            return float.TryParse(t.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
        }
        static int I(JToken t, int def) { return (t == null || t.Type == JTokenType.Null) ? def : Mathf.RoundToInt(F(t, def)); }
        static string S(JToken t) { return (t == null || t.Type == JTokenType.Null) ? "" : t.ToString().Trim(); }

        public static AiAction FromJson(JObject o)
        {
            var a = new AiAction();
            a.Do = (S(o["do"]) + S(o["action"]) + S(o["type"])).ToLowerInvariant().Trim();
            if (a.Do.Length == 0) return null;
            a.Dir = S(o["dir"]).ToLowerInvariant();
            a.Target = S(o["target"]).ToUpperInvariant();
            a.Limb = S(o["limb"]);
            a.Text = S(o["text"]);
            a.Target2 = S(o["target2"]).ToUpperInvariant();
            a.Exit = S(o["exit"]).ToLowerInvariant();
            a.Sec = F(o["sec"], 0f);
            a.Slot = I(o["slot"], -1);
            a.Slot2 = I(o["slot2"], -1);
            a.Times = Mathf.Max(1, I(o["times"], 1));
            if (o["dx"] != null || o["dy"] != null) { a.HasDxDy = true; a.Dx = F(o["dx"], 0f); a.Dy = F(o["dy"], 0f); }
            return a;
        }
    }

    /// <summary>
    /// Executes AiActions over time by driving the game's own Body methods/fields.
    /// A Harmony postfix on PlayerCamera.Update calls Apply() every frame to push the controls into the Body.
    /// </summary>
    public partial class Executor
    {
        readonly Plugin P;
        public Vector2 WantMove;
        public bool WantCrouch;
        public float FacingDir = 1f;
        bool jumpPending;
        float jumpHold;
        float jumpLock;
        Transform aimTf;
        Vector2? aimRel;

        static readonly FieldInfo JumpCdF = AccessTools.Field(typeof(Body), "jumpCooldown");
        static readonly FieldInfo SlideL = AccessTools.Field(typeof(Body), "slidingLeft");
        static readonly FieldInfo SlideR = AccessTools.Field(typeof(Body), "slidingRight");
        static readonly MethodInfo BandageStep = AccessTools.Method(typeof(BandageMinigame), "DoBandageAction");

        public Executor(Plugin p) { P = p; }

        public void Update(float dt) { if (jumpLock > 0f) jumpLock -= dt; if (ropeIntent > 0f) ropeIntent -= dt; }

        public void ResetControls() { WantMove = Vector2.zero; WantCrouch = false; jumpPending = false; jumpHold = 0f; ResetAim(); }
        public void StopMotion() { WantMove = Vector2.zero; WantCrouch = false; }
        public void ResetAim() { aimTf = null; aimRel = null; }
        bool AimIsSet { get { return aimTf != null || aimRel.HasValue; } }

        // ---------------------------------------------------------------- per-frame application
        public void Apply(Body b)
        {
            var A = P.Cfg.Allow;
            Vector2 mv = A.Move ? WantMove : Vector2.zero;
            mv = RopeAssist(b, mv);
            b.moveDir = mv;
            b.crouching = (WantCrouch || AutoCrouchNeeded(b)) && A.Crouch;
            b.targetLookPos = AimWorld(b);
            if (jumpPending)
            {
                jumpPending = false;
                b.Jump();
                b.endedJump = false;
            }
            if (jumpHold > 0f)
            {
                jumpHold -= Time.deltaTime;
                b.endedJump = jumpHold <= 0f;
            }
        }

        Vector3 AimWorld(Body b)
        {
            Vector2 p = b.transform.position;
            if (aimTf != null) { Vector2 t = aimTf.position; return new Vector3(t.x, t.y, 0f); }
            if (aimRel.HasValue) { Vector2 t = p + aimRel.Value; return new Vector3(t.x, t.y, 0f); }
            return new Vector3(p.x + FacingDir * 6f, p.y + 0.3f, 0f);
        }

        // ---------------------------------------------------------------- helpers
        static string Denied(string verb, PermissionSettings A)
        {
            bool ok;
            switch (verb)
            {
                case "move": case "walk_to": case "follow": case "climb": ok = A.Move; break;
                case "jump": case "leap": case "walljump": ok = A.Jump; break;
                case "crouch": case "stand": ok = A.Crouch; break;
                case "aim": ok = A.Aim; break;
                case "attack": ok = A.Attack; break;
                case "use": ok = A.UseItems; break;
                case "apply": case "relocate": case "pull_shrapnel": ok = A.ApplyToLimbs; break;
                case "grab": ok = A.Grab; break;
                case "drop": case "throw": ok = A.DropThrow; break;
                case "wear": ok = A.Wear; break;
                case "swap_hands": case "swap_slots": ok = A.Inventory; break;
                case "craft": ok = A.Craft; break;
                case "inspect": ok = A.Inspect; break;
                case "say": ok = A.Speak; break;
                case "wait": ok = true; break;
                default: return DeniedExtra(verb, A);
            }
            return ok ? null : "NOT ALLOWED by the human: " + verb;
        }

        static Vector2 DirVec(string d)
        {
            switch (d) { case "left": return Vector2.left; case "right": return Vector2.right; case "up": return Vector2.up; case "down": return Vector2.down; default: return Vector2.zero; }
        }

        static int ParseIndex(string s, char prefix)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            s = s.Trim();
            if (s.Length > 0 && char.ToUpperInvariant(s[0]) == char.ToUpperInvariant(prefix)) s = s.Substring(1);
            int n; return int.TryParse(s, out n) ? n : -1;
        }

        static bool Resolve(string id, WorldIndex idx, Body me, out Vector2 pos, out Transform tf)
        {
            pos = default(Vector2); tf = null;
            if (string.IsNullOrEmpty(id)) return false;
            string up = id.ToUpperInvariant();
            if (up == "PTR" || up == "POINTER") { if (!Pointer.Set) return false; pos = Pointer.World; tf = Pointer.Tf; return true; }
            char c = up[0];
            int n = ParseIndex(up, c);
            if (n < 1) return false;
            switch (c)
            {
                case 'I': if (n <= idx.Items.Count && idx.Items[n - 1] != null) { tf = idx.Items[n - 1].transform; pos = tf.position; return true; } break;
                case 'E': if (n <= idx.Creatures.Count && idx.Creatures[n - 1] != null) { tf = idx.Creatures[n - 1].transform; pos = tf.position; return true; } break;
                case 'H': if (n <= idx.Hazards.Count && idx.Hazards[n - 1] != null) { tf = idx.Hazards[n - 1].transform; pos = tf.position; return true; } break;
                case 'P': if (n <= idx.Players.Count && idx.Players[n - 1] != null) { tf = idx.Players[n - 1].transform; pos = tf.position; return true; } break;
                case 'C': if (n <= idx.Climbs.Count && idx.Climbs[n - 1] != null) { pos = idx.Climbs[n - 1].GetGrabInfo(me.transform.position).position; return true; } break;
                case 'O': if (n <= idx.Objects.Count && idx.Objects[n - 1] != null) { tf = idx.Objects[n - 1].transform; pos = tf.position; return true; } break;
            }
            return false;
        }

        static Item ItemFromTarget(string id, WorldIndex idx)
        {
            int n = ParseIndex(id, 'I');
            return (n >= 1 && n <= idx.Items.Count) ? idx.Items[n - 1] : null;
        }

        static Limb ResolveLimb(Body b, string s)
        {
            if (string.IsNullOrEmpty(s) || b.limbs == null) return null;
            int n = ParseIndex(s, 'L');
            if (n >= 0 && n < b.limbs.Length) return b.limbs[n];
            string low = s.ToLowerInvariant();
            foreach (var l in b.limbs) if (l != null && l.name.ToLowerInvariant() == low) return l;
            foreach (var l in b.limbs) if (l != null && (l.name.ToLowerInvariant().Contains(low) || (!string.IsNullOrEmpty(l.fullName) && Senser.Strip(l.fullName).ToLowerInvariant().Contains(low)))) return l;
            return null;
        }

        static Item SlotItem(Body b, int slot)
        {
            if (b.slots == null || slot < 0 || slot >= b.slots.Length) return null;
            return b.HoldingItem(slot) ? b.GetItem(slot) : null;
        }

        static float JumpCd(Body b) { try { return JumpCdF != null ? (float)JumpCdF.GetValue(b) : 0f; } catch { return 0f; } }
        static bool Sliding(Body b, bool left) { try { FieldInfo f = left ? SlideL : SlideR; return f != null && (bool)f.GetValue(b); } catch { return false; } }

        static bool Touching(Body b, float side)
        {
            Bounds bb = b.col.bounds;
            RaycastHit2D h = Physics2D.Raycast(bb.center, new Vector2(side, 0f), bb.extents.x + 0.4f, LayerMask.GetMask("Ground"));
            return h.collider != null;
        }

        static bool LedgeAhead(Body b, float dirx, out float drop)
        {
            drop = 0f;
            if (!b.grounded || Mathf.Abs(dirx) < 0.1f) return false;
            Bounds bb = b.col.bounds;
            Vector2 o = new Vector2(bb.center.x + Mathf.Sign(dirx) * (bb.extents.x + 0.7f), bb.min.y + 0.3f);
            RaycastHit2D h = Physics2D.Raycast(o, Vector2.down, 9f, LayerMask.GetMask("Ground"));
            if (h.collider == null) { drop = 9f; return true; }
            drop = h.distance - 0.3f;
            return false;
        }

        void DoJump() { if (!P.Cfg.Allow.Jump) return; jumpPending = true; jumpHold = 0.35f; jumpLock = 0.5f; }

        void AutoJump(AiAction a, Body b, float dirx)
        {
            if (!P.Cfg.AutoJumpHelper || !P.Cfg.Allow.Jump || Mathf.Abs(dirx) < 0.1f) return;
            if (a.T > 0.35f && b.grounded && jumpLock <= 0f && Mathf.Abs(b.rb.velocity.x) < 0.4f && Touching(b, Mathf.Sign(dirx))) DoJump();
        }

        static string Moved(AiAction a, Body b)
        {
            Vector2 d = (Vector2)b.transform.position - a.StartPos;
            return "moved dx=" + d.x.ToString("+0.0;-0.0;0.0") + " dy=" + d.y.ToString("+0.0;-0.0;0.0");
        }

        static string HealthOf(Transform t)
        {
            if (t == null) return "";
            var be = t.GetComponent<BuildingEntity>();
            return be != null ? Mathf.RoundToInt(be.health).ToString() : "";
        }

        static string LimbState(Limb l)
        {
            return "bleeding " + l.bleedAmount.ToString("0.0") + ", pain " + Mathf.RoundToInt(l.pain) + ", skin " + Mathf.RoundToInt(l.skinHealth);
        }

        // ---------------------------------------------------------------- the action interpreter
        /// <summary>Advances one action. Returns true when it is finished (and sets res).</summary>
        public bool Step(AiAction a, Body b, WorldIndex idx, float dt, out string res)
        {
            res = null;
            Vector2 pos = b.transform.position;

            if (!a.Started)
            {
                a.Started = true;
                a.StartPos = pos;
                string deny = Denied(a.Do, P.Cfg.Allow);
                if (deny != null) { res = deny; return true; }
            }
            a.T += dt;
            if (UseExtra(a, b, idx)) return StepExtra(a, b, idx, dt, out res);

            switch (a.Do)
            {
                // ------------------------------------------------ movement
                case "move":
                {
                    float sec = Mathf.Clamp(a.Sec <= 0f ? 1f : a.Sec, 0.1f, 6f);
                    Vector2 d = DirVec(a.Dir);
                    if (d == Vector2.zero) { res = "move needs dir = left/right/up/down"; return true; }
                    if (d.x != 0f)
                    {
                        float drop;
                        if (P.Cfg.LedgeGuard && LedgeAhead(b, d.x, out drop)) { WantMove = Vector2.zero; res = "stopped at the edge of a big drop (no ground within 9 blocks ahead). Use 'leap' to jump a gap, or choose another route"; return true; }
                        FacingDir = d.x;
                    }
                    WantMove = d;
                    AutoJump(a, b, d.x);
                    if (a.T >= sec)
                    {
                        WantMove = Vector2.zero;
                        string m = Moved(a, b);
                        Vector2 dd = (Vector2)b.transform.position - a.StartPos;
                        if (d.x != 0f && Mathf.Abs(dd.x) < 0.5f && sec >= 0.8f) m += "  WARNING: barely moved - blocked by a wall/obstacle. Try jump, walljump, or another way";
                        res = m; return true;
                    }
                    return false;
                }

                case "walk_to":
                case "follow":
                {
                    bool follow = a.Do == "follow";
                    float sec = Mathf.Clamp(a.Sec <= 0f ? (follow ? 8f : 6f) : a.Sec, 0.5f, follow ? 30f : 12f);
                    Vector2 t; Transform tt;
                    if (!Resolve(a.Target, idx, b, out t, out tt)) { WantMove = Vector2.zero; res = a.Do + ": target '" + a.Target + "' not found (gone, picked up, or the id is from an old turn)"; return true; }
                    float dx = t.x - pos.x, dy = t.y - pos.y;
                    char kind = a.Target.Length > 0 ? a.Target[0] : 'X';
                    float arrive = kind == 'I' ? 1.5f : (kind == 'C' ? 0.6f : 2.4f);
                    if (Mathf.Abs(dx) <= arrive && Mathf.Abs(dy) <= 2.5f)
                    {
                        WantMove = Vector2.zero;
                        if (kind == 'C' && Mathf.Abs(dy) > 0.5f) WantMove = new Vector2(0f, Mathf.Sign(dy));
                        if (!follow && kind != 'C') { res = "arrived next to " + a.Target + " (dx=" + dx.ToString("+0.0;-0.0;0.0") + " dy=" + dy.ToString("+0.0;-0.0;0.0") + ")"; return true; }
                        if (a.T >= sec) { WantMove = Vector2.zero; res = (follow ? "followed " : "reached ") + a.Target; return true; }
                        return false;
                    }
                    float dirx = Mathf.Sign(dx);
                    float drop;
                    if (P.Cfg.LedgeGuard && dy > -6f && LedgeAhead(b, dirx, out drop)) { WantMove = Vector2.zero; res = a.Do + " " + a.Target + ": stopped at a big drop on the way (find another route or leap)"; return true; }
                    if (Mathf.Abs(dx) > arrive * 0.6f) { WantMove = new Vector2(dirx, 0f); FacingDir = dirx; } else WantMove = Vector2.zero;
                    if (dy > 1.2f && Mathf.Abs(dx) < 3f && b.grounded && jumpLock <= 0f) DoJump();
                    AutoJump(a, b, dirx);
                    if (a.T >= sec)
                    {
                        WantMove = Vector2.zero;
                        res = a.Do + " " + a.Target + (follow ? " finished" : " not finished") + ": still dx=" + dx.ToString("+0.0;-0.0;0.0") + " dy=" + dy.ToString("+0.0;-0.0;0.0")
                              + (Mathf.Abs(dy) > 2.5f ? " (different height - find another route)" : "");
                        return true;
                    }
                    return false;
                }

                case "jump":
                {
                    if (!a.Flag)
                    {
                        a.Flag = true;
                        if (!b.grounded && b.currentClimbable == null) { res = "cannot jump: not on the ground (to jump off a wall use walljump)"; return true; }
                        Vector2 d = DirVec(a.Dir);
                        WantMove = new Vector2(d.x, 0f);
                        if (d.x != 0f) FacingDir = d.x;
                        DoJump();
                    }
                    if (a.T >= 0.7f) { WantMove = Vector2.zero; res = "jumped, " + Moved(a, b); return true; }
                    return false;
                }

                case "leap":   // run-up + jump to cross a gap
                {
                    Vector2 d = DirVec(a.Dir);
                    if (d.x == 0f) { res = "leap needs dir left or right"; return true; }
                    float run = Mathf.Clamp(a.Sec <= 0f ? 0.45f : a.Sec, 0.1f, 2f);
                    FacingDir = d.x; WantMove = new Vector2(d.x, 0f);
                    if (!a.Flag && a.T >= run)
                    {
                        a.Flag = true;
                        if (!b.grounded) { WantMove = Vector2.zero; res = "leap: not on the ground"; return true; }
                        DoJump(); a.Extra = a.T;
                    }
                    if (a.Flag && a.T - a.Extra > 1.1f) { WantMove = Vector2.zero; res = "leaped, " + Moved(a, b) + (b.grounded ? "" : " (still in the air)"); return true; }
                    return false;
                }

                case "walljump":   // zig-zag up a wall / shaft
                {
                    int times = Mathf.Clamp(a.Times, 1, 8);
                    if (!a.Started || !a.Flag)
                    {
                        a.Flag = true;
                        a.Extra = a.Dir == "left" ? -1f : (a.Dir == "right" ? 1f : (Touching(b, -1f) ? -1f : 1f));
                        a.Cool = 0f;
                    }
                    float side = a.Extra;
                    WantMove = new Vector2(side, 0f); FacingDir = side;
                    a.Cool -= dt;
                    if (a.Flag2)      // we asked for a jump last frame: did the game accept it?
                    {
                        a.Flag2 = false;
                        if (JumpCd(b) > 0.1f)
                        {
                            if (a.Extra2 > 0.5f)
                            {
                                a.Count++; a.Extra = -side; a.Cool = 0.28f; a.Fail = 0;
                                if (a.Count >= times) { WantMove = Vector2.zero; res = "wall-jumped " + a.Count + "x, " + Moved(a, b); return true; }
                            }
                            else a.Cool = 0.3f;
                        }
                        else { a.Fail++; a.Cool = 0.1f; }
                        return false;
                    }
                    bool touching = Touching(b, side) || Sliding(b, side < 0f);
                    if (a.Cool <= 0f && jumpLock <= 0f)
                    {
                        if (b.grounded && a.Count == 0 && touching) { DoJump(); a.Flag2 = true; a.Extra2 = 0f; }
                        else if (!b.grounded && touching) { DoJump(); a.Flag2 = true; a.Extra2 = 1f; jumpLock = 0.1f; }
                    }
                    if (b.grounded && !touching && a.T > 1.2f) { WantMove = Vector2.zero; res = "walljump: no wall within reach on the " + (side < 0f ? "left" : "right") + " (" + a.Count + " wall jumps done)"; return true; }
                    if (b.grounded && a.Count > 0 && a.T > 1f) { WantMove = Vector2.zero; res = "wall-jumped " + a.Count + "x then landed, " + Moved(a, b); return true; }
                    if (a.T > 9f || a.Fail > 8) { WantMove = Vector2.zero; res = "walljump stopped after " + a.Count + " wall jumps (" + (a.Fail > 8 ? "the game refused: the same wall twice in a row needs the other wall in between or climbing claws" : "timeout") + "), " + Moved(a, b); return true; }
                    return false;
                }

                case "climb":
                {
                    float sec = Mathf.Clamp(a.Sec <= 0f ? 2f : a.Sec, 0.3f, 10f);
                    float dy = a.Dir == "down" ? -1f : 1f;
                    WantMove = new Vector2(0f, dy);
                    if (a.T > 1f && b.currentClimbable == null && dy > 0f) { WantMove = Vector2.zero; res = "climb: nothing to grab (stand right at a rope/ladder, see CLIMBABLES)"; return true; }
                    if (a.T >= sec) { WantMove = Vector2.zero; res = (b.currentClimbable != null ? "on a rope/ladder, " : "") + Moved(a, b); return true; }
                    return false;
                }

                case "crouch":
                {
                    float sec = Mathf.Clamp(a.Sec <= 0f ? 1f : a.Sec, 0.2f, 5f);
                    WantCrouch = true;
                    if (a.T >= sec) { WantCrouch = false; res = "crouched " + sec.ToString("0.#") + "s"; return true; }
                    return false;
                }

                case "stand": WantCrouch = false; res = "standing"; return true;

                case "wait":
                {
                    float sec = Mathf.Clamp(a.Sec <= 0f ? 1f : a.Sec, 0.1f, 10f);
                    WantMove = Vector2.zero;
                    if (a.T >= sec) { res = "waited " + sec.ToString("0.#") + "s"; return true; }
                    return false;
                }

                // ------------------------------------------------ aiming / combat
                case "aim":
                {
                    Vector2 t; Transform tt;
                    if (!string.IsNullOrEmpty(a.Target))
                    {
                        if (!Resolve(a.Target, idx, b, out t, out tt)) { res = "aim: target '" + a.Target + "' not found"; return true; }
                        if (tt != null) { aimTf = tt; aimRel = null; } else { aimTf = null; aimRel = t - pos; }
                        res = "aiming at " + a.Target;
                    }
                    else if (a.HasDxDy)
                    {
                        aimTf = null; aimRel = new Vector2(a.Dx, a.Dy);
                        if (Mathf.Abs(a.Dx) > 0.5f) FacingDir = Mathf.Sign(a.Dx);
                        res = "aiming at dx=" + a.Dx.ToString("0.#") + " dy=" + a.Dy.ToString("0.#");
                    }
                    else res = "aim needs a target id or dx/dy";
                    return true;
                }

                case "attack":
                {
                    if (!b.conscious) { res = "cannot attack: unconscious"; return true; }
                    int times = Mathf.Clamp(a.Times, 1, 8);
                    if (!a.Flag)
                    {
                        a.Flag = true;
                        if (!AimIsSet)
                        {
                            Transform best = null; float bd = 8f;
                            foreach (var g in idx.Creatures)
                            {
                                if (g == null) continue;
                                float dd = Vector2.Distance(pos, g.transform.position);
                                if (dd < bd) { bd = dd; best = g.transform; }
                            }
                            if (best != null) aimTf = best;
                        }
                        a.Pending = HealthOf(aimTf);
                        a.Cool = 0.25f;
                    }
                    a.Cool -= dt;
                    if (a.Cool > 0f) return false;
                    if (a.Count < times)
                    {
                        if (b.allowUseItem) { b.UseItemInHand(); a.Count++; a.Cool = 0.45f; }
                        else { a.Cool = 0.2f; a.Fail++; if (a.Fail > 10) { res = "cannot attack right now (blocked by the game state, e.g. a minigame is open)"; return true; } }
                        return false;
                    }
                    Item held = b.GetItem(b.handSlot);
                    string what = held != null ? Senser.ItemName(held) : "bare hands";
                    string after = HealthOf(aimTf);
                    res = "attacked " + a.Count + "x with " + what + (aimTf != null ? " at " + (string.IsNullOrEmpty(a.Target) ? "the aim point" : a.Target) : " straight ahead")
                          + (a.Pending.Length > 0 && after.Length > 0 ? "; target hp " + a.Pending + " -> " + after : (a.Pending.Length > 0 ? "; target died or vanished" : ""));
                    return true;
                }

                // ------------------------------------------------ items
                case "use":
                {
                    Item it = SlotItem(b, a.Slot);
                    if (!a.Flag)
                    {
                        a.Flag = true;
                        if (it == null) { res = "use: slot " + a.Slot + " is empty or invalid"; return true; }
                        if (!b.conscious) { res = "cannot use items while unconscious"; return true; }
                        if (!it.Stats.usable) { res = Senser.ItemName(it) + " is not directly usable" + (it.Stats.ActuallyUsableOnLimb(it) ? " - use 'apply' with a limb" : it.Stats.wearable ? " - use 'wear'" : ""); return true; }
                        if (it.Stats.usableWithLMB) { res = Senser.ItemName(it) + " is a held tool/weapon: put it in your active hand and use 'attack'"; return true; }
                        if (!b.allowUseItem) { res = "cannot use items right now"; return true; }
                        a.Pending = Senser.ItemName(it);
                        b.UseItem(it);
                        return false;
                    }
                    if (a.T >= 0.4f) { res = "used " + a.Pending + (it != null ? "; now " + Mathf.RoundToInt(it.condition * 100f) + "%" : "; item used up"); return true; }
                    return false;
                }

                case "apply":   // bandages, splints, injections... incl. the hand-operated bandage/syringe minigames
                {
                    if (!a.Flag)
                    {
                        a.Flag = true;
                        Item it = SlotItem(b, a.Slot); Limb limb = ResolveLimb(b, a.Limb);
                        if (it == null) { res = "apply: slot " + a.Slot + " is empty or invalid"; return true; }
                        if (limb == null) { res = "apply: limb '" + a.Limb + "' not found (use L0, L1... from the limb list)"; return true; }
                        if (!b.conscious) { res = "cannot treat wounds while unconscious"; return true; }
                        if (limb.dismembered) { res = "that limb is gone"; return true; }
                        if (!it.Stats.ActuallyUsableOnLimb(it)) { res = Senser.ItemName(it) + " cannot be applied to a limb" + (it.Stats.usable ? " - use 'use'" : ""); return true; }
                        if (MinigameBase.main != null && MinigameBase.main.currentMinigame != null) { res = "another treatment is already in progress"; return true; }
                        a.ItemRef = it; a.LimbRef = limb; a.Extra = it.condition;
                        a.Pending = Senser.ItemLabel(it) + " -> " + limb.name + " (before: " + LimbState(limb) + ")";
                        PlayerCamera.main.selectedLimb = limb;
                        PlayerCamera.main.ApplyWoundItem(it);      // the game's own code path (also multiplayer-synced)
                        return false;
                    }
                    Item item = a.ItemRef; Limb lb = a.LimbRef;
                    Minigame mg = MinigameBase.main != null ? MinigameBase.main.currentMinigame : null;
                    if (mg == null)
                    {
                        if (a.T < 0.4f) return false;      // give the minigame a moment to open
                        bool changed = a.Count > 0 || item == null || Mathf.Abs(item.condition - a.Extra) > 0.001f;
                        res = (changed ? "applied " : "tried to apply ") + a.Pending + "; after: " + LimbState(lb) + (item != null ? "; item " + Mathf.RoundToInt(item.condition * 100f) + "%" : "; item used up")
                              + (changed ? "" : "  (nothing happened - see GAME MESSAGES for the reason)");
                        return true;
                    }
                    float maxSec = a.Sec > 0f ? Mathf.Clamp(a.Sec, 1f, 20f) : 9f;
                    bool finish;
                    if (mg is BandageMinigame)
                    {
                        var bm = (BandageMinigame)mg;
                        float acc = a.Extra2 + dt * 30f; int n = (int)acc; a.Extra2 = acc - n;
                        for (int i = 0; i < n; i++)
                        {
                            try { if (BandageStep != null) BandageStep.Invoke(bm, null); else if (bm.OnUse != null) bm.OnUse(1f / 18f); } catch { }
                            a.Count++;
                        }
                        finish = BandageDone(a, item, lb, maxSec);
                    }
                    else if (mg is SyringeMinigame)
                    {
                        var sm = (SyringeMinigame)mg;
                        try { if (sm.OnUse != null) sm.OnUse(dt); } catch { }
                        a.Count++;
                        finish = (a.Sec > 0f && a.T > maxSec) || a.T > 14f || item == null || item.condition <= 0.001f;
                    }
                    else
                    {
                        if (DriveOther(mg, a, b, dt, out res)) return true;
                        return false;
                    }
                    if (finish)
                    {
                        MinigameBase.main.EndMinigame();
                        res = "applied " + a.Pending + " for " + a.T.ToString("0.0") + "s; after: " + LimbState(lb) + (item != null ? "; item " + Mathf.RoundToInt(item.condition * 100f) + "%" : "; item used up");
                        return true;
                    }
                    return false;
                }

                case "relocate":
                {
                    Limb l = ResolveLimb(b, a.Limb);
                    if (l == null) { res = "relocate: limb '" + a.Limb + "' not found"; return true; }
                    if (!l.dislocated) { res = l.name + " is not dislocated"; return true; }
                    if (!b.conscious) { res = "cannot do that while unconscious"; return true; }
                    if (b.averagePain > 75f) { res = "too much pain (" + Mathf.RoundToInt(b.averagePain) + " > 75) to relocate a joint. Take painkillers first"; return true; }
                    l.pain += 22f; l.UnDislocate();
                    res = "relocated the " + l.name + " joint (it hurt: pain +22)";
                    return true;
                }

                case "pull_shrapnel":
                {
                    Limb l = ResolveLimb(b, a.Limb);
                    if (l == null) { res = "pull_shrapnel: limb '" + a.Limb + "' not found"; return true; }
                    int n = l.shrapnel;
                    if (n <= 0) { res = l.name + " has no shrapnel"; return true; }
                    l.shrapnel = 0; l.skinHealth -= 5f * n; l.bleedAmount += 0.7f * n; l.pain += 12f * n;
                    try { b.DoGoreSound(); } catch { }
                    res = "pulled " + n + " piece(s) of shrapnel out of the " + l.name + " (painful, it bleeds more now)";
                    return true;
                }

                case "grab":
                {
                    Item it = ItemFromTarget(a.Target, idx);
                    if (it == null) { res = "grab: item '" + a.Target + "' not found (already taken, or the id is from an old turn)"; return true; }
                    Vector2 ip = it.transform.position;
                    float dist = Vector2.Distance(pos, ip);
                    if (dist > Body.interactionRange) { res = "grab: " + a.Target + " is too far (" + dist.ToString("0.0") + " > 10). Use walk_to first"; return true; }
                    if (Physics2D.Linecast(pos, ip, LayerMask.GetMask("Ground"))) { res = "grab: a wall is between you and " + a.Target + ". Move to get a clear line"; return true; }
                    string nm = Senser.ItemName(it);
                    if (it.Stats.wearable)
                    {
                        b.WearWearable(it);
                        res = b.GetAllWearables().Contains(it) ? "picked up and put on " + nm : "could not wear " + nm + " (that body slot is probably already used)";
                        return true;
                    }
                    int slot = -1;
                    if (a.Slot >= 0 && a.Slot < b.slots.Length && !b.HoldingItem(a.Slot) && b.slots[a.Slot].canPickUp) slot = a.Slot;
                    else
                        for (int i = 0; i < b.slots.Length; i++)
                        {
                            if (b.HoldingItem(i) || !b.slots[i].canPickUp) continue;
                            if (it.Stats.onlyHoldInHands && !b.slots[i].isHand) continue;
                            slot = i; break;
                        }
                    if (slot < 0) { res = "grab: no free slot" + (it.Stats.onlyHoldInHands ? " (this item needs a free HAND slot)" : "") + ". Drop something first"; return true; }
                    b.PickUpItem(it, slot);
                    res = b.HoldingItem(it) ? "grabbed " + nm + " into slot " + slot : "could not pick up " + nm + " (too heavy or not allowed in slot " + slot + ")";
                    return true;
                }

                case "wear":
                {
                    Item it = a.Slot >= 0 ? SlotItem(b, a.Slot) : ItemFromTarget(a.Target, idx);
                    if (it == null) { res = "wear: item not found"; return true; }
                    if (!it.Stats.wearable) { res = Senser.ItemName(it) + " is not wearable"; return true; }
                    string nm = Senser.ItemName(it);
                    b.WearWearable(it);
                    res = b.GetAllWearables().Contains(it) ? "now wearing " + nm : "could not wear " + nm + " (that body slot is probably used)";
                    return true;
                }

                case "drop":
                {
                    Item it = SlotItem(b, a.Slot);
                    if (it == null) { res = "drop: slot " + a.Slot + " is empty or invalid"; return true; }
                    string nm = Senser.ItemName(it);
                    b.DropItem(a.Slot);
                    res = "dropped " + nm;
                    return true;
                }

                case "throw":
                {
                    Item it = b.GetItem(b.handSlot);
                    if (it == null) { res = "throw: nothing in your active hand"; return true; }
                    string nm = Senser.ItemName(it);
                    b.ThrowItem(1f);
                    res = "threw " + nm;
                    return true;
                }

                case "swap_hands": b.SwitchHands(); res = "switched hands; active hand is now slot " + b.handSlot; return true;

                case "swap_slots":
                {
                    if (b.slots == null || a.Slot < 0 || a.Slot2 < 0 || a.Slot >= b.slots.Length || a.Slot2 >= b.slots.Length || a.Slot == a.Slot2)
                    { res = "swap_slots needs two different valid slots (slot, slot2)"; return true; }
                    b.SwapSlots(a.Slot, a.Slot2);
                    res = "swapped slots " + a.Slot + " and " + a.Slot2;
                    return true;
                }

                case "inspect":   // learn what an item does (kept short on purpose)
                {
                    Item it = a.Slot >= 0 ? SlotItem(b, a.Slot) : ItemFromTarget(a.Target, idx);
                    if (it == null) { res = "inspect: item not found (use slot or an I# id)"; return true; }
                    bool old = PlayerCamera.alwaysExpandDescriptions;
                    string name = "", desc = "";
                    try { PlayerCamera.alwaysExpandDescriptions = true; var tip = PlayerCamera.ItemHoverDescription(it); name = Senser.Strip(tip.Item1); desc = Senser.Strip(tip.Item2); }
                    catch { }
                    finally { PlayerCamera.alwaysExpandDescriptions = old; }
                    string caps = "";
                    try { if (it.Stats.rec.recognizable) caps = Senser.Flags(it).Trim(); } catch { }
                    bool known = false; try { known = it.Stats.rec.recognizable; } catch { }
                    if (!known) { res = "inspected an unrecognized item: you cannot tell what it is or does yet (" + Senser.Trunc(desc, 160) + ")"; return true; }
                    res = "INSPECT " + name + " " + caps + ": " + Senser.Trunc(desc, 650);
                    if (P.Mem != null) P.Mem.SetNote(it.id, Senser.Trunc(name + " " + caps + " - " + desc, 150));
                    return true;
                }

                case "craft":
                {
                    int n = ParseIndex(a.Target, 'R');
                    if (n < 1 || n > idx.Recipes.Count) { res = "craft: unknown recipe '" + a.Target + "' (see CRAFTABLE RIGHT NOW)"; return true; }
                    Recipe r = idx.Recipes[n - 1];
                    string nm = Senser.Strip(r.simpleName);
                    if (r.GetItemsForRecipe() == null) { res = "craft: you no longer have the ingredients for " + nm + " nearby"; return true; }
                    try { PlayerCamera.main.selectedRecipe = r.index; PlayerCamera.main.TryCraft(); res = "crafted " + nm + " (check your inventory / the ground next to you)"; }
                    catch (Exception e1)
                    {
                        try { r.TryMake(); res = "crafted " + nm; }
                        catch (Exception e2) { res = "crafting " + nm + " failed: " + e2.Message; Plugin.Log?.LogWarning("craft: " + e1.Message); }
                    }
                    return true;
                }

                case "say":
                {
                    if (string.IsNullOrEmpty(a.Text)) { res = "say needs text"; return true; }
                    try { if (b.talker != null) b.talker.Talk(a.Text); } catch { }
                    P.Agent.AddChat("AI (in game)", a.Text);
                    res = "said it out loud";
                    return true;
                }

                default: res = "unknown action '" + a.Do + "'"; return true;
            }
        }
    }
}
