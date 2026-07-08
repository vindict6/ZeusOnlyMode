using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using static CounterStrikeSharp.API.Core.Listeners;

namespace ZeusOnlyMode
{
    // Auto-generated on first load and read from:
    //   addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/ZeusOnlyMode.json
    // Edit the JSON and reload the plugin to apply changes.
    public class ZeusOnlyConfig : BasePluginConfig
    {
        [JsonPropertyName("AdminFlag")]
        public string AdminFlag { get; set; } = "@css/generic";

        [JsonPropertyName("RefundIllegalPurchases")]
        public bool RefundIllegalPurchases { get; set; } = true;

        [JsonPropertyName("SuperZeusKnockbackForce")]
        public float SuperZeusKnockbackForce { get; set; } = 900.0f;

        // The game's burned-chicken model (carried over from CS:GO). If a
        // future update moves/renames it, fix the path here; set to "" to
        // fall back to a charred copy of the live chicken model instead.
        [JsonPropertyName("CookedChickenModel")]
        public string CookedChickenModel { get; set; } = "models/chicken/chicken_roasted.vmdl";

        [JsonPropertyName("CookedChickenParticle")]
        public string CookedChickenParticle { get; set; } = "particles/chicken/chicken_roasted_steam.vpcf";

        // Used to refund players when a blocked purchase slips through
        // (e.g. via the buy menu UI). Values verified against current CS2.
        [JsonPropertyName("WeaponPrices")]
        public Dictionary<string, int> WeaponPrices { get; set; } = new()
        {
            // Rifles
            { "ak47", 2700 },
            { "m4a1", 3100 },           // M4A4
            { "m4a1_silencer", 2900 },  // M4A1-S
            { "famas", 2050 },
            { "galilar", 1800 },
            { "aug", 3300 },
            { "sg556", 3000 },
            // Snipers
            { "awp", 4750 },
            { "ssg08", 1700 },
            { "scar20", 5000 },
            { "g3sg1", 5000 },
            // SMGs
            { "p90", 2350 },
            { "ump45", 1200 },
            { "mp5sd", 1500 },
            { "mp7", 1500 },
            { "mp9", 1250 },
            { "mac10", 1050 },
            { "bizon", 1400 },
            // Heavy
            { "nova", 1050 },
            { "xm1014", 2000 },
            { "mag7", 1300 },
            { "sawedoff", 1050 },
            { "m249", 5200 },
            { "negev", 1700 },
            // Pistols
            { "glock", 200 },
            { "hkp2000", 200 },
            { "usp_silencer", 200 },
            { "p250", 300 },
            { "elite", 300 },           // Dual Berettas
            { "fiveseven", 500 },
            { "cz75a", 500 },
            { "tec9", 500 },
            { "deagle", 700 },
            { "revolver", 600 }
            // Grenades/armor are omitted because they're allowed
        };
    }

    public class ZeusOnlyPlugin : BasePlugin, IPluginConfig<ZeusOnlyConfig>
    {
        public override string ModuleName => "Zeus Only Mode";
        public override string ModuleVersion => "3.2";

        public ZeusOnlyConfig Config { get; set; } = new();

        public void OnConfigParsed(ZeusOnlyConfig config)
        {
            Config = config;
        }

        private bool zeusOnlyEnabled = false;
        private bool superZeusEnabled = false;

        // Stock taser range (m_flRange) is ~190 units; we read the real value
        // at runtime the first time we touch a taser and remember it here.
        private const float DefaultTaserRange = 190.0f;
        private const float SuperZeusRangeMultiplier = 3.0f;
        private float? originalTaserRange = null;

        private readonly Random rng = new();

        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Whitelist of allowed weapons (lowercase, no "weapon_" prefix)
        private readonly HashSet<string> allowedWeapons = new HashSet<string>
        {
            "taser",
            "knife",
            // Grenades and utilities
            "flashbang",
            "hegrenade", "grenade", // HE grenade
            "smokegrenade", "smoke",
            "molotov", "incgrenade", "firebomb", // T and CT fire nades
            "decoy", "decoygrenade", "c4",
            // Armor + defuser are gear, not guns — leave them buyable
            "kevlar", "vest", "assaultsuit", "vesthelm", "defuser"
        };

        // Weapon prices now live in the config (ZeusOnlyConfig.WeaponPrices):
        //   addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/ZeusOnlyMode.json

        // Snapshot of a weapon rich enough to restore skins, not just base guns.
        private sealed class SavedWeapon
        {
            public string Name = "";
            public int PaintKit;
            public int Seed;
            public float Wear;
            public int StatTrak = -1;
        }

