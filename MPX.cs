using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CasualtiesOllama
{
    /// <summary>Piggyback / carry support for the Casualties Together mod (reflection only).</summary>
    public static class MPX
    {
        static bool init;
        static Type tNB;
        static FieldInfo fCarrying, fPiggy, fBody;
        static MethodInfo mStart, mStop;

        static void Init()
        {
            if (init) return;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "KrokoshaCasualtiesMP");
                if (asm == null) return;
                init = true;
                tNB = asm.GetType("KrokoshaCasualtiesMP.NetBody");
                if (tNB == null) return;
                BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                fCarrying = tNB.GetField("carrying_person", all);
                fPiggy = tNB.GetField("piggybacking_on", all);
                fBody = tNB.GetField("body", all);
                mStart = tNB.GetMethod("StartPiggyback", all);
                mStop = tNB.GetMethod("StopPiggyback", all);
            }
            catch (Exception e) { Plugin.Log?.LogWarning("MPX init: " + e.Message); }
        }

        public static bool Available { get { Init(); return tNB != null && mStart != null; } }

        public static object NB(Body b)
        {
            Init();
            if (tNB == null || b == null) return null;
            object o = b.GetComponent(tNB);
            if (o == null) o = b.GetComponentInParent(tNB);
            if (o == null && fBody != null)
            {
                try { foreach (var x in UnityEngine.Object.FindObjectsOfType(tNB)) if (fBody.GetValue(x) as Body == b) return x; } catch { }
            }
            return o;
        }

        static Body BodyOf(object nb) { return (nb != null && fBody != null) ? fBody.GetValue(nb) as Body : null; }

        public static string NameOf(Body b)
        {
            foreach (var p in MP.Players()) if (p.Body == b) return p.Name;
            return "someone";
        }

        /// <summary>Text for the observation: who is on my back / who I am riding.</summary>
        public static string Info(Body me)
        {
            try
            {
                object nb = NB(me);
                if (nb == null) return "";
                var sb = new StringBuilder();
                object carried = fCarrying != null ? fCarrying.GetValue(nb) : null;
                object riding = fPiggy != null ? fPiggy.GetValue(nb) : null;
                if (carried != null) sb.AppendLine("CARRYING: " + NameOf(BodyOf(carried)) + " is on your back. Walk steadily, avoid big jumps. 'drop_carried' puts them down.");
                if (riding != null) sb.AppendLine("RIDING: you are on the back of " + NameOf(BodyOf(riding)) + ". 'dismount' gets you off.");
                return sb.ToString();
            }
            catch { return ""; }
        }

        static string Start(object rider, object carrier, string riderName, string carrierName)
        {
            if (rider == null || carrier == null) return "multiplayer body not found";
            try
            {
                bool ok = (bool)mStart.Invoke(rider, new object[] { carrier, true, false });
                return ok ? (riderName + " is now on the back of " + carrierName) : ("refused: " + riderName + " cannot ride " + carrierName + " (too far away, the carrier is not standing, already carrying someone, or the stack limit)");
            }
            catch (Exception e) { return "piggyback failed: " + e.Message; }
        }

        /// <summary>I climb on the back of another player.</summary>
        public static string Ride(Body me, Body target)
        {
            if (!Available) return "piggyback needs the multiplayer mod";
            if (target == null) return "piggyback: target player not found";
            return Start(NB(me), NB(target), "you", NameOf(target));
        }

        /// <summary>Another player (usually a downed one) is put on MY back.</summary>
        public static string Carry(Body me, Body target)
        {
            if (!Available) return "carry needs the multiplayer mod";
            if (target == null) return "carry: target player not found";
            return Start(NB(target), NB(me), NameOf(target), "you");
        }

        public static string Dismount(Body me)
        {
            if (!Available) return "needs the multiplayer mod";
            try
            {
                object nb = NB(me);
                if (nb == null || fPiggy == null || fPiggy.GetValue(nb) == null) return "you are not riding anyone";
                mStop.Invoke(nb, new object[] { false });
                return "you got off";
            }
            catch (Exception e) { return "dismount failed: " + e.Message; }
        }

        public static string DropCarried(Body me)
        {
            if (!Available) return "needs the multiplayer mod";
            try
            {
                object nb = NB(me);
                object carried = nb != null && fCarrying != null ? fCarrying.GetValue(nb) : null;
                if (carried == null) return "you are not carrying anyone";
                string nm = NameOf(BodyOf(carried));
                mStop.Invoke(carried, new object[] { false });
                return "put " + nm + " down";
            }
            catch (Exception e) { return "drop_carried failed: " + e.Message; }
        }
    }
}
