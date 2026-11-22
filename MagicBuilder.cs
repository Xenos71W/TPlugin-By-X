using System;
using System.Collections.Generic;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MagicBuilder
{
    /// <summary>
    /// TAHAP 1: pilih titik pakai Dirt Rod + penanda wire (hanya terlihat oleh pemain itu).
    /// Belum ada fitur bangun.
    ///
    /// Hasil dari log tahap 0: setiap klik Dirt Rod mengirim packet Tile dengan action 4
    /// (KillTileNoItem) di tile kursor, lalu projectile, lalu Tile action 1 (PlaceTile)
    /// di tile yang sama. Jadi action 4 dipakai sebagai "klik titik", dan action 1
    /// dibatalkan supaya dunia tidak berubah.
    /// </summary>
    [ApiVersion(2, 1)]
    public class MagicBuilderPlugin : TerrariaPlugin
    {
        public override string Name => "MagicBuilder";
        public override string Author => "You";
        public override string Description => "Pilih titik pakai Dirt Rod untuk fitur bangun otomatis.";
        public override Version Version => new Version(0, 1, 0, 0);

        private const string UsePermission = "magicbuilder.use";
        private const int DirtRodItemType = 114; // dikonfirmasi dari log ("held=114 Dirt Rod")
        private const int MaxPoints = 8;

        private class Session
        {
            public bool Selecting;
            public readonly List<(int X, int Y)> Points = new List<(int X, int Y)>();
        }

        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly object _lock = new object();

        public MagicBuilderPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            GetDataHandlers.TileEdit += OnTileEdit;
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            Commands.ChatCommands.Add(new Command(UsePermission, BuildCommand, "build"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetDataHandlers.TileEdit -= OnTileEdit;
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == BuildCommand);
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------
        // Command: /build select | points | clear
        // ---------------------------------------------------------------

        private void BuildCommand(CommandArgs args)
        {
            TSPlayer player = args.Player;
            if (!player.RealPlayer)
            {
                player.SendErrorMessage("Command ini hanya bisa dipakai di dalam game.");
                return;
            }

            string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";

            switch (sub)
            {
                case "select":
                {
                    bool now;
                    lock (_lock)
                    {
                        Session s = GetSession(player.Index);
                        s.Selecting = !s.Selecting;
                        now = s.Selecting;
                    }
                    if (now)
                        player.SendSuccessMessage("Mode pilih titik ON. Pegang Dirt Rod lalu klik tile untuk menandai titik (klik lagi di titik yang sama untuk menghapusnya).");
                    else
                        player.SendInfoMessage("Mode pilih titik OFF. Dirt Rod kembali normal.");
                    break;
                }

                case "points":
                {
                    List<(int X, int Y)> copy;
                    lock (_lock) { copy = new List<(int X, int Y)>(GetSession(player.Index).Points); }
                    if (copy.Count == 0)
                    {
                        player.SendInfoMessage("Belum ada titik.");
                        break;
                    }
                    for (int i = 0; i < copy.Count; i++)
                        player.SendInfoMessage($"Titik {i + 1}: ({copy[i].X}, {copy[i].Y})");
                    break;
                }

                case "clear":
                {
                    List<(int X, int Y)> old;
                    lock (_lock)
                    {
                        Session s = GetSession(player.Index);
                        old = new List<(int X, int Y)>(s.Points);
                        s.Points.Clear();
                    }
                    foreach (var p in old)
                        player.SendTileRect((short)p.X, (short)p.Y, 1, 1);
                    player.SendSuccessMessage($"{old.Count} titik dihapus.");
                    break;
                }

                default:
                    player.SendInfoMessage("Pemakaian: /build select | points | clear");
                    break;
            }
        }

        private Session GetSession(int index)
        {
            // dipanggil di dalam lock
            Session s;
            if (!_sessions.TryGetValue(index, out s))
            {
                s = new Session();
                _sessions[index] = s;
            }
            return s;
        }

        private void OnLeave(LeaveEventArgs args)
        {
            lock (_lock) { _sessions.Remove(args.Who); }
        }

        // ---------------------------------------------------------------
        // Tangkap klik Dirt Rod
        // ---------------------------------------------------------------

        private void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs args)
        {
            if (args.Handled)
                return;

            TSPlayer player = args.Player;
            if (player == null || !player.RealPlayer)
                return;

            bool selecting;
            lock (_lock)
            {
                Session existing;
                selecting = _sessions.TryGetValue(player.Index, out existing) && existing.Selecting;
            }
            if (!selecting || !IsHoldingDirtRod(player))
                return;

            int action = (int)args.Action;
            if (action != 4 && action != 1)
                return;

            int x = args.X;
            int y = args.Y;
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
                return;

            // Batalkan perubahan dunia, apapun aksinya
            args.Handled = true;

            if (action == 1)
            {
                // Aksi "place" setelah klik: cukup sinkronkan ulang tile aslinya
                Resync(player, x, y);
                return;
            }

            // action == 4: ini klik titik
            string message;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                int found = s.Points.FindIndex(p => p.X == x && p.Y == y);

                if (found >= 0)
                {
                    s.Points.RemoveAt(found);
                    message = $"Titik ({x}, {y}) dihapus.";
                }
                else if (s.Points.Count >= MaxPoints)
                {
                    message = $"Maksimal {MaxPoints} titik. Pakai /build clear untuk mengulang.";
                }
                else
                {
                    s.Points.Add((x, y));
                    message = $"Titik {s.Points.Count}: ({x}, {y})";
                }
            }

            Resync(player, x, y);
            player.SendInfoMessage(message);
        }

        private static bool IsHoldingDirtRod(TSPlayer player)
        {
            Player tp = player.TPlayer;
            int idx = tp.selectedItem;
            if (idx < 0 || idx >= tp.inventory.Length)
                return false;
            return tp.inventory[idx].type == DirtRodItemType;
        }

        // ---------------------------------------------------------------
        // Penanda wire palsu (hanya dikirim ke pemain ini, dunia tidak berubah)
        // ---------------------------------------------------------------

        private void Resync(TSPlayer player, int x, int y)
        {
            // kirim kondisi asli tile, lalu pasang penanda lagi kalau tile ini titik terpilih
            player.SendTileRect((short)x, (short)y, 1, 1);

            bool marked;
            lock (_lock)
            {
                Session s;
                marked = _sessions.TryGetValue(player.Index, out s)
                         && s.Points.Exists(p => p.X == x && p.Y == y);
            }
            if (marked)
                ShowMarker(player, x, y);
        }

        private static void ShowMarker(TSPlayer player, int x, int y)
        {
            try
            {
                var tile = Main.tile[x, y];
                bool had = tile.wire();

                tile.wire(true);
                player.SendTileRect((short)x, (short)y, 1, 1);
                tile.wire(had); // kembalikan di sisi server
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[MagicBuilder] Gagal pasang penanda: {ex.Message}");
            }
        }
    }
}
