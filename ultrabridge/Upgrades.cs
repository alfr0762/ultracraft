// UltraBridge, part five: what the shop's upgrades do (Minecraft keeps the levels and sells them: UPGRADES, UPBUY).
//
// Every weapon and each arm has its own Power (40% at first, so a zombie takes two headshots; 300% fully upgraded, 1500% with the OP Shop: damage to
// enemies, Minecraft's mobs and blocks) and upgrades of its own (blast sizes go to 1500% with the OP Shop too):
// - Revolver: Hair Trigger (fires and charges faster), Capacitor (coins and specials recharge faster)
// - Shotgun: Payload (bigger core and overpump blasts), Capacitor (cores, saws and the Jackhammer recharge faster)
// - Nailgun: Overclock (fires faster), Heatsink (cools, refills and recharges magnets faster)
// - Railcannon: Capacitor (recharges faster), Payload (bigger Malicious blasts)
// - Rocket Launcher: Payload (bigger blasts), Autoloader (fires faster, cannonballs, napalm and the freeze recharge)
// - Feedbacker: Reflex (longer parry window), Return to Sender (parried projectiles hit harder and fly faster),
//   Bloodfist (punches heal), Arc (punches arc lightning to enemies nearby), Counterblast (a parry blasts what's in
//   front), Adrenaline (a parry refills stamina)
// - Knuckleblaster: Shockwave (bigger blast wave), Demolition (punches dig a wider hole), Detonator (punches that land
//   explode), Seismic (slams erupt), Ironclad (less damage taken while it's out), Overcharge (punches recharge faster)
// - Whiplash: Reel (throws again sooner), Barbs (the hook itself hurts), Grapple (it bites into blocks and yanks V1
//   there), Live Wire (a caught enemy is shocked), Slingshot (a pull ends in a launch), Ripcord (a reeled-in enemy
//   bursts)
// Bigger blasts are bigger in every way: the fireball, its smoke and light, the hurt, and the crater in Minecraft. The
// mini nuke grows with the biggest Payload V1 has.

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UltraBridge
{
    public partial class Bridge
    {
        public class UpTrack
        {
            public int level = 1, max = 1, cost;
        }

        /// <summary>Every upgrade's level, how far it goes and what the next level costs (UPGRADES).</summary>
        public static readonly Dictionary<string, UpTrack> UcUp = new Dictionary<string, UpTrack>();

        // weapon or arm, its name, then its own upgrades (key, name)
        public static readonly string[][] UpGroups =
        {
            new[] { "rev", "Revolver", "rate", "Hair Trigger", "charge", "Capacitor" },
            new[] { "sho", "Shotgun", "payload", "Payload", "charge", "Capacitor" },
            new[] { "nai", "Nailgun", "rate", "Overclock", "charge", "Heatsink" },
            new[] { "rai", "Railcannon", "charge", "Capacitor", "payload", "Payload" },
            new[] { "rock", "Rocket Launcher", "payload", "Payload", "rate", "Autoloader" },
            new[] { "arm0", "Feedbacker", "reflex", "Reflex", "sender", "Return to Sender", "bloodfist", "Bloodfist", "arc", "Arc",
                "counter", "Counterblast", "adrenaline", "Adrenaline" },
            new[] { "arm1", "Knuckleblaster", "shockwave", "Shockwave", "demolition", "Demolition", "detonator", "Detonator", "seismic", "Seismic",
                "ironclad", "Ironclad", "overcharge", "Overcharge" },
            new[] { "arm2", "Whiplash", "reel", "Reel", "barbs", "Barbs", "grapple", "Grapple", "livewire", "Live Wire",
                "slingshot", "Slingshot", "ripcord", "Ripcord" },
        };

        // levels past 11 (Power) and 5 (blast sizes) are the OP Shop's
        static readonly float[] PowerTable = { 0.4f, 0.6f, 0.8f, 1f, 1.2f, 1.45f, 1.7f, 2f, 2.3f, 2.6f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f };
        static readonly float[] RateTable = { 1f, 1.25f, 1.5f, 1.8f, 2.2f };
        static readonly float[] SizeTable = { 1f, 1.2f, 1.4f, 1.65f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 15f };
        static readonly float[] ReflexTable = { 0f, 0.08f, 0.16f, 0.25f, 0.35f };
        static readonly float[] SenderTable = { 1f, 1.5f, 2f, 2.5f, 3f };
        static readonly float[] DemolitionTable = { 1f, 1.5f, 2f, 2.5f, 3f };
        static readonly float[] BarbsTable = { 1f, 2.5f, 5f, 7.5f, 10f };
        static readonly float[] BloodfistTable = { 0f, 3f, 6f, 10f, 15f };
        static readonly float[] ArcTable = { 0f, 1f, 2f, 3f, 4f };
        static readonly float[] ArcDamageTable = { 0f, 0.5f, 0.75f, 1f, 1.5f };
        static readonly float[] CounterTable = { 0f, 0.6f, 0.9f, 1.2f, 1.6f };
        static readonly float[] AdrenalineTable = { 0f, 1f, 2f, 3f, 3f };
        static readonly float[] DetonatorTable = { 0f, 0.5f, 0.75f, 1f, 1.4f };
        static readonly float[] SeismicTable = { 0f, 0.6f, 0.9f, 1.2f, 1.6f };
        static readonly float[] IroncladTable = { 0f, 0.1f, 0.2f, 0.3f, 0.4f };
        static readonly float[] GrappleTable = { 0f, 50f, 65f, 80f, 100f };
        static readonly float[] LiveWireTable = { 0f, 0.5f, 1f, 1.5f, 2.5f };
        static readonly float[] SlingshotTable = { 0f, 20f, 30f, 40f, 55f };
        static readonly float[] RipcordTable = { 0f, 0.5f, 0.75f, 1f, 1.4f };

        /// <summary>An upgrade's level, as far as it goes now (levels bought in the OP Shop rest while it's off).</summary>
        public static int UpLevel(string key) => key != null && UcUp.TryGetValue(key, out var t) ? Mathf.Clamp(t.level, 1, Mathf.Max(1, t.max)) : 1;

        static float At(float[] table, string key, int level = 0)
        {
            if (level <= 0) level = UpLevel(key);
            return table[Mathf.Clamp(level - 1, 0, table.Length - 1)];
        }

        /// <summary>How hard a weapon or arm hits (rev, sho, nai, rai, rock, arm0, arm1, arm2): 0.4 to 3.</summary>
        public static float Power(string group) => group == null ? 1f : At(PowerTable, group + ".power");

        /// <summary>Power against blocks: it counts for more there (x0.46 at first, x5.2 at 300%), so an upgraded weapon or
        /// arm digs visibly faster. The weaker start (40%) is for fights, not digging: blocks go as they did at 60%.</summary>
        public static float BlockPower(string group) => Mathf.Pow(Mathf.Max(0.6f, Power(group)), 1.5f);

        /// <summary>The same, by the name the damage code knows a weapon by.</summary>
        public static float UpgradeMult(string kind) => Power(kind);

        /// <summary>One of the arms' own upgrades, by its table (0, or x1, at level 1: nothing yet).</summary>
        public static float Arm(string key) => AllWeapons ? At(ArmTable(key), key) : ArmTable(key)[0];

        /// <summary>Arc: how hard each arc hits.</summary>
        public static float ArcDamage => AllWeapons ? At(ArcDamageTable, "arm0.arc") : 0f;

        static float[] ArmTable(string key)
        {
            switch (key.Substring(key.IndexOf('.') + 1))
            {
                case "bloodfist": return BloodfistTable;
                case "arc": return ArcTable;
                case "counter": return CounterTable;
                case "adrenaline": return AdrenalineTable;
                case "detonator": return DetonatorTable;
                case "seismic": return SeismicTable;
                case "ironclad": return IroncladTable;
                case "overcharge": return RateTable;
                case "grapple": return GrappleTable;
                case "livewire": return LiveWireTable;
                case "slingshot": return SlingshotTable;
                case "ripcord": return RipcordTable;
            }
            return RateTable;
        }

        /// <summary>A speed-up (rate, charge, reel): 1 to 2.2.</summary>
        public static float Rate(string key) => AllWeapons ? At(RateTable, key) : 1f;

        /// <summary>A blast size (payload, shockwave): 1 to 2 (15 with the OP Shop). "nuke": the biggest Payload.</summary>
        public static float BlastSize(string key)
        {
            if (!AllWeapons) return 1f;
            if (key == "nuke") return Mathf.Max(At(SizeTable, "rai.payload"), Mathf.Max(At(SizeTable, "rock.payload"), At(SizeTable, "sho.payload")));
            return At(SizeTable, key);
        }

        /// <summary>The Feedbacker's Reflex: seconds added to the early-parry window (and 30% of it to the parry window).</summary>
        public static float ReflexBonus => AllWeapons ? At(ReflexTable, "arm0.reflex") : 0f;

        /// <summary>The Knuckleblaster's Demolition: how many blocks around a heavy punch it digs.</summary>
        public static float DemolitionRadius => AllWeapons ? At(DemolitionTable, "arm1.demolition") : 1f;

        /// <summary>The Whiplash's Barbs: how much more the hook itself hurts.</summary>
        public static float Barbs => AllWeapons ? At(BarbsTable, "arm2.barbs") : 1f;

        /// <summary>What one level of an upgrade does, in a few words ("120%", "x1.5 as fast").</summary>
        public static string UpEffect(string key, int level)
        {
            string track = key.Substring(key.IndexOf('.') + 1);
            switch (track)
            {
                case "power": return Mathf.RoundToInt(At(PowerTable, key, level) * 100f) + "% damage";
                case "rate":
                case "charge":
                case "reel": return "x" + At(RateTable, key, level).ToString("0.##") + " speed";
                case "payload":
                case "shockwave": return Mathf.RoundToInt(At(SizeTable, key, level) * 100f) + "% size";
                case "reflex":
                    {
                        // Plugin's ParryWindow gets 30% of the bonus, EarlyParry all of it
                        float r = At(ReflexTable, key, level);
                        return "+" + Mathf.RoundToInt(r * 300f) + " ms parry, +" + Mathf.RoundToInt(r * 1000f) + " ms early";
                    }
                case "sender": return "x" + At(SenderTable, key, level).ToString("0.#") + " return";
                case "demolition": return At(DemolitionTable, key, level).ToString("0.#") + " blocks";
                case "barbs": return "x" + At(BarbsTable, key, level).ToString("0.#") + " hook damage";
                case "bloodfist": return level <= 1 ? "off" : "+" + At(BloodfistTable, key, level) + " HP a hit";
                case "arc": return level <= 1 ? "off" : At(ArcTable, key, level) + " arcs, " + At(ArcDamageTable, key, level).ToString("0.##") + " damage";
                case "counter": return level <= 1 ? "off" : Mathf.RoundToInt(At(CounterTable, key, level) * 100f) + "% blast";
                case "adrenaline": return level <= 1 ? "off" : "+" + At(AdrenalineTable, key, level) + " stamina" + (level >= 5 ? ", +25 HP" : "");
                case "detonator": return level <= 1 ? "off" : Mathf.RoundToInt(At(DetonatorTable, key, level) * 100f) + "% blast";
                case "seismic": return level <= 1 ? "off" : Mathf.RoundToInt(At(SeismicTable, key, level) * 100f) + "% blast";
                case "ironclad": return level <= 1 ? "off" : "-" + Mathf.RoundToInt(At(IroncladTable, key, level) * 100f) + "% damage taken";
                case "overcharge": return "x" + At(RateTable, key, level).ToString("0.##") + " recharge";
                case "grapple": return level <= 1 ? "off" : At(GrappleTable, key, level) + " yank speed";
                case "livewire": return level <= 1 ? "off" : At(LiveWireTable, key, level).ToString("0.#") + " damage/s";
                case "slingshot": return level <= 1 ? "off" : "+" + At(SlingshotTable, key, level) + " launch";
                case "ripcord": return level <= 1 ? "off" : Mathf.RoundToInt(At(RipcordTable, key, level) * 100f) + "% blast";
            }
            return "";
        }

        /// <summary>What an upgrade is for.</summary>
        public static string UpAbout(string key)
        {
            switch (key)
            {
                case "rev.power": case "sho.power": case "nai.power": case "rai.power": case "rock.power":
                    return "Hits enemies, mobs and blocks harder (every variant).";
                case "arm0.power": return "Feedbacker punches hit harder and dig deeper.";
                case "arm1.power": return "Knuckleblaster punches hit harder and dig deeper.";
                case "arm2.power": return "The Whiplash's hook hits harder.";
                case "rev.rate": return "Fires again sooner and the Piercer charges faster.";
                case "rev.charge": return "Coins and the Marksman and Sharpshooter's specials recharge faster.";
                case "sho.payload": return "Core ejects and overpumped shells blow up bigger.";
                case "sho.charge": return "Cores, saws and the Jackhammer recharge faster.";
                case "nai.rate": return "Fires faster.";
                case "nai.charge": return "Cools down, refills and recharges magnets faster.";
                case "rai.charge": return "Recharges faster.";
                case "rai.payload": return "The Malicious railcannon's blast is bigger.";
                case "rock.payload": return "Rockets blow up bigger.";
                case "rock.rate": return "Fires faster; cannonballs, napalm and the freeze recharge faster.";
                case "arm0.reflex": return "A longer window to parry Minecraft's arrows, fireballs and swings.";
                case "arm0.sender": return "Parried projectiles fly back harder and faster.";
                case "arm1.shockwave": return "The blast wave (hold punch) is bigger.";
                case "arm1.demolition": return "Punches into the ground dig a wider hole.";
                case "arm2.reel": return "Throws again sooner.";
                case "arm2.barbs": return "The hook itself hurts what it catches.";
                case "arm0.bloodfist": return "Every punch that lands heals V1.";
                case "arm0.arc": return "Punches arc lightning to enemies nearby.";
                case "arm0.counter": return "A parry sets off a blast in front of V1 that only hurts enemies.";
                case "arm0.adrenaline": return "A parry refills stamina (at the top, heals too).";
                case "arm1.detonator": return "Punches that land blow up on what they hit.";
                case "arm1.seismic": return "Slams erupt in a blast around V1 when they land.";
                case "arm1.ironclad": return "V1 takes less damage while the Knuckleblaster is out.";
                case "arm1.overcharge": return "Punch stamina comes back faster with the Knuckleblaster out.";
                case "arm2.grapple": return "The hook bites into blocks and yanks V1 to them.";
                case "arm2.livewire": return "A caught enemy is shocked for as long as the hook holds it.";
                case "arm2.slingshot": return "Being pulled to something ends in a launch the way V1 looks.";
                case "arm2.ripcord": return "An enemy reeled in bursts in a blast (it can't hurt V1).";
            }
            return "";
        }

        /// <summary>UPGRADES key=level/max/cost,...: returns whether any level went up.</summary>
        static bool ApplyUpgrades(string rest)
        {
            bool rose = false;
            foreach (var kv in rest.Trim().Split(','))
            {
                var p = kv.Split('=');
                if (p.Length != 2) continue;
                var v = p[1].Split('/');
                if (!int.TryParse(v[0], out var lv)) continue;
                if (!UcUp.TryGetValue(p[0], out var t)) UcUp[p[0]] = t = new UpTrack();
                else if (lv > t.level) rose = true;
                t.level = lv;
                if (v.Length > 1 && int.TryParse(v[1], out var max)) t.max = max;
                if (v.Length > 2 && int.TryParse(v[2], out var cost)) t.cost = cost;
            }
            return rose;
        }

        /// <summary>The upgrade that sizes a blast: whose weapon set it off.</summary>
        public static string BlastUpgrade(Explosion e)
        {
            if (e.GetComponentInParent<NoUpgrade>() != null) return null;
            if (e.playerProjectileForceDirection.HasValue) return "arm1.shockwave";
            if (IsNuke(e)) return "nuke";
            switch (WeaponKind(e.sourceWeapon))
            {
                case "rock": return "rock.payload";
                case "sho": return "sho.payload";
                case "rai": return "rai.payload";
            }
            return null;
        }
    }

    /// <summary>Upgraded blasts are bigger in every way: the sphere that hurts (and the crater Minecraft makes of it,
    /// sized from it) and everything drawn with it.</summary>
    [HarmonyPatch(typeof(Explosion), "Start")]
    static class UpgradedBlast
    {
        static readonly HashSet<int> grown = new HashSet<int>();

        [HarmonyPriority(Priority.First)]
        static void Prefix(Explosion __instance)
        {
            if (!Bridge.AllWeapons) return;
            var key = Bridge.BlastUpgrade(__instance);
            if (key == null) return;
            float s = Bridge.BlastSize(key);
            if (s <= 1.001f) return;
            __instance.maxSize *= s;
            var root = __instance.transform.parent;
            if (root != null && root.GetComponent<Explosion>() == null)
            {
                // the whole effect, once: its sphere grows s times as fast to s times the size, with its smoke and light
                if (grown.Add(root.GetInstanceID()))
                {
                    root.localScale *= s;
                    foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = ps.main;
                        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    }
                    foreach (var l in root.GetComponentsInChildren<Light>(true)) l.range *= s;
                    if (grown.Count > 512) grown.Clear();
                }
            }
            else
            {
                __instance.speed *= s;
            }
        }
    }

    /// <summary>Capacitors, Heatsinks and Autoloaders: what WeaponCharges recharges each frame, recharged faster.</summary>
    [HarmonyPatch(typeof(WeaponCharges), nameof(WeaponCharges.Charge))]
    static class UpgradedRecharge
    {
        static void Prefix(WeaponCharges __instance, out float[] __state)
        {
            var w = __instance;
            __state = new[]
            {
                w.rev0charge, w.rev1charge, w.rev2charge, w.shoAltNadeCharge, w.shoSawCharge, w.naiMagnetCharge, w.naiZapperRecharge,
                w.naiHeatsinks, w.naiSawHeatsinks, w.naiAmmo, w.naiSaws, w.raicharge, w.rocketCannonballCharge, w.rocketNapalmFuel, w.rocketFreezeTime
            };
        }

        static void Postfix(WeaponCharges __instance, float[] __state)
        {
            if (!Bridge.AllWeapons || __state == null) return;
            var w = __instance;
            float r = Bridge.Rate("rev.charge");
            if (r > 1f)
            {
                w.rev0charge = Up(__state[0], w.rev0charge, r, 100f);
                w.rev1charge = Up(__state[1], w.rev1charge, r, 400f);
                w.rev2charge = Up(__state[2], w.rev2charge, r, 300f);
            }
            r = Bridge.Rate("sho.charge");
            if (r > 1f)
            {
                w.shoAltNadeCharge = Up(__state[3], w.shoAltNadeCharge, r, 1f);
                w.shoSawCharge = Up(__state[4], w.shoSawCharge, r, 1f);
            }
            r = Bridge.Rate("nai.charge");
            if (r > 1f)
            {
                w.naiMagnetCharge = Up(__state[5], w.naiMagnetCharge, r, 3f);
                w.naiZapperRecharge = Up(__state[6], w.naiZapperRecharge, r, 5f);
                w.naiHeatsinks = Up(__state[7], w.naiHeatsinks, r, 2f);
                w.naiSawHeatsinks = Up(__state[8], w.naiSawHeatsinks, r, 1f);
                w.naiAmmo = Up(__state[9], w.naiAmmo, r, 100f);
                w.naiSaws = Up(__state[10], w.naiSaws, r, 10f);
            }
            r = Bridge.Rate("rai.charge");
            if (r > 1f) w.raicharge = Up(__state[11], w.raicharge, r, 5f);
            r = Bridge.Rate("rock.rate");
            if (r > 1f)
            {
                w.rocketCannonballCharge = Up(__state[12], w.rocketCannonballCharge, r, 1f);
                w.rocketNapalmFuel = Up(__state[13], w.rocketNapalmFuel, r, 1f);
                if (!w.rocketFrozen) w.rocketFreezeTime = Up(__state[14], w.rocketFreezeTime, r, 5f);
            }
        }

        /// <summary>A charge that rose this frame rises r times as much (never past its full).</summary>
        public static float Up(float before, float after, float r, float cap) => after > before ? Mathf.Min(cap, after + (after - before) * (r - 1f)) : after;

        /// <summary>A cooldown that fell this frame falls r times as much (never below zero).</summary>
        public static float Down(float before, float after, float r) => after < before ? Mathf.Max(0f, after - (before - after) * (r - 1f)) : after;
    }

    /// <summary>Hair Trigger: the revolver's shot and the Piercer's charge come back faster.</summary>
    [HarmonyPatch(typeof(Revolver), "Update")]
    static class UpgradedRevolver
    {
        static void Prefix(Revolver __instance, out Vector2 __state) => __state = new Vector2(__instance.shootCharge, __instance.pierceCharge);

        static void Postfix(Revolver __instance, Vector2 __state)
        {
            float r = Bridge.Rate("rev.rate");
            if (r <= 1f) return;
            __instance.shootCharge = UpgradedRecharge.Up(__state.x, __instance.shootCharge, r, 100f);
            __instance.pierceCharge = UpgradedRecharge.Up(__state.y, __instance.pierceCharge, r, 100f);
        }
    }

    /// <summary>Overclock (fire rate) and Heatsink (cooling) for the nailgun.</summary>
    [HarmonyPatch]
    static class UpgradedNailgun
    {
        static readonly AccessTools.FieldRef<Nailgun, float> FireCooldown = AccessTools.FieldRefAccess<Nailgun, float>("fireCooldown");
        static readonly AccessTools.FieldRef<Nailgun, float> HeatUp = AccessTools.FieldRefAccess<Nailgun, float>("heatUp");
        static readonly AccessTools.FieldRef<Nailgun, float> HeatSinks = AccessTools.FieldRefAccess<Nailgun, float>("heatSinks");

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Nailgun), "Update");
            yield return AccessTools.Method(typeof(Nailgun), "FixedUpdate");
        }

        static void Prefix(Nailgun __instance, out Vector3 __state) => __state = new Vector3(FireCooldown(__instance), HeatUp(__instance), HeatSinks(__instance));

        static void Postfix(Nailgun __instance, Vector3 __state)
        {
            float r = Bridge.Rate("nai.rate");
            if (r > 1f) FireCooldown(__instance) = UpgradedRecharge.Down(__state.x, FireCooldown(__instance), r);
            r = Bridge.Rate("nai.charge");
            if (r > 1f)
            {
                HeatUp(__instance) = UpgradedRecharge.Down(__state.y, HeatUp(__instance), r);
                HeatSinks(__instance) = UpgradedRecharge.Up(__state.z, HeatSinks(__instance), r, 2f);
            }
        }
    }

    /// <summary>Autoloader: the rocket launcher fires again sooner and its cannonball charges faster.</summary>
    [HarmonyPatch(typeof(RocketLauncher), "Update")]
    static class UpgradedRockets
    {
        static readonly AccessTools.FieldRef<RocketLauncher, float> Cooldown = AccessTools.FieldRefAccess<RocketLauncher, float>("cooldown");
        static readonly AccessTools.FieldRef<RocketLauncher, float> CbCharge = AccessTools.FieldRefAccess<RocketLauncher, float>("cbCharge");

        static void Prefix(RocketLauncher __instance, out Vector2 __state) => __state = new Vector2(Cooldown(__instance), CbCharge(__instance));

        static void Postfix(RocketLauncher __instance, Vector2 __state)
        {
            float r = Bridge.Rate("rock.rate");
            if (r <= 1f) return;
            Cooldown(__instance) = UpgradedRecharge.Down(__state.x, Cooldown(__instance), r);
            CbCharge(__instance) = UpgradedRecharge.Up(__state.y, CbCharge(__instance), r, 1f);
        }
    }

    /// <summary>The shotgun's Capacitor: the Jackhammer swings again sooner.</summary>
    [HarmonyPatch(typeof(ShotgunHammer), "Update")]
    static class UpgradedHammer
    {
        static readonly AccessTools.FieldRef<ShotgunHammer, float> HammerCooldown = AccessTools.FieldRefAccess<ShotgunHammer, float>("hammerCooldown");

        static void Prefix(ShotgunHammer __instance, out float __state) => __state = HammerCooldown(__instance);

        static void Postfix(ShotgunHammer __instance, float __state)
        {
            float r = Bridge.Rate("sho.charge");
            if (r > 1f) HammerCooldown(__instance) = UpgradedRecharge.Down(__state, HammerCooldown(__instance), r);
        }
    }

    /// <summary>Reel: the Whiplash can be thrown again sooner.</summary>
    [HarmonyPatch(typeof(HookArm), "Update")]
    static class UpgradedWhiplash
    {
        static readonly AccessTools.FieldRef<HookArm, float> Cooldown = AccessTools.FieldRefAccess<HookArm, float>("cooldown");

        static void Prefix(HookArm __instance, out float __state) => __state = Cooldown(__instance);

        static void Postfix(HookArm __instance, float __state)
        {
            float r = Bridge.Rate("arm2.reel");
            if (r > 1f) Cooldown(__instance) = UpgradedRecharge.Down(__state, Cooldown(__instance), r);
        }
    }
}
