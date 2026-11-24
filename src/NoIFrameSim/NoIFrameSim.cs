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
    public override Version Version => new Version(3, 0, 0);
    public override string Author => "Custom";
    public override string Description => "Kombinasi rotasi 6 slot damage asli musuh dan tumpukan debuff DoT tanpa i-frame";

    // === 1. PENGATURAN HIT FISIK / PROYEKTIL (REAL DAMAGE) ===
    // 115ms x 6 slot = 690ms (batas tercepat untuk menembus 667ms cooldown Terraria)
    private const int IntervalMs = 115;

    // Pengali damage asli musuh (1.0 = 100% asli, 1.25 = 125%, 2.0 = 2x lipat)
    private const double DamageMultiplier = 1.0;

    // True = kena badan bos + laser secara bersamaan bakal memicu 2 hit sekaligus
    private const bool EnableMultiHazardBurst = true;

    // === 2. PENGATURAN ALIRAN DEBUFF DoT (PER FRAME 60x/DETIK) ===
    private const bool EnableDotDebuffs = true;

    // Durasi debuff dalam tick (60 tick = 1 detik).
    // 25 tick (~0.4 detik) bikin aliran DoT langsung mati kalau berhasil dash kabur.
    // Ubah ke 90 atau 120 kalau mau efek kebakarnya tetap nempel 1.5 - 2 detik setelah lepas.
    private const int DebuffDurationTicks = 25;

    // Tumpukan 9 debuff DoT (~180+ HP/detik di luar damage fisik):
    // 39 = Cursed Inferno, 144 = Electrified, 68 = Suffocation, 323 = Hellfire,
    // 189 = Daybreak, 324 = Frostbite, 153 = Shadowflame, 70 = Acid Venom, 204 = Oiled
    private static readonly int[] DotDebuffIds =
    {
        39, 144, 68, 323, 189, 324, 153, 70, 204
    };

    // 6 jalur slot terpisah di Terraria (-1 = global immune, 0..4 = hurtCooldowns)
    private static readonly int[] CooldownSlots = { -1, 0, 1, 2, 3, 4 };

    private static readonly MethodInfo? SendPlayerHurtMethod =
        typeof(NetMessage).GetMethod("SendPlayerHurt", BindingFlags.Public | BindingFlags.Static);

    private readonly int[] _slotRotation = new int[Main.maxPlayers];
    private CancellationTokenSource? _cts;

    public NoIFrameSim(Main game) : base(game) { }

    public override void Initialize()
    {
        _cts = new CancellationTokenSource();
        _ = MonitorComboDamageLoopAsync(_cts.Token);
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

    private async Task MonitorComboDamageLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, token);

                var players = TShock.Players;
                if (players == null) continue;

                for (int i = 0; i < players.Length; i++)
                {
                    TSPlayer? player = players[i];
                    if (player == null || !player.Active || player.Dead || player.TPlayer == null ||
                        player.GodMode || player.TPlayer.creativeGodMode)
                    {
                        continue;
                    }

                    ProcessPlayerThreats(player, i);
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch
            {
                // Abaikan error sesaat saat entity despawn di tengah loop
            }
        }
    }

    private void ProcessPlayerThreats(TSPlayer player, int playerIndex)
    {
        Player tPlayer = player.TPlayer;
        Rectangle playerBox = tPlayer.Hitbox;

        int bestNpcDamage = 0;
        int bestNpcIndex = -1;
        int npcHitDir = 0;

        // 1. Cek sentuhan dengan monster / bos
        for (int n = 0; n < Main.npc.Length; n++)
        {
            NPC npc = Main.npc[n];
            if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
            {
                if (playerBox.Intersects(npc.Hitbox) && npc.damage > bestNpcDamage)
                {
                    bestNpcDamage = npc.damage;
                    bestNpcIndex = n;
                    npcHitDir = tPlayer.Center.X < npc.Center.X ? -1 : 1;
                }
            }
        }

        int bestProjDamage = 0;
        int bestProjIndex = -1;
        int projHitDir = 0;
        int projDifficultyMult = Main.masterMode ? 6 : (Main.expertMode ? 4 : 2);

        // 2. Cek sentuhan dengan proyektil musuh
        for (int p = 0; p < Main.projectile.Length; p++)
        {
            Projectile proj = Main.projectile[p];
            if (proj != null && proj.active && proj.hostile && proj.damage > 0)
            {
                int scaledDamage = proj.damage * projDifficultyMult;
                if (playerBox.Intersects(proj.Hitbox) && scaledDamage > bestProjDamage)
                {
                    bestProjDamage = scaledDamage;
                    bestProjIndex = p;
                    projHitDir = tPlayer.Center.X < proj.Center.X ? -1 : 1;
                }
            }
        }

        if (bestNpcDamage <= 0 && bestProjDamage <= 0)
            return;

        // 3. Suntikkan aliran DoT 60fps secara bersamaan saat bersentuhan
        if (EnableDotDebuffs)
        {
            for (int d = 0; d < DotDebuffIds.Length; d++)
            {
                player.SetBuff(DotDebuffIds[d], DebuffDurationTicks);
            }
        }

        // 4. Hantam dengan rotasi 6 slot damage asli musuh
        if (EnableMultiHazardBurst)
        {
            if (bestNpcDamage > 0)
            {
                int dmg = Math.Max(1, (int)(bestNpcDamage * DamageMultiplier));
                SendSlotHurt(player, PlayerDeathReason.ByNPC(bestNpcIndex), dmg, npcHitDir, NextSlot(playerIndex));
            }
            if (bestProjDamage > 0)
            {
                int dmg = Math.Max(1, (int)(bestProjDamage * DamageMultiplier));
                SendSlotHurt(player, PlayerDeathReason.ByProjectile(-1, bestProjIndex), dmg, projHitDir, NextSlot(playerIndex));
            }
        }
        else
        {
            if (bestNpcDamage >= bestProjDamage)
            {
                int dmg = Math.Max(1, (int)(bestNpcDamage * DamageMultiplier));
                SendSlotHurt(player, PlayerDeathReason.ByNPC(bestNpcIndex), dmg, npcHitDir, NextSlot(playerIndex));
            }
            else
            {
                int dmg = Math.Max(1, (int)(bestProjDamage * DamageMultiplier));
                SendSlotHurt(player, PlayerDeathReason.ByProjectile(-1, bestProjIndex), dmg, projHitDir, NextSlot(playerIndex));
            }
        }
    }

    private int NextSlot(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _slotRotation.Length)
            return 0;

        int idx = _slotRotation[playerIndex];
        _slotRotation[playerIndex] = (idx + 1) % CooldownSlots.Length;
        return CooldownSlots[idx];
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
