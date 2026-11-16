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
    public override Version Version => new(1, 0, 1); // Versi dinaikkan
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
        ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);

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
            _originalDisableTombstones = TShock.Config.Settings.DisableTombstones;
            TShock.Config.Settings.DisableTombstones = true;
            _bossWasActive = true;
        }
        else if (!bossActive && _bossWasActive)
        {
            TShock.Config.Settings.DisableTombstones = _originalDisableTombstones;
            _bossWasActive = false;
        }
    }
