using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace NoIFrameSim;

[ApiVersion(2, 1)]
public class NoIFrameSim : TerrariaPlugin
{
    public override string Name => "NoIFrameSim";
    public override Version Version => new Version(1, 0, 0);
    public override string Author => "Custom";
    public override string Description => "Memberikan damage susulan beruntun saat pemain menempel dengan musuh";

    private readonly ConcurrentDictionary<int, bool> _activeTracking = new();

    public NoIFrameSim(Main game) : base(game) { }

    public override void Initialize()
    {
        GetDataHandlers.PlayerDamage += OnPlayerDamage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GetDataHandlers.PlayerDamage -= OnPlayerDamage;
        }
        base.Dispose(disposing);
    }

    private void OnPlayerDamage(object? sender, GetDataHandlers.PlayerDamageEventArgs args)
    {
        TSPlayer player = args.Player;
        if (player == null || !player.Active || player.Dead || args.PVP)
            return;

        // Cegah loop ganda kalau pemain sudah masuk daftar pantauan
        if (!_activeTracking.TryAdd(player.Index, true))
            return;

        // Damage susulan diambil dari setengah damage pukulan awal (minimal 10)
        int tickDamage = Math.Max(10, args.Damage / 2);
        _ = TrackContactDamageAsync(player, tickDamage);
    }

    private async Task TrackContactDamageAsync(TSPlayer player, int tickDamage)
    {
        try
        {
            for (int i = 0; i < 15; i++)
            {
                // Jeda 700ms (pas melewati 40-tick i-frame bawaan client)
                await Task.Delay(700);

                if (player == null || !player.Active || player.Dead || player.TPlayer == null)
                    break;

                bool stillTouchingEnemy = false;
                for (int n = 0; n < Main.maxNPCs; n++)
                {
                    NPC npc = Main.npc[n];
                    if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
                    {
                        if (player.TPlayer.Hitbox.Intersects(npc.Hitbox) ||
                            player.TPlayer.Distance(npc.Center) < 56f)
                        {
                            stillTouchingEnemy = true;
                            break;
                        }
                    }
                }

                if (!stillTouchingEnemy)
                    break;

                player.DamagePlayer(tickDamage);
            }
        }
        finally
        {
            _activeTracking.TryRemove(player.Index, out _);
        }
    }
}
