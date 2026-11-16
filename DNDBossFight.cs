using System;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace DNDBossFight;

[ApiVersion(2, 1)]
public class DNDBossFightPlugin : TerrariaPlugin
{
    public override string Name => "DNDBossFight";
    public override string Author => "Brok";
    public override Version Version => new(1, 0, 4); // Versi baru
    public override string Description => "Menonaktifkan penjatuhan tombstone saat pemain mati ketika ada boss yang aktif.";

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

        // Command untuk toggle plugin
        Commands.ChatCommands.Add(new Command("dndboss.admin", TogglePlugin, "dndboss"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
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
}
