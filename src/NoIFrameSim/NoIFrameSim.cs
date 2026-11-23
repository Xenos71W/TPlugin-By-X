using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;

namespace NoIFrameSim;

[ApiVersion(2, 1)]
public class NoIFrameSim : TerrariaPlugin
{
    public override string Name => "NoIFrameSim";
    public override Version Version => new Version(1, 2, 0);
    public override string Author => "Custom";
    public override string Description => "Bypass i-frame pemain menggunakan rotasi slot hurtCooldowns";

    // === PENGATURAN KECEPATAN & DAMAGE ===
    // 150ms = ~6.7 hit per detik (pas buat rotasi 5 slot cooldown Terraria)
    private const int IntervalMs = 150;
    // 1.0 = 100% full damage musuh di setiap tick (naikkan ke 1.5 atau 2.0 kalau mau lebih sadis)
    private const double DamageMultiplier = 1.0;
    private const int MaxDurationMs = 9000;

    private static readonly MethodInfo? SendPlayerHurtMethod =
        typeof(NetMessage).GetMethod("SendPlayerHurt", BindingFlags.Public | BindingFlags.Static);

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

        int tickDamage = Math.Max(10, (int)(args.Damage * DamageMultiplier));
        _ = TrackBrutalDamageAsync(player, tickDamage);
    }

    private async Task TrackBrutalDamageAsync(TSPlayer player, int tickDamage)
    {
        try
        {
            int maxIterations = MaxDurationMs / IntervalMs;
            int cooldownSlot = 0;

            for (int i = 0; i < maxIterations; i++)
            {
                await Task.Delay(IntervalMs);

                if (player == null || !player.Active || player.Dead || player.TPlayer == null)
                    break;

                if (!IsTouchingDanger(player.TPlayer))
                    break;

                ApplyRealHit(player, tickDamage, cooldownSlot);
                cooldownSlot = (cooldownSlot + 1) % 5;
            }
        }
        finally
        {
            _activeTracking.TryRemove(player.Index, out _);
        }
    }

    private static bool IsTouchingDanger(Player tPlayer)
    {
        for (int n = 0; n < Main.maxNPCs; n++)
        {
            NPC npc = Main.npc[n];
            if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
            {
                if (tPlayer.Hitbox.Intersects(npc.Hitbox) || tPlayer.Distance(npc.Center) < 64f)
                    return true;
            }
        }

        for (int p = 0; p < Main.maxProjectiles; p++)
        {
            Projectile proj = Main.projectile[p];
            if (proj != null && proj.active && proj.hostile && proj.damage > 0)
            {
                if (tPlayer.Hitbox.Intersects(proj.Hitbox))
                    return true;
            }
        }

        return false;
    }

    private static void ApplyRealHit(TSPlayer player, int damage, int cooldownSlot)
    {
        if (SendPlayerHurtMethod != null)
        {
            var parameters = SendPlayerHurtMethod.GetParameters();
            object?[] invokeArgs = new object?[parameters.Length];

            if (parameters.Length > 0) invokeArgs[0] = player.Index;
            if (parameters.Length > 1) invokeArgs[1] = PlayerDeathReason.LegacyDefault();
            if (parameters.Length > 2) invokeArgs[2] = damage;
            if (parameters.Length > 3) invokeArgs[3] = 0;
            if (parameters.Length > 4) invokeArgs[4] = false;
            if (parameters.Length > 5) invokeArgs[5] = false;
            if (parameters.Length > 6) invokeArgs[6] = 0;
            if (parameters.Length > 7) invokeArgs[7] = cooldownSlot;

            for (int p = 8; p < parameters.Length; p++)
            {
                invokeArgs[p] = parameters[p].HasDefaultValue ? parameters[p].DefaultValue : 0;
            }

            SendPlayerHurtMethod.Invoke(null, invokeArgs);
            return;
        }

        player.DamagePlayer(damage);
    }
}
