using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace BossFocus
{
    [ApiVersion(2, 1)]
    public class BossFocusPlugin : TerrariaPlugin
    {
        public override string Name => "BossFocus";
        public override string Author => "You";
        public override string Description =>
            "Saat boss aktif: monster biasa di sekitar boss tidak spawn & tidak ada tombstone saat player mati.";
        public override Version Version => new Version(1, 0, 0, 0);

        private string _configPath;
        private BossFocusConfig _config = new BossFocusConfig();

        // ID projectile tombstone (Tombstone, Gravestone, dst, + RichGravestone1-5)
        private static readonly HashSet<short> TombstoneProjectiles = new HashSet<short>
        {
            43, 201, 202, 203, 204, 205, 527, 528, 529, 530, 531
        };

        // NPC yang selalu boleh spawn (bagian tubuh boss yang bersegmen)
        private static readonly HashSet<int> AlwaysAllowedNpcs = new HashSet<int>
        {
            13, 14, 15,       // Eater of Worlds (head, body, tail)
            135, 136,         // The Destroyer (body, tail)
            114,              // Wall of Flesh eye
            396, 397, 398     // Moon Lord (head, hand, core)
        };

        public BossFocusPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            _configPath = Path.Combine(TShock.SavePath, "BossFocus.json");
            LoadConfig();

            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
            GetDataHandlers.NewProjectile += OnNewProjectile;
            GeneralHooks.ReloadEvent += OnReload;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                GetDataHandlers.NewProjectile -= OnNewProjectile;
                GeneralHooks.ReloadEvent -= OnReload;
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------
        // Config
        // ---------------------------------------------------------------

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = JsonConvert.DeserializeObject<BossFocusConfig>(File.ReadAllText(_configPath));
                    _config = loaded ?? new BossFocusConfig();
                }
                else
                {
                    _config = new BossFocusConfig();
                }

                // Tulis ulang supaya field baru ikut muncul di file
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[BossFocus] Gagal load config, pakai default: {ex.Message}");
                _config = new BossFocusConfig();
            }
        }

        private void OnReload(ReloadEventArgs e)
        {
            LoadConfig();
            e.Player?.SendSuccessMessage("[BossFocus] Config di-reload.");
        }

        // ---------------------------------------------------------------
        // Blokir spawn monster di area boss
        // ---------------------------------------------------------------

        private void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            if (!_config.Enabled || !_config.BlockMonsterSpawns || args.Handled)
                return;

            int id = args.NpcId;
            if (id < 0 || id >= Main.maxNPCs)
                return;

            NPC npc = Main.npc[id];
            if (npc == null || IsExempt(npc))
                return;

            float areaPx = _config.BossAreaRadiusTiles * 16f;
            float minionPx = _config.BossMinionRadiusTiles * 16f;
            float areaSq = areaPx * areaPx;
            float minionSq = minionPx * minionPx;

            bool inBossArea = false;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (i == id)
                    continue;

                NPC boss = Main.npc[i];
                if (boss == null || !boss.active || !boss.boss)
                    continue;

                float dx = boss.Center.X - npc.Center.X;
                float dy = boss.Center.Y - npc.Center.Y;
                float distSq = dx * dx + dy * dy;

                // Sangat dekat boss = anak buah boss sendiri, jangan diblokir
                if (distSq <= minionSq)
                    return;

                if (distSq <= areaSq)
                    inBossArea = true;
            }

            if (inBossArea)
                args.Handled = true;
        }

        private static bool IsExempt(NPC npc)
        {
            return npc.boss
                || npc.friendly
                || npc.townNPC
                || npc.CountsAsACritter
                || AlwaysAllowedNpcs.Contains(npc.type);
        }

        // ---------------------------------------------------------------
        // Blokir tombstone saat mati di dekat boss
        // ---------------------------------------------------------------

        private void OnNewProjectile(object sender, GetDataHandlers.NewProjectileEventArgs args)
        {
            if (!_config.Enabled || !_config.BlockTombstones || args.Handled)
                return;

            if (!TombstoneProjectiles.Contains(args.Type))
                return;

            if (!IsBossNear(args.Position.X, args.Position.Y, _config.BossAreaRadiusTiles))
                return;

            args.Player.RemoveProjectile(args.Identity, args.Owner);
            args.Handled = true;
        }

        private static bool IsBossNear(float x, float y, int radiusTiles)
        {
            float radiusPx = radiusTiles * 16f;
            float radiusSq = radiusPx * radiusPx;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC boss = Main.npc[i];
                if (boss == null || !boss.active || !boss.boss)
                    continue;

                float dx = boss.Center.X - x;
                float dy = boss.Center.Y - y;
                if (dx * dx + dy * dy <= radiusSq)
                    return true;
            }
            return false;
        }
    }

    public class BossFocusConfig
    {
        public bool Enabled { get; set; } = true;
        public bool BlockMonsterSpawns { get; set; } = true;
        public bool BlockTombstones { get; set; } = true;

        /// <summary>Radius (dalam tile) dari boss yang dianggap "area boss fight".</summary>
        public int BossAreaRadiusTiles { get; set; } = 100;

        /// <summary>Spawn dalam radius ini dari boss dianggap minion boss, jadi tetap diizinkan.</summary>
        public int BossMinionRadiusTiles { get; set; } = 12;
    }
}
