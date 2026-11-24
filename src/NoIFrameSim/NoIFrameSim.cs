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
    public override Version Version => new Version(2, 1, 0);
    public override string Author => "Custom";
    public override string Description => "Menghilangkan i-frame pemain menggunakan rotasi 5 slot hurtCooldowns dengan damage asli musuh";

    // === PENGATURAN KECEPATAN & PENGALI DAMAGE ===
    // 140ms x 5 slot = 700ms (pas melewati 667ms cooldown per slot Terraria)
    private const int IntervalMs = 140;
    // 1.0 = 100% damage asli musuh/proyektil (ubah ke 1.5 atau 2.0 kalau mau lebih pedih)
    private const double DamageMultiplier = 1.0;

    private static readonly MethodInfo? SendPlayerHurtMethod =
        typeof(NetMessage).GetMethod("SendPlayerHurt", BindingFlags.Public | BindingFlags.Static);

    // Menyimpan jumlah tick berturut-turut pemain menyentuh bahaya & rotasi slot per pemain
    private readonly int[] _contactTicks = new int[Main.maxPlayers];
    private readonly int[] _slotRotation = new int[Main.maxPlayers];

    private CancellationTokenSource? _cts;

    public NoIFrameSim(Main game) : base(game) { }

    public override void Initialize()
    {
        _cts = new CancellationTokenSource();
        _ = MonitorRealDamageLoopAsync(_cts.Token);
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

    private async Task MonitorRealDamageLoopAsync(CancellationToken token)
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
                        if (i < _contactTicks.Length) _contactTicks[i] = 0;
                        continue;
                    }

                    if (TryGetTouchingThreat(player.TPlayer, out int rawDamage, out PlayerDeathReason reason, out int hitDir))
                    {
                        // Lewati tick ke-0 karena hit pertama sudah ditangani otomatis oleh game di HP pemain
                        if (_contactTicks[i] == 0)
                        {
                            _contactTicks[i] = 1;
                            continue;
                        }

                        _contactTicks[i]++;
                        int finalDamage = Math.Max(1, (int)(rawDamage * DamageMultiplier));
                        int slot = _slotRotation[i];
                        _slotRotation[i] = (slot + 1) % 5;

                        SendSlotHurt(player, reason, finalDamage, hitDir, slot);
                    }
                    else
                    {
                        _contactTicks[i] = 0;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch
            {
                // Abaikan error sesaat jika ada entity despawn di tengah pengecekan
            }
        }
    }

    private static bool TryGetTouchingThreat(Player tPlayer, out int highestDamage, out PlayerDeathReason reason, out int hitDirection)
    {
        highestDamage = 0;
        reason = PlayerDeathReason.LegacyDefault();
        hitDirection = 0;

        Rectangle playerBox = tPlayer.Hitbox;

        // 1. Cek sentuhan langsung dengan monster / bos
        for (int n = 0; n < Main.npc.Length; n++)
        {
            NPC npc = Main.npc[n];
            if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
            {
                if (playerBox.Intersects(npc.Hitbox) && npc.damage > highestDamage)
                {
                    highestDamage = npc.damage;
                    reason = PlayerDeathReason.ByNPC(n);
                    hitDirection = tPlayer.Center.X < npc.Center.X ? -1 : 1;
                }
            }
        }

        // 2. Cek sentuhan dengan proyektil musuh (laser, bola api, dll.)
        // Di Terraria, damage proyektil musuh di server dikali 2 (Normal), 4 (Expert), atau 6 (Master)
        int projDifficultyMult = Main.masterMode ? 6 : (Main.expertMode ? 4 : 2);

        for (int p = 0; p < Main.projectile.Length; p++)
        {
            Projectile proj = Main.projectile[p];
            if (proj != null && proj.active && proj.hostile && proj.damage > 0)
            {
                int scaledProjDamage = proj.damage * projDifficultyMult;
                if (playerBox.Intersects(proj.Hitbox) && scaledProjDamage > highestDamage)
                {
                    highestDamage = scaledProjDamage;
                    reason = PlayerDeathReason.ByProjectile(-1, p);
                    hitDirection = tPlayer.Center.X < proj.Center.X ? -1 : 1;
                }
            }
        }

        return highestDamage > 0;
    }

    private static void SendSlotHurt(TSPlayer player, PlayerDeathReason reason, int damage, int hitDirection, int cooldownSlot)
    {
        if (SendPlayerHurtMethod != null)
        {
            var parameters = SendPlayerHurtMethod.GetParameters();
            object?[] invokeArgs = new object?[parameters.Length];

            // Urutan parameter resmi Terraria NetMessage.SendPlayerHurt:
            // [0] playerTargetIndex, [1] reason, [2] damage, [3] direction,
            // [4] critical, [5] pvp, [6] cooldownCounter (0..4), [7] remoteClient (-1), [8] ignoreClient (-1)
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
