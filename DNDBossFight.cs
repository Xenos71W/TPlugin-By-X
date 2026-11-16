using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace DNDBossFight;

[ApiVersion(2, 1)]
public class DNDBossFightPlugin : TerrariaPlugin
{
    public override string Name => "DNDBossFight";
    public override string Author => "Brok";
    public override Version Version => new(1, 0, 2); // Versi dinaikkan lagi
    public override string Description => "Mencegah spawn monster di sekitar boss dan menonaktifkan tombstone saat melawan boss.";

    // Radius (dalam tiles) di sekitar boss yang tidak boleh ada monster spawn
    private const float BossProtectionRadius = 120f;

    private bool _originalDisableTombstones;
    private bool _pluginEnabled = true;
    private bool _bossWasActive = false;

    public DNDBossFightPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        _originalDisableTombstones = TShock.Config.Settings.DisableTombstones;

        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
        ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn); // Tetap dipakai buat jaga-jaga

        Commands.ChatCommands.Add(new Command("dndboss.admin", TogglePlugin, "dndboss"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == TogglePlugin);

            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
        }
        base.Dispose(disposing);
    }

    private void TogglePlugin(CommandArgs args)
    {
        _pluginEnabled = !_pluginEnabled;
        args.Player.SendSuccessMessage($"[DNDBossFight] Plugin {(_pluginEnabled ? "diaktifkan" : "dinonaktifkan")}.");

        if (!_pluginEnabled && _bossWasActive)
        {
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
            _bossWasActive = false;
        }
    }

    private void OnGameUpdate(EventArgs args)
    {
        if (!_pluginEnabled) return;

        // 1. Cek apakah ada boss yang aktif dan simpan posisinya
        bool bossActive = false;
        var activeBosses = new List<NPC>();
        
        foreach (var npc in Main.npc)
        {
            if (npc.active && npc.boss)
            {
                bossActive = true;
                activeBosses.Add(npc);
            }
        }

        // 2. Kelola pengaturan tombstone berdasarkan status boss
        if (bossActive && !_bossWasActive)
        {
            _originalDisableTombstones = TShock.Config.Settings.DisableTombstones;
            TShock.Config.Settings.DisableTombstones = true;
            _bossWasActive = true;
        }
        else if (!bossActive && _bossWasActive)
        {
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
            _bossWasActive = false;
        }

        // 3. THE KILL AURA: Bersihkan monster biasa di sekitar boss
        if (bossActive)
        {
            float radiusSq = BossProtectionRadius * BossProtectionRadius;

            foreach (var npc in Main.npc)
            {
                if (!npc.active) continue;

                // Abaikan Boss, NPC Kota, Hewan peliharaan, dan Minion Boss (realLife != -1)
                if (npc.boss || npc.townNPC || npc.friendly || npc.realLife != -1) continue;

                foreach (var boss in activeBosses)
                {
                    if (Vector2.DistanceSquared(boss.Center, npc.Center) <= radiusSq)
                    {
                        // Hapus paksa monster dari server (dan client)
                        npc.active = false;
                        npc.type = 0;
                        break; // Keluar dari loop boss karena NPC sudah dihapus
                    }
                }
            }
        }
    }

    private void OnNpcSpawn(NpcSpawnEventArgs args)
    {
        if (!_pluginEnabled) return;

        var npc = Main.npc[args.NpcId];
        if (npc == null || !npc.active) return;

        // Abaikan Boss, NPC Kota, Hewan, dan Minion Boss
        if (npc.boss || npc.townNPC || npc.friendly || npc.realLife != -1) return;

        foreach (var boss in Main.npc)
        {
            if (!boss.active || !boss.boss) continue;

            if (Vector2.DistanceSquared(boss.Center, npc.Center) <= BossProtectionRadius * BossProtectionRadius)
            {
                args.Handled = true; 
                npc.active = false;
                npc.type = 0;
                break;
            }
        }
    }
}
