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
    [ApiVersion(2, 1)]
    public class MagicBuilderPlugin : TerrariaPlugin
    {
        public override string Name => "MagicBuilder";
        public override string Author => "You";
        public override Version Version => new Version(1, 0, 0, 0);
        public override string Description => "Pilih titik pakai Ice Rod dan bangun struktur otomatis.";

        private const string UsePermission = "magicbuilder.use";
        private const string BuildPermission = "magicbuilder.build";
        private const string DebugPermission = "magicbuilder.debug";

        // --- Pemilih titik ---
        private const int IceRodItemType = 496;
        private const int IceBlockProjectile = 80;
        private const int IceBlockTileType = 127;
        private const ushort MarkerTileType = 8;
        private const int MaxPoints = 8;

        // --- Hellevator (TIDAK DIUBAH) ---
        private const ushort BrickTileType = 38;
        private const ushort WallType = 5;
        private const int TorchTileType = 4;
        private const int TorchStyle = 0;
        private const int RopeTileType = 213;
        private const int TorchInterval = 6;
        private const int RowsPerTick = 8;
        private const int ConfirmSeconds = 60;

        // --- Rumah NPC (11 lebar x 7 tinggi per lantai) ---
        private const int HouseHalfWidth = 5; // 5 + 1 + 5 = 11 tile
        private const int FloorStepHeight = 6; // +1 lantai dasar = 7 tile per tingkat

        private static readonly HashSet<int> DebugPackets = new HashSet<int> { 17, 27, 29 };

        private class Session
        {
            public bool Selecting;
            public readonly List<(int X, int Y)> Points = new List<(int X, int Y)>();

            public bool HasPending;
            public string PendingKind;
            public List<HouseSpec> PendingHouses;
            public int PendingX;
            public int PendingStartY;
            public int PendingEndY;
            public DateTime PendingExpires;
        }

        private class HouseStyle
        {
            public string Name;
            public ushort Block;
            public ushort Wall;
            public int Platform;
            public ushort TableTile;
            public int Table;
            public int Chair;
            public int Torch;

            public HouseStyle(string name, ushort block, ushort wall, int platform, ushort tableTile, int table, int chair, int torch)
            {
                Name = name;
                Block = block;
                Wall = wall;
                Platform = platform;
                TableTile = tableTile;
                Table = table;
                Chair = chair;
                Torch = torch;
            }
        }

        private class HouseSpec
        {
            public int X;
            public int Y;
            public int Floors;
            public HouseStyle Style;
        }

        private static readonly Dictionary<string, HouseStyle> HouseStyles = new Dictionary<string, HouseStyle>
        {
            ["forest"]     = new HouseStyle("Forest",     TileID.WoodBlock,       WallID.Wood,            0,  TileID.Tables,  0,  0,  0),
            ["snow"]       = new HouseStyle("Snow",       TileID.BorealWood,      WallID.BorealWood,      19, TileID.Tables,  28, 30, 9),
            ["desert"]     = new HouseStyle("Desert",     TileID.SmoothSandstone, WallID.SmoothSandstone, 42, TileID.Tables2, 1,  39, 16),
            ["jungle"]     = new HouseStyle("Jungle",     TileID.RichMahogany,    WallID.RichMaogany,     2,  TileID.Tables,  2,  2,  21),
            ["ocean"]      = new HouseStyle("Ocean",      TileID.PalmWood,        WallID.PalmWood,        17, TileID.Tables,  26, 29, 23),
            ["hallow"]     = new HouseStyle("Hallow",     TileID.Pearlwood,       WallID.Pearlwood,       3,  TileID.Tables,  3,  3,  20),
            ["cavern"]     = new HouseStyle("Cavern",     TileID.GrayBrick,       WallID.GrayBrick,       0,  TileID.Tables,  0,  0,  0),
            ["underworld"] = new HouseStyle("Underworld", TileID.ObsidianBrick,   WallID.ObsidianBrick,   13, TileID.Tables,  13, 16, 7),
        };

        private class BuildJob
        {
            public bool IsHouse;
            public int Floors;
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
                    PrepareNpcHouses(player, args.Parameters);
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
                    player.SendInfoMessage("Pemakaian: /build select | points | remove <n> | clear | hellevator | npchouse [1|2|3] | confirm | cancel");
                    break;
            }
        }

        private Session GetSession(int index)
        {
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
        // Hellevator (TIDAK DIUBAH)
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
                            Floors = h.Floors,
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
                player.SendSuccessMessage($"Membangun {queued} bangunan rumah NPC. Pakai /build cancel untuk menghentikan sisanya.");
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
                    owner.SendSuccessMessage(job.IsHouse
                        ? $"Rumah NPC ({job.Style.Name}, {job.Floors} tingkat) di ({job.X}, {job.StartY}) selesai."
                        : "Hellevator selesai.");
                    if (job.Blocked > 0)
                        owner.SendWarningMessage($"{job.Blocked} tile tidak bisa dihancurkan (misalnya peti berisi), jadi ada celah.");
                    if (job.TorchFailed > 0)
                        owner.SendWarningMessage($"{job.TorchFailed} obor gagal dipasang.");
                    if (job.RopeFailed > 0)
                        owner.SendWarningMessage($"{job.RopeFailed} tile tali gagal dipasang.");
                    if (job.ObjectFailed > 0)
                        owner.SendWarningMessage($"{job.ObjectFailed} furnitur gagal dipasang.");
                }
            }

            TShock.Log.ConsoleInfo($"[MagicBuilder] {(job.IsHouse ? $"Rumah({job.Floors}L)" : "Hellevator")} {job.PlayerName} x={job.X} y={job.StartY}..{job.EndY} selesai (blocked={job.Blocked}, objFailed={job.ObjectFailed}, cancelled={job.Cancelled}, error={error ?? "-"})");
        }

        private void BuildRow(BuildJob job, int y)
        {
            int cx = job.X;

            PlaceBrick(job, cx - 2, y);
            PlaceBrick(job, cx + 2, y);

            for (int x = cx - 1; x <= cx + 1; x++)
                DigAndWall(job, x, y);

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

            if (WorldGen.PlaceTile(cx, y, RopeTileType, true, true, -1, 0))
                WorldGen.SquareTileFrame(cx, y, true);
            else
                job.RopeFailed++;
        }

        private void PlaceRopeAnchor(BuildJob job)
        {
            job.AnchorDone = true;

            int x = job.X;
            int y = job.StartY - 1;
            var tile = Main.tile[x, y];

            if (tile.active() && Main.tileSolid[tile.type])
                return;

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
        // Rumah NPC (Simpel 11x7 bertingkat 1/2/3 + Deteksi Biome Otomatis)
        // ---------------------------------------------------------------

        private void PrepareNpcHouses(TSPlayer player, List<string> parameters)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            int floors = 1;
            if (parameters.Count > 2)
            {
                player.SendErrorMessage("Pemakaian: /build npchouse [1|2|3]");
                return;
            }
            if (parameters.Count == 2)
            {
                if (!int.TryParse(parameters[1], out floors) || floors < 1 || floors > 3)
                {
                    player.SendErrorMessage("Tingkat rumah hanya tersedia: 1, 2, atau 3.");
                    return;
                }
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
                player.SendErrorMessage("Belum ada titik. Pakai /build select lalu klik tile lantai tempat rumah berdiri.");
                return;
            }

            int heightAbove = floors * FloorStepHeight;
            var specs = new List<HouseSpec>();

            for (int i = 0; i < points.Count; i++)
            {
                int cx = points[i].X;
                int fy = points[i].Y;

                if (cx - HouseHalfWidth < 2 || cx + HouseHalfWidth >= Main.maxTilesX - 2
                    || fy - heightAbove < 2 || fy >= Main.maxTilesY - 5)
                {
                    player.SendErrorMessage($"Titik {i + 1} ({cx}, {fy}) terlalu dekat tepi dunia.");
                    return;
                }

                for (int x = cx - HouseHalfWidth; x <= cx + HouseHalfWidth; x++)
                {
                    for (int y = fy - heightAbove; y <= fy; y++)
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
                    int otherTop = other.Y - (other.Floors * FloorStepHeight);
                    int curTop = fy - heightAbove;
                    bool overlapX = Math.Abs(other.X - cx) <= HouseHalfWidth * 2;
                    bool overlapY = !(fy < otherTop || curTop > other.Y);
                    if (overlapX && overlapY)
                    {
                        player.SendErrorMessage($"Titik {i + 1} bertabrakan dengan rumah lain (minimal 11 tile antar titik). Dibatalkan.");
                        return;
                    }
                }

                specs.Add(new HouseSpec { X = cx, Y = fy, Floors = floors, Style = DetectHouseStyle(cx, fy) });
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
                player.SendInfoMessage($"Rumah {i + 1}: {specs[i].Floors} tingkat di ({specs[i].X}, {specs[i].Y}), biome {specs[i].Style.Name}.");

            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        private static HouseStyle DetectHouseStyle(int cx, int cy)
        {
            if (cy >= Main.UnderworldLayer)
                return HouseStyles["underworld"];

            if (cy <= Main.worldSurface && (cx < 380 || cx > Main.maxTilesX - 380))
                return HouseStyles["ocean"];

            int hallow = 0, snow = 0, desert = 0, jungle = 0, ocean = 0;

            int x0 = Math.Max(0, cx - 22), x1 = Math.Min(Main.maxTilesX - 1, cx + 22);
            int y0 = Math.Max(0, cy - 14), y1 = Math.Min(Main.maxTilesY - 1, cy + 14);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    var t = Main.tile[x, y];
                    if (t.liquid > 120 && t.liquidType() == 0 && y >= cy)
                        ocean++;

                    if (!t.active())
                        continue;

                    switch ((int)t.type)
                    {
                        case 109: case 117: case 116: case 164:
                            if (Main.hardMode) hallow++;
                            break;
                        case 147: case 161:
                            snow++; break;
                        case 53: case 397: case 396:
                            desert++; break;
                        case 60:
                            jungle++; break;
                        case 81: case 323:
                            ocean += 3; break;
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

            if (Main.hardMode)
                Consider("hallow", hallow);
            Consider("snow", snow);
            Consider("desert", desert);
            Consider("jungle", jungle);
            if (cy <= Main.worldSurface && (cx < 550 || cx > Main.maxTilesX - 550))
                Consider("ocean", ocean);

            if (best != null)
                return HouseStyles[best];

            return cy > Main.worldSurface ? HouseStyles["cavern"] : HouseStyles["forest"];
        }

        private void BuildNpcHouse(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int floors = job.Floors;
            int left = cx - HouseHalfWidth;   // 11 tile: left .. left+10
            int right = cx + HouseHalfWidth;
            int topY = fy - (floors * FloorStepHeight);

            // Bersihkan area 11 x (1 + 6*floors)
            for (int y = topY; y <= fy; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    ClearTile(job, x, y);
                    var t = Main.tile[x, y];
                    t.wall = 0;
                    t.wallColor(0);
                    WorldGen.SquareWallFrame(x, y, true);
                }
            }

            // Lantai paling bawah (11 tile)
            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, fy, st.Block);

            // Bangun tiap lantai ke atas (masing-masing 7 tile total, berbagi pembatas lantai)
            for (int f = 0; f < floors; f++)
            {
                int floorBottom = fy - (f * FloorStepHeight);
                int floorTop = floorBottom - FloorStepHeight;

                // Dinding samping kiri & kanan (5 tile tinggi) + dinding latar interior (9x5)
                for (int y = floorTop + 1; y <= floorBottom - 1; y++)
                {
                    PlaceBlock(job, left, y, st.Block);
                    PlaceBlock(job, right, y, st.Block);

                    for (int x = left + 1; x <= right - 1; x++)
                    {
                        var t = Main.tile[x, y];
                        t.wall = st.Wall;
                        t.wallColor(0);
                        WorldGen.SquareWallFrame(x, y, true);
                    }
                }

                // Langit-langit / atap lantai ini: 7 block kiri + 3 platform kanan + 1 block sudut kanan
                for (int x = left; x <= left + 6; x++)
                    PlaceBlock(job, x, floorTop, st.Block);

                for (int x = left + 7; x <= left + 9; x++)
                    PlacePlatform(job, x, floorTop, st.Platform);

                PlaceBlock(job, right, floorTop, st.Block);

                // Torch di pojok kiri atas ruang
                if (!WorldGen.PlaceTile(left + 1, floorTop + 1, TileID.Torches, true, true, -1, st.Torch))
                    job.TorchFailed++;

                // Chair & Table di kiri bawah ruang
                if (!WorldGen.PlaceObject(left + 2, floorBottom - 1, TileID.Chairs, true, st.Chair, 0, -1, 1))
                    job.ObjectFailed++;

                if (!WorldGen.PlaceObject(left + 4, floorBottom - 1, st.TableTile, true, st.Table))
                    job.ObjectFailed++;
            }

            int height = fy - topY + 1;
            TSPlayer.All.SendTileRect((short)left, (short)topY, 11, (byte)height);
        }

        private static void PlacePlatform(BuildJob job, int x, int y, int style)
        {
            ClearTile(job, x, y);
            if (WorldGen.PlaceTile(x, y, TileID.Platforms, true, true, -1, style))
                WorldGen.SquareTileFrame(x, y, true);
            else
                job.ObjectFailed++;
        }

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
            tile.color(0);
        }

        private static void PlaceBlock(BuildJob job, int x, int y, ushort type)
        {
            var tile = Main.tile[x, y];

            if (tile.active() && tile.type == type)
            {
                tile.color(0);
                return;
            }

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
            tile.color(0);
            WorldGen.SquareTileFrame(x, y, true);
        }

        // ---------------------------------------------------------------
        // Titik: baca dari packet projectile Ice Rod (TIDAK DIUBAH)
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

            if (args.Length < 31 || i + 31 > buffer.Length)
                return;

            short type = BitConverter.ToInt16(buffer, i + 20);
            if (type != IceBlockProjectile)
                return;

            byte flags = buffer[i + 22];
            if ((flags & 0x03) != 0x03)
                return;

            float ai0 = BitConverter.ToSingle(buffer, i + 23);
            float ai1 = BitConverter.ToSingle(buffer, i + 27);
            if (float.IsNaN(ai0) || float.IsNaN(ai1) || ai0 < 0f || ai1 < 0f)
                return;

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
        // Batalkan es dari Ice Rod (TIDAK DIUBAH)
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
