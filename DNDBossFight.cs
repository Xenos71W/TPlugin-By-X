using System;
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
    public override Version Version => new(1, 0, 0);
    public override string Description => "Mencegah spawn monster di sekitar boss dan menonaktifkan tombstone saat melawan boss.";

    // Radius (dalam tiles) di sekitar boss yang tidak boleh ada monster spawn
    private const float BossProtectionRadius = 120f;

    // Menyimpan nilai asli DisableTombstones dari config TShock
    private bool _originalDisableTombstones;
    private bool _pluginEnabled = true;
    private bool _bossWasActive = false;

    public DNDBossFightPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        // Simpan nilai awal config tombstone
        _originalDisableTombstones = TShock.Config.Settings.DisableTombstones;

        // Hook untuk update setiap tick (memantau boss & mengatur tombstone)
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

        // Hook untuk mencegah spawn NPC
        ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);

        // Command untuk toggle plugin
        Commands.ChatCommands.Add(new Command("dndboss.admin", TogglePlugin, "dndboss"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == TogglePlugin);

            // Kembalikan config tombstone ke nilai asli saat plugin dimatikan
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
        }
        base.Dispose(disposing);
    }

    private void TogglePlugin(CommandArgs args)
    {
        _pluginEnabled = !_pluginEnabled;
        args.Player.SendSuccessMessage($"[DNDBossFight] Plugin {(_pluginEnabled ? "diaktifkan" : "dinonaktifkan")}.");

        // Jika dinonaktifkan, kembalikan pengaturan tombstone
        if (!_pluginEnabled && _bossWasActive)
        {
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
            _bossWasActive = false;
        }
    }

    private void OnGameUpdate(EventArgs args)
    {
        if (!_pluginEnabled) return;

        // Cek apakah ada boss yang aktif
        bool bossActive = false;
        foreach (var npc in Main.npc)
        {
            if (npc.active && npc.boss)
            {
                bossActive = true;
                break;
            }
        }

        // Kelola pengaturan tombstone berdasarkan status boss
        if (bossActive && !_bossWasActive)
        {
            // Boss baru muncul -> nonaktifkan tombstone
            _originalDisableTombstones = TShock.Config.Settings.DisableTombstones;
            TShock.Config.Settings.DisableTombstones = true;
            _bossWasActive = true;
        }
        else if (!bossActive && _bossWasActive)
        {
            // Boss sudah mati -> kembalikan pengaturan tombstone
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
            _bossWasActive = false;
        }
    }

    private void OnNpcSpawn(NpcSpawnEventArgs args)
    {
        if (!_pluginEnabled) return;

        // Gunakan args.NpcId untuk mendapatkan index NPC
        var npc = Main.npc[args.NpcId];
        if (npc == null || !npc.active) return;

        // Jangan batalkan spawn jika NPC tersebut adalah boss (atau bagian dari boss)
        if (npc.boss || npc.type == NPCID.MoonLordCore || npc.type == NPCID.MoonLordHand || npc.type == NPCID.MoonLordHead)
            return;

        // Cari boss aktif terdekat
        Vector2? nearestBossPos = null;
        float nearestDistSq = float.MaxValue;

        foreach (var boss in Main.npc)
        {
            if (!boss.active || !boss.boss) continue;

            float distSq = Vector2.DistanceSquared(boss.Center, npc.Center);
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestBossPos = boss.Center;
            }
        }

        // Jika ada boss dalam radius, batalkan spawn
        if (nearestBossPos.HasValue && nearestDistSq <= BossProtectionRadius * BossProtectionRadius)
        {
            args.Handled = true; // Batalkan spawn NPC
        }
    }
}
