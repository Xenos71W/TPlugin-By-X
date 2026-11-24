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
        public override Version Version => new Version(1, 3, 3, 0);
        public override string Description => "Pilih titik pakai Ice Rod dan bangun struktur otomatis.";

        private const string UsePermission = "magicbuilder.use";
        private const string BuildPermission = "magicbuilder.build";
        private const string DebugPermission = "magicbuilder.debug";

        // --- Pemilih titik & Toko Demolitionist ---
        private const int IceRodItemType = 496;
        private const int IceBlockProjectile = 80;
        private const int IceBlockTileType = 127;
        private const ushort MarkerTileType = 8;
        private const int MaxPoints = 8;
        private const int DemolitionistShopIndex = 4;
        private const int ShopSyncIntervalTicks = 5;
        private const int ShopOverridePacketId = 104;
        private const int NpcTalkPacketId = 40;

        // --- Hellevator (TIDAK DIUBAH) ---
        private const ushort BrickTileType = 38;
        private const ushort WallType = 5;
        private const int TorchTileType = 4;
        private const int TorchStyle = 0;
        private const int RopeTileType = 213;
        private const int TorchInterval = 6;
        private const int RowsPerTick = 8;
        private const int ConfirmSeconds = 60;

        // --- Rumah NPC (11 lebar x 7 tinggi per lantai - TIDAK DIUBAH) ---
        private const int HouseHalfWidth = 5; // 5 + 1 + 5 = 11 tile
        private const int FloorStepHeight = 6; // +1 lantai dasar = 7 tile per tingkat

        // --- Boss Arena (TIDAK DIUBAH) ---
        private const int ArenaColsPerTick = 12;
        private const int ArenaMinWidth = 20;
        private const int ArenaMaxWidth = 600;
        private const int ArenaMinSpacing = 8;
        private const int ArenaMaxSpacing = 40;
        private const int ArenaDefaultTiers = 3;
        private const int ArenaBuffInterval = 70;
        private const int ArenaTorchInterval = 14;

        private const ushort BastStatueTileType = 506;
        private const int StarInBottleStyle = 7;
        private const int HeartLanternStyle = 9;

        // --- Fishing Pond (25 x 13 = 325 tile cairan -> 100% Fishing Power - TIDAK DIUBAH) ---
        private const int PondInnerHalfWidth = 12; // 12 + 1 + 12 = 25 tile lebar dalam
        private const int PondLiquidDepth = 13;    // 25 x 13 = 325 tile cairan
        private const int PondRoomHeight = 5;      // 5 tile tinggi ruang mancing di atas jembatan

        private static readonly HashSet<int> DebugPackets = new HashSet<int> { 17, 27, 29 };

        private struct PreviewBox
        {
            public int X0;
            public int Y0;
            public int W;
            public int H;

            public PreviewBox(int x0, int y0, int w, int h)
            {
                X0 = x0;
                Y0 = y0;
                W = w;
                H = h;
            }
        }

        private struct TileBackup
        {
            public bool Active;
            public ushort Type;
            public ushort Wall;
            public byte Liquid;
            public byte LiquidType;
            public short FrameX;
            public short FrameY;
            public bool HalfBrick;
            public byte Slope;
            public byte Color;
            public byte WallColor;
        }

        private class Session
        {
            public bool Selecting;
            public readonly List<(int X, int Y)> Points = new List<(int X, int Y)>();

            public bool HasPending;
            public string PendingKind;
            public List<HouseSpec> PendingHouses;
            public ArenaSpec PendingArena;
            public PondSpec PendingPond;
            public int PendingX;
            public int PendingStartY;
            public int PendingEndY;
            public DateTime PendingExpires;
            public readonly List<PreviewBox> PreviewBoxes = new List<PreviewBox>();
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
            public int Campfire;

            public HouseStyle(string name, ushort block, ushort wall, int platform, ushort tableTile, int table, int chair, int torch, int campfire)
            {
                Name = name;
                Block = block;
                Wall = wall;
                Platform = platform;
                TableTile = tableTile;
                Table = table;
                Chair = chair;
                Torch = torch;
                Campfire = campfire;
            }
        }

        private class HouseSpec
        {
            public int X;
            public int Y;
            public int Floors;
            public HouseStyle Style;
        }

        private class ArenaSpec
        {
            public int LeftX;
            public int RightX;
            public int TopY;
            public int BottomY;
            public int Spacing;
            public List<int> TierYs;
            public bool UseAsphalt;
            public HouseStyle Style;
        }

        private class PondSpec
        {
            public int X;
            public int Y;
            public byte LiquidType; // 0 = Water, 1 = Lava, 2 = Honey
            public string LiquidName;
            public HouseStyle Style;
        }

        private static readonly Dictionary<string, HouseStyle> HouseStyles = new Dictionary<string, HouseStyle>
        {
            ["forest"]     = new HouseStyle("Forest",     TileID.WoodBlock,       WallID.Wood,            0,  TileID.Tables,  0,  0,  0,  0),
            ["snow"]       = new HouseStyle("Snow",       TileID.BorealWood,      WallID.BorealWood,      19, TileID.Tables,  28, 30, 9,  3),
            ["desert"]     = new HouseStyle("Desert",     TileID.SmoothSandstone, WallID.SmoothSandstone, 42, TileID.Tables2, 1,  39, 16, 11),
            ["jungle"]     = new HouseStyle("Jungle",     TileID.RichMahogany,    WallID.RichMaogany,     2,  TileID.Tables,  2,  2,  21, 13),
            ["ocean"]      = new HouseStyle("Ocean",      TileID.PalmWood,        WallID.PalmWood,        17, TileID.Tables,  26, 29, 23, 8),
            ["hallow"]     = new HouseStyle("Hallow",     TileID.Pearlwood,       WallID.Pearlwood,       3,  TileID.Tables,  3,  3,  20, 12),
            ["cavern"]     = new HouseStyle("Cavern",     TileID.GrayBrick,       WallID.GrayBrick,       0,  TileID.Tables,  0,  0,  0,  0),
            ["underworld"] = new HouseStyle("Underworld", TileID.ObsidianBrick,   WallID.ObsidianBrick,   13, TileID.Tables,  13, 16, 7,  2),
        };

        private class BuildJob
        {
            public string Kind; // "hellevator", "house", "arena", "pond"
            public int Floors;
            public HouseStyle Style;
            public ArenaSpec Arena;
            public PondSpec Pond;
            public int NextX;
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
        private readonly HashSet<int> _talkingDemolitionist = new HashSet<int>();
        private readonly object _lock = new object();
        private string _logPath;
        private int _shopSyncTick;
        private int _iceRodPrice = -1;
        private int _demolitionistShopSlot = -1;

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
                    List<PreviewBox> oldPreviews;
                    lock (_lock)
                    {
                        Session s = GetSession(player.Index);
                        old = new List<(int X, int Y)>(s.Points);
                        s.Points.Clear();
                        s.HasPending = false;
                        s.PendingHouses = null;
                        s.PendingArena = null;
                        s.PendingPond = null;
                        oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                        s.PreviewBoxes.Clear();
                    }
                    ClearPreviewBoxes(player, oldPreviews);
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

                case "arena":
                    PrepareArena(player, args.Parameters);
                    break;

                case "pond":
                case "fishing":
                    PreparePond(player, args.Parameters);
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
                    player.SendInfoMessage("Pemakaian: /build select | points | remove <n> | clear | hellevator | npchouse [1|2|3] | arena <jarak> [tingkat] | pond <water|lava|honey> | confirm | cancel");
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
                _talkingDemolitionist.Remove(args.Who);
            }
        }

        // ---------------------------------------------------------------
        // Toko Demolitionist: kirim paket 104 (Slot, ItemType, Stack, Prefix, Value, Flags)
        // ---------------------------------------------------------------

        private int GetDemolitionistShopSlot()
        {
            if (_demolitionistShopSlot >= 0)
                return _demolitionistShopSlot;

            int emptySlot = 12;
            try
            {
                Chest shop = new Chest();
                shop.SetupShop(DemolitionistShopIndex);
                if (shop.item != null)
                {
                    int maxSlots = Math.Min(40, shop.item.Length);
                    for (int s = 0; s < maxSlots; s++)
                    {
                        if (shop.item[s] == null || shop.item[s].type == 0 || shop.item[s].IsAir)
                        {
                            emptySlot = Math.Max(s, 12);
                            break;
                        }
                    }
                }
            }
            catch
            {
                emptySlot = 12;
            }

            _demolitionistShopSlot = emptySlot;
            return emptySlot;
        }

        private void SendIceRodShopSlot(int playerIndex)
        {
            if (_iceRodPrice < 0)
            {
                Item temp = new Item();
                temp.SetDefaults(IceRodItemType);
                _iceRodPrice = temp.value;
            }

            int slot = GetDemolitionistShopSlot();
            NetMessage.SendData(
                ShopOverridePacketId,
                playerIndex,
                -1,
                null,
                slot,
                IceRodItemType,
                1f,
                0f,
                _iceRodPrice,
                0,
                0
            );
        }

        private void SyncDemolitionistShop()
        {
            _shopSyncTick++;
            if (_shopSyncTick < ShopSyncIntervalTicks)
                return;
            _shopSyncTick = 0;

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                TSPlayer p = TShock.Players[i];
                if (p == null || !p.Active || !p.RealPlayer)
                    continue;

                bool talking = false;
                Player tp = p.TPlayer;
                if (tp != null && tp.talkNPC >= 0 && tp.talkNPC < Main.maxNPCs)
                {
                    NPC npc = Main.npc[tp.talkNPC];
                    if (npc != null && npc.active && npc.type == NPCID.Demolitionist)
                        talking = true;
                }

                lock (_lock)
                {
                    if (_talkingDemolitionist.Contains(i))
                        talking = true;
                }

                if (talking)
                    SendIceRodShopSlot(i);
            }
        }

        // ---------------------------------------------------------------
        // Preview Sistem (Client-side sementara sebelum /build confirm - TIDAK DIUBAH)
        // ---------------------------------------------------------------

        private static void ClearPreviewBoxes(TSPlayer player, List<PreviewBox> boxes)
        {
            if (player == null || !player.Active || boxes == null || boxes.Count == 0)
                return;

            foreach (PreviewBox box in boxes)
            {
                SendLargeTileRectToPlayer(player, box.X0, box.Y0, box.W, box.H);
            }
        }

        private static void SendLargeTileRectToPlayer(TSPlayer player, int x0, int y0, int totalW, int totalH)
        {
            const int Chunk = 64;
            for (int x = x0; x < x0 + totalW; x += Chunk)
            {
                byte w = (byte)Math.Min(Chunk, (x0 + totalW) - x);
                for (int y = y0; y < y0 + totalH; y += Chunk)
                {
                    byte h = (byte)Math.Min(Chunk, (y0 + totalH) - y);
                    player.SendTileRect((short)x, (short)y, w, h);
                }
            }
        }

        private static TileBackup[,] CaptureBackup(int x0, int y0, int w, int h)
        {
            var backup = new TileBackup[w, h];
            for (int dx = 0; dx < w; dx++)
            {
                for (int dy = 0; dy < h; dy++)
                {
                    var t = Main.tile[x0 + dx, y0 + dy];
                    if (t == null)
                        continue;

                    backup[dx, dy] = new TileBackup
                    {
                        Active = t.active(),
                        Type = t.type,
                        Wall = t.wall,
                        Liquid = t.liquid,
                        LiquidType = (byte)t.liquidType(),
                        FrameX = t.frameX,
                        FrameY = t.frameY,
                        HalfBrick = t.halfBrick(),
                        Slope = t.slope(),
                        Color = t.color(),
                        WallColor = t.wallColor()
                    };
                }
            }
            return backup;
        }

        private static void RestoreBackup(int x0, int y0, int w, int h, TileBackup[,] backup)
        {
            for (int dx = 0; dx < w; dx++)
            {
                for (int dy = 0; dy < h; dy++)
                {
                    var t = Main.tile[x0 + dx, y0 + dy];
                    if (t == null)
                        continue;

                    TileBackup b = backup[dx, dy];
                    t.active(b.Active);
                    t.type = b.Type;
                    t.wall = b.Wall;
                    t.liquid = b.Liquid;
                    t.liquidType(b.LiquidType);
                    t.frameX = b.FrameX;
                    t.frameY = b.FrameY;
                    t.halfBrick(b.HalfBrick);
                    t.slope(b.Slope);
                    t.color(b.Color);
                    t.wallColor(b.WallColor);
                }
            }
        }

        private static void SetPreviewEmpty(int x, int y, ushort wall = 0)
        {
            var t = Main.tile[x, y];
            if (t == null) return;
            t.active(false);
            t.wall = wall;
            t.liquid = 0;
            t.liquidType(0);
            t.halfBrick(false);
            t.slope(0);
            t.color(0);
            t.wallColor(0);
        }

        private static void SetPreviewBlock(int x, int y, ushort blockType, ushort wall = 0)
        {
            var t = Main.tile[x, y];
            if (t == null) return;
            t.active(true);
            t.type = blockType;
            t.wall = wall;
            t.liquid = 0;
            t.liquidType(0);
            t.halfBrick(false);
            t.slope(0);
            t.color(0);
            t.wallColor(0);
        }

        private static void SetPreviewPlatform(int x, int y, int style, ushort wall = 0)
        {
            var t = Main.tile[x, y];
            if (t == null) return;
            t.active(true);
            t.type = TileID.Platforms;
            t.frameX = 0;
            t.frameY = (short)(style * 18);
            t.wall = wall;
            t.liquid = 0;
            t.liquidType(0);
            t.halfBrick(false);
            t.slope(0);
            t.color(0);
            t.wallColor(0);
        }

        private static void SetPreviewLiquid(int x, int y, byte liquidType, ushort wall)
        {
            var t = Main.tile[x, y];
            if (t == null) return;
            t.active(false);
            t.wall = wall;
            t.liquidType(liquidType);
            t.liquid = 255;
            t.halfBrick(false);
            t.slope(0);
            t.color(0);
            t.wallColor(0);
        }

        private static PreviewBox ShowHellevatorPreview(TSPlayer player, int cx, int sy, int ey)
        {
            int x0 = cx - 2;
            int y0 = sy - 1;
            int w = 5;
            int h = (ey - y0) + 1;

            TileBackup[,] backup = CaptureBackup(x0, y0, w, h);
            try
            {
                SetPreviewBlock(cx, sy - 1, BrickTileType);

                for (int y = sy; y <= ey; y++)
                {
                    SetPreviewBlock(cx - 2, y, BrickTileType);
                    SetPreviewBlock(cx + 2, y, BrickTileType);
                    SetPreviewEmpty(cx - 1, y, WallType);
                    SetPreviewEmpty(cx + 1, y, WallType);
                    SetPreviewBlock(cx, y, RopeTileType, WallType);
                }

                SendLargeTileRectToPlayer(player, x0, y0, w, h);
            }
            finally
            {
                RestoreBackup(x0, y0, w, h, backup);
            }

            return new PreviewBox(x0, y0, w, h);
        }

        private static PreviewBox ShowHousePreview(TSPlayer player, HouseSpec spec)
        {
            HouseStyle st = spec.Style;
            int cx = spec.X;
            int fy = spec.Y;
            int floors = spec.Floors;
            int left = cx - HouseHalfWidth;
            int right = cx + HouseHalfWidth;
            int topY = fy - (floors * FloorStepHeight);
            int w = 11;
            int h = (fy - topY) + 1;

            TileBackup[,] backup = CaptureBackup(left, topY, w, h);
            try
            {
                for (int x = left; x <= right; x++)
                    SetPreviewBlock(x, fy, st.Block);

                for (int f = 0; f < floors; f++)
                {
                    int floorBottom = fy - (f * FloorStepHeight);
                    int floorTop = floorBottom - FloorStepHeight;

                    for (int y = floorTop + 1; y <= floorBottom - 1; y++)
                    {
                        SetPreviewBlock(left, y, st.Block);
                        SetPreviewBlock(right, y, st.Block);

                        for (int x = left + 1; x <= right - 1; x++)
                            SetPreviewEmpty(x, y, st.Wall);
                    }

                    for (int x = left; x <= left + 6; x++)
                        SetPreviewBlock(x, floorTop, st.Block);

                    for (int x = left + 7; x <= left + 9; x++)
                        SetPreviewPlatform(x, floorTop, st.Platform);

                    SetPreviewBlock(right, floorTop, st.Block);
                }

                SendLargeTileRectToPlayer(player, left, topY, w, h);
            }
            finally
            {
                RestoreBackup(left, topY, w, h, backup);
            }

            return new PreviewBox(left, topY, w, h);
        }

        private PreviewBox ShowArenaPreview(TSPlayer player, ArenaSpec a)
        {
            HouseStyle st = a.Style;
            int w = a.RightX - a.LeftX + 1;
            int h = a.BottomY - a.TopY + 1;
            List<int> buffCenters = GetArenaBuffCenters(a);

            TileBackup[,] backup = CaptureBackup(a.LeftX, a.TopY, w, h);
            try
            {
                for (int x = a.LeftX; x <= a.RightX; x++)
                {
                    for (int y = a.TopY; y <= a.BottomY - 1; y++)
                        SetPreviewEmpty(x, y);

                    if (a.UseAsphalt)
                        SetPreviewBlock(x, a.BottomY, TileID.Asphalt);

                    foreach (int tierY in a.TierYs)
                        SetPreviewPlatform(x, tierY, st.Platform);
                }

                foreach (int tierY in a.TierYs)
                {
                    foreach (int bx in buffCenters)
                    {
                        if (tierY - 1 >= a.TopY)
                        {
                            SetPreviewBlock(bx - 1, tierY - 1, MarkerTileType);
                            SetPreviewBlock(bx, tierY - 1, MarkerTileType);
                            SetPreviewBlock(bx + 1, tierY - 1, MarkerTileType);
                        }
                    }
                }

                SendLargeTileRectToPlayer(player, a.LeftX, a.TopY, w, h);
            }
            finally
            {
                RestoreBackup(a.LeftX, a.TopY, w, h, backup);
            }

            return new PreviewBox(a.LeftX, a.TopY, w, h);
        }

        private static PreviewBox ShowPondPreview(TSPlayer player, PondSpec p)
        {
            int cx = p.X;
            int fy = p.Y;

            int innerLeft = cx - PondInnerHalfWidth;
            int innerRight = cx + PondInnerHalfWidth;
            int leftX = innerLeft - 1;
            int rightX = innerRight + 1;
            int topY = fy - PondRoomHeight - 1;
            int bottomY = fy + PondLiquidDepth + 1;
            int w = rightX - leftX + 1;
            int h = bottomY - topY + 1;

            TileBackup[,] backup = CaptureBackup(leftX, topY, w, h);
            try
            {
                for (int x = leftX; x <= rightX; x++)
                {
                    SetPreviewBlock(x, topY, TileID.GrayBrick);
                    SetPreviewBlock(x, bottomY, TileID.GrayBrick);
                }

                for (int y = topY + 1; y <= bottomY - 1; y++)
                {
                    if (y >= fy - 3 && y <= fy - 1)
                    {
                        SetPreviewEmpty(leftX, y);
                        SetPreviewEmpty(rightX, y);
                    }
                    else
                    {
                        SetPreviewBlock(leftX, y, TileID.GrayBrick);
                        SetPreviewBlock(rightX, y, TileID.GrayBrick);
                    }
                }

                for (int y = topY + 1; y <= fy - 1; y++)
                {
                    for (int x = innerLeft; x <= innerRight; x++)
                        SetPreviewEmpty(x, y, WallID.Glass);
                }

                for (int x = innerLeft; x <= innerRight; x++)
                    SetPreviewPlatform(x, fy, 0, WallID.Glass);

                for (int y = fy + 1; y <= fy + PondLiquidDepth; y++)
                {
                    for (int x = innerLeft; x <= innerRight; x++)
                        SetPreviewLiquid(x, y, p.LiquidType, WallID.Glass);
                }

                SendLargeTileRectToPlayer(player, leftX, topY, w, h);
            }
            finally
            {
                RestoreBackup(leftX, topY, w, h, backup);
            }

            return new PreviewBox(leftX, topY, w, h);
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

            List<PreviewBox> oldPreviews;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();
            }
            ClearPreviewBoxes(player, oldPreviews);

            PreviewBox preview = ShowHellevatorPreview(player, cx, sy, ey);

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "hellevator";
                s.PendingX = cx;
                s.PendingStartY = sy;
                s.PendingEndY = ey;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
                s.PreviewBoxes.Add(preview);
            }

            player.SendInfoMessage($"Preview Hellevator ditampilkan: dari ({cx}, {sy}) turun sampai y = {ey} ({ey - sy + 1} tile), lebar total 5.");
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
            List<PreviewBox> oldPreviews = null;
            int queued = 0;
            string kind = null;

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();

                if (!s.HasPending || DateTime.UtcNow > s.PendingExpires)
                {
                    s.HasPending = false;
                    ClearPreviewBoxes(player, oldPreviews);
                    player.SendErrorMessage("Tidak ada pembangunan yang menunggu konfirmasi. Mulai lagi dengan /build hellevator, /build npchouse, /build arena, atau /build pond.");
                    return;
                }

                kind = s.PendingKind;

                if (kind == "house" && s.PendingHouses != null)
                {
                    foreach (HouseSpec h in s.PendingHouses)
                    {
                        _jobs.Enqueue(new BuildJob
                        {
                            Kind = "house",
                            PlayerIndex = player.Index,
                            PlayerName = player.Name,
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
                else if (kind == "arena" && s.PendingArena != null)
                {
                    ArenaSpec a = s.PendingArena;
                    _jobs.Enqueue(new BuildJob
                    {
                        Kind = "arena",
                        PlayerIndex = player.Index,
                        PlayerName = player.Name,
                        Style = a.Style,
                        Arena = a,
                        X = a.LeftX,
                        EndY = a.RightX,
                        NextX = a.LeftX
                    });
                    queued = 1;
                }
                else if (kind == "pond" && s.PendingPond != null)
                {
                    PondSpec p = s.PendingPond;
                    _jobs.Enqueue(new BuildJob
                    {
                        Kind = "pond",
                        PlayerIndex = player.Index,
                        PlayerName = player.Name,
                        Style = p.Style,
                        Pond = p,
                        X = p.X,
                        StartY = p.Y,
                        EndY = p.Y,
                        NextY = p.Y
                    });
                    queued = 1;
                }
                else
                {
                    _jobs.Enqueue(new BuildJob
                    {
                        Kind = "hellevator",
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
                s.PendingArena = null;
                s.PendingPond = null;
                oldPoints = new List<(int X, int Y)>(s.Points);
                s.Points.Clear();
            }

            ClearPreviewBoxes(player, oldPreviews);
            foreach (var p in oldPoints)
                player.SendTileRect((short)p.X, (short)p.Y, 1, 1);

            if (kind == "house")
                player.SendSuccessMessage($"Membangun {queued} bangunan rumah NPC. Pakai /build cancel untuk menghentikan sisanya.");
            else if (kind == "arena")
                player.SendSuccessMessage("Pembangunan Boss Arena dimulai. Pakai /build cancel untuk menghentikan.");
            else if (kind == "pond")
                player.SendSuccessMessage("Pembangunan kolam pancing dimulai. Pakai /build cancel untuk menghentikan.");
            else
                player.SendSuccessMessage("Pembangunan hellevator dimulai. Pakai /build cancel untuk menghentikan.");
        }

        private void CancelBuild(TSPlayer player)
        {
            int count = 0;
            List<PreviewBox> oldPreviews;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();

                if (s.HasPending)
                {
                    s.HasPending = false;
                    s.PendingHouses = null;
                    s.PendingArena = null;
                    s.PendingPond = null;
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

            ClearPreviewBoxes(player, oldPreviews);

            if (count == 0)
                player.SendInfoMessage("Tidak ada pembangunan untuk dibatalkan.");
            else
                player.SendSuccessMessage("Dibatalkan. Preview dihapus dan bagian yang sudah dibangun tidak dikembalikan.");
        }

        private void OnGameUpdate(EventArgs args)
        {
            SyncDemolitionistShop();

            List<(int PlayerIndex, List<PreviewBox> Boxes)> expiredPreviews = null;
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    Session s = kv.Value;
                    if (s.HasPending && now > s.PendingExpires)
                    {
                        s.HasPending = false;
                        s.PendingHouses = null;
                        s.PendingArena = null;
                        s.PendingPond = null;
                        if (s.PreviewBoxes.Count > 0)
                        {
                            if (expiredPreviews == null)
                                expiredPreviews = new List<(int, List<PreviewBox>)>();
                            expiredPreviews.Add((kv.Key, new List<PreviewBox>(s.PreviewBoxes)));
                            s.PreviewBoxes.Clear();
                        }
                    }
                }
            }

            if (expiredPreviews != null)
            {
                foreach (var item in expiredPreviews)
                {
                    TSPlayer p = TShock.Players[item.PlayerIndex];
                    if (p != null && p.Active)
                    {
                        ClearPreviewBoxes(p, item.Boxes);
                        p.SendInfoMessage("Waktu konfirmasi habis, preview build dihapus.");
                    }
                }
            }

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
                else if (job.Kind == "house")
                {
                    BuildNpcHouse(job);
                    finished = true;
                }
                else if (job.Kind == "arena")
                {
                    finished = StepArenaBuild(job);
                }
                else if (job.Kind == "pond")
                {
                    BuildFishingPond(job);
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
                    if (job.Kind == "house")
                        owner.SendSuccessMessage($"Rumah NPC ({job.Style.Name}, {job.Floors} tingkat) di ({job.X}, {job.StartY}) selesai.");
                    else if (job.Kind == "arena")
                        owner.SendSuccessMessage($"Boss Arena ({job.Style.Name}, {job.Arena.TierYs.Count} tingkat platform) selesai.");
                    else if (job.Kind == "pond")
                        owner.SendSuccessMessage($"Kolam Pancing ({job.Pond.LiquidName}, biome {job.Style.Name}, 100% fishing power) di ({job.X}, {job.StartY}) selesai.");
                    else
                        owner.SendSuccessMessage("Hellevator selesai.");

                    if (job.Blocked > 0)
                        owner.SendWarningMessage($"{job.Blocked} tile tidak bisa dihancurkan (misalnya peti berisi), jadi ada celah.");
                    if (job.TorchFailed > 0)
                        owner.SendWarningMessage($"{job.TorchFailed} obor gagal dipasang.");
                    if (job.RopeFailed > 0)
                        owner.SendWarningMessage($"{job.RopeFailed} tile tali gagal dipasang.");
                    if (job.ObjectFailed > 0)
                        owner.SendWarningMessage($"{job.ObjectFailed} furnitur/pintu gagal dipasang.");
                }
            }

            TShock.Log.ConsoleInfo($"[MagicBuilder] {job.Kind} {job.PlayerName} selesai (blocked={job.Blocked}, objFailed={job.ObjectFailed}, cancelled={job.Cancelled}, error={error ?? "-"})");
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
        // Fishing Pond (TIDAK DIUBAH)
        // ---------------------------------------------------------------

        private void PreparePond(TSPlayer player, List<string> parameters)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            if (parameters.Count != 2)
            {
                player.SendErrorMessage("Pemakaian: /build pond <water|lava|honey>");
                return;
            }

            string liqArg = parameters[1].ToLowerInvariant();
            byte liquidType;
            string liquidName;

            switch (liqArg)
            {
                case "water":
                    liquidType = 0;
                    liquidName = "Water";
                    break;
                case "lava":
                    liquidType = 1;
                    liquidName = "Lava";
                    break;
                case "honey":
                    liquidType = 2;
                    liquidName = "Honey";
                    break;
                default:
                    player.SendErrorMessage("Jenis cairan harus dipilih: water, lava, atau honey.");
                    return;
            }

            (int X, int Y) point;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                if (s.Points.Count == 0)
                {
                    player.SendErrorMessage("Belum ada titik. Pakai /build select lalu klik 1 titik permukaan kolam dengan Ice Rod.");
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
            int fy = point.Y;

            int leftX = cx - PondInnerHalfWidth - 1;  // cx - 13
            int rightX = cx + PondInnerHalfWidth + 1; // cx + 13
            int topY = fy - PondRoomHeight - 1;       // fy - 6
            int bottomY = fy + PondLiquidDepth + 1;   // fy + 14

            if (leftX < 5 || rightX >= Main.maxTilesX - 5 || topY < 5 || bottomY >= Main.maxTilesY - 5)
            {
                player.SendErrorMessage("Area kolam pancing terlalu dekat tepi dunia.");
                return;
            }

            for (int x = leftX; x <= rightX; x++)
            {
                for (int y = topY; y <= bottomY; y++)
                {
                    if (!player.HasBuildPermission(x, y, false))
                    {
                        player.SendErrorMessage($"Kamu tidak boleh membangun di ({x}, {y}) (region/spawn protection). Dibatalkan.");
                        return;
                    }
                }
            }

            HouseStyle style = DetectHouseStyle(cx, fy);

            var spec = new PondSpec
            {
                X = cx,
                Y = fy,
                LiquidType = liquidType,
                LiquidName = liquidName,
                Style = style
            };

            List<PreviewBox> oldPreviews;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();
            }
            ClearPreviewBoxes(player, oldPreviews);

            PreviewBox preview = ShowPondPreview(player, spec);

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "pond";
                s.PendingPond = spec;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
                s.PreviewBoxes.Add(preview);
            }

            player.SendInfoMessage($"Preview Kolam Pancing ditampilkan: cairan {liquidName} (25x13 = 325 tile, 100% fishing power) di ({cx}, {fy}), Gray Brick + Glass Wall, torch biome {style.Name}.");
            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        private void BuildFishingPond(BuildJob job)
        {
            PondSpec p = job.Pond;
            HouseStyle st = job.Style;
            int cx = p.X;
            int fy = p.Y;

            int innerLeft = cx - PondInnerHalfWidth;   // cx - 12
            int innerRight = cx + PondInnerHalfWidth;  // cx + 12
            int leftX = innerLeft - 1;                 // cx - 13
            int rightX = innerRight + 1;               // cx + 13
            int topY = fy - PondRoomHeight - 1;        // fy - 6
            int bottomY = fy + PondLiquidDepth + 1;    // fy + 14

            for (int y = topY; y <= bottomY; y++)
            {
                for (int x = leftX; x <= rightX; x++)
                {
                    ClearTile(job, x, y);
                    var t = Main.tile[x, y];
                    t.liquid = 0;
                    t.liquidType(0);
                    t.wall = 0;
                    t.wallColor(0);
                    WorldGen.SquareWallFrame(x, y, true);
                }
            }

            for (int x = leftX; x <= rightX; x++)
            {
                PlaceBlock(job, x, topY, TileID.GrayBrick);
                PlaceBlock(job, x, bottomY, TileID.GrayBrick);
            }

            for (int y = topY + 1; y <= bottomY - 1; y++)
            {
                PlaceBlock(job, leftX, y, TileID.GrayBrick);
                PlaceBlock(job, rightX, y, TileID.GrayBrick);
            }

            for (int y = topY + 1; y <= bottomY - 1; y++)
            {
                for (int x = innerLeft; x <= innerRight; x++)
                {
                    var t = Main.tile[x, y];
                    t.wall = WallID.Glass;
                    t.wallColor(0);
                    WorldGen.SquareWallFrame(x, y, true);
                }
            }

            for (int y = fy + 1; y <= fy + PondLiquidDepth; y++)
            {
                for (int x = innerLeft; x <= innerRight; x++)
                {
                    var t = Main.tile[x, y];
                    t.active(false);
                    t.halfBrick(false);
                    t.slope(0);
                    t.liquidType(p.LiquidType);
                    t.liquid = 255;
                }
            }

            for (int x = innerLeft; x <= innerRight; x++)
            {
                PlacePlatform(job, x, fy, 0);
            }

            for (int y = fy - 3; y <= fy - 1; y++)
            {
                ClearTile(job, leftX, y);
                ClearTile(job, rightX, y);
            }
            if (!WorldGen.PlaceDoor(leftX, fy - 2, TileID.ClosedDoor, 0))
                job.ObjectFailed++;
            if (!WorldGen.PlaceDoor(rightX, fy - 2, TileID.ClosedDoor, 0))
                job.ObjectFailed++;

            int[] torchXs = { innerLeft, cx - 6, cx, cx + 6, innerRight };
            foreach (int tx in torchXs)
            {
                if (!WorldGen.PlaceTile(tx, fy - 4, TileID.Torches, true, true, -1, st.Torch))
                    job.TorchFailed++;
            }

            int width = rightX - leftX + 1;   // 27
            int height = bottomY - topY + 1;  // 21
            TSPlayer.All.SendTileRect((short)leftX, (short)topY, (byte)width, (byte)height);
        }

        // ---------------------------------------------------------------
        // Boss Arena (TIDAK DIUBAH)
        // ---------------------------------------------------------------

        private void PrepareArena(TSPlayer player, List<string> parameters)
        {
            if (!player.HasPermission(BuildPermission))
            {
                player.SendErrorMessage("Kamu tidak punya permission " + BuildPermission);
                return;
            }

            if (parameters.Count < 2 || parameters.Count > 3)
            {
                player.SendErrorMessage("Pemakaian: /build arena <jarak_platform> [jumlah_tingkat]");
                return;
            }

            int spacing;
            if (!int.TryParse(parameters[1], out spacing) || spacing < ArenaMinSpacing || spacing > ArenaMaxSpacing)
            {
                player.SendErrorMessage($"Jarak tinggi per platform harus angka antara {ArenaMinSpacing} sampai {ArenaMaxSpacing} tile.");
                return;
            }

            int forcedTiers = 0;
            if (parameters.Count == 3)
            {
                if (!int.TryParse(parameters[2], out forcedTiers) || forcedTiers < 1 || forcedTiers > 15)
                {
                    player.SendErrorMessage("Jumlah tingkat platform harus antara 1 sampai 15.");
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

            if (points.Count < 2)
            {
                player.SendErrorMessage("Butuh 2 titik untuk menentukan panjang arena. Pakai /build select lalu klik 2 titik dengan Ice Rod.");
                return;
            }

            int leftX = Math.Min(points[0].X, points[1].X);
            int rightX = Math.Max(points[0].X, points[1].X);
            int width = rightX - leftX + 1;

            if (width < ArenaMinWidth || width > ArenaMaxWidth)
            {
                player.SendErrorMessage($"Panjang arena harus antara {ArenaMinWidth} sampai {ArenaMaxWidth} tile (sekarang: {width} tile).");
                return;
            }

            int bottomY = Math.Max(points[0].Y, points[1].Y);
            int topPointY = Math.Min(points[0].Y, points[1].Y);

            int tiers;
            if (forcedTiers > 0)
                tiers = forcedTiers;
            else if (bottomY - topPointY >= spacing)
                tiers = (bottomY - topPointY) / spacing;
            else
                tiers = ArenaDefaultTiers;

            var tierYs = new List<int>();
            for (int t = 1; t <= tiers; t++)
            {
                tierYs.Add(bottomY - (t * spacing));
            }

            int topY = tierYs[tierYs.Count - 1] - 4;

            if (leftX < 5 || rightX >= Main.maxTilesX - 5 || topY < 5 || bottomY >= Main.maxTilesY - 5)
            {
                player.SendErrorMessage("Area arena terlalu dekat tepi dunia.");
                return;
            }

            for (int x = leftX; x <= rightX; x++)
            {
                for (int y = topY; y <= bottomY; y++)
                {
                    if (!player.HasBuildPermission(x, y, false))
                    {
                        player.SendErrorMessage($"Kamu tidak boleh membangun di ({x}, {y}) (region/spawn protection). Dibatalkan.");
                        return;
                    }
                }
            }

            int midX = (leftX + rightX) / 2;
            HouseStyle style = DetectHouseStyle(midX, bottomY);
            bool useAsphalt = Main.hardMode && NPC.downedMechBossAny;

            var spec = new ArenaSpec
            {
                LeftX = leftX,
                RightX = rightX,
                TopY = topY,
                BottomY = bottomY,
                Spacing = spacing,
                TierYs = tierYs,
                UseAsphalt = useAsphalt,
                Style = style
            };

            List<PreviewBox> oldPreviews;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();
            }
            ClearPreviewBoxes(player, oldPreviews);

            PreviewBox preview = ShowArenaPreview(player, spec);

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "arena";
                s.PendingArena = spec;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
                s.PreviewBoxes.Add(preview);
            }

            string floorInfo = useAsphalt
                ? "Asphalt (Hardmode + Mech Boss aktif)"
                : "Dibiarkan asli (belum Hardmode + 1 Mech Boss)";

            player.SendInfoMessage($"Preview Boss Arena ditampilkan: lebar {width} tile ({leftX}..{rightX}), {tiers} tingkat platform (jarak {spacing} tile), biome {style.Name}, lantai bawah: {floorInfo}.");
            player.SendWarningMessage($"Ini mengubah dunia dan BELUM ADA UNDO. Backup dunia dulu. Ketik /build confirm dalam {ConfirmSeconds} detik untuk mulai.");
        }

        private List<int> GetArenaBuffCenters(ArenaSpec a)
        {
            var centers = new List<int>();
            int width = a.RightX - a.LeftX + 1;
            int count = Math.Max(1, width / ArenaBuffInterval);

            for (int i = 0; i < count; i++)
            {
                int bx = a.LeftX + (int)Math.Round((i + 0.5) * width / count);
                bx = Math.Max(a.LeftX + 4, Math.Min(a.RightX - 4, bx));
                if (!centers.Contains(bx))
                    centers.Add(bx);
            }
            return centers;
        }

        private bool StepArenaBuild(BuildJob job)
        {
            ArenaSpec a = job.Arena;
            HouseStyle st = job.Style;

            int xStart = job.NextX;
            int cols = 0;

            while (cols < ArenaColsPerTick && job.NextX <= a.RightX)
            {
                int x = job.NextX;

                for (int y = a.TopY; y <= a.BottomY - 1; y++)
                    ClearTile(job, x, y);

                if (a.UseAsphalt)
                    PlaceBlock(job, x, a.BottomY, TileID.Asphalt);

                foreach (int tierY in a.TierYs)
                    PlacePlatform(job, x, tierY, st.Platform);

                job.NextX++;
                cols++;
            }

            if (cols > 0)
            {
                int rectHeight = a.BottomY - a.TopY + 1;
                SendLargeTileRect(xStart, a.TopY, cols, rectHeight);
            }

            if (job.NextX <= a.RightX)
                return false;

            List<int> buffCenters = GetArenaBuffCenters(a);
            PlaceArenaBuffsAndLights(job, buffCenters);
            return true;
        }

        private void PlaceArenaBuffsAndLights(BuildJob job, List<int> buffCenters)
        {
            ArenaSpec a = job.Arena;
            HouseStyle st = job.Style;

            if (a.UseAsphalt)
            {
                foreach (int bx in buffCenters)
                {
                    if (!WorldGen.PlaceObject(bx - 1, a.BottomY - 1, TileID.Campfire, true, st.Campfire))
                        job.ObjectFailed++;

                    if (!WorldGen.PlaceObject(bx + 1, a.BottomY - 1, BastStatueTileType, true))
                        job.ObjectFailed++;
                }
            }

            foreach (int tierY in a.TierYs)
            {
                foreach (int bx in buffCenters)
                {
                    if (!WorldGen.PlaceObject(bx - 1, tierY - 1, TileID.Campfire, true, st.Campfire))
                        job.ObjectFailed++;

                    if (!WorldGen.PlaceObject(bx + 1, tierY - 1, BastStatueTileType, true))
                        job.ObjectFailed++;

                    if (!WorldGen.PlaceObject(bx, tierY + 1, TileID.HangingLanterns, true, HeartLanternStyle))
                        job.ObjectFailed++;

                    if (!WorldGen.PlaceObject(bx + 1, tierY + 1, TileID.HangingLanterns, true, StarInBottleStyle))
                        job.ObjectFailed++;
                }

                for (int x = a.LeftX + 2; x <= a.RightX - 2; x += ArenaTorchInterval)
                {
                    bool nearBuff = false;
                    foreach (int bx in buffCenters)
                    {
                        if (Math.Abs(x - bx) <= 3)
                        {
                            nearBuff = true;
                            break;
                        }
                    }

                    if (!nearBuff)
                    {
                        if (!WorldGen.PlaceTile(x, tierY - 1, TileID.Torches, true, true, -1, st.Torch))
                            job.TorchFailed++;
                    }
                }
            }

            foreach (int tierY in a.TierYs)
            {
                SendLargeTileRect(a.LeftX, tierY - 3, a.RightX - a.LeftX + 1, 6);
            }

            if (a.UseAsphalt)
            {
                SendLargeTileRect(a.LeftX, a.BottomY - 3, a.RightX - a.LeftX + 1, 4);
            }
        }

        private static void SendLargeTileRect(int x0, int y0, int totalW, int totalH)
        {
            const int Chunk = 64;
            for (int x = x0; x < x0 + totalW; x += Chunk)
            {
                byte w = (byte)Math.Min(Chunk, (x0 + totalW) - x);
                for (int y = y0; y < y0 + totalH; y += Chunk)
                {
                    byte h = (byte)Math.Min(Chunk, (y0 + totalH) - y);
                    TSPlayer.All.SendTileRect((short)x, (short)y, w, h);
                }
            }
        }

        // ---------------------------------------------------------------
        // Rumah NPC (TIDAK DIUBAH)
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

            List<PreviewBox> oldPreviews;
            lock (_lock)
            {
                Session s = GetSession(player.Index);
                oldPreviews = new List<PreviewBox>(s.PreviewBoxes);
                s.PreviewBoxes.Clear();
            }
            ClearPreviewBoxes(player, oldPreviews);

            var newPreviews = new List<PreviewBox>();
            foreach (HouseSpec spec in specs)
                newPreviews.Add(ShowHousePreview(player, spec));

            lock (_lock)
            {
                Session s = GetSession(player.Index);
                s.HasPending = true;
                s.PendingKind = "house";
                s.PendingHouses = specs;
                s.PendingExpires = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
                s.PreviewBoxes.AddRange(newPreviews);
            }

            for (int i = 0; i < specs.Count; i++)
                player.SendInfoMessage($"Preview Rumah {i + 1}: {specs[i].Floors} tingkat di ({specs[i].X}, {specs[i].Y}), biome {specs[i].Style.Name}.");

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

            for (int x = left; x <= right; x++)
                PlaceBlock(job, x, fy, st.Block);

            for (int f = 0; f < floors; f++)
            {
                int floorBottom = fy - (f * FloorStepHeight);
                int floorTop = floorBottom - FloorStepHeight;

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

                for (int x = left; x <= left + 6; x++)
                    PlaceBlock(job, x, floorTop, st.Block);

                for (int x = left + 7; x <= left + 9; x++)
                    PlacePlatform(job, x, floorTop, st.Platform);

                PlaceBlock(job, right, floorTop, st.Block);

                if (!WorldGen.PlaceTile(left + 1, floorTop + 1, TileID.Torches, true, true, -1, st.Torch))
                    job.TorchFailed++;

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
        // Titik: baca dari packet projectile Ice Rod (TIDAK DIUBAH) & pantau NpcTalk
        // ---------------------------------------------------------------

        private void OnGetData(GetDataEventArgs args)
        {
            int who = args.Msg.whoAmI;
            if (who < 0 || who >= Main.maxPlayers)
                return;

            int msgId = (int)args.MsgID;

            byte[] buffer = args.Msg.readBuffer;
            if (buffer == null || args.Index < 0 || args.Index >= buffer.Length)
                return;

            if (msgId == NpcTalkPacketId && args.Length >= 3 && args.Index + 3 <= buffer.Length)
            {
                short npcIdx = BitConverter.ToInt16(buffer, args.Index + 1);
                bool isDemolitionist = npcIdx >= 0 && npcIdx < Main.maxNPCs
                    && Main.npc[npcIdx] != null
                    && Main.npc[npcIdx].active
                    && Main.npc[npcIdx].type == NPCID.Demolitionist;

                lock (_lock)
                {
                    if (isDemolitionist)
                        _talkingDemolitionist.Add(who);
                    else
                        _talkingDemolitionist.Remove(who);
                }

                if (isDemolitionist)
                    SendIceRodShopSlot(who);
                return;
            }

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
