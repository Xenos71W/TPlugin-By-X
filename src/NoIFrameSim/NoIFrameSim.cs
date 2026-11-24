using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace NoIFrameSim;

[ApiVersion(2, 1)]
public class NoIFrameSim : TerrariaPlugin
{
    public override string Name => "NoIFrameSim";
    public override Version Version => new Version(1, 3, 0);
    public override string Author => "Custom";
    public override string Description => "Memberikan efek DoT mengalir seperti Cursed Inferno & Suffocation saat menempel musuh";

    // === PENGATURAN INTENSITAS BOCOR DARAH ===
    // 0 = Otomatis menyesuaikan damage musuh (Early game ringan, Bos Hardmode brutal)
    // 1 = Ringan (~25 HP/detik)
    // 2 = Sedang (~65 HP/detik)
    // 3 = Brutal Maksimal (~115+ HP/detik, tembus Ankh Shield)
    private const int ForceTier = 0;

    // Durasi debuff dalam tick (60 tick = 1 detik). 35 tick = ~0.58 detik setelah lepas dari musuh langsung hilang
    private const int DebuffDurationTicks = 35;
    private const int CheckIntervalMs = 200;
    private const int MaxDurationMs = 15000;

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

        int tier = DetermineTier(args.Damage);
        ApplyFlowingDebuffs(player, tier);

        if (!_activeTracking.TryAdd(player.Index, true))
            return;

        _ = TrackContactDebuffAsync(player, tier);
    }

    private static int DetermineTier(int hitDamage)
    {
        if (ForceTier is >= 1 and <= 3)
            return ForceTier;

        if (hitDamage >= 100) return 3;
        if (hitDamage >= 45) return 2;
        return 1;
    }

    private async Task TrackContactDebuffAsync(TSPlayer player, int tier)
    {
        try
        {
            int maxIterations = MaxDurationMs / CheckIntervalMs;

            for (int i = 0; i < maxIterations; i++)
            {
                await Task.Delay(CheckIntervalMs);

                if (player == null || !player.Active || player.Dead || player.TPlayer == null)
                    break;

                if (!IsTouchingDanger(player.TPlayer))
                    break;

                ApplyFlowingDebuffs(player, tier);
            }
        }
        finally
        {
            _activeTracking.TryRemove(player.Index, out _);
        }
    }

    private static void ApplyFlowingDebuffs(TSPlayer player, int tier)
    {
        // Tier 1: Cursed Inferno + Electrified (darah mengalir halus)
        player.SetBuff(BuffID.CursedInferno, DebuffDurationTicks);
        player.SetBuff(BuffID.Electrified, DebuffDurationTicks);

        // Tier 2: Tambah Suffocation + Oiled (mempercepat DoT api secara drastis)
        if (tier >= 2)
        {
            player.SetBuff(BuffID.Suffocation, DebuffDurationTicks);
            player.SetBuff(BuffID.Oiled, DebuffDurationTicks);
        }

        // Tier 3: Tambah Daybreak + Acid Venom + Burning (bocor super deras)
        if (tier >= 3)
        {
            player.SetBuff(BuffID.Daybreak, DebuffDurationTicks);
            player.SetBuff(BuffID.Venom, DebuffDurationTicks);
            player.SetBuff(BuffID.Burning, DebuffDurationTicks);
        }
    }

    private static bool IsTouchingDanger(Player tPlayer)
    {
        for (int n = 0; n < Main.maxNPCs; n++)
        {
            NPC npc = Main.npc[n];
            if (npc != null && npc.active && !npc.friendly && npc.damage > 0 && !npc.dontTakeDamage)
            {
                if (tPlayer.Hitbox.Intersects(npc.Hitbox) || tPlayer.Distance(npc.Center) < 60f)
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
}