        // Several weapons share a DesignerName with their sibling
        // (M4A1-S reports "weapon_m4a1", USP-S reports "weapon_hkp2000",
        // R8 reports "weapon_deagle", MP5-SD reports "weapon_mp7"), so the
        // item definition index is the only reliable identity for restoring.
        private static readonly Dictionary<ushort, string> DefIndexToName = new()
        {
            { 23, "weapon_mp5sd" },
            { 60, "weapon_m4a1_silencer" },
            { 61, "weapon_usp_silencer" },
            { 63, "weapon_cz75a" },
            { 64, "weapon_revolver" },
        };

        private static string GetRealWeaponName(CBasePlayerWeapon weapon)
        {
            ushort defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            return DefIndexToName.TryGetValue(defIndex, out var name) ? name : weapon.DesignerName;
        }

        // Dictionary to hold saved weapons per player
        private readonly Dictionary<ulong, List<SavedWeapon>> savedWeapons = new();
        public override void Load(bool hotReload)
        {
            // Console / RCON entry points (css_zeusmode, css_zeus_mode,
            // css_superzeus) are auto-registered from the [ConsoleCommand]
            // attributes on their handler methods and are admin-gated by the
            // [RequiresPermissions] attribute.
            //
            // Chat is handled separately in OnSay so we can (a) accept the
            // commands with OR without a !// prefix and (b) swallow the
            // message so it never appears in public chat.
            AddCommandListener("say", OnSay);
            AddCommandListener("say_team", OnSay);

            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventItemPurchase>(OnItemPurchase);
            RegisterEventHandler<EventItemEquip>(OnItemEquip);
            RegisterEventHandler<EventWeaponFire>(OnWeaponFire);

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);

            // EventGameEnd does NOT fire on mp_changelevel / RTV map switches,
            // which left the mode stuck on across maps. These listeners fire
            // on every map transition, so state always resets.
            RegisterListener<Listeners.OnMapEnd>(OnMapEndCleanup);
            RegisterListener<Listeners.OnMapStart>(OnMapStartCleanup);

            // Follows each zeus shot to its exact trace endpoint; powers both
            // the extended lightning visual and chicken detection.
            RegisterEventHandler<EventBulletImpact>(OnBulletImpact);

            // Superzeus knockback. Applied in a PRE-damage hook so the launch
            // velocity is on the pawn BEFORE the lethal hit resolves — the
            // ragdoll then inherits it at creation (the only thing that
            // actually moves a ragdoll). Also launches survivors.
            RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);

