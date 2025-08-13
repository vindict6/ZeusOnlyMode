using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Generic;
using System.Linq;

namespace ZeusOnlyMode
{
    public class ZeusOnlyPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Only Mode";
        public override string ModuleVersion => "2.0";

        private bool zeusOnlyEnabled = false;

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
            "decoy", "decoygrenade"
        };

        // Weapon prices in CS2
        private readonly Dictionary<string, int> weaponPrices = new()
        {
            { "ak47", 2700 },
            { "m4a1", 3100 }, { "m4a1_silencer", 2900 },
            { "awp", 4750 },
            { "ssg08", 1700 },
            { "aug", 3300 },
            { "sg556", 3000 },
            { "galilar", 1800 },
            { "famas", 2050 },
            { "p90", 2350 },
            { "ump45", 1200 },
            { "mp7", 1500 },
            { "mp9", 1250 },
            { "mac10", 1050 },
            { "nova", 1050 },
            { "xm1014", 2000 },
            { "mag7", 1300 },
            { "m249", 5200 },
            { "negev", 1700 },
            { "glock", 200 },
            { "hkp2000", 200 }, { "usp_silencer", 200 },
            { "p250", 300 },
            { "fiveseven", 500 }, { "cz75a", 500 },
            { "deagle", 700 }, { "revolver", 600 },
            { "tec9", 500 }
            // Grenades are omitted because they’re allowed
};

        private readonly Dictionary<ulong, List<string>> savedLoadouts = new();

        public override void Load(bool hotReload)
        {
            AddCommand("css_zeus_mode", "Enable or disable Zeus-only mode", Command_ToggleZeusMode);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);

            // Hook buy command
            AddCommandListener("buy", OnBuyCommand);
        }

        private void Command_ToggleZeusMode(CCSPlayerController? player, CommandInfo info)
        {
            if (info.ArgCount < 2)
            {
                player?.PrintToChat("Usage: css_zeus_mode <0/1>");
                return;
            }

            bool newState = info.GetArg(1) == "1";

            if (newState == zeusOnlyEnabled)
            {
                player?.PrintToChat($"[Zeus Mode] Already {(zeusOnlyEnabled ? "enabled" : "disabled")}.");
                return;
            }

            zeusOnlyEnabled = newState;

            if (zeusOnlyEnabled)
            {
                SaveAllLoadouts();
                Server.PrintToChatAll("[Zeus Mode] Zeus/grenade/knife-only mode ENABLED.");
            }
            else
            {
                RestoreAllLoadouts();
                savedLoadouts.Clear();
                Server.PrintToChatAll("[Zeus Mode] Zeus mode DISABLED — loadouts restored.");
            }
        }

        private void SaveAllLoadouts()
        {
            foreach (var player in Utilities.GetPlayers())
                SavePlayerLoadout(player);
        }

        private void SavePlayerLoadout(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.TeamNum < 2 || player.SteamID == 0) return;

            var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>();
            savedLoadouts[player.SteamID] = weapons
                .Select(w => w.Value?.DesignerName ?? "")
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
        }

        private void RestoreAllLoadouts()
        {
            foreach (var player in Utilities.GetPlayers())
                RestorePlayerLoadout(player);
        }

        private void RestorePlayerLoadout(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.TeamNum < 2 || player.SteamID == 0) return;

            foreach (var weapon in player.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                weapon.Value?.Remove();

            if (savedLoadouts.TryGetValue(player.SteamID, out var weaponList))
            {
                foreach (var weapon in weaponList)
                    player.GiveNamedItem(weapon);
            }
        }

        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            if (!zeusOnlyEnabled) return HookResult.Continue;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || player.TeamNum < 2) continue;

                StripIllegalWeapons(player);

                // Always give Zeus
                player.GiveNamedItem("weapon_taser");
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
                        weapEnt.Remove();
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
                player.PrintToChat("[Zeus Mode] You can only buy Zeus, grenades, or knife!");
                return HookResult.Stop;
            }

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
                ev.Userid?.PrintToChat("[Zeus Mode] Illegal weapon removed!");
                StripIllegalWeapons(ev.Userid!);
            }

            return HookResult.Continue;
        }

        private void OnPlayerJoin(int playerSlot)
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.SteamID == 0) return;

            if (zeusOnlyEnabled)
            {
                StripIllegalWeapons(player);
                player.GiveNamedItem("weapon_taser");

                if (!savedLoadouts.ContainsKey(player.SteamID))
                    SavePlayerLoadout(player);
            }
        }
    }
}
