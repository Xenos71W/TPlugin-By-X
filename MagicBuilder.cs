using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MagicBuilder
{
    /// <summary>
    /// PROTOTIPE TAHAP 0: logger packet untuk mencari tahu bagaimana posisi
    /// klik Dirt Rod bisa dibaca server. Belum ada fitur bangun.
    /// </summary>
    [ApiVersion(2, 1)]
    public class MagicBuilderPlugin : TerrariaPlugin
    {
        public override string Name => "MagicBuilder";
        public override string Author => "You";
        public override string Description => "Prototipe: logger packet untuk deteksi titik Dirt Rod.";
        public override Version Version => new Version(0, 0, 1, 0);

        private const string DebugPermission = "magicbuilder.debug";

        // Packet yang berisik / tidak relevan, dilewati
        private static readonly HashSet<int> IgnoredPackets = new HashSet<int>
        {
            4,   // PlayerInfo
            5,   // PlayerSlot
            16,  // PlayerHp
            42,  // PlayerMana
            50,  // PlayerBuff
            68   // ClientUUID
        };

        private readonly HashSet<int> _logging = new HashSet<int>();
        private readonly object _lock = new object();
        private string _logPath;

        public MagicBuilderPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            _logPath = Path.Combine(TShock.SavePath, "MagicBuilderDebug.log");

            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);

            Commands.ChatCommands.Add(new Command(DebugPermission, DirtLogCommand, "dirtlog"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == DirtLogCommand);
            }
            base.Dispose(disposing);
        }

        // /dirtlog on | off
        private void DirtLogCommand(CommandArgs args)
        {
            if (!args.Player.RealPlayer)
            {
                args.Player.SendErrorMessage("Command ini hanya bisa dipakai di dalam game.");
                return;
            }

            string mode = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";
            int index = args.Player.Index;

            if (mode == "on")
            {
                lock (_lock) { _logging.Add(index); }
                Write($"===== LOG START: {args.Player.Name} (index {index}) =====");
                args.Player.SendSuccessMessage("Logging ON. Pegang Dirt Rod lalu klik titik yang mau dites. Selesai? /dirtlog off");
            }
            else if (mode == "off")
            {
                lock (_lock) { _logging.Remove(index); }
                Write($"===== LOG END: {args.Player.Name} =====");
                args.Player.SendSuccessMessage("Logging OFF. Hasil ada di: " + _logPath);
            }
            else
            {
                args.Player.SendInfoMessage("Pemakaian: /dirtlog on | off");
            }
        }

        private void OnLeave(LeaveEventArgs args)
        {
            lock (_lock) { _logging.Remove(args.Who); }
        }

        private void OnGetData(GetDataEventArgs args)
        {
            int who = args.Msg.whoAmI;
            if (who < 0 || who >= Main.maxPlayers)
                return;

            lock (_lock)
            {
                if (!_logging.Contains(who))
                    return;
            }

            int msgId = (int)args.MsgID;
            if (IgnoredPackets.Contains(msgId))
                return;

            byte[] buffer = args.Msg.readBuffer;
            if (buffer == null || args.Index < 0 || args.Index >= buffer.Length)
                return;

            // PlayerUpdate (13) dikirim terus-menerus; hanya catat saat tombol pakai-item ditekan
            // payload[0] = player id, payload[1] = control flags, bit 5 (32) = use item
            if (msgId == 13)
            {
                if (args.Index + 1 >= buffer.Length)
                    return;
                if ((buffer[args.Index + 1] & 32) == 0)
                    return;
            }

            Player p = Main.player[who];
            string heldInfo = "?";
            if (p != null && p.selectedItem >= 0 && p.selectedItem < p.inventory.Length)
            {
                Item held = p.inventory[p.selectedItem];
                heldInfo = $"{held.type} \"{held.Name}\"";
            }

            int len = Math.Min(args.Length, 64);
            var hex = new StringBuilder();
            for (int i = 0; i < len && args.Index + i < buffer.Length; i++)
            {
                hex.Append(buffer[args.Index + i].ToString("X2"));
                hex.Append(' ');
            }

            Write($"{DateTime.Now:HH:mm:ss.fff} | msg={msgId} ({args.MsgID}) | len={args.Length} | held={heldInfo} | {hex}");
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
