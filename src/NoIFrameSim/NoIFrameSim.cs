using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;

namespace NoIFrameSim;

[ApiVersion(2, 1)]
public class NoIFrameSim : TerrariaPlugin
{
    public override string Name => "NoIFrameSim";
    public override Version Version => new Version(2, 3, 0);
    public override string Author => "Custom";
    public override string Description => "Aliran damage murni super deras menggunakan burst 6 slot immunity";

    // === PENGATURAN KEBOCORAN DAMAGE MURNI ===
    // 35ms = memuntahkan seluruh 6 slot dalam 0.21 detik saat bersentuhan
    private const int IntervalMs = 35;

    // 1.0 = 100% damage murni asli musuh/proyektil tanpa tambahan buatan
    private const double DamageMultiplier = 1.0;

    // Maksimal slot yang ditembak bersamaan dalam 1 tick jika menyentuh banyak hitbox (bos + laser / segmen cacing)
    private const int MaxHitsPerTick = 3;

    // Durasi reset tiap slot immunity di Terraria (40 tick = ~667ms)
    private const long SlotCooldownMs = 670;

    private static readonly int[] CooldownSlots = { -1, 0, 1, 2, 3, 4 };

    private static readonly MethodInfo? SendPlayerHurtMethod =
        typeof(NetMessage).GetMethod("SendPlayerHurt", BindingFlags.Public | BindingFlags.Static);

    // Menyimpan timestamp kapan masing-masing dari 6 slot kembali terbuka untuk tiap pemain
    private readonly long[,] _slotUnlockTime = new long[Main.maxPlayers, 6];
    private CancellationTokenSource? _cts;

    public NoIFrameSim(Main game) : base(game) { }

    public override void Initialize()
    {
        _cts = new CancellationTokenSource();
        _ = MonitorPureBurstLoopAsync(_cts.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task MonitorPureBurstLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, token);

                var players = TShock.Players;
                if (players == null) continue;

                long nowMs = Environment.TickCount64;

                for (int i = 0; i < players.Length; i++)
                {
                    TSPlayer? player = players[i];
                    if (player == null || !player.Active || player.Dead || player.TPlayer == null ||
                        player.GodMode || player.TPlayer.creativeGodMode)
                    {
                        ResetPlayerSlots(i);
                        continue;
                    }

                    ProcessPlayerContacts(player, i, nowMs);
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch
            {
                // Abaikan error sesaat jika entity despawn di tengah pengecekan
            }
        }
    }

    private void ProcessPlayerContacts(TSPlayer player, int playerIndex, long nowMs)
    {
        Player tPlayer = player.TPlayer;
        Rectangle playerBox = tPlayer.Hitbox;
        int hitsThisTick = 0;

        // 1. Cek semua monster / segmen bos yang bersentuhan dengan hitbox pemain
        for (int n = 0; n < Main.npc.Length && hitsThisTick < MaxHitsPerTick; n++)
        {
            NPC npc = Main.npc[n];
            if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
            {
                if (playerBox.Intersects(npc.Hitbox))
                {
                    if (!TryClaimReadySlot(playerIndex, nowMs, out int slot))
                        return; // Semua 6 slot sedang cooldown, hentikan loop agar hemat CPU

                    int rawDamage = Math.Max(1, (int)(npc.damage * DamageMultiplier));
                    int hitDir = tPlayer.Center.X < npc.Center.X ? -1 : 1;
                    SendSlotHurt(player, PlayerDeathReason.ByNPC(n), rawDamage, hitDir, slot);
                    hitsThisTick++;
                }
            }
        }

        if (hitsThisTick >= MaxHitsPerTick)
            return;

        // 2. Cek semua proyektil musuh yang bersentuhan dengan hitbox pemain
        int projDifficultyMult = Main.masterMode ? 6 : (Main.expertMode ? 4 : 2);

        for (int p = 0; p < Main.projectile.Length && hitsThisTick < MaxHitsPerTick; p++)
        {
            Projectile proj = Main.projectile[p];
            if (proj != null && proj.active && proj.hostile && proj.damage > 0)
            {
                if (playerBox.Intersects(proj.Hitbox))
                {
                    if (!TryClaimReadySlot(playerIndex, nowMs, out int slot))
                        return;

                    int rawDamage = Math.Max(1, (int)(proj.damage * projDifficultyMult * DamageMultiplier));
                    int hitDir = tPlayer.Center.X < proj.Center.X ? -1 : 1;
                    SendSlotHurt(player, PlayerDeathReason.ByProjectile(-1, p), rawDamage, hitDir, slot);
                    hitsThisTick++;
                }
            }
        }
    }

    private bool TryClaimReadySlot(int playerIndex, long nowMs, out int cooldownSlot)
    {
        for (int s = 0; s < CooldownSlots.Length; s++)
        {
            if (nowMs >= _slotUnlockTime[playerIndex, s])
            {
                _slotUnlockTime[playerIndex, s] = nowMs + SlotCooldownMs;
                cooldownSlot = CooldownSlots[s];
                return true;
            }
        }

        cooldownSlot = -1;
        return false;
    }

    private void ResetPlayerSlots(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= Main.maxPlayers) return;
        for (int s = 0; s < CooldownSlots.Length; s++)
        {
            _slotUnlockTime[playerIndex, s] = 0;
        }
    }

    private static void SendSlotHurt(TSPlayer player, PlayerDeathReason reason, int damage, int hitDirection, int cooldownSlot)
    {
        if (SendPlayerHurtMethod != null)
        {
            var parameters = SendPlayerHurtMethod.GetParameters();
            object?[] invokeArgs = new object?[parameters.Length];

            if (parameters.Length > 0) invokeArgs[0] = player.Index;
            if (parameters.Length > 1) invokeArgs[1] = reason;
            if (parameters.Length > 2) invokeArgs[2] = damage;
            if (parameters.Length > 3) invokeArgs[3] = hitDirection;
            if (parameters.Length > 4) invokeArgs[4] = false;
            if (parameters.Length > 5) invokeArgs[5] = false;
            if (parameters.Length > 6) invokeArgs[6] = cooldownSlot;
            if (parameters.Length > 7) invokeArgs[7] = -1;
            if (parameters.Length > 8) invokeArgs[8] = -1;

            SendPlayerHurtMethod.Invoke(null, invokeArgs);
            return;
        }

        player.DamagePlayer(damage);
    }
}
