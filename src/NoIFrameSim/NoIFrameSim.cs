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
    public override Version Version => new Version(1, 1, 0);
    public override string Author => "Custom";
    public override string Description => "Memberikan damage susulan super cepat menembus i-frame pemain";

    // === PENGATURAN KECEPATAN & DAMAGE ===
    // Jeda antar hit dalam milidetik (150ms = ~6-7 hit per detik, 50ms = 20 hit per detik)
    private const int IntervalMs = 150;
    // Pengali damage susulan dari hit awal (0.5 = 50% damage awal, 1.0 = 100% full damage)
    private const double DamageMultiplier = 0.5;
    // Batas maksimal durasi loop (9000ms = 9 detik) agar aman dari memory leak
    private const int MaxDurationMs = 9000;

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

        if (!_activeTracking.TryAdd(player.Index, true))
            return;

        int tickDamage = Math.Max(5, (int)(args.Damage * DamageMultiplier));
        _ = TrackFastDamageAsync(player, tickDamage);
    }

    private async Task TrackFastDamageAsync(TSPlayer player, int tickDamage)
    {
        try
        {
            int maxIterations = MaxDurationMs / IntervalMs;
            int elapsedSinceAnim = 0;

            for (int i = 0; i < maxIterations; i++)
            {
                await Task.Delay(IntervalMs);
                elapsedSinceAnim += IntervalMs;

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

                // Jika sudah lewat 700ms (i-frame client habis), pakai DamagePlayer biar keluar suara & angka merah
                if (elapsedSinceAnim >= 700)
                {
                    elapsedSinceAnim = 0;
                    player.DamagePlayer(tickDamage);
                }
                else
                {
                    // Tembus i-frame client dengan memotong statLife langsung & sync paket darah
                    player.TPlayer.statLife -= tickDamage;
                    if (player.TPlayer.statLife <= 0)
                    {
                        player.TPlayer.statLife = 0;
                        player.KillPlayer();
                        break;
                    }
                    else
                    {
                        TSPlayer.All.SendData(PacketTypes.PlayerHp, "", player.Index);
                    }
                }
            }
        }
        finally
        {
            _activeTracking.TryRemove(player.Index, out _);
        }
    }
}
