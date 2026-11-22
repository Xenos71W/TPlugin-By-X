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
        public override Version Version => new Version(0, 8, 0, 0);
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

        // --- Rumah NPC ---
        private const int FoundationMax = 10;

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
            public ushort MainBlock;    // Blok utama tembok & lantai
            public ushort BaseBlock;    // Blok fondasi, bingkai sudut & trim
            public ushort RoofBlock;    // Blok atap miring
            public ushort MainWall;     // Dinding utama interior
            public ushort AccentWall;   // Dinding aksen bawah (wainscoting) & pilar jendela
            public int Door;
            public int Table;
            public int Chair;
            public int Bookcase;
            public int Platform;
            public int Lantern;
            public int Torch;
            public int Painting3x2;

            public HouseStyle(
                string name,
                ushort mainBlock, ushort baseBlock, ushort roofBlock,
                ushort mainWall, ushort accentWall,
                int door, int table, int chair, int bookcase,
                int platform, int lantern, int torch,
                int painting3x2)
            {
                Name = name;
                MainBlock = mainBlock;
                BaseBlock = baseBlock;
                RoofBlock = roofBlock;
                MainWall = mainWall;
                AccentWall = accentWall;
                Door = door;
                Table = table;
                Chair = chair;
                Bookcase = bookcase;
                Platform = platform;
                Lantern = lantern;
                Torch = torch;
                Painting3x2 = painting3x2;
            }
        }

        private class HouseSpec
        {
            public int X;
            public int Y;
            public int Size; // 1, 2, atau 3 NPC
            public HouseStyle Style;
        }

        // Semua ID diverifikasi terhadap sprite sheet resmi Terraria 1.4.4+
        // Bookcases (Tile 101): 0=Wood, 4=Obsidian, 5=Glass, 6=Golden, 8=Steampunk, 12=Frozen, 13=Dynasty, 14=LivingWood, 15=Shadewood, 16=Boreal, 25=Granite, 26=Marble, 27=Crystal
        // HangingLanterns (Tile 42): 1=Brass, 3=Carriage, 11=Chinese, 12=Glass, 13=Frozen, 14=Obsidian, 17=Ebonwood, 18=RichMahogany, 19=Pearlwood, 20=Shadewood, 25=PalmWood, 26=Boreal
        // Painting3X2 (Tile 242): 11=FindingGold, 13=GoodMorning, 15=ThroughTheWindow, 16=AboveClouds, 17=JungleGrass, 18=ColdWaters, 19=LightlessChasm, 20=CrimsonLand, 21=Daylight, 22=SecretOfSands, 27=Discover, 32=LakeOfFire
        private static readonly Dictionary<string, HouseStyle> HouseStyles = new Dictionary<string, HouseStyle>
        {
            ["forest"] = new HouseStyle(
                "Forest",
                TileID.WoodBlock, TileID.StoneSlab, TileID.RedBrick,
                WallID.Wood, WallID.Planked,
                door: 0, table: 0, chair: 0, bookcase: 0,
                platform: 0, lantern: 3, torch: 0,
                painting3x2: 13),

            ["ocean"] = new HouseStyle(
                "Ocean",
                TileID.PalmWood, TileID.SandstoneBrick, TileID.RedDynastyShingles,
                WallID.PalmWood, WallID.SandstoneBrick,
                door: 28, table: 26, chair: 26, bookcase: 13,
                platform: 17, lantern: 25, torch: 0,
                painting3x2: 27),

            ["snow"] = new HouseStyle(
                "Snow",
                TileID.BorealWood, TileID.SnowBrick, TileID.IceBrick,
                WallID.BorealWood, WallID.SnowBrick,
                door: 29, table: 27, chair: 27, bookcase: 16,
                platform: 19, lantern: 26, torch: 9,
                painting3x2: 18),

            ["desert"] = new HouseStyle(
                "Desert",
                TileID.SmoothSandstone, TileID.SandstoneBrick, TileID.HardenedSand,
                WallID.SmoothSandstone, WallID.SandstoneBrick,
                door: 28, table: 18, chair: 18, bookcase: 6,
                platform: 17, lantern: 1, torch: 16,
                painting3x2: 22),

            ["jungle"] = new HouseStyle(
                "Jungle",
                TileID.RichMahogany, TileID.Mudstone, TileID.IridescentBrick,
                WallID.RichMaogany, WallID.MudstoneBrick,
                door: 2, table: 2, chair: 3, bookcase: 14,
                platform: 2, lantern: 18, torch: 21,
                painting3x2: 17),

            ["corruption"] = new HouseStyle(
                "Corruption",
                TileID.Ebonwood, TileID.EbonstoneBrick, TileID.DemoniteBrick,
                WallID.Ebonwood, WallID.EbonstoneBrick,
                door: 1, table: 1, chair: 2, bookcase: 4,
                platform: 1, lantern: 17, torch: 18,
                painting3x2: 19),

            ["crimson"] = new HouseStyle(
                "Crimson",
                TileID.Shadewood, TileID.CrimstoneBrick, TileID.CrimtaneBrick,
                WallID.Shadewood, WallID.CrimstoneBrick,
                door: 10, table: 8, chair: 10, bookcase: 15,
                platform: 26, lantern: 20, torch: 19,
                painting3x2: 20),

            ["hallow"] = new HouseStyle(
                "Hallow",
                TileID.Pearlwood, TileID.PearlstoneBrick, TileID.CrystalBlock,
                WallID.Pearlwood, WallID.PearlstoneBrick,
                door: 3, table: 3, chair: 4, bookcase: 27,
                platform: 3, lantern: 19, torch: 20,
                painting3x2: 16),

            ["underworld"] = new HouseStyle(
                "Underworld",
                TileID.ObsidianBrick, TileID.HellstoneBrick, TileID.AshWood,
                WallID.ObsidianBrick, WallID.HellstoneBrick,
                door: 18, table: 13, chair: 15, bookcase: 4,
                platform: 13, lantern: 14, torch: 7,
                painting3x2: 32),

            ["cavern"] = new HouseStyle(
                "Cavern",
                TileID.StoneSlab, TileID.GrayBrick, TileID.TinBrick,
                WallID.StoneSlab, WallID.GrayBrick,
                door: 0, table: 0, chair: 0, bookcase: 25,
                platform: 0, lantern: 1, torch: 0,
                painting3x2: 11),
        };

        private class BuildJob
        {
            public bool IsHouse;
            public int HouseSize;
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
                    player.SendInfoMessage("Pemakaian: /build select | points | remove <n> | clear | hellevator | npchouse [1|2|3] [biome] | confirm | cancel");
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
                            HouseSize = h.Size,
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
                        ? $"Rumah NPC ({job.Style.Name}, {job.HouseSize} kamar) di ({job.X}, {job.StartY}) selesai."
                        : "Hellevator selesai.");
                    if (job.Blocked > 0)
                        owner.SendWarningMessage($"{job.Blocked} tile tidak bisa dihancurkan (misalnya peti berisi), jadi ada celah.");
                    if (job.TorchFailed > 0)
                        owner.SendWarningMessage($"{job.TorchFailed} obor gagal dipasang.");
                    if (job.RopeFailed > 0)
                        owner.SendWarningMessage($"{job.RopeFailed} tile tali gagal dipasang.");
                    if (job.ObjectFailed > 0)
                        owner.SendWarningMessage($"{job.ObjectFailed} furnitur/dekorasi gagal dipasang.");
                }
            }

            TShock.Log.ConsoleInfo($"[MagicBuilder] {(job.IsHouse ? $"Rumah({job.HouseSize})" : "Hellevator")} {job.PlayerName} x={job.X} y={job.StartY}..{job.EndY} selesai (blocked={job.Blocked}, objFailed={job.ObjectFailed}, cancelled={job.Cancelled}, error={error ?? "-"})");
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
        // Rumah NPC (3 Ukuran + 10 Biome Termasuk Ocean + Desain Refined)
        // ---------------------------------------------------------------

        private static void GetHouseBounds(int size, out int halfWidth, out int heightAbove)
        {
            switch (size)
            {
                case 2:
                    halfWidth = 10;
                    heightAbove = 21;
                    break;
                case 3:
                    halfWidth = 15;
                    heightAbove = 21;
                    break;
                default:
                    halfWidth = 10;
                    heightAbove = 15;
                    break;
            }
        }

        private void PrepareNpcHouses(TSPlayer player, List<string> parameters)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            int size = 1;
            string forcedBiome = null;

            for (int p = 1; p < parameters.Count; p++)
            {
                string arg = parameters[p].ToLowerInvariant();
                int parsedSize;
                if (int.TryParse(arg, out parsedSize))
                {
                    if (parsedSize < 1 || parsedSize > 3)
                    {
                        player.SendErrorMessage("Ukuran rumah hanya tersedia: 1 (1 NPC), 2 (2 NPC), atau 3 (3 NPC).");
                        return;
                    }
                    size = parsedSize;
                }
                else
                {
                    forcedBiome = arg;
                }
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

            int hw, ha;
            GetHouseBounds(size, out hw, out ha);

            var specs = new List<HouseSpec>();

            for (int i = 0; i < points.Count; i++)
            {
                int cx = points[i].X;
                int fy = points[i].Y;

                if (cx - hw < 2 || cx + hw >= Main.maxTilesX - 2
                    || fy - ha < 2 || fy + FoundationMax >= Main.maxTilesY - 5)
                {
                    player.SendErrorMessage($"Titik {i + 1} ({cx}, {fy}) terlalu dekat tepi dunia.");
                    return;
                }

                for (int x = cx - hw; x <= cx + hw; x++)
                {
                    for (int y = fy - ha; y <= fy + FoundationMax; y++)
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
                    if (Math.Abs(other.X - cx) <= hw * 2 && Math.Abs(other.Y - fy) <= ha + FoundationMax)
                    {
                        player.SendErrorMessage($"Titik {i + 1} terlalu dekat dengan rumah lain (minimal {hw * 2 + 1} tile antar titik). Dibatalkan.");
                        return;
                    }
                }

                specs.Add(new HouseSpec { X = cx, Y = fy, Size = size, Style = forced ?? DetectHouseStyle(cx, fy) });
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
                player.SendInfoMessage($"Rumah {i + 1}: ukuran {specs[i].Size} NPC di ({specs[i].X}, {specs[i].Y}), gaya {specs[i].Style.Name}.");

            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        private static HouseStyle DetectHouseStyle(int cx, int cy)
        {
            if (cy >= Main.UnderworldLayer)
                return HouseStyles["underworld"];

            // Deteksi area pantai/Ocean di ujung kiri & kanan dunia (di atas permukaan)
            if (cy <= Main.worldSurface && (cx < 380 || cx > Main.maxTilesX - 380))
                return HouseStyles["ocean"];

            int corruption = 0, crimson = 0, hallow = 0, snow = 0, desert = 0, jungle = 0, ocean = 0;

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
                        case 23: case 25: case 112: case 163:
                            corruption++; break;
                        case 199: case 203: case 234: case 200:
                            crimson++; break;
                        case 109: case 117: case 116: case 164:
                            hallow++; break;
                        case 147: case 161:
                            snow++; break;
                        case 53: case 397: case 396:
                            desert++; break;
                        case 60:
                            jungle++; break;
                        case 81: case 323: // Coral & Palm Tree
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

            Consider("corruption", corruption);
            Consider("crimson", crimson);
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
            switch (job.HouseSize)
            {
                case 2:
                    BuildHouse2Npc(job);
                    break;
                case 3:
                    BuildHouse3Npc(job);
                    break;
                default:
                    BuildHouse1Npc(job);
                    break;
            }
        }

        // ===============================================================
        // VARIAN 1: COTTAGE 1 NPC (Porch + Atap Sleek + Cerobong Asap)
        // ===============================================================
        private void BuildHouse1Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 7;     // Tembok kiri di cx-7 (interior cx-6..cx+6 = 13 lebar)
            int right = cx + 7;    // Tembok kanan di cx+7
            int ceilingY = fy - 7; // Plafon di fy-7 (interior fy-6..fy-1 = 6 tinggi)
            int topY = fy - 14;

            ClearAreaAndOutsideWalls(job, cx - 10, topY, cx + 10, fy - 1);
            int deepest = BuildSmartFoundation(job, left - 1, right, fy, st.BaseBlock);

            // Lantai utama + teras depan di left-1 (cx-8)
            PlaceBlock(job, left - 1, fy, st.BaseBlock);
            for (int x = left; x <= right; x++)
            {
                ushort mat = (x <= left + 1 || x >= right - 1) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, x, fy, mat);
            }

            // Tiang teras depan pakai WoodenBeam di cx-8
            for (int y = ceilingY + 1; y < fy; y++)
                PlaceBeam(job, left - 1, y);

            // Tembok Kiri & Kanan (Fondasi batu di bawah, MainBlock di atas)
            for (int y = ceilingY; y < fy; y++)
            {
                ushort mat = (y >= fy - 2) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, left, y, mat);
                PlaceBlock(job, right, y, mat);
            }

            // Plafon memanjang sampai menutupi teras depan (left-1 .. right)
            for (int x = left - 1; x <= right; x++)
                PlaceBlock(job, x, ceilingY, st.MainBlock);

            // Dinding Latar Interior (HANYA di dalam ruangan left+1 .. right-1)
            DecorateRoomWalls(left + 1, right - 1, ceilingY + 1, fy - 1, cx - 1, cx + 1, st);

            // Atap Sleek 4-Lapis dengan Eaves Miring + Cerobong Asap Aktif
            BuildSleekRoof(job, cx, ceilingY - 1, 9, st.RoofBlock, st.BaseBlock, st.AccentWall);
            WorldGen.PlaceObject(cx + 3, ceilingY - 5, TileID.Chimney, true);

            // Pintu Kiri & Kanan
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);

            // Furnitur & Dekorasi (Semua duduk di atas blok padat!)
            PlaceChair(job, cx - 5, fy - 1, st.Chair, 1);
            PlaceTableWithDecor(job, cx - 3, fy - 1, st.Table, 1); // Mug di atas meja
            PlacePainting(job, cx - 3, fy - 5, st.Painting3x2);
            PlaceBookcase(job, cx + 4, fy - 1, st.Bookcase);       // Duduk di cx+3..cx+5 (padat!)
            PlaceCeilingLight(job, cx, ceilingY + 1, st.Lantern, st.Torch);

            // Obor teras luar & obor dekat pintu kanan
            WorldGen.PlaceTile(left - 1, fy - 5, TileID.Torches, true, true, -1, st.Torch);
            WorldGen.PlaceTile(cx + 6, fy - 5, TileID.Torches, true, true, -1, st.Torch);

            int width = (cx + 10) - (cx - 10) + 1;
            int height = deepest - topY + 1;
            TSPlayer.All.SendTileRect((short)(cx - 10), (short)topY, (byte)width, (byte)height);
        }

        // ===============================================================
        // VARIAN 2: DUPLEX 2 LANTAI (2 NPC - Tangga Platform Bersih)
        // ===============================================================
        private void BuildHouse2Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 7;
            int right = cx + 7;       // Interior lebar 13 (cx-6..cx+6)
            int midFloorY = fy - 7;   // Lantai 2 di fy-7 (interior lt.1: fy-6..fy-1 = 6 tinggi)
            int ceilingY = fy - 14;   // Plafon lt.2 di fy-14 (interior lt.2: fy-13..fy-8 = 6 tinggi)
            int topY = fy - 21;

            ClearAreaAndOutsideWalls(job, cx - 10, topY, cx + 10, fy - 1);
            int deepest = BuildSmartFoundation(job, left, right, fy, st.BaseBlock);

            // Lantai Dasar
            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, fy, (x <= left + 1 || x >= right - 1) ? st.BaseBlock : st.MainBlock);

            // Lantai 2: cx-7..cx+2 & cx+6..cx+7 PADAT, cx+3..cx+5 PLATFORM untuk akses naik
            for (int x = left; x <= right; x++)
            {
                if (x >= cx + 3 && x <= cx + 5)
                    PlacePlatform(job, x, midFloorY, st.Platform);
                else
                    PlaceBlock(job, x, midFloorY, st.MainBlock);
            }

            // Plafon Atas
            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, ceilingY, st.MainBlock);

            // Tembok Luar Kiri & Kanan
            for (int y = ceilingY; y < fy; y++)
            {
                ushort mat = (y >= fy - 2 || y == midFloorY) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, left, y, mat);
                PlaceBlock(job, right, y, mat);
            }

            // Dinding Latar Lantai 1 & Lantai 2 (Hanya interior, tidak bocor keluar)
            DecorateRoomWalls(left + 1, right - 1, midFloorY + 1, fy - 1, cx - 1, cx + 1, st);
            DecorateRoomWalls(left + 1, right - 1, ceilingY + 1, midFloorY - 1, cx - 2, cx, st);

            // Atap Sleek 4-Lapis + Cerobong Asap
            BuildSleekRoof(job, cx, ceilingY - 1, 9, st.RoofBlock, st.BaseBlock, st.AccentWall);
            WorldGen.PlaceObject(cx + 3, ceilingY - 5, TileID.Chimney, true);

            // Pintu Kiri & Kanan Lantai 1
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);

            // --- Furnitur Lantai 1 (NPC 1) ---
            PlaceChair(job, cx - 5, fy - 1, st.Chair, 1);
            PlaceTableWithDecor(job, cx - 3, fy - 1, st.Table, 1);
            PlacePainting(job, cx - 3, fy - 5, st.Painting3x2);
            PlaceBookcase(job, cx + 3, fy - 1, st.Bookcase);       // Duduk di cx+2..cx+4 (padat)
            PlaceCeilingLight(job, cx, midFloorY + 1, st.Lantern, st.Torch);
            WorldGen.PlaceTile(cx + 6, fy - 5, TileID.Torches, true, true, -1, st.Torch);

            // --- Furnitur Lantai 2 (NPC 2 - 100% di atas lantai padat cx-6..cx+2!) ---
            PlaceChair(job, cx - 5, midFloorY - 1, st.Chair, 1);
            PlaceTableWithDecor(job, cx - 3, midFloorY - 1, st.Table, 2); // Vas bunga di atas meja
            PlacePainting(job, cx - 3, midFloorY - 5, st.Painting3x2);
            PlaceBookcase(job, cx + 1, midFloorY - 1, st.Bookcase); // Duduk di cx, cx+1, cx+2 (100% padat!)
            PlaceCeilingLight(job, cx - 1, ceilingY + 1, st.Lantern, st.Torch);
            WorldGen.PlaceTile(cx + 6, midFloorY - 5, TileID.Torches, true, true, -1, st.Torch);

            int width = (cx + 10) - (cx - 10) + 1;
            int height = deepest - topY + 1;
            TSPlayer.All.SendTileRect((short)(cx - 10), (short)topY, (byte)width, (byte)height);
        }

        // ===============================================================
        // VARIAN 3: VILLA / MANOR 3 KAMAR (3 NPC - Atap Sayap Bertingkat)
        // ===============================================================
        private void BuildHouse3Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 12;
            int right = cx + 12;      // Lebar total 25 (Kamar kiri: cx-11..cx-1, Kamar kanan: cx+1..cx+11)
            int midFloorY = fy - 7;   // Lantai 2 di fy-7
            int topLeft = cx - 8;
            int topRight = cx + 8;    // Kamar atas lebar interior 15 (cx-7..cx+7)
            int ceilingY = fy - 14;   // Plafon lt.2 di fy-14
            int topY = fy - 21;

            ClearAreaAndOutsideWalls(job, cx - 15, topY, cx + 15, fy - 1);
            int deepest = BuildSmartFoundation(job, left, right, fy, st.BaseBlock);

            // Lantai Dasar
            for (int x = left; x <= right; x++)
            {
                bool isTrim = (x <= left + 1 || x >= right - 1 || x == cx);
                PlaceBlock(job, x, fy, isTrim ? st.BaseBlock : st.MainBlock);
            }

            // Tembok Luar Kiri & Kanan Lantai 1 + Sekat Tengah Lantai 1
            for (int y = midFloorY; y < fy; y++)
            {
                PlaceBlock(job, left, y, st.MainBlock);
                PlaceBlock(job, right, y, st.MainBlock);
                PlaceBlock(job, cx, y, st.MainBlock);
            }

            // Lantai 2 (midFloorY):
            // cx-12..cx+2 PADAT | cx+3..cx+5 PLATFORM (tangga naik) | cx+6..cx+12 PADAT
            for (int x = left; x <= right; x++)
            {
                if (x >= cx + 3 && x <= cx + 5)
                    PlacePlatform(job, x, midFloorY, st.Platform);
                else
                    PlaceBlock(job, x, midFloorY, st.MainBlock);
            }

            // Tembok Kamar Atas (cx-8 & cx+8) + Plafon Atas
            for (int y = ceilingY; y < midFloorY; y++)
            {
                PlaceBlock(job, topLeft, y, st.MainBlock);
                PlaceBlock(job, topRight, y, st.MainBlock);
            }
            for (int x = topLeft; x <= topRight; x++)
                PlaceBlock(job, x, ceilingY, st.MainBlock);

            // Dinding Latar Interior Lantai 1 (Kiri & Kanan) + Lantai 2
            DecorateRoomWalls(left + 1, cx - 1, midFloorY + 1, fy - 1, cx - 7, cx - 5, st);
            DecorateRoomWalls(cx + 1, right - 1, midFloorY + 1, fy - 1, cx + 5, cx + 7, st);
            DecorateRoomWalls(topLeft + 1, topRight - 1, ceilingY + 1, midFloorY - 1, cx - 2, cx, st);

            // Pilar WoodenBeam dekoratif di sudut kamar atas
            for (int y = ceilingY + 1; y < midFloorY; y++)
            {
                PlaceBeam(job, topLeft + 1, y);
                PlaceBeam(job, topRight - 1, y);
            }

            // Atap Sayap Samping (kiri & kanan lantai 1) + Atap Utama Lantai 2 (tanpa platform melayang!)
            BuildWingRoof(job, left - 1, topLeft - 1, midFloorY - 1, true, st.RoofBlock, st.BaseBlock);
            BuildWingRoof(job, topRight + 1, right + 1, midFloorY - 1, false, st.RoofBlock, st.BaseBlock);
            BuildSleekRoof(job, cx, ceilingY - 1, 10, st.RoofBlock, st.BaseBlock, st.AccentWall);
            WorldGen.PlaceObject(cx + 4, ceilingY - 5, TileID.Chimney, true);

            // Pintu: Luar kiri, Tengah penghubung, Luar kanan
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, cx, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);

            // --- Kamar 1: Bawah Kiri (NPC 1) ---
            PlaceChair(job, cx - 10, fy - 1, st.Chair, 1);
            PlaceTableWithDecor(job, cx - 8, fy - 1, st.Table, 1);
            PlacePainting(job, cx - 8, fy - 5, st.Painting3x2);
            PlaceBookcase(job, cx - 3, fy - 1, st.Bookcase);       // Duduk di cx-4..cx-2 (padat)
            PlaceCeilingLight(job, cx - 6, midFloorY + 1, st.Lantern, st.Torch);

            // --- Kamar 2: Bawah Kanan (NPC 2) ---
            PlaceBookcase(job, cx + 3, fy - 1, st.Bookcase);       // Duduk di cx+2..cx+4 (padat)
            PlaceTableWithDecor(job, cx + 8, fy - 1, st.Table, 1);
            PlaceChair(job, cx + 10, fy - 1, st.Chair, -1);
            PlacePainting(job, cx + 8, fy - 5, st.Painting3x2);
            PlaceCeilingLight(job, cx + 6, midFloorY + 1, st.Lantern, st.Torch);

            // --- Kamar 3: Atas Tengah (NPC 3 - Bookcase di cx-1..cx+1 = 100% PADAT, anti-hancur!) ---
            PlaceChair(job, cx - 6, midFloorY - 1, st.Chair, 1);
            PlaceTableWithDecor(job, cx - 4, midFloorY - 1, st.Table, 2);
            PlacePainting(job, cx - 4, midFloorY - 5, st.Painting3x2);
            PlaceBookcase(job, cx, midFloorY - 1, st.Bookcase);     // Duduk di cx-1, cx, cx+1 (100% padat!)
            PlaceCeilingLight(job, cx - 1, ceilingY + 1, st.Lantern, st.Torch);
            WorldGen.PlaceTile(cx + 6, midFloorY - 5, TileID.Torches, true, true, -1, st.Torch);

            int width = (cx + 15) - (cx - 15) + 1;
            int height = deepest - topY + 1;
            TSPlayer.All.SendTileRect((short)(cx - 15), (short)topY, (byte)width, (byte)height);
        }

        // ---------------------------------------------------------------
        // Helper Konstruksi Rumah & Dekorasi
        // ---------------------------------------------------------------

        private static void DecorateRoomWalls(int x0, int x1, int y0, int y1, int winX0, int winX1, HouseStyle st)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    // Baris paling bawah (wainscoting) & bingkai sisi jendela pakai AccentWall
                    bool isAccent = (y == y1) || (x == winX0 - 1) || (x == winX1 + 1);
                    SetWall(x, y, isAccent ? st.AccentWall : st.MainWall);
                }
            }

            // Jendela kaca 3x3 di tengah
            for (int x = winX0; x <= winX1; x++)
            {
                for (int y = y1 - 4; y <= y1 - 2; y++)
                {
                    if (y >= y0 && x >= x0 && x <= x1)
                        SetWall(x, y, WallID.Glass);
                }
            }
        }

        /// <summary>
        /// Atap miring rendah 4-lapis dengan tritisan (eaves) yang di-slope atas & bawah agar ramping dan realistis.
        /// </summary>
        private static void BuildSleekRoof(BuildJob job, int cx, int baseRoofY, int halfSpan, ushort roofBlock, ushort trimBlock, ushort atticWall)
        {
            // Lapis 0 (tritisan paling bawah, lebar penuh dengan ujung bawah meruncing)
            int xL0 = cx - halfSpan;
            int xR0 = cx + halfSpan;
            for (int x = xL0; x <= xR0; x++)
                PlaceBlock(job, x, baseRoofY, roofBlock);
            SetSlope(xL0, baseRoofY, 4); // Miring bawah kiri
            SetSlope(xR0, baseRoofY, 3); // Miring bawah kanan

            // Lapis 1..3 (atap miring bertingkat, bagian atas datar agar bisa dipasangi Chimney)
            for (int step = 1; step <= 3; step++)
            {
                int y = baseRoofY - step;
                int shrink = (step == 1) ? 1 : (step == 2 ? 3 : 5);
                int xL = cx - halfSpan + shrink;
                int xR = cx + halfSpan - shrink;

                // Isi dinding loteng di bagian dalam saja
                for (int wx = xL + 1; wx <= xR - 1; wx++)
                    SetWall(wx, y, atticWall);

                for (int x = xL; x <= xR; x++)
                {
                    ushort mat = (step == 3 && (x == xL || x == xR)) ? trimBlock : roofBlock;
                    PlaceBlock(job, x, y, mat);
                }

                SetSlope(xL, y, 2); // Miring atas kiri (/)
                SetSlope(xR, y, 1); // Miring atas kanan (\)
            }
        }

        /// <summary>Atap sayap miring untuk sisi kiri/kanan pada rumah ukuran 3.</summary>
        private static void BuildWingRoof(BuildJob job, int outerX, int innerX, int baseY, bool slopeUpRight, ushort roofBlock, ushort trimBlock)
        {
            // Baris bawah sayap
            for (int x = outerX; x <= innerX; x++)
                PlaceBlock(job, x, baseY, roofBlock);

            if (slopeUpRight)
            {
                SetSlope(outerX, baseY, 4);
                for (int x = outerX + 1; x <= innerX; x++)
                    PlaceBlock(job, x, baseY - 1, (x == innerX) ? trimBlock : roofBlock);
                SetSlope(outerX + 1, baseY - 1, 2);

                for (int x = outerX + 3; x <= innerX; x++)
                    PlaceBlock(job, x, baseY - 2, roofBlock);
                SetSlope(outerX + 3, baseY - 2, 2);
            }
            else
            {
                SetSlope(innerX, baseY, 3);
                for (int x = outerX; x <= innerX - 1; x++)
                    PlaceBlock(job, x, baseY - 1, (x == outerX) ? trimBlock : roofBlock);
                SetSlope(innerX - 1, baseY - 1, 1);

                for (int x = outerX; x <= innerX - 3; x++)
                    PlaceBlock(job, x, baseY - 2, roofBlock);
                SetSlope(innerX - 3, baseY - 2, 1);
            }
        }

        private static void PlaceChair(BuildJob job, int x, int y, int style, int dir)
        {
            if (!WorldGen.PlaceObject(x, y, TileID.Chairs, true, style, 0, -1, dir))
                job.ObjectFailed++;
        }

        private static void PlaceTableWithDecor(BuildJob job, int x, int y, int tableStyle, int bottleStyle)
        {
            if (!WorldGen.PlaceObject(x, y, TileID.Tables, true, tableStyle))
                job.ObjectFailed++;
            else
                WorldGen.PlaceTile(x, y - 2, TileID.Bottles, true, true, -1, bottleStyle);
        }

        private static void PlaceBookcase(BuildJob job, int x, int y, int style)
        {
            if (!WorldGen.PlaceObject(x, y, TileID.Bookcases, true, style))
                job.ObjectFailed++;
        }

        private static void PlacePainting(BuildJob job, int x, int y, int style)
        {
            if (!WorldGen.PlaceObject(x, y, TileID.Painting3X2, true, style))
                job.ObjectFailed++;
        }

        private static void PlaceCeilingLight(BuildJob job, int x, int y, int lanternStyle, int fallbackTorch)
        {
            if (!WorldGen.PlaceObject(x, y, TileID.HangingLanterns, true, lanternStyle))
            {
                if (!WorldGen.PlaceTile(x, y, TileID.Torches, true, true, -1, fallbackTorch))
                    job.ObjectFailed++;
            }
        }

        private static void PlaceBeam(BuildJob job, int x, int y)
        {
            ClearTile(job, x, y);
            if (WorldGen.PlaceTile(x, y, TileID.WoodenBeam, true, true, -1, 0))
                WorldGen.SquareTileFrame(x, y, true);
        }

        private static void ClearAreaAndOutsideWalls(BuildJob job, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    ClearTile(job, x, y);
                    var t = Main.tile[x, y];
                    if (t.wall != 0)
                    {
                        t.wall = 0;
                        WorldGen.SquareWallFrame(x, y, true);
                    }
                }
            }
        }

        private static int BuildSmartFoundation(BuildJob job, int left, int right, int fy, ushort baseBlock)
        {
            int deepest = fy;

            for (int x = left; x <= right; x++)
            {
                var t = Main.tile[x, fy + 1];
                if (!t.active() || !Main.tileSolid[t.type])
                {
                    PlaceBlock(job, x, fy + 1, baseBlock);
                    if (fy + 1 > deepest) deepest = fy + 1;
                }
            }

            int cx = (left + right) / 2;
            int[] pillars = { left, left + 1, cx, right - 1, right };

            foreach (int px in pillars)
            {
                for (int y = fy + 2; y <= fy + FoundationMax; y++)
                {
                    var t = Main.tile[px, y];
                    if (t.active() && Main.tileSolid[t.type])
                        break;

                    PlaceBlock(job, px, y, baseBlock);
                    if (y > deepest) deepest = y;
                }
            }

            return deepest;
        }

        private static void PlaceHouseDoor(BuildJob job, int x, int bottomY, int doorStyle)
        {
            for (int y = bottomY - 2; y <= bottomY; y++)
                ClearTile(job, x, y);

            if (!WorldGen.PlaceDoor(x, bottomY - 1, TileID.ClosedDoor, doorStyle))
                job.ObjectFailed++;
        }

        private static void PlacePlatform(BuildJob job, int x, int y, int style)
        {
            ClearTile(job, x, y);
            if (WorldGen.PlaceTile(x, y, TileID.Platforms, true, true, -1, style))
                WorldGen.SquareTileFrame(x, y, true);
            else
                job.ObjectFailed++;
        }

        private static void SetWall(int x, int y, ushort wallType)
        {
            var t = Main.tile[x, y];
            t.wall = wallType;
            WorldGen.SquareWallFrame(x, y, true);
        }

        private static void SetSlope(int x, int y, byte slopeId)
        {
            var t = Main.tile[x, y];
            if (t.active())
            {
                t.halfBrick(false);
                t.slope(slopeId);
                WorldGen.SquareTileFrame(x, y, true);
            }
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
        }

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