            // Make sure the roast model is available even on maps that never
            // loaded it themselves
            RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
            {
                if (!string.IsNullOrEmpty(Config.CookedChickenModel))
                    manifest.AddResource(Config.CookedChickenModel);
                if (!string.IsNullOrEmpty(Config.CookedChickenParticle))
                    manifest.AddResource(Config.CookedChickenParticle);
            });

            RegisterEventHandler<EventGameEnd>(OnMapEnd);
            // Hook buy command
            AddCommandListener("buy", OnBuyCommand);
        }
        // Natural game end (scoreboard) — entities are still alive here, so we
        // can be polite: restore loadouts and announce before resetting.
        private HookResult OnMapEnd(EventGameEnd ev, GameEventInfo info)
        {
            if (zeusOnlyEnabled)
            {
                Server.PrintToChatAll("Zeus Mode Was Disabled — Thanks For Playing!");

                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.TeamNum < 2 || !p.PawnIsAlive) continue;

                    RestorePlayerLoadout(p);
                }
            }

            ResetPluginState();

            return HookResult.Continue;
        }

        // Map is being torn down. Covers mp_changelevel, RTV plugins, and
        // admin map switches — everything EventGameEnd misses. Entities are
        // unsafe to touch here, so this only resets flags and timers.
        private void OnMapEndCleanup()
        {
            ResetPluginState();
        }

        // Fresh map: weapon data is reloaded from disk, so the cached stock
        // taser range is stale — forget it and start clean.
        private void OnMapStartCleanup(string mapName)
        {
            ResetPluginState();
            originalTaserRange = null;
        }

        private void ResetPluginState()
        {
            if (superZeusEnabled)
            {
                superZeusEnabled = false;
                ApplyTaserRangeToAll();
            }

            zeusOnlyEnabled = false;
            savedWeapons.Clear();
            Array.Clear(pendingZap, 0, pendingZap.Length);

            zeusReminderTimer?.Kill();
            zeusReminderTimer = null;
        }

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------

        // Admin gate for all toggles. The flag is configurable ("AdminFlag" in
        // the plugin config, default @css/generic). Grant it in
        // addons/counterstrikesharp/configs/admins.json, e.g.
        //   "You": { "identity": "STEAM_1:0:1234567", "flags": ["@css/generic"] }
        // A null caller means server console / RCON, which always passes.
        private bool IsAdmin(CCSPlayerController? caller)
        {
            return caller == null || AdminManager.PlayerHasPermissions(caller, Config.AdminFlag);
        }

        // A single chat line can reach the toggle twice (once via OnSay, once
        // via the built-in !// -> css_ dispatch). This debounce guarantees one
        // physical command = one toggle no matter what.
        private float lastZeusToggle = -1.0f;
        private float lastSuperToggle = -1.0f;
        private const float ToggleDebounce = 0.25f;

        // --- Console / RCON commands (attribute-registered, admin only) ----

        // Permission checks happen at runtime (not via [RequiresPermissions])
        // so the flag can come from the config file.
        [ConsoleCommand("css_zeusmode", "Toggle Zeus-only mode")]
        [ConsoleCommand("css_zeus_mode", "Toggle Zeus-only mode (legacy alias)")]
        public void ConCommand_ZeusMode(CCSPlayerController? caller, CommandInfo info)
        {
            if (!IsAdmin(caller))
            {
                info.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            DoZeusToggle(caller, ParseToggleArg(info));
        }

        [ConsoleCommand("css_superzeus", "Toggle Super Zeus (3x range)")]
        public void ConCommand_SuperZeus(CCSPlayerController? caller, CommandInfo info)
        {
            if (!IsAdmin(caller))
            {
                info.ReplyToCommand("You do not have permission to use this command.");
                return;
            }

            DoSuperToggle(caller, ParseToggleArg(info));
        }

        // --- Chat handler: with or without ! or /, invisible to everyone ---
        //
        // Fires for every "say" / "say_team". We only act on our own command
        // words; anything else is passed straight through as normal chat.
        private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            string message = info.GetArg(1)?.Trim() ?? string.Empty;
            if (message.Length == 0)
                return HookResult.Continue;

            // Accept an optional single ! or / prefix
            if (message[0] == '!' || message[0] == '/')
                message = message.Substring(1).Trim();

            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return HookResult.Continue;

            string cmd = parts[0].ToLowerInvariant();
            bool isZeus = cmd == "zeusmode" || cmd == "zeus_mode";
            bool isSuper = cmd == "superzeus";

            if (!isZeus && !isSuper)
                return HookResult.Continue; // ordinary chat — leave it alone

            // From here it's one of our commands. Whatever happens next, we
            // swallow the message so players never see it in chat.

            if (!AdminManager.PlayerHasPermissions(player, Config.AdminFlag))
                return HookResult.Handled; // silently deny; don't advertise it

            bool? explicitState = ParseChatToggleArg(parts);

            if (isSuper)
                DoSuperToggle(player, explicitState);
            else
                DoZeusToggle(player, explicitState);

            return HookResult.Handled;
        }

        // --- Shared toggle logic -------------------------------------------

        private void DoZeusToggle(CCSPlayerController? caller, bool? explicitState)
        {
            if (Server.CurrentTime - lastZeusToggle < ToggleDebounce) return;
            lastZeusToggle = Server.CurrentTime;

            bool newState = explicitState ?? !zeusOnlyEnabled;

            if (newState == zeusOnlyEnabled)
            {
                caller?.PrintToChat($"Zeus Mode Is Already {(zeusOnlyEnabled ? "Enabled" : "Disabled")}.");
                return;
            }

            SetZeusMode(newState);
        }

        private void DoSuperToggle(CCSPlayerController? caller, bool? explicitState)
        {
            if (Server.CurrentTime - lastSuperToggle < ToggleDebounce) return;
            lastSuperToggle = Server.CurrentTime;

            bool newState = explicitState ?? !superZeusEnabled;

            if (newState == superZeusEnabled)
            {
                caller?.PrintToChat($"Super Zeus Is Already {(superZeusEnabled ? "Enabled" : "Disabled")}.");
                return;
            }

            superZeusEnabled = newState;
            ApplyTaserRangeToAll();

            Server.PrintToChatAll(superZeusEnabled
                ? "Super Zeus Enabled — The Zeus Now Reaches 3x As Far!"
                : "Super Zeus Disabled — Zeus Range Is Back To Normal.");
        }

        // "1/on/enable" -> true, "0/off/disable" -> false, otherwise null (toggle)
        private static bool? ParseToggleArg(CommandInfo info)
        {
            if (info.ArgCount < 2) return null;
            return NormalizeToggle(info.GetArg(1));
        }

        private static bool? ParseChatToggleArg(string[] parts)
        {
            if (parts.Length < 2) return null;
            return NormalizeToggle(parts[1]);
        }

        private static bool? NormalizeToggle(string arg)
        {
            switch (arg.ToLowerInvariant())
            {
                case "1": case "on": case "enable": case "enabled": return true;
                case "0": case "off": case "disable": case "disabled": return false;
                default: return null;
            }
        }

        private void SetZeusMode(bool newState)
        {
            zeusOnlyEnabled = newState;

            if (zeusOnlyEnabled) // after you set zeusOnlyEnabled = newState and it's true
            {
                Server.PrintToChatAll("Zeus Mode Enabled — Loadouts Have Been Snapshot.");

                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.TeamNum < 2 || !p.PawnIsAlive)
                        continue;

                    // safe to manipulate loadout
                    SavePlayerLoadout(p);
                    StripIllegalWeapons(p);

                    // ensure Zeus
                    p.GiveNamedItem("weapon_taser");
                    p.ExecuteClientCommand("slot11");
                }

                // Start reminder timer
                zeusReminderTimer = AddTimer(5.0f, () =>
                {
                    if (zeusOnlyEnabled)
                    {
                        Server.PrintToChatAll("Zeus, Utility, and Knife only!");
                    }
                }, TimerFlags.REPEAT);
            }
            else
            {
                Server.PrintToChatAll("Zeus Mode Disabled — Loadouts Have Been Restored.");

                // Restore immediately
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.TeamNum < 2) continue;
                    RestorePlayerLoadout(p);
                }

                // Stop reminder timer
                zeusReminderTimer?.Kill();
                zeusReminderTimer = null;
            }
        }

        // ------------------------------------------------------------------
        // Chicken cooking.
        //
        // The zeus's trace ignores chickens entirely in CS2 — they never take
        // damage from it, so there is no damage event to piggyback on. We
        // detect chickens near the zap line ourselves and serve them
        // well-done: the live chicken is removed and replaced with a charred,
        // knocked-over copy of its own model.
        // ------------------------------------------------------------------
        private void CookChickensAlongLine(Vector start, Vector end)
        {
            foreach (var chicken in Utilities.FindAllEntitiesByDesignerName<CChicken>("chicken"))
            {
                if (chicken == null || !chicken.IsValid || chicken.AbsOrigin == null)
                    continue;

                // Compare against the chicken's body center, not its feet
                var center = new Vector(chicken.AbsOrigin.X, chicken.AbsOrigin.Y, chicken.AbsOrigin.Z + 18.0f);

                if (DistancePointToSegment(center, start, end) <= 20.0f)
                    CookChicken(chicken);
            }
        }

        private static float DistancePointToSegment(Vector p, Vector a, Vector b)
        {
            float abX = b.X - a.X, abY = b.Y - a.Y, abZ = b.Z - a.Z;
            float apX = p.X - a.X, apY = p.Y - a.Y, apZ = p.Z - a.Z;

            float abLenSq = abX * abX + abY * abY + abZ * abZ;
            float t = abLenSq < 0.0001f ? 0.0f
                : Math.Clamp((apX * abX + apY * abY + apZ * abZ) / abLenSq, 0.0f, 1.0f);

            float cX = a.X + abX * t - p.X;
            float cY = a.Y + abY * t - p.Y;
            float cZ = a.Z + abZ * t - p.Z;

            return (float)Math.Sqrt(cX * cX + cY * cY + cZ * cZ);
        }

        private void CookChicken(CChicken chicken)
        {
            // Copy everything we need BEFORE removing the chicken
            var origin = chicken.AbsOrigin!;
            // Spawn at the chicken's height (reverted). As a physics prop it
            // settles onto the ground on its own.
            var pos = new Vector(origin.X, origin.Y, origin.Z + 2.0f);
            float yaw = (float)rng.Next(0, 360);
            string liveModel = chicken.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? string.Empty;

            chicken.AcceptInput("Kill");

            // Physics prop so gravity drops the roast to the floor
            var roast = Utilities.CreateEntityByName<CPhysicsProp>("prop_physics_override");
            if (roast == null) return;

            string cookedModel = Config.CookedChickenModel;
            if (!string.IsNullOrEmpty(cookedModel))
            {
                // The game's actual roast model (precached by this plugin)
                roast.SetModel(cookedModel);
            }
            else if (!string.IsNullOrEmpty(liveModel))
            {
                // Fallback: charred copy of the live chicken
                roast.SetModel(liveModel);
                roast.Render = Color.FromArgb(255, 45, 28, 18);
                Utilities.SetStateChanged(roast, "CBaseModelEntity", "m_clrRender");
            }
            else
            {
                roast.Remove();
                return;
            }

            roast.DispatchSpawn();
            roast.Teleport(pos, new QAngle(0.0f, yaw, 0.0f), new Vector());

            // Enable motion and wake the physics body so it actually falls
            roast.AcceptInput("EnableMotion");
            roast.AcceptInput("Wake");

            SpawnSteam(new Vector(origin.X, origin.Y, origin.Z + 4.0f));

            var roastRef = roast;
            AddTimer(60.0f, () =>
            {
                if (roastRef.IsValid)
                    roastRef.Remove();
            });
        }

        // Rising steam over a fresh roast
        private void SpawnSteam(Vector pos)
        {
            if (string.IsNullOrEmpty(Config.CookedChickenParticle))
                return;

            var particle = Utilities.CreateEntityByName<CInfoParticleSystem>("info_particle_system");
            if (particle == null) return;

            particle.EffectName = Config.CookedChickenParticle;
            particle.StartActive = true;
            particle.Teleport(pos, new QAngle(), new Vector());
            particle.DispatchSpawn();

            // StartActive alone often doesn't kick the system; the explicit
            // Start input is what reliably makes it emit.
            particle.AcceptInput("Start");

            var particleRef = particle;
            AddTimer(60.0f, () =>
            {
                if (particleRef.IsValid)
                    particleRef.Remove();
            });
        }

        // ------------------------------------------------------------------
        // Super Zeus: 3x range.
        //
        // The zeus's hit-detection distance comes from its weapon data
        // (CCSWeaponBaseVData.Range, ~190 units stock). That data object is
        // shared by every taser of the same type, so writing it through any
        // one taser instance changes the real server-side attack range for
        // all of them at once. We remember the original value and restore it
        // on disable/unload/map end.
        // ------------------------------------------------------------------
        private void ApplyTaserRangeToAll()
        {
            foreach (var taser in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>("weapon_taser"))
            {
                if (taser == null || !taser.IsValid) continue;

                ApplyTaserRange(taser);
                break; // VData is shared per weapon type; one write covers all tasers
            }
        }

        private void ApplyTaserRange(CBasePlayerWeapon taser)
        {
            // VData is typed as the base CBasePlayerWeaponVData; Range
            // (m_flRange) lives on the derived CCSWeaponBaseVData, so re-wrap.
            var vdata = taser.VData?.As<CCSWeaponBaseVData>();
            if (vdata == null) return;

            originalTaserRange ??= vdata.Range;

            float target = superZeusEnabled
                ? originalTaserRange.Value * SuperZeusRangeMultiplier
                : originalTaserRange.Value;

            if (Math.Abs(vdata.Range - target) > 0.01f)
                vdata.Range = target;
        }

        // Keep the range correct whenever a taser shows up in someone's hands
        private HookResult OnItemEquip(EventItemEquip ev, GameEventInfo info)
        {
            if (ev.Item == "taser")
                ApplyTaserRangeToAll();

            return HookResult.Continue;
        }

        // Zap tracking. weapon_fire tells us a zeus was fired; the game's own
        // bullet_impact event for that same shot tells us EXACTLY where the
        // trace ended in world space. Anchoring on the impact point fixes the
        // bolt's direction (no eye-angle math), makes it reach precisely as
        // far as the shot did, stops it at walls, and gives us the true line
        // for chicken detection.
        private readonly bool[] pendingZap = new bool[65];

        private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
        {
            string weaponName = ev.Weapon.Replace("weapon_", "").ToLowerInvariant();
            if (weaponName != "taser")
                return HookResult.Continue;

            // Belt-and-suspenders: make sure the range matches the toggle
            ApplyTaserRangeToAll();

            var player = ev.Userid;
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            int slot = player.Slot;
            pendingZap[slot] = true;

            // Fallback: if no impact event arrives for this shot, project
            // along the shooter's view for the full zeus range next frame.
            Server.NextFrame(() =>
            {
                if (!pendingZap[slot]) return;
                pendingZap[slot] = false;

                if (player == null || !player.IsValid) return;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || pawn.AbsOrigin == null) return;

                var eye = GetEyePosition(pawn);
                var fwd = AngleToForward(pawn.EyeAngles);
                float range = (originalTaserRange ?? DefaultTaserRange)
                              * (superZeusEnabled ? SuperZeusRangeMultiplier : 1.0f);
                var end = new Vector(eye.X + fwd.X * range, eye.Y + fwd.Y * range, eye.Z + fwd.Z * range);

                HandleZap(player, eye, end);
            });

            return HookResult.Continue;
        }

        private HookResult OnBulletImpact(EventBulletImpact ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            int slot = player.Slot;
            if (!pendingZap[slot])
                return HookResult.Continue; // some other weapon's bullet

            pendingZap[slot] = false;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null)
                return HookResult.Continue;

            var eye = GetEyePosition(pawn);
            var end = new Vector(ev.X, ev.Y, ev.Z);

            HandleZap(player, eye, end);

            return HookResult.Continue;
        }

        // Superzeus knockback, done the way that actually moves a ragdoll.
        //
        // A ragdoll's motion comes from m_vecRagdollVelocity, which the engine
        // copies from the pawn's velocity at the instant the ragdoll is created
        // during Event_Killed. So the trick is to set the victim's velocity
        // BEFORE the damage is applied (this hook is Pre): if the hit kills
        // them, the ragdoll inherits the launch; if they survive, the same
        // velocity shoves the living player. Pushing the ragdoll after the
        // fact (the old approach) does nothing, because the physics bodies were
        // already spawned with zero inherited velocity.
        private HookResult OnEntityTakeDamagePre(CBaseEntity entity, CTakeDamageInfo info)
        {
            if (!superZeusEnabled)
                return HookResult.Continue;

            if (entity == null || !entity.IsValid || entity.DesignerName != "player")
                return HookResult.Continue;

            bool fromTaser =
                info.Ability.Value?.DesignerName == "weapon_taser" ||
                ((int)info.BitsDamageType & (int)DamageTypes_t.DMG_SHOCK) != 0;
            if (!fromTaser)
                return HookResult.Continue;

            var attacker = info.Attacker.Value;
            if (attacker == null || !attacker.IsValid || attacker.Index == entity.Index)
                return HookResult.Continue;

            var victimPawn = entity.As<CCSPlayerPawn>();
            if (victimPawn.AbsOrigin == null || attacker.AbsOrigin == null)
                return HookResult.Continue;

            float dx = victimPawn.AbsOrigin.X - attacker.AbsOrigin.X;
            float dy = victimPawn.AbsOrigin.Y - attacker.AbsOrigin.Y;
            float horiz = (float)Math.Sqrt(dx * dx + dy * dy);
            if (horiz < 1.0f) { dx = 1.0f; dy = 0.0f; horiz = 1.0f; }

            float force = Config.SuperZeusKnockbackForce;
            var vel = new Vector(dx / horiz * force, dy / horiz * force, force * 0.35f);
            
            // 1. Explicitly set AbsVelocity so the ragdoll snapshot catches it immediately upon death
            if (victimPawn.AbsVelocity != null)
            {
                victimPawn.AbsVelocity.X = vel.X;
                victimPawn.AbsVelocity.Y = vel.Y;
                victimPawn.AbsVelocity.Z = vel.Z;
            }
            
            // 2. Queue the standard Teleport to shove players who survive the damage
            victimPawn.Teleport(null, null, vel);
            
            return HookResult.Continue;
        }

        private void HandleZap(CCSPlayerController player, Vector eye, Vector end)
        {
            var dir = new Vector(end.X - eye.X, end.Y - eye.Y, end.Z - eye.Z);
            float len = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (len < 1.0f) return;

            var unit = new Vector(dir.X / len, dir.Y / len, dir.Z / len);

            if (superZeusEnabled)
                DrawZeusLightning(player, len);

            // Chicken detection always spans the zeus's FULL current reach —
            // impact events can under-report distance (or not arrive at all on
            // an empty long-range zap), which made cooking feel short-ranged.
            float zeusRange = (originalTaserRange ?? DefaultTaserRange)
                              * (superZeusEnabled ? SuperZeusRangeMultiplier : 1.0f);
            var chickenEnd = new Vector(eye.X + unit.X * zeusRange,
                                        eye.Y + unit.Y * zeusRange,
                                        eye.Z + unit.Z * zeusRange);

            CookChickensAlongLine(eye, chickenEnd);
        }

        private static Vector GetEyePosition(CCSPlayerPawn pawn)
        {
            // ViewOffset is the live camera offset (standing ~64, crouched
            // ~46). The previous OldPlayerViewOffsetZ source could report a
            // stale/low value, which is what dragged the bolt down.
            float viewZ = pawn.ViewOffset.Z;
            if (viewZ < 1.0f) viewZ = 64.0f;
            return new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + viewZ);
        }

        private void DrawZeusLightning(CCSPlayerController player, float beamLength)
        {
            var amps = new float[] { 6.0f, 10.0f, 14.0f };
            var bolts = new List<CBeam[]>(3);

            for (int bolt = 0; bolt < 3; bolt++)
            {
                var segs = new CBeam[3];
                bool ok = true;
                for (int s = 0; s < 3; s++)
                {
                    var beam = CreateBeam();
                    if (beam == null) { ok = false; break; }
                    segs[s] = beam;
                }

                if (!ok)
                {
                    foreach (var sg in segs)
                        if (sg != null && sg.IsValid) sg.Remove();
                    continue;
                }

                bolts.Add(segs);
            }

            if (bolts.Count == 0) return;

            // Recompute barrel + endpoint from the shooter's LIVE eye/aim every
            // frame, so if the player turns or moves during the animation the
            // bolts stay glued to the weapon instead of hanging in the air.
            void Reposition()
            {
                var pawn = player.PlayerPawn.Value;
                if (pawn == null || pawn.AbsOrigin == null) return;

                var eye = GetEyePosition(pawn);
                var fwd = AngleToForward(pawn.EyeAngles);

                float hLen = (float)Math.Sqrt(fwd.X * fwd.X + fwd.Y * fwd.Y);
                var right = hLen < 0.01f
                    ? new Vector(1.0f, 0.0f, 0.0f) // aiming straight up/down
                    : new Vector(fwd.Y / hLen, -fwd.X / hLen, 0.0f);

                // Barrel of the zeus: forward, a touch right, below the eye.
                var barrel = new Vector(
                    eye.X + fwd.X * 16.0f + right.X * 5.0f,
                    eye.Y + fwd.Y * 16.0f + right.Y * 5.0f,
                    eye.Z + fwd.Z * 16.0f - 6.0f);

                // Endpoint follows current aim at the original zap length.
                var end = new Vector(
                    eye.X + fwd.X * beamLength,
                    eye.Y + fwd.Y * beamLength,
                    eye.Z + fwd.Z * beamLength);

                for (int b = 0; b < bolts.Count; b++)
                {
                    float amp = amps[b];
                    var mid1 = JitterPoint(Lerp(barrel, end, 0.33f), right, amp);
                    var mid2 = JitterPoint(Lerp(barrel, end, 0.66f), right, amp);

                    SetBeam(bolts[b][0], barrel, mid1);
                    SetBeam(bolts[b][1], mid1, mid2);
                    SetBeam(bolts[b][2], mid2, end);
                }
            }

            // First frame immediately, then animate for ~0.2s (5 frames @ 40ms)
            Reposition();

            int frames = 0;
            const int maxFrames = 5;
            CounterStrikeSharp.API.Modules.Timers.Timer? anim = null;
            anim = AddTimer(0.04f, () =>
            {
                frames++;

                if (frames >= maxFrames)
                {
                    anim?.Kill();
                    foreach (var segs in bolts)
                        foreach (var beam in segs)
                            if (beam.IsValid) beam.Remove();
                    return;
                }

                Reposition();
            }, TimerFlags.REPEAT);
        }

        private static Vector Lerp(Vector a, Vector b, float t)
        {
            return new Vector(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }

        private Vector JitterPoint(Vector basePoint, Vector right, float amount)
        {
            float r = ((float)rng.NextDouble() * 2.0f - 1.0f) * amount;
            float u = ((float)rng.NextDouble() * 2.0f - 1.0f) * amount;
            return new Vector(
                basePoint.X + right.X * r,
                basePoint.Y + right.Y * r,
                basePoint.Z + u);
        }

        // Create a beam entity, styled, spawned, but left in place for the
        // animation loop to position each frame.
        private CBeam? CreateBeam()
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;

            beam.Render = Color.FromArgb(255, 170, 215, 255); // electric blue-white
            beam.Width = 0.12f;
            beam.DispatchSpawn();
            return beam;
        }

        // Move an existing beam's endpoints and network the change.
        private static void SetBeam(CBeam beam, Vector start, Vector end)
        {
            if (!beam.IsValid) return;

            beam.Teleport(start, new QAngle(), new Vector());
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
        }

        private static Vector AngleToForward(QAngle angles)
        {
            double pitch = angles.X * Math.PI / 180.0;
            double yaw = angles.Y * Math.PI / 180.0;

            return new Vector(
                (float)(Math.Cos(pitch) * Math.Cos(yaw)),
                (float)(Math.Cos(pitch) * Math.Sin(yaw)),
                (float)(-Math.Sin(pitch)));
        }

        public void SavePlayerLoadout(CCSPlayerController player)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            ulong steamId = player.SteamID;
            savedWeapons[steamId] = new List<SavedWeapon>();

            var pawn = player.Pawn.Value;
            if (pawn == null) return;

            var weaponServices = pawn.WeaponServices;
            if (weaponServices == null) return;

            foreach (var handle in weaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon == null || !weapon.IsValid) continue;

                savedWeapons[steamId].Add(new SavedWeapon
                {
                    // Resolved from the item definition index so silenced
                    // variants (M4A1-S, USP-S, R8, MP5-SD...) come back as
                    // themselves instead of their DesignerName sibling.
                    Name = GetRealWeaponName(weapon),

                    // Skin data set by !ws-style plugins. Genuine Steam
                    // inventory skins re-apply automatically as long as the
                    // correct weapon is given back to the same player.
                    PaintKit = weapon.FallbackPaintKit,
                    Seed = weapon.FallbackSeed,
                    Wear = weapon.FallbackWear,
                    StatTrak = weapon.FallbackStatTrak,
                });
            }
        }

        public void RestorePlayerLoadout(CCSPlayerController player)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            ulong steamId = player.SteamID;
            if (!savedWeapons.TryGetValue(steamId, out var weapons))
                return;

            // Remove everything the player currently has
            foreach (var handle in player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                     ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
            {
                var weapon = handle.Value;
                if (weapon != null && weapon.IsValid)
                {
                    weapon.AcceptInput("Kill");
                }
            }

            // Give back saved weapons, then re-apply any plugin-set skin data
            foreach (var saved in weapons)
            {
                if (string.IsNullOrEmpty(saved.Name)) continue;

                nint ptr = player.GiveNamedItem(saved.Name);
                if (ptr == nint.Zero || saved.PaintKit <= 0) continue;

                var weapon = new CBasePlayerWeapon(ptr);
                if (!weapon.IsValid) continue;

                // Standard fallback-attribute skin application
                weapon.AttributeManager.Item.ItemID = 16384;
                weapon.AttributeManager.Item.ItemIDLow = 16384;
                weapon.AttributeManager.Item.ItemIDHigh = 0;
                weapon.FallbackPaintKit = saved.PaintKit;
                weapon.FallbackSeed = saved.Seed;
                weapon.FallbackWear = saved.Wear;
                if (saved.StatTrak >= 0)
                    weapon.FallbackStatTrak = saved.StatTrak;
            }
        }

        // (Old string-based Save/Restore helpers removed — superseded by the
        // skin-aware SavedWeapon snapshot above.)



        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            // Clear all loadouts when a new round starts
            savedWeapons.Clear();

            // Freshly spawned tasers should respect the current range setting
            ApplyTaserRangeToAll();

            if (!zeusOnlyEnabled)
                return HookResult.Continue;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || player.TeamNum < 2) continue;

                // Save fresh round loadout before stripping
                SavePlayerLoadout(player);

                StripIllegalWeapons(player);

                // Ensure Zeus is in slot
                bool hasTaser = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                    .Any(w => w.Value != null
                           && w.Value.IsValid
                           && w.Value.DesignerName.Equals("weapon_taser", StringComparison.OrdinalIgnoreCase))
                    ?? false;

                if (!hasTaser)
                {
                    player.GiveNamedItem("weapon_taser");
                }
                player.ExecuteClientCommand("slot11");
            }

            return HookResult.Continue;
        }

        // Removes weapons not in whitelist
        private void StripIllegalWeapons(CCSPlayerController player)
        {
            foreach (var weapon in player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
            {
                var weapEnt = weapon.Value;

                if (weapEnt != null && weapEnt.IsValid)
                {
                    string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                    if (!allowedWeapons.Contains(className))
                    {
                        weapEnt.Remove(); // detaches from player
                    }
                }
            }
        }

        // Block buying before it happens
        private HookResult OnBuyCommand(CCSPlayerController? player, CommandInfo cmd)
        {
            if (!zeusOnlyEnabled || player == null || !player.IsValid || player.TeamNum < 2)
                return HookResult.Continue;

            if (cmd.ArgCount < 2) return HookResult.Continue;

            string weaponName = cmd.GetArg(1)
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            // If name is "unused", let it through. Check in pickup
            if (weaponName == "unused")
                return HookResult.Continue;

            if (!allowedWeapons.Contains(weaponName))
            {
                player.PrintToChat("Only Zeus and Utility Are Allowed.");
                return HookResult.Stop;
            }

            return HookResult.Continue;
        }

        // The console "buy" listener above can't see every purchase path (the
        // buy menu UI in particular), so purchases can slip through: the
        // pickup handler then deletes the gun — but the player already PAID.
        // That's the "steals your money" bug. This fires on the actual
        // purchase and gives the money back.
        private HookResult OnItemPurchase(EventItemPurchase ev, GameEventInfo info)
        {
            if (!zeusOnlyEnabled) return HookResult.Continue;

            var player = ev.Userid;
            if (player == null || !player.IsValid) return HookResult.Continue;

            string weaponName = ev.Weapon
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            if (allowedWeapons.Contains(weaponName))
                return HookResult.Continue;

            if (Config.RefundIllegalPurchases &&
                Config.WeaponPrices.TryGetValue(weaponName, out int price) && price > 0)
            {
                var money = player.InGameMoneyServices;
                if (money != null)
                {
                    money.Account += price;
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
                    player.PrintToChat($"Only Zeus and Utility Are Allowed — ${price} Refunded.");
                }
            }

            // Safety net: whatever was bought must not stay equipped
            Server.NextFrame(() =>
            {
                if (player != null && player.IsValid)
                    StripIllegalWeapons(player);
            });

            return HookResult.Continue;
        }

        // Remove illegal pickups from ground
        private HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info)
        {
            if (!zeusOnlyEnabled) return HookResult.Continue;

            string weaponName = ev.Item
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            if (!allowedWeapons.Contains(weaponName))
            {
                var player = ev.Userid;
                if (player != null && player.IsValid)
                {
                    var pawn = player.PlayerPawn.Value;
                    var weaponServices = pawn?.WeaponServices;

                    // Find the illegal weapon entity just picked up
                    foreach (var handle in weaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                    {
                        var weapEnt = handle.Value;
                        if (weapEnt != null && weapEnt.IsValid)
                        {
                            string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                            if (className == weaponName)
                            {
                                // Remove from player and kill entity so it disappears completely
                                weapEnt.AcceptInput("Kill");

                                //player.PrintToChat("[Zeus Mode] Illegal weapon removed!");
                                break;
                            }
                        }
                    }
                }
            }

            return HookResult.Continue;
        }

        private void OnPlayerJoin(int playerSlot)
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.SteamID == 0) return;

            if (zeusOnlyEnabled)
            {
                // Fresh snapshot of whatever they spawned with
                SavePlayerLoadout(player);

                StripIllegalWeapons(player);
                player.GiveNamedItem("weapon_taser");
                player.ExecuteClientCommand("slot11");
            }
        }
        public override void Unload(bool hotReload)
        {
            ResetPluginState();
        }
    }
}
