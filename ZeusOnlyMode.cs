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

            // Intercept damage before it's applied: this is how we turn the
            // zeus's shock damage into burn damage on chickens, which makes
            // the game roast them exactly like a molotov/incendiary kill.
            RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);

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
        // Chicken cooking: zeus a chicken -> roast chicken.
        //
        // The game decides "roasted" by the damage type that killed the
        // chicken (DMG_BURN, i.e. molotov/incendiary). The zeus deals
        // DMG_SHOCK, so we intercept the damage right before it's applied
        // and swap shock for burn — the game then handles the cooked model
        // and sizzle itself, identical to a fire kill.
        // ------------------------------------------------------------------
        private HookResult OnEntityTakeDamagePre(CBaseEntity entity, CTakeDamageInfo info)
        {
            if (entity == null || !entity.IsValid)
                return HookResult.Continue;

            if (entity.DesignerName != "chicken")
                return HookResult.Continue;

            bool fromZeus =
                info.Ability.Value?.DesignerName == "weapon_taser" ||
                ((int)info.BitsDamageType & (int)DamageTypes_t.DMG_SHOCK) != 0;

            if (!fromZeus)
                return HookResult.Continue;

            info.BitsDamageType = DamageTypes_t.DMG_BURN;
            info.Damage = Math.Max(info.Damage, 100.0f); // make sure it cooks through

            return HookResult.Continue;
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
            var vdata = taser.VData;
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

        // Draw the extended electrical arc when a super-zeus is fired.
        // The stock zap particle is client-side and stays short, so we draw
        // a jagged server-side lightning beam out to the real (3x) range
        // that everyone can see.
        private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
        {
            string weaponName = ev.Weapon.Replace("weapon_", "").ToLowerInvariant();
            if (weaponName != "taser")
                return HookResult.Continue;

            // Belt-and-suspenders: make sure the range matches the toggle
            ApplyTaserRangeToAll();

            if (!superZeusEnabled)
                return HookResult.Continue;

            var player = ev.Userid;
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            DrawZeusLightning(player);

            return HookResult.Continue;
        }

        private void DrawZeusLightning(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null)
                return;

            float viewOffsetZ = pawn.CameraServices?.OldPlayerViewOffsetZ ?? 64.0f;
            var eye = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + viewOffsetZ);
            var fwd = AngleToForward(pawn.EyeAngles);

            float range = (originalTaserRange ?? DefaultTaserRange) * SuperZeusRangeMultiplier;

            // Start slightly below the eye so the arc reads as coming from the weapon
            var start = new Vector(eye.X, eye.Y, eye.Z - 6.0f);
            var end = new Vector(eye.X + fwd.X * range, eye.Y + fwd.Y * range, eye.Z + fwd.Z * range);

            // Two jittered midpoints give it a jagged, electric look
            var right = new Vector(fwd.Y, -fwd.X, 0.0f);
            var mid1 = JitterPoint(Lerp(start, end, 0.33f), right, 10.0f);
            var mid2 = JitterPoint(Lerp(start, end, 0.66f), right, 10.0f);

            SpawnBeamSegment(start, mid1);
            SpawnBeamSegment(mid1, mid2);
            SpawnBeamSegment(mid2, end);
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

        private void SpawnBeamSegment(Vector start, Vector end)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return;

            beam.RenderMode = RenderMode_t.kRenderTransColor;
            beam.Render = Color.FromArgb(255, 170, 215, 255); // electric blue-white
            beam.Width = 1.2f;

            beam.Teleport(start, new QAngle(), new Vector());
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;

            beam.DispatchSpawn();

            AddTimer(0.15f, () =>
            {
                if (beam.IsValid)
                    beam.Remove();
            });
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
