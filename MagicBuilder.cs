using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace MagicBuilder
{
    /// <summary>
    /// TAHAP 3: pilih titik pakai Ice Rod + /build hellevator + /build npchouse.
    ///
    /// Titik: tile target Ice Rod dibaca dari ai[0]/ai[1] packet projectile (msg 27, tipe 80).
    /// Hellevator: dari titik 1 turun sampai lapisan underworld.
    ///   - lubang 3 tile (x-1 .. x+1), dindingnya Gray Brick Wall
    ///   - 1 tile gray brick di tiap sisi (x-2 dan x+2)
    ///   - obor bergantian kiri/kanan tiap beberapa tile
    ///   - tali (Rope) di kolom tengah dari atas sampai bawah, digantung dari 1 gray brick
    ///     di atas titik awal (tali harus punya penahan di atasnya)
    /// NPC house: tiap titik = 1 rumah (titik = tile lantai di tengah). Gaya otomatis dari biome di
    ///   sekitar titik (atau dipaksa: /build npchouse <biome>). Interior 11x7, 2 pintu, meja, kursi,
    ///   rak buku, lukisan, obor, platform. Valid untuk housing NPC.
    /// Dibangun bertahap per tick (GameUpdate) supaya server tidak lag.
    /// BELUM ADA UNDO. Backup dunia sebelum dipakai.
    /// </summary>
    [ApiVersion(2, 1)]
    public class MagicBuilderPlugin : TerrariaPlugin
    {
        public override string Name => "MagicBuilder";
        public override string Author => "You";
        public override string Description => "Pilih titik pakai Ice Rod dan bangun struktur otomatis.";
        public override Version Version => new Version(0, 6, 0, 0);

        private const string UsePermission = "magicbuilder.use";
        private const string BuildPermission = "magicbuilder.build";
        private const string DebugPermission = "magicbuilder.debug";

        // --- Pemilih titik ---
        private const int IceRodItemType = 496;      // Ice Rod
        private const int IceBlockProjectile = 80;   // projectile Ice Rod
        private const int IceBlockTileType = 127;    // tile yang dibuat Ice Rod
        private const ushort MarkerTileType = 8;     // Gold Ore, blok penanda palsu
        private const int MaxPoints = 8;

        // --- Hellevator ---
        private const ushort BrickTileType = 38;     // Gray Brick
        private const ushort WallType = 5;           // Gray Brick Wall (wall aman, tidak memunculkan musuh)
        private const int TorchTileType = 4;         // Torch
        private const int TorchStyle = 0;            // Torch biasa
        private const int RopeTileType = 213;        // Rope
        private const int TorchInterval = 6;         // jarak antar obor (tile)
        private const int RowsPerTick = 8;           // baris yang dikerjakan per tick
        private const int ConfirmSeconds = 60;

        // --- Rumah NPC ---
        private const int HouseHalfWidth = 6;        // interior 11 tile (cx-5..cx+5), tembok di cx-6 dan cx+6
        private const int HouseHeight = 8;           // baris di atas lantai: atap di fy-8, interior fy-7..fy-1
        private const int FoundationMax = 12;        // maksimal fondasi ke bawah kalau lantai menggantung

        // Packet yang direkam oleh /build debug: Tile, ProjectileNew, ProjectileDestroy
        private static readonly HashSet<int> DebugPackets = new HashSet<int> { 17, 27, 29 };

        private class Session
        {
            public bool Selecting;
            public readonly List<(int X, int Y)> Points = new List<(int X, int Y)>();

            public bool HasPending;
            public string PendingKind;               // "hellevator" atau "house"
            public List<HouseSpec> PendingHouses;
            public int PendingX;
            public int PendingStartY;
            public int PendingEndY;
            public DateTime PendingExpires;
        }

        private class HouseStyle
        {
            public string Name;
            public int Block, Wall, Door, Table, Chair, Bookcase, Torch, Painting;

            public HouseStyle(string name, int block, int wall, int door, int table, int chair, int bookcase, int torch, int painting)
            {
                Name = name; Block = block; Wall = wall; Door = door; Table = table;
                Chair = chair; Bookcase = bookcase; Torch = torch; Painting = painting;
            }
        }

        private class HouseSpec
        {
            public int X;
            public int Y;
            public HouseStyle Style;
        }

        // Style furniture diambil dari daftar Tile IDs wiki Terraria. Yang belum terverifikasi memakai style 0 (kayu biasa):
        // kursi Boreal/Obsidian, rak buku Rich Mahogany/Ebonwood/Pearlwood, dan semua platform.
        private static readonly Dictionary<string, HouseStyle> HouseStyles = new Dictionary<string, HouseStyle>
        {
            //                                  nama         blok                   dinding                 pintu table chair rak torch lukisan
            ["forest"]     = new HouseStyle("Forest",     TileID.WoodBlock,      WallID.Wood,            0,    0,   0,   0,  0,   0),
            ["snow"]       = new HouseStyle("Snow",       TileID.BorealWood,     WallID.SnowBrick,       30,   28,  0,   24, 9,   1),
            ["desert"]     = new HouseStyle("Desert",     TileID.SandstoneBrick, WallID.SandstoneBrick,  4,    30,  6,   6,  16,  2),
            ["jungle"]     = new HouseStyle("Jungle",     TileID.RichMahogany,   WallID.RichMaogany,     2,    2,   3,   0,  21,  3),
            ["corruption"] = new HouseStyle("Corruption", TileID.Ebonwood,       WallID.Ebonwood,        1,    1,   2,   0,  18,  4),
            ["crimson"]    = new HouseStyle("Crimson",    TileID.Shadewood,      WallID.RedBrick,        10,   8,   11,  19, 19,  5),
            ["hallow"]     = new HouseStyle("Hallow",     TileID.Pearlwood,      WallID.Pearlwood,       3,    3,   4,   0,  20,  6),
            ["underworld"] = new HouseStyle("Underworld", TileID.ObsidianBrick,  WallID.ObsidianBrick,   19,   13,  0,   4,  7,   7),
            ["cavern"]     = new HouseStyle("Cavern",     TileID.GrayBrick,      WallID.GrayBrick,       0,    0,   0,   0,  0,   8),
        };

        private class BuildJob
        {
            public bool IsHouse;
            public HouseStyle Style;
            public int ObjectFailed;
            public int PlayerIndex;
            public string PlayerName;
            public int X;
            public int StartY;
            public int EndY;
            public int NextY;
            public int Blocked;
            public int TorchFailed;
            public int RopeFailed;
            public bool AnchorDone;
            public bool Cancelled;
        }

        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly Queue<BuildJob> _jobs = new Queue<BuildJob>();
        private readonly HashSet<int> _debug = new HashSet<int>();
        private readonly object _lock = new object();
        private string _logPath;

        public MagicBuilderPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            _logPath = Path.Combine(TShock.SavePath, "MagicBuilderDebug.log");

            GetDataHandlers.TileEdit += OnTileEdit;
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

            Commands.ChatCommands.Add(new Command(UsePermission, BuildCommand, "build"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetDataHandlers.TileEdit -= OnTileEdit;
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == BuildCommand);
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------
        // Command
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
                        player.SendSuccessMessage("Mode pilih titik ON. Pegang Ice Rod lalu klik tile untuk menandai titik.");
                    else
                        player.SendInfoMessage("Mode pilih titik OFF. Ice Rod kembali normal.");
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

                case "remove":
                {
                    int n;
                    if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out n))
                    {
                        player.SendErrorMessage("Pemakaian: /build remove <nomor titik>");
                        break;
                    }

                    (int X, int Y) removed = (0, 0);
                    bool ok = false;
                    lock (_lock)
                    {
                        Session s = GetSession(player.Index);
                        if (n >= 1 && n <= s.Points.Count)
                        {
                            removed = s.Points[n - 1];
                            s.Points.RemoveAt(n - 1);
                            ok = true;
                        }
                    }

                    if (!ok)
                    {
                        player.SendErrorMessage("Nomor titik tidak ada. Lihat /build points");
                        break;
                    }
                    player.SendTileRect((short)removed.X, (short)removed.Y, 1, 1);
                    player.SendSuccessMessage($"Titik {n} ({removed.X}, {removed.Y}) dihapus.");
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

                case "test":
                {
                    string msg = AddPoint(player, player.TileX + 2, player.TileY);
                    player.SendInfoMessage(msg ?? "Titik itu sudah ada.");
                    break;
                }

                case "hellevator":
                    PrepareHellevator(player);
                    break;

                case "npchouse":
                    PrepareNpcHouses(player, args.Parameters.Count > 1 ? args.Parameters[1].ToLowerInvariant() : null);
                    break;

                case "confirm":
                    ConfirmBuild(player);
                    break;

                case "cancel":
                    CancelBuild(player);
                    break;

                case "debug":
                {
                    if (!player.HasPermission(DebugPermission))
                    {
                        player.SendErrorMessage("Kamu tidak punya permission " + DebugPermission);
                        break;
                    }
                    bool on;
                    lock (_lock)
                    {
                        on = !_debug.Remove(player.Index);
                        if (on) _debug.Add(player.Index);
                    }
                    Write(on ? $"===== LOG START: {player.Name} =====" : $"===== LOG END: {player.Name} =====");
                    player.SendSuccessMessage(on
                        ? "Debug ON. Pegang Ice Rod, klik beberapa titik, lalu /build debug lagi untuk mematikan."
                        : "Debug OFF. Log ada di: " + _logPath);
                    break;
                }

                default:
                    player.SendInfoMessage("Pemakaian: /build select | points | remove <n> | clear | hellevator | npchouse [biome] | confirm | cancel");
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
            lock (_lock)
            {
                _sessions.Remove(args.Who);
                _debug.Remove(args.Who);
            }
        }

        // ---------------------------------------------------------------
        // Hellevator: persiapan, konfirmasi, eksekusi bertahap
        // ---------------------------------------------------------------

        private void PrepareHellevator(TSPlayer player)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            (int X, int Y) point;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                if (s.Points.Count == 0)
                {
                    player.SendErrorMessage("Belum ada titik. Pakai /build select lalu klik titik awal dengan Ice Rod.");
                    return;
                }
                point = s.Points[0];

                foreach (BuildJob j in _jobs)
                {
                    if (j.PlayerIndex == player.Index && !j.Cancelled)
                    {
                        player.SendErrorMessage("Kamu masih punya pembangunan yang berjalan. Tunggu selesai atau /build cancel.");
                        return;
                    }
                }
            }

            int cx = point.X;
            int sy = point.Y;
            int ey = Main.UnderworldLayer;

            if (cx - 2 < 0 || cx + 2 >= Main.maxTilesX || sy < 1)
            {
                player.SendErrorMessage("Titik terlalu dekat tepi dunia.");
                return;
            }
            if (sy >= ey || ey >= Main.maxTilesY - 5)
            {
                player.SendErrorMessage($"Titik harus berada di atas underworld (y < {ey}). Titik kamu: y = {sy}.");
                return;
            }

            // Cek izin build (region/spawn protection) di seluruh area
            for (int y = sy; y <= ey; y++)
            {
                for (int x = cx - 2; x <= cx + 2; x++)
                {
                    if (!player.HasBuildPermission(x, y, false))
                    {
                        player.SendErrorMessage($"Kamu tidak boleh membangun di ({x}, {y}) (region/spawn protection). Pembangunan dibatalkan.");
                        return;
                    }
                }
            }

            // penahan tali (1 tile di atas titik awal)
            if (!player.HasBuildPermission(cx, sy - 1, false))
            {
                player.SendErrorMessage($"Kamu tidak boleh membangun di ({cx}, {sy - 1}) (region/spawn protection). Pembangunan dibatalkan.");
                return;
            }

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "hellevator";
                s.PendingX = cx;
                s.PendingStartY = sy;
                s.PendingEndY = ey;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
            }

            player.SendInfoMessage($"Hellevator: dari ({cx}, {sy}) turun sampai y = {ey} ({ey - sy + 1} tile), lebar total 5 (lubang 3 + brick 1 di tiap sisi), dengan tali di tengah.");
            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        private void ConfirmBuild(TSPlayer player)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            List<(int X, int Y)> oldPoints = null;
            int queued = 0;
            string kind = null;

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                if (!s.HasPending || DateTime.UtcNow > s.PendingExpires)
                {
                    s.HasPending = false;
                    player.SendErrorMessage("Tidak ada pembangunan yang menunggu konfirmasi. Mulai lagi dengan /build hellevator atau /build npchouse.");
                    return;
                }

                kind = s.PendingKind;

                if (kind == "house" && s.PendingHouses != null)
                {
                    foreach (HouseSpec h in s.PendingHouses)
                    {
                        _jobs.Enqueue(new BuildJob
                        {
                            PlayerIndex = player.Index,
                            PlayerName = player.Name,
                            IsHouse = true,
                            Style = h.Style,
                            X = h.X,
                            StartY = h.Y,
                            EndY = h.Y,
                            NextY = h.Y
                        });
                        queued++;
                    }
                }
                else
                {
                    _jobs.Enqueue(new BuildJob
                    {
                        PlayerIndex = player.Index,
                        PlayerName = player.Name,
                        X = s.PendingX,
                        StartY = s.PendingStartY,
                        EndY = s.PendingEndY,
                        NextY = s.PendingStartY
                    });
                    queued = 1;
                }

                s.HasPending = false;
                s.PendingHouses = null;
                oldPoints = new List<(int X, int Y)>(s.Points);
                s.Points.Clear();
            }

            foreach (var p in oldPoints)
                player.SendTileRect((short)p.X, (short)p.Y, 1, 1);

            if (kind == "house")
                player.SendSuccessMessage($"Membangun {queued} rumah NPC. Pakai /build cancel untuk menghentikan sisanya.");
            else
                player.SendSuccessMessage("Pembangunan hellevator dimulai. Pakai /build cancel untuk menghentikan.");
        }

        private void CancelBuild(TSPlayer player)
        {
            int count = 0;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                if (s.HasPending)
                {
                    s.HasPending = false;
                    count++;
                }
                foreach (BuildJob j in _jobs)
                {
                    if (j.PlayerIndex == player.Index && !j.Cancelled)
                    {
                        j.Cancelled = true;
                        count++;
                    }
                }
            }

            if (count == 0)
                player.SendInfoMessage("Tidak ada pembangunan untuk dibatalkan.");
            else
                player.SendSuccessMessage("Dibatalkan. Bagian yang sudah dibangun tidak dikembalikan.");
        }

        private void OnGameUpdate(EventArgs args)
        {
            BuildJob job;
            lock (_lock)
            {
                if (_jobs.Count == 0)
                    return;
                job = _jobs.Peek();
            }

            bool finished = false;
            string error = null;

            try
            {
                if (job.Cancelled)
                {
                    finished = true;
                }
                else if (job.IsHouse)
                {
                    BuildNpcHouse(job);
                    finished = true;
                }
                else
                {
                    if (!job.AnchorDone)
                        PlaceRopeAnchor(job);

                    int y0 = job.NextY;
                    int rows = 0;

                    while (rows < RowsPerTick && job.NextY <= job.EndY)
                    {
                        BuildRow(job, job.NextY);
                        job.NextY++;
                        rows++;
                    }

                    if (rows > 0)
                        TSPlayer.All.SendTileRect((short)(job.X - 2), (short)y0, 5, (byte)rows);

                    if (job.NextY > job.EndY)
                        finished = true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                finished = true;
                TShock.Log.ConsoleError($"[MagicBuilder] Error saat membangun: {ex}");
            }

            if (!finished)
                return;

            lock (_lock) { _jobs.Dequeue(); }

            TSPlayer owner = TShock.Players[job.PlayerIndex];
            bool ownerHere = owner != null && owner.Active && owner.Name == job.PlayerName;

            if (ownerHere)
            {
                if (error != null)
                    owner.SendErrorMessage("Pembangunan berhenti karena error: " + error);
                else if (job.Cancelled)
                    owner.SendInfoMessage("Pembangunan dihentikan.");
                else
                {
                    owner.SendSuccessMessage(job.IsHouse ? $"Rumah NPC ({job.Style.Name}) di ({job.X}, {job.StartY}) selesai." : "Hellevator selesai.");
                    if (job.Blocked > 0)
                        owner.SendWarningMessage($"{job.Blocked} tile tidak bisa dihancurkan (misalnya peti berisi), jadi ada celah.");
                    if (job.TorchFailed > 0)
                        owner.SendWarningMessage($"{job.TorchFailed} obor gagal dipasang.");
                    if (job.RopeFailed > 0)
                        owner.SendWarningMessage($"{job.RopeFailed} tile tali gagal dipasang.");
                    if (job.ObjectFailed > 0)
                        owner.SendWarningMessage($"{job.ObjectFailed} furnitur/pintu/obor gagal dipasang.");
                }
            }

            TShock.Log.ConsoleInfo($"[MagicBuilder] {(job.IsHouse ? "Rumah" : "Hellevator")} {job.PlayerName} x={job.X} y={job.StartY}..{job.EndY} selesai (blocked={job.Blocked}, torchFailed={job.TorchFailed}, ropeFailed={job.RopeFailed}, cancelled={job.Cancelled}, error={error ?? "-"})");
        }

        /// <summary>Kerjakan satu baris hellevator: brick kiri/kanan, gali lubang, pasang dinding, obor.</summary>
        private void BuildRow(BuildJob job, int y)
        {
            int cx = job.X;

            // 1) kolom brick di kedua sisi
            PlaceBrick(job, cx - 2, y);
            PlaceBrick(job, cx + 2, y);

            // 2) lubang 3 tile: gali + dinding
            for (int x = cx - 1; x <= cx + 1; x++)
                DigAndWall(job, x, y);

            // 3) obor bergantian kiri/kanan
            int offset = y - job.StartY;
            if (offset % TorchInterval == 0)
            {
                int k = offset / TorchInterval;
                int tx = (k % 2 == 0) ? cx - 1 : cx + 1;

                if (WorldGen.PlaceTile(tx, y, TorchTileType, true, true, -1, TorchStyle))
                    WorldGen.SquareTileFrame(tx, y, true);
                else
                    job.TorchFailed++;
            }

            // 4) tali di kolom tengah (menggantung dari tile di atasnya)
            if (WorldGen.PlaceTile(cx, y, RopeTileType, true, true, -1, 0))
                WorldGen.SquareTileFrame(cx, y, true);
            else
                job.RopeFailed++;
        }

        /// <summary>Pasang 1 gray brick di atas titik awal sebagai penahan tali (jika belum ada tile padat di situ).</summary>
        private void PlaceRopeAnchor(BuildJob job)
        {
            job.AnchorDone = true;

            int x = job.X;
            int y = job.StartY - 1;
            var tile = Main.tile[x, y];

            if (tile.active() && Main.tileSolid[tile.type])
                return; // sudah ada tile padat sebagai penahan

            if (tile.active())
            {
                WorldGen.KillTile(x, y, false, false, true);
                tile = Main.tile[x, y];
                if (tile.active())
                {
                    job.RopeFailed++;
                    return;
                }
            }

            tile.liquid = 0;
            tile.type = BrickTileType;
            tile.active(true);
            tile.halfBrick(false);
            tile.slope(0);
            WorldGen.SquareTileFrame(x, y, true);
            TSPlayer.All.SendTileRect((short)x, (short)y, 1, 1);
        }

        private void PlaceBrick(BuildJob job, int x, int y)
        {
            var tile = Main.tile[x, y];

            if (tile.active() && tile.type == BrickTileType)
                return;

            if (tile.active())
            {
                WorldGen.KillTile(x, y, false, false, true);
                tile = Main.tile[x, y];
                if (tile.active())
                {
                    job.Blocked++;
                    return;
                }
            }

            tile.liquid = 0;
            tile.type = BrickTileType;
            tile.active(true);
            tile.halfBrick(false);
            tile.slope(0);
            WorldGen.SquareTileFrame(x, y, true);
        }

        private void DigAndWall(BuildJob job, int x, int y)
        {
            var tile = Main.tile[x, y];

            if (tile.active())
            {
                WorldGen.KillTile(x, y, false, false, true);
                tile = Main.tile[x, y];
                if (tile.active())
                {
                    job.Blocked++;
                    return;
                }
            }

            tile.liquid = 0;
            tile.wall = WallType;
            WorldGen.SquareWallFrame(x, y, true);
        }

        // ---------------------------------------------------------------
        // Rumah NPC
        // ---------------------------------------------------------------

        private void PrepareNpcHouses(TSPlayer player, string forcedBiome)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            HouseStyle forced = null;
            if (forcedBiome != null && !HouseStyles.TryGetValue(forcedBiome, out forced))
            {
                player.SendErrorMessage("Biome tidak dikenal. Pilihan: " + string.Join(", ", HouseStyles.Keys));
                return;
            }

            List<(int X, int Y)> points;
            lock (_lock)
            {
                points = new List<(int X, int Y)>(GetSession(player.Index).Points);

                foreach (BuildJob j in _jobs)
                {
                    if (j.PlayerIndex == player.Index && !j.Cancelled)
                    {
                        player.SendErrorMessage("Kamu masih punya pembangunan yang berjalan. Tunggu selesai atau /build cancel.");
                        return;
                    }
                }
            }

            if (points.Count == 0)
            {
                player.SendErrorMessage("Belum ada titik. Pakai /build select lalu klik tile lantai (permukaan tanah) tempat rumah berdiri.");
                return;
            }

            var specs = new List<HouseSpec>();

            for (int i = 0; i < points.Count; i++)
            {
                int cx = points[i].X;
                int fy = points[i].Y;

                if (cx - HouseHalfWidth < 0 || cx + HouseHalfWidth >= Main.maxTilesX
                    || fy - HouseHeight < 1 || fy + FoundationMax >= Main.maxTilesY - 5)
                {
                    player.SendErrorMessage($"Titik {i + 1} ({cx}, {fy}) terlalu dekat tepi dunia.");
                    return;
                }

                for (int x = cx - HouseHalfWidth; x <= cx + HouseHalfWidth; x++)
                {
                    for (int y = fy - HouseHeight; y <= fy + FoundationMax; y++)
                    {
                        if (!player.HasBuildPermission(x, y, false))
                        {
                            player.SendErrorMessage($"Titik {i + 1}: kamu tidak boleh membangun di ({x}, {y}) (region/spawn protection). Dibatalkan.");
                            return;
                        }
                    }
                }

                foreach (HouseSpec other in specs)
                {
                    if (Math.Abs(other.X - cx) <= HouseHalfWidth * 2 && Math.Abs(other.Y - fy) <= HouseHeight + FoundationMax)
                    {
                        player.SendErrorMessage($"Titik {i + 1} terlalu dekat dengan rumah lain (minimal {HouseHalfWidth * 2 + 1} tile antar titik). Dibatalkan.");
                        return;
                    }
                }

                specs.Add(new HouseSpec { X = cx, Y = fy, Style = forced ?? DetectHouseStyle(cx, fy) });
            }

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "house";
                s.PendingHouses = specs;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
            }

            for (int i = 0; i < specs.Count; i++)
                player.SendInfoMessage($"Rumah {i + 1}: lantai di ({specs[i].X}, {specs[i].Y}), gaya {specs[i].Style.Name}.");

            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        /// <summary>Tentukan gaya rumah dari tile-tile biome di sekitar titik.</summary>
        private static HouseStyle DetectHouseStyle(int cx, int cy)
        {
            if (cy >= Main.UnderworldLayer)
                return HouseStyles["underworld"];

            int corruption = 0, crimson = 0, hallow = 0, snow = 0, desert = 0, jungle = 0;

            int x0 = Math.Max(0, cx - 20), x1 = Math.Min(Main.maxTilesX - 1, cx + 20);
            int y0 = Math.Max(0, cy - 12), y1 = Math.Min(Main.maxTilesY - 1, cy + 12);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    var t = Main.tile[x, y];
                    if (!t.active())
                        continue;

                    switch ((int)t.type)
                    {
                        case 23: case 25: case 112: case 163:   // rumput corrupt, ebonstone, ebonsand, es ungu
                            corruption++; break;
                        case 199: case 203: case 234: case 200: // rumput crimson, crimstone, crimsand, es merah
                            crimson++; break;
                        case 109: case 117: case 116: case 164: // rumput hallow, pearlstone, pearlsand, es pink
                            hallow++; break;
                        case 147: case 161:                     // salju, es
                            snow++; break;
                        case 53: case 397: case 396:            // pasir, hardened sand, sandstone
                            desert++; break;
                        case 60:                                // rumput jungle
                            jungle++; break;
                    }
                }
            }

            const int Threshold = 10;
            string best = null;
            int bestCount = Threshold - 1;

            void Consider(string key, int count)
            {
                if (count > bestCount)
                {
                    best = key;
                    bestCount = count;
                }
            }

            Consider("corruption", corruption);
            Consider("crimson", crimson);
            Consider("hallow", hallow);
            Consider("snow", snow);
            Consider("desert", desert);
            Consider("jungle", jungle);

            if (best != null)
                return HouseStyles[best];

            return cy > Main.worldSurface ? HouseStyles["cavern"] : HouseStyles["forest"];
        }

        /// <summary>
        /// Bangun satu rumah. Koordinat: X = tengah, StartY = baris lantai.
        /// Atap di y-8, interior y-7..y-1 (11 lebar), tembok di x-6 dan x+6.
        /// </summary>
        private void BuildNpcHouse(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - HouseHalfWidth;
            int right = cx + HouseHalfWidth;
            int top = fy - HouseHeight;
            ushort block = (ushort)st.Block;

            // 1) fondasi: isi celah di bawah lantai sampai ketemu tile padat
            int deepest = fy;
            for (int x = left; x <= right; x++)
            {
                for (int y = fy + 1; y <= fy + FoundationMax; y++)
                {
                    var t = Main.tile[x, y];
                    if (t.active() && Main.tileSolid[t.type])
                        break;

                    PlaceBlock(job, x, y, block);
                    if (y > deepest)
                        deepest = y;
                }
            }

            // 2) bersihkan seluruh kotak rumah + pasang dinding latar
            for (int y = top; y <= fy; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    ClearTile(job, x, y);
                    var t = Main.tile[x, y];
                    t.wall = (ushort)st.Wall;
                    WorldGen.SquareWallFrame(x, y, true);
                }
            }

            // 3) kerangka: atap, lantai, tembok kiri dan kanan
            for (int x = left; x <= right; x++)
            {
                PlaceBlock(job, x, top, block);
                PlaceBlock(job, x, fy, block);
            }
            for (int y = top; y <= fy; y++)
            {
                PlaceBlock(job, left, y, block);
                PlaceBlock(job, right, y, block);
            }

            // 4) pintu di kedua sisi (3 tile tinggi, menempel di lantai)
            foreach (int dx in new[] { left, right })
            {
                for (int y = fy - 3; y <= fy - 1; y++)
                    ClearTile(job, dx, y);

                if (!WorldGen.PlaceDoor(dx, fy - 2, TileID.ClosedDoor, st.Door))
                    job.ObjectFailed++;
            }

            // 5) lantai platform (loft) di atas area meja
            for (int x = cx - 5; x <= cx - 2; x++)
            {
                if (!WorldGen.PlaceTile(x, fy - 4, TileID.Platforms, true, true, -1, 0))
                    job.ObjectFailed++;
            }

            // 6) furnitur
            if (!WorldGen.PlaceObject(cx - 3, fy - 1, TileID.Tables, true, st.Table))
                job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 5, fy - 1, TileID.Chairs, true, st.Chair, 0, -1, 1))
                job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 3, fy - 1, TileID.Bookcases, true, st.Bookcase))
                job.ObjectFailed++;

            // 7) lukisan di tengah dinding atas
            if (!WorldGen.PlaceObject(cx, fy - 5, TileID.Painting3X3, true, st.Painting))
                job.ObjectFailed++;

            // 8) obor di dinding kiri dan kanan
            if (!WorldGen.PlaceTile(cx - 5, fy - 5, TileID.Torches, true, true, -1, st.Torch))
                job.ObjectFailed++;
            if (!WorldGen.PlaceTile(cx + 5, fy - 5, TileID.Torches, true, true, -1, st.Torch))
                job.ObjectFailed++;

            // 9) kirim hasilnya ke semua pemain
            int rows = deepest - top + 1;
            TSPlayer.All.SendTileRect((short)left, (short)top, (byte)(HouseHalfWidth * 2 + 1), (byte)rows);
        }

        /// <summary>Hancurkan tile (tanpa drop) dan kosongkan cairan.</summary>
        private static void ClearTile(BuildJob job, int x, int y)
        {
            var tile = Main.tile[x, y];
            if (tile.active())
            {
                WorldGen.KillTile(x, y, false, false, true);
                tile = Main.tile[x, y];
                if (tile.active())
                {
                    job.Blocked++;
                    return;
                }
            }
            tile.liquid = 0;
        }

        /// <summary>Taruh blok padat di (x, y); tile lama dihancurkan dulu.</summary>
        private static void PlaceBlock(BuildJob job, int x, int y, ushort type)
        {
            var tile = Main.tile[x, y];

            if (tile.active() && tile.type == type)
                return;

            if (tile.active())
            {
                WorldGen.KillTile(x, y, false, false, true);
                tile = Main.tile[x, y];
                if (tile.active())
                {
                    job.Blocked++;
                    return;
                }
            }

            tile.liquid = 0;
            tile.type = type;
            tile.active(true);
            tile.halfBrick(false);
            tile.slope(0);
            WorldGen.SquareTileFrame(x, y, true);
        }

        // ---------------------------------------------------------------
        // Titik: baca dari packet projectile Ice Rod
        // ---------------------------------------------------------------

        private void OnGetData(GetDataEventArgs args)
        {
            int who = args.Msg.whoAmI;
            if (who < 0 || who >= Main.maxPlayers)
                return;

            int msgId = (int)args.MsgID;

            bool debug;
            bool selecting;
            lock (_lock)
            {
                debug = _debug.Contains(who);
                Session s;
                selecting = _sessions.TryGetValue(who, out s) && s.Selecting;
            }

            bool wantDebug = debug && DebugPackets.Contains(msgId);
            bool wantSelect = selecting && msgId == 27;
            if (!wantDebug && !wantSelect)
                return;

            byte[] buffer = args.Msg.readBuffer;
            if (buffer == null || args.Index < 0 || args.Index >= buffer.Length)
                return;

            Player p = Main.player[who];
            if (p == null || p.selectedItem < 0 || p.selectedItem >= p.inventory.Length)
                return;

            Item held = p.inventory[p.selectedItem];
            if (held.type != IceRodItemType)
                return;

            if (wantDebug)
            {
                int len = Math.Min(args.Length, 64);
                var hex = new StringBuilder();
                for (int i = 0; i < len && args.Index + i < buffer.Length; i++)
                {
                    hex.Append(buffer[args.Index + i].ToString("X2"));
                    hex.Append(' ');
                }
                Write($"{DateTime.Now:HH:mm:ss.fff} | msg={msgId} ({args.MsgID}) | len={args.Length} | held={held.type} \"{held.Name}\" | {hex}");
            }

            if (wantSelect)
                TryHandleIceProjectile(args, who, buffer);
        }

        private void TryHandleIceProjectile(GetDataEventArgs args, int who, byte[] buffer)
        {
            int i = args.Index;

            // header(4) + pos(8) + vel(8) + type(2) + flags(1) + ai0(4) + ai1(4) = 31 byte
            if (args.Length < 31 || i + 31 > buffer.Length)
                return;

            short type = BitConverter.ToInt16(buffer, i + 20);
            if (type != IceBlockProjectile)
                return;

            byte flags = buffer[i + 22];
            if ((flags & 0x03) != 0x03)   // bit0 = ai[0] ada, bit1 = ai[1] ada
                return;

            float ai0 = BitConverter.ToSingle(buffer, i + 23);
            float ai1 = BitConverter.ToSingle(buffer, i + 27);
            if (float.IsNaN(ai0) || float.IsNaN(ai1) || ai0 < 0f || ai1 < 0f)
                return; // paket "sudah dipasang" membawa ai[0] = -1, abaikan

            int x = (int)ai0;
            int y = (int)ai1;
            if (x >= Main.maxTilesX || y >= Main.maxTilesY)
                return;

            TSPlayer player = TShock.Players[who];
            if (player == null || !player.RealPlayer)
                return;

            string message = AddPoint(player, x, y);
            if (message != null)
                player.SendInfoMessage(message);
        }

        // ---------------------------------------------------------------
        // Batalkan es dari Ice Rod
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
            if (!selecting || !IsHoldingIceRod(player))
                return;

            if ((int)args.Action != 1 || args.EditData != IceBlockTileType)
                return;

            int x = args.X;
            int y = args.Y;
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
                return;

            args.Handled = true;

            // kirim kondisi asli tile (membatalkan es di client), lalu pasang lagi penanda kalau tile ini titik terpilih
            player.SendTileRect((short)x, (short)y, 1, 1);

            bool marked;
            lock (_lock)
            {
                Session s;
                marked = _sessions.TryGetValue(player.Index, out s) && s.Points.Exists(pt => pt.X == x && pt.Y == y);
            }
            if (marked)
                ShowMarker(player, x, y);
        }

        /// <summary>
        /// Tambah titik. Mengembalikan pesan untuk pemain, atau null jika titik duplikat
        /// (autofire Ice Rod sering mengirim titik yang sama berkali-kali).
        /// </summary>
        private string AddPoint(TSPlayer player, int x, int y)
        {
            string message;
            bool added = false;

            lock (_lock)
            {
                Session s = GetSession(player.Index);

                if (s.Points.Exists(pt => pt.X == x && pt.Y == y))
                {
                    return null;
                }
                else if (s.Points.Count >= MaxPoints)
                {
                    message = $"Maksimal {MaxPoints} titik. Pakai /build clear untuk mengulang.";
                }
                else
                {
                    s.Points.Add((x, y));
                    message = $"Titik {s.Points.Count}: ({x}, {y})";
                    added = true;
                }
            }

            if (added)
                ShowMarker(player, x, y);

            return message;
        }

        private static bool IsHoldingIceRod(TSPlayer player)
        {
            Player tp = player.TPlayer;
            int idx = tp.selectedItem;
            if (idx < 0 || idx >= tp.inventory.Length)
                return false;
            return tp.inventory[idx].type == IceRodItemType;
        }

        // ---------------------------------------------------------------
        // Blok penanda palsu (hanya dikirim ke pemain ini, dunia server tidak berubah)
        // ---------------------------------------------------------------

        private static void ShowMarker(TSPlayer player, int x, int y)
        {
            try
            {
                var tile = Main.tile[x, y];
                bool wasActive = tile.active();
                ushort oldType = tile.type;

                tile.type = MarkerTileType;
                tile.active(true);
                player.SendTileRect((short)x, (short)y, 1, 1);

                // kembalikan di sisi server
                tile.type = oldType;
                tile.active(wasActive);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[MagicBuilder] Gagal pasang penanda: {ex.Message}");
            }
        }

        private void Write(string line)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[MagicBuilder] Gagal tulis log: {ex.Message}");
            }
        }
    }
}
