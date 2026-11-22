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
        public override string Description => "Pilih titik pakai Ice Rod dan bangun struktur otomatis.";
        public override Version Version => new Version(0, 7, 0, 0);

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
            public ushort MainBlock;    // Blok utama tembok/lantai atas
            public ushort BaseBlock;    // Blok kombinasi (fondasi, pilar sudut, trim atap)
            public ushort RoofBlock;    // Blok atap miring
            public ushort MainWall;     // Dinding utama ruangan
            public ushort AccentWall;   // Dinding aksen/wainscoting bagian bawah & pilar
            public int Door;
            public int Table;
            public int Chair;
            public int Bookcase;
            public int Platform;
            public int Lantern;
            public int Torch;
            public int Painting3x2;
            public int Painting2x3;

            public HouseStyle(
                string name,
                ushort mainBlock, ushort baseBlock, ushort roofBlock,
                ushort mainWall, ushort accentWall,
                int door, int table, int chair, int bookcase,
                int platform, int lantern, int torch,
                int painting3x2, int painting2x3)
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
                Painting2x3 = painting2x3;
            }
        }

        private class HouseSpec
        {
            public int X;
            public int Y;
            public int Size; // 1, 2, atau 3 NPC
            public HouseStyle Style;
        }

        // Semua ID style sudah dicocokkan dengan sprite sheet Terraria 1.4.4+
        // Bookcase (Tile 101): 0=Wood, 1=BlueDungeon, 2=GreenDungeon, 3=PinkDungeon, 4=Obsidian, 5=Glass, 6=Golden, 7=Honey, 8=Steampunk, 9=Pumpkin, 10=Spooky, 11=Pine, 12=Frozen, 13=Dynasty, 14=LivingWood, 15=Shadewood, 16=Boreal, 17=Slime, 18=Bone, 19=Cactus, 20=Flesh, 21=Mushroom, 22=Skyware, 23=Lihzahrd, 24=Martian, 25=Granite, 26=Marble, 27=Crystal, 28=Meteorite, 29=RichMahogany, 30=Ebonwood, 31=Pearlwood, 32=Sandstone
        // Painting3X2 (Tile 242) & Painting2X3 (Tile 245): semua style 0..15 adalah lukisan murni (bukan trophy)
        private static readonly Dictionary<string, HouseStyle> HouseStyles = new Dictionary<string, HouseStyle>
        {
            ["forest"] = new HouseStyle(
                "Forest",
                TileID.WoodBlock, TileID.GrayBrick, TileID.RedBrick,
                WallID.Wood, WallID.Planked,
                door: 0, table: 0, chair: 0, bookcase: 0,
                platform: 0, lantern: 0, torch: 0,
                painting3x2: 0, painting2x3: 1),

            ["snow"] = new HouseStyle(
                "Snow",
                TileID.BorealWood, TileID.SnowBrick, TileID.IceBrick,
                WallID.BorealWood, WallID.SnowBrick,
                door: 30, table: 28, chair: 30, bookcase: 16,
                platform: 19, lantern: 26, torch: 9,
                painting3x2: 2, painting2x3: 3),

            ["desert"] = new HouseStyle(
                "Desert",
                TileID.SmoothSandstone, TileID.SandstoneBrick, TileID.RedSandstoneBrick,
                WallID.SmoothSandstone, WallID.SandstoneBrick,
                door: 4, table: 30, chair: 6, bookcase: 32,
                platform: 42, lantern: 36, torch: 16,
                painting3x2: 4, painting2x3: 5),

            ["jungle"] = new HouseStyle(
                "Jungle",
                TileID.RichMahogany, TileID.Mudstone, TileID.IridescentBrick,
                WallID.RichMaogany, WallID.MudstoneBrick,
                door: 2, table: 2, chair: 3, bookcase: 29,
                platform: 2, lantern: 20, torch: 21,
                painting3x2: 6, painting2x3: 7),

            ["corruption"] = new HouseStyle(
                "Corruption",
                TileID.Ebonwood, TileID.EbonstoneBrick, TileID.DemoniteBrick,
                WallID.Ebonwood, WallID.EbonstoneBrick,
                door: 1, table: 1, chair: 2, bookcase: 30,
                platform: 1, lantern: 17, torch: 18,
                painting3x2: 8, painting2x3: 9),

            ["crimson"] = new HouseStyle(
                "Crimson",
                TileID.Shadewood, TileID.CrimstoneBrick, TileID.CrimtaneBrick,
                WallID.Shadewood, WallID.CrimstoneBrick,
                door: 10, table: 8, chair: 11, bookcase: 15,
                platform: 11, lantern: 22, torch: 19,
                painting3x2: 10, painting2x3: 11),

            ["hallow"] = new HouseStyle(
                "Hallow",
                TileID.Pearlwood, TileID.PearlstoneBrick, TileID.CrystalBlock,
                WallID.Pearlwood, WallID.PearlstoneBrick,
                door: 3, table: 3, chair: 4, bookcase: 31,
                platform: 3, lantern: 21, torch: 20,
                painting3x2: 12, painting2x3: 13),

            ["underworld"] = new HouseStyle(
                "Underworld",
                TileID.ObsidianBrick, TileID.HellstoneBrick, TileID.AshWood,
                WallID.ObsidianBrick, WallID.HellstoneBrick,
                door: 19, table: 13, chair: 16, bookcase: 4,
                platform: 13, lantern: 14, torch: 7,
                painting3x2: 14, painting2x3: 15),

            ["cavern"] = new HouseStyle(
                "Cavern",
                TileID.StoneSlab, TileID.GrayBrick, TileID.TinBrick,
                WallID.StoneSlab, WallID.GrayBrick,
                door: 0, table: 0, chair: 0, bookcase: 0,
                platform: 0, lantern: 12, torch: 0,
                painting3x2: 1, painting2x3: 2),
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
        // Rumah NPC (UPGRADED: 3 Ukuran + Arsitektur Bervariasi + Anti-Overwall)
        // ---------------------------------------------------------------

        private static void GetHouseBounds(int size, out int halfWidth, out int heightAbove)
        {
            switch (size)
            {
                case 2: // 2 lantai vertikal (2 NPC): lebar interior 11 (total 13 + over-roof 15), tinggi 18
                    halfWidth = 8;
                    heightAbove = 18;
                    break;
                case 3: // Manor 3 kamar (3 NPC): lebar interior 21 (total 23 + over-roof 25), tinggi 19
                    halfWidth = 13;
                    heightAbove = 19;
                    break;
                default: // Cottage 1 NPC: lebar interior 11 (total 13 + over-roof 15), tinggi 12
                    halfWidth = 8;
                    heightAbove = 12;
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

            // Parse parameter fleksibel: bisa /build npchouse 2 snow atau /build npchouse snow 2
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
        // VARIAN 1: COTTAGE 1 NPC (Atap Pelana Miring + Cerobong + Jendela)
        // ===============================================================
        private void BuildHouse1Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 6;   // Tembok kiri di cx-6
            int right = cx + 6;  // Tembok kanan di cx+6 (interior cx-5..cx+5 = 11 lebar)
            int ceilingY = fy - 6; // Plafon di fy-6 (interior fy-5..fy-1 = 5 tinggi)
            int roofPeakY = fy - 11;

            // 1. Bersihkan area atas secara presisi (hanya area bangunan & atap, TIDAK menghapus dinding luar)
            ClearAreaAndOutsideWalls(job, cx - 8, roofPeakY - 1, cx + 8, fy - 1);

            // 2. Fondasi cerdas: 1 baris batu di bawah lantai + hanya 3 pilar penyangga jika menggantung (bukan kotak penuh!)
            int deepest = BuildSmartFoundation(job, left, right, fy, st.BaseBlock);

            // 3. Lantai kombinasi (Batu di pinggir, Kayu/Utama di tengah)
            for (int x = left; x <= right; x++)
            {
                ushort floorMat = (x <= left + 1 || x >= right - 1) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, x, fy, floorMat);
            }

            // 4. Tembok Kiri & Kanan (Kombinasi batu di bawah, kayu di atas) & Plafon
            for (int y = ceilingY; y < fy; y++)
            {
                ushort wallMat = (y >= fy - 2) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, left, y, wallMat);
                PlaceBlock(job, right, y, wallMat);
            }
            for (int x = left - 1; x <= right + 1; x++)
            {
                PlaceBlock(job, x, ceilingY, st.MainBlock);
            }

            // 5. Dinding latar HANYA di dalam interior (left+1 .. right-1) -> anti overwall!
            for (int y = ceilingY + 1; y <= fy - 1; y++)
            {
                for (int x = left + 1; x <= right - 1; x++)
                {
                    // Baris paling bawah pakai AccentWall (wainscoting), sisanya MainWall
                    ushort w = (y == fy - 1) ? st.AccentWall : st.MainWall;
                    SetWall(x, y, w);
                }
            }

            // Jendela kaca di tengah (3x2)
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                SetWall(x, fy - 4, WallID.Glass);
                SetWall(x, fy - 3, WallID.Glass);
            }

            // 6. Atap Pelana Segitiga Miring (Gable Roof) + Loteng (Attic)
            BuildGableRoof(job, cx, ceilingY - 1, 7, st.RoofBlock, st.MainBlock, st.AccentWall);

            // Cerobong asap di sisi kanan atap
            PlaceBlock(job, cx + 4, ceilingY - 4, st.BaseBlock);
            PlaceBlock(job, cx + 4, ceilingY - 5, st.BaseBlock);
            PlaceBlock(job, cx + 4, ceilingY - 6, st.BaseBlock);

            // 7. Pintu kiri & kanan
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);

            // 8. Furnitur & Dekorasi Valid (Tanpa Trophy, Rak Buku Sesuai Biome)
            // Kiri: Meja & Kursi
            if (!WorldGen.PlaceObject(cx - 3, fy - 1, TileID.Tables, true, st.Table))
                job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 5, fy - 1, TileID.Chairs, true, st.Chair, 0, -1, 1))
                job.ObjectFailed++;

            // Kanan: Rak Buku sesuai biome
            if (!WorldGen.PlaceObject(cx + 3, fy - 1, TileID.Bookcases, true, st.Bookcase))
                job.ObjectFailed++;

            // Lukisan asli (3x2) di atas meja/jendela samping
            if (!WorldGen.PlaceObject(cx - 3, fy - 4, TileID.Painting3X2, true, st.Painting3x2))
                job.ObjectFailed++;

            // Penerangan: Lentera gantung di tengah + Obor biome di dekat pintu
            if (!WorldGen.PlaceObject(cx, ceilingY + 1, TileID.HangingLanterns, true, st.Lantern))
            {
                if (!WorldGen.PlaceTile(cx, fy - 5, TileID.Torches, true, true, -1, st.Torch))
                    job.ObjectFailed++;
            }
            WorldGen.PlaceTile(cx - 5, fy - 4, TileID.Torches, true, true, -1, st.Torch);
            WorldGen.PlaceTile(cx + 5, fy - 4, TileID.Torches, true, true, -1, st.Torch);

            // Kirim update rect ke semua player
            int width = (cx + 8) - (cx - 8) + 1;
            int height = deepest - (roofPeakY - 1) + 1;
            TSPlayer.All.SendTileRect((short)(cx - 8), (short)(roofPeakY - 1), (byte)width, (byte)height);
        }

        // ===============================================================
        // VARIAN 2: DUPLEX 2 LANTAI (2 NPC - Bertingkat + Tangga Platform)
        // ===============================================================
        private void BuildHouse2Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 6;
            int right = cx + 6;       // Interior lebar 11 (cx-5..cx+5)
            int midFloorY = fy - 6;   // Lantai 2 di fy-6 (interior lt.1: fy-5..fy-1)
            int ceilingY = fy - 12;   // Plafon lt.2 di fy-12 (interior lt.2: fy-11..fy-7)
            int roofPeakY = fy - 17;

            ClearAreaAndOutsideWalls(job, cx - 8, roofPeakY - 1, cx + 8, fy - 1);
            int deepest = BuildSmartFoundation(job, left, right, fy, st.BaseBlock);

            // Lantai Dasar (Batu di pinggir, Utama di tengah)
            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, fy, (x <= left + 1 || x >= right - 1) ? st.BaseBlock : st.MainBlock);

            // Sekat Lantai 2 (dengan lubang akses tangga platform di kanan: cx+2..cx+4)
            for (int x = left; x <= right; x++)
            {
                if (x >= cx + 2 && x <= cx + 4)
                    PlacePlatform(job, x, midFloorY, st.Platform);
                else
                    PlaceBlock(job, x, midFloorY, st.MainBlock);
            }

            // Plafon Atas
            for (int x = left - 1; x <= right + 1; x++)
                PlaceBlock(job, x, ceilingY, st.MainBlock);

            // Tembok Luar Kiri & Kanan
            for (int y = ceilingY; y < fy; y++)
            {
                ushort mat = (y >= fy - 2 || y == midFloorY) ? st.BaseBlock : st.MainBlock;
                PlaceBlock(job, left, y, mat);
                PlaceBlock(job, right, y, mat);
            }

            // Dinding Latar Interior Lantai 1 & Lantai 2 (Presisi di dalam tembok)
            for (int y = ceilingY + 1; y <= fy - 1; y++)
            {
                if (y == midFloorY) continue;
                bool isBottomRow = (y == fy - 1 || y == midFloorY - 1);
                for (int x = left + 1; x <= right - 1; x++)
                {
                    SetWall(x, y, isBottomRow ? st.AccentWall : st.MainWall);
                }
            }

            // Jendela Kaca Lantai 1 & Lantai 2
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                SetWall(x, fy - 4, WallID.Glass);
                SetWall(x, fy - 3, WallID.Glass);
                SetWall(x, midFloorY - 4, WallID.Glass);
                SetWall(x, midFloorY - 3, WallID.Glass);
            }

            // Atap Miring
            BuildGableRoof(job, cx, ceilingY - 1, 7, st.RoofBlock, st.MainBlock, st.AccentWall);

            // Pintu Kiri & Kanan di Lantai 1
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);

            // --- Furnitur Lantai 1 (NPC 1) ---
            if (!WorldGen.PlaceObject(cx - 3, fy - 1, TileID.Tables, true, st.Table)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 5, fy - 1, TileID.Chairs, true, st.Chair, 0, -1, 1)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx, fy - 1, TileID.Bookcases, true, st.Bookcase)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 3, fy - 4, TileID.Painting2X3, true, st.Painting2x3)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx, midFloorY + 1, TileID.HangingLanterns, true, st.Lantern))
                WorldGen.PlaceTile(cx - 5, fy - 4, TileID.Torches, true, true, -1, st.Torch);

            // --- Furnitur Lantai 2 (NPC 2) ---
            if (!WorldGen.PlaceObject(cx - 3, midFloorY - 1, TileID.Tables, true, st.Table)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 5, midFloorY - 1, TileID.Chairs, true, st.Chair, 0, -1, 1)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 3, midFloorY - 1, TileID.Bookcases, true, st.Bookcase)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 3, midFloorY - 4, TileID.Painting3X2, true, st.Painting3x2)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx, ceilingY + 1, TileID.HangingLanterns, true, st.Lantern))
                WorldGen.PlaceTile(cx - 5, midFloorY - 4, TileID.Torches, true, true, -1, st.Torch);

            int width = (cx + 8) - (cx - 8) + 1;
            int height = deepest - (roofPeakY - 1) + 1;
            TSPlayer.All.SendTileRect((short)(cx - 8), (short)(roofPeakY - 1), (byte)width, (byte)height);
        }

        // ===============================================================
        // VARIAN 3: MANOR 3 KAMAR (3 NPC - 2 Kamar Bawah + 1 Kamar Atas)
        // ===============================================================
        private void BuildHouse3Npc(BuildJob job)
        {
            HouseStyle st = job.Style;
            int cx = job.X;
            int fy = job.StartY;
            int left = cx - 11;
            int right = cx + 11;      // Lebar total 23 (interior 21: dibagi 2 kamar @10 tile + 1 sekat tengah)
            int midFloorY = fy - 6;   // Lantai 2 di fy-6
            int ceilingY = fy - 12;   // Plafon lt.2 di fy-12 (atap hanya selebar cx-7..cx+7 agar bertingkat)
            int roofPeakY = fy - 18;

            ClearAreaAndOutsideWalls(job, cx - 13, roofPeakY - 1, cx + 13, fy - 1);
            int deepest = BuildSmartFoundation(job, left, right, fy, st.BaseBlock);

            // Lantai Dasar
            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, fy, (x <= left + 1 || x >= right - 1 || x == cx) ? st.BaseBlock : st.MainBlock);

            // Dinding Luar Kiri & Kanan Lantai 1 + Sekat Tengah Lantai 1
            for (int y = midFloorY; y < fy; y++)
            {
                PlaceBlock(job, left, y, st.MainBlock);
                PlaceBlock(job, right, y, st.MainBlock);
                PlaceBlock(job, cx, y, st.MainBlock); // Sekat pemisah kamar kiri & kanan
            }

            // Lantai 2 (Full dari left-1 sampai right+1 sebagai balkon/teras atap samping, dengan akses platform)
            for (int x = left - 1; x <= right + 1; x++)
            {
                if ((x >= cx - 4 && x <= cx - 2) || (x >= cx + 2 && x <= cx + 4))
                    PlacePlatform(job, x, midFloorY, st.Platform);
                else
                    PlaceBlock(job, x, midFloorY, st.MainBlock);
            }

            // Struktur Kamar Lantai 2 (lebih ramping di tengah: cx-7 .. cx+7 biar ada balkon samping!)
            int topLeft = cx - 7;
            int topRight = cx + 7;
            for (int y = ceilingY; y < midFloorY; y++)
            {
                PlaceBlock(job, topLeft, y, st.MainBlock);
                PlaceBlock(job, topRight, y, st.MainBlock);
            }
            for (int x = topLeft - 1; x <= topRight + 1; x++)
                PlaceBlock(job, x, ceilingY, st.MainBlock);

            // Pagar balkon teras kiri & kanan di lantai 2 pakai platform
            PlacePlatform(job, left, midFloorY - 1, st.Platform);
            PlacePlatform(job, right, midFloorY - 1, st.Platform);

            // Dinding Latar Interior Lantai 1 (Kiri: left+1..cx-1, Kanan: cx+1..right-1)
            for (int y = midFloorY + 1; y <= fy - 1; y++)
            {
                ushort w = (y == fy - 1) ? st.AccentWall : st.MainWall;
                for (int x = left + 1; x <= cx - 1; x++) SetWall(x, y, w);
                for (int x = cx + 1; x <= right - 1; x++) SetWall(x, y, w);
            }

            // Dinding Latar Interior Lantai 2 (topLeft+1 .. topRight-1)
            for (int y = ceilingY + 1; y <= midFloorY - 1; y++)
            {
                ushort w = (y == midFloorY - 1) ? st.AccentWall : st.MainWall;
                for (int x = topLeft + 1; x <= topRight - 1; x++) SetWall(x, y, w);
            }

            // Jendela kaca di ketiga kamar
            for (int dx = -1; dx <= 1; dx++)
            {
                SetWall(cx - 5 + dx, fy - 4, WallID.Glass);
                SetWall(cx - 5 + dx, fy - 3, WallID.Glass);

                SetWall(cx + 5 + dx, fy - 4, WallID.Glass);
                SetWall(cx + 5 + dx, fy - 3, WallID.Glass);

                SetWall(cx + dx, midFloorY - 4, WallID.Glass);
                SetWall(cx + dx, midFloorY - 3, WallID.Glass);
            }

            // Atap Miring di atas Lantai 2
            BuildGableRoof(job, cx, ceilingY - 1, 8, st.RoofBlock, st.MainBlock, st.AccentWall);

            // Pintu: 2 pintu luar lantai 1, 1 pintu penghubung tengah lantai 1, 2 pintu balkon lantai 2
            PlaceHouseDoor(job, left, fy - 1, st.Door);
            PlaceHouseDoor(job, right, fy - 1, st.Door);
            PlaceHouseDoor(job, cx, fy - 1, st.Door);
            PlaceHouseDoor(job, topLeft, midFloorY - 1, st.Door);
            PlaceHouseDoor(job, topRight, midFloorY - 1, st.Door);

            // --- Kamar 1 (Bawah Kiri - NPC 1) ---
            if (!WorldGen.PlaceObject(cx - 7, fy - 1, TileID.Tables, true, st.Table)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 9, fy - 1, TileID.Chairs, true, st.Chair, 0, -1, 1)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 3, fy - 1, TileID.Bookcases, true, st.Bookcase)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 7, fy - 4, TileID.Painting2X3, true, st.Painting2x3)) job.ObjectFailed++;
            WorldGen.PlaceObject(cx - 5, midFloorY + 1, TileID.HangingLanterns, true, st.Lantern);
            WorldGen.PlaceTile(cx - 9, fy - 4, TileID.Torches, true, true, -1, st.Torch);

            // --- Kamar 2 (Bawah Kanan - NPC 2) ---
            if (!WorldGen.PlaceObject(cx + 7, fy - 1, TileID.Tables, true, st.Table)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 9, fy - 1, TileID.Chairs, true, st.Chair, 0, -1, -1)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 3, fy - 1, TileID.Bookcases, true, st.Bookcase)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 7, fy - 4, TileID.Painting2X3, true, st.Painting2x3)) job.ObjectFailed++;
            WorldGen.PlaceObject(cx + 5, midFloorY + 1, TileID.HangingLanterns, true, st.Lantern);
            WorldGen.PlaceTile(cx + 9, fy - 4, TileID.Torches, true, true, -1, st.Torch);

            // --- Kamar 3 (Atas Tengah - NPC 3) ---
            if (!WorldGen.PlaceObject(cx - 3, midFloorY - 1, TileID.Tables, true, st.Table)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 5, midFloorY - 1, TileID.Chairs, true, st.Chair, 0, -1, 1)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx + 4, midFloorY - 1, TileID.Bookcases, true, st.Bookcase)) job.ObjectFailed++;
            if (!WorldGen.PlaceObject(cx - 3, midFloorY - 4, TileID.Painting3X2, true, st.Painting3x2)) job.ObjectFailed++;
            WorldGen.PlaceObject(cx, ceilingY + 1, TileID.HangingLanterns, true, st.Lantern);
            WorldGen.PlaceTile(cx + 5, midFloorY - 4, TileID.Torches, true, true, -1, st.Torch);

            int width = (cx + 13) - (cx - 13) + 1;
            int height = deepest - (roofPeakY - 1) + 1;
            TSPlayer.All.SendTileRect((short)(cx - 13), (short)(roofPeakY - 1), (byte)width, (byte)height);
        }

        // ---------------------------------------------------------------
        // Helper Konstruksi Rumah
        // ---------------------------------------------------------------

        /// <summary>Bersihkan blok dan dinding lama di area atas rumah supaya dinding tidak bocor/over.</summary>
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

        /// <summary>
        /// Fondasi pintar: buat 1 baris sub-lantai di fy+1, dan hanya bangun pilar penyangga vertikal
        /// di ujung kiri, ujung kanan, dan tengah apabila lantai menggantung di udara (tidak penuh kotak!).
        /// </summary>
        private static int BuildSmartFoundation(BuildJob job, int left, int right, int fy, ushort baseBlock)
        {
            int deepest = fy;

            // 1 lapis sub-lantai batu di fy+1 hanya di titik yang kosong (agar rumah tidak melayang)
            for (int x = left; x <= right; x++)
            {
                var t = Main.tile[x, fy + 1];
                if (!t.active() || !Main.tileSolid[t.type])
                {
                    PlaceBlock(job, x, fy + 1, baseBlock);
                    if (fy + 1 > deepest) deepest = fy + 1;
                }
            }

            // Pilar penyangga vertikal di sisi kiri (left, left+1), kanan (right-1, right), dan tengah (cx)
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

        /// <summary>Bangun atap segitiga miring (sloped roof) dengan loteng berisi dinding aksen.</summary>
        private static void BuildGableRoof(BuildJob job, int cx, int baseRoofY, int halfSpan, ushort roofBlock, ushort trimBlock, ushort atticWall)
        {
            for (int step = 0; step <= halfSpan; step++)
            {
                int y = baseRoofY - step;
                int xL = cx - halfSpan + step;
                int xR = cx + halfSpan - step;

                if (xL == xR)
                {
                    // Puncak atap
                    PlaceBlock(job, xL, y, roofBlock);
                    PlaceBlock(job, xL, y - 1, trimBlock);
                    break;
                }

                // Isi dinding loteng HANYA di bagian dalam antara xL+1 .. xR-1
                for (int wx = xL + 1; wx <= xR - 1; wx++)
                {
                    SetWall(wx, y, atticWall);
                }

                // Pasang blok atap kiri & kanan lalu miringkan (slope)
                PlaceBlock(job, xL, y, roofBlock);
                SetSlope(xL, y, 2); // Miring ke kiri atas

                PlaceBlock(job, xR, y, roofBlock);
                SetSlope(xR, y, 1); // Miring ke kanan atas

                // Tambah blok penguat tepat di bawah genteng miring agar tidak bolong diagonal
                if (xL + 1 < xR)
                    PlaceBlock(job, xL + 1, y, trimBlock);
                if (xR - 1 > xL + 1)
                    PlaceBlock(job, xR - 1, y, trimBlock);
            }
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
