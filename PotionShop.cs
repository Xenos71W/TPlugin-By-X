using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace PotionShop;

[ApiVersion(2, 1)]
public class PotionShopPlugin : TerrariaPlugin
{
    public override string Name => "PotionShop";
    public override string Author => "Brok";
    public override Version Version => new(1, 0, 0);
    public override string Description => "Menambahkan Potion ke Dye Trader dan Flask ke Witch Doctor.";

    // Status toko per player
    private static readonly Dictionary<int, bool> PlayerHardmodeShop = new();
    private static readonly HashSet<int> VanillaShopPlayers = new();

    public PotionShopPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        // Register Hook untuk Ngetik NPC (NpcTalk)
        ServerApi.Hooks.NetGetData.Register(this, OnGetData);
        
        // Register Command
        Commands.ChatCommands.Add(new Command("potion.shop.hm", ToggleHardmodeShop, "hm"));
        Commands.ChatCommands.Add(new Command("potion.shop.switch", SwitchShopCmd, "switchshop", "switch"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            PlayerHardmodeShop.Clear();
            VanillaShopPlayers.Clear();
        }
        base.Dispose(disposing);
    }

    private void ToggleHardmodeShop(CommandArgs args)
    {
        var plr = args.Player;
        if (!Main.hardMode)
        {
            plr.SendErrorMessage("Dunia masih Pre-Hardmode! Kamu belum bisa mengakses toko Hardmode.");
            return;
        }

        PlayerHardmodeShop.TryGetValue(plr.Index, out var isHm);
        PlayerHardmodeShop[plr.Index] = !isHm;
        plr.SendSuccessMessage($"Toko Dye Trader dialihkan ke mode: [{(PlayerHardmodeShop[plr.Index] ? "Hardmode" : "Pre-Hardmode")}]. Silakan buka kembali toko.");
    }

    private void SwitchShopCmd(CommandArgs args)
    {
        var plr = args.Player;
        if (VanillaShopPlayers.Contains(plr.Index))
        {
            VanillaShopPlayers.Remove(plr.Index);
            plr.SendSuccessMessage("Toko diubah ke mode: [Custom]. Silakan buka kembali toko.");
        }
        else
        {
            VanillaShopPlayers.Add(plr.Index);
            plr.SendSuccessMessage("Toko diubah ke mode: [Vanilla/Asli]. Silakan buka kembali toko.");
        }
    }

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled || args.MsgID != PacketTypes.NpcTalk) return;

        var index = args.Msg.readBuffer[args.Index];
        var npcID = (short)(args.Msg.readBuffer[args.Index + 1] + (args.Msg.readBuffer[args.Index + 2] << 8));
        
        if (index != args.Msg.whoAmI || npcID == -1) return;

        var plr = TShock.Players[index];
        if (plr == null || plr.TPlayer.talkNPC == npcID) return;

        // Kalau player lagi mode Vanilla, biarin toko asli kebuka
        if (VanillaShopPlayers.Contains(plr.Index)) return;

        var npcType = Main.npc[npcID].type;
        if (npcType != NPCID.DyeTrader && npcType != NPCID.WitchDoctor) return;

        // Siapin daftar item
        var shopItems = new List<(int itemId, int price)>();

        if (npcType == NPCID.DyeTrader)
        {
            PlayerHardmodeShop.TryGetValue(plr.Index, out var isHm);
            
            if (isHm && !Main.hardMode)
            {
                isHm = false;
                PlayerHardmodeShop[plr.Index] = false;
                plr.SendErrorMessage("Dunia masih Pre-Hardmode! Toko dialihkan ke Pre-Hardmode.");
            }

            if (isHm) shopItems = GetDyeTraderHardmodeItems();
            else shopItems = GetDyeTraderPreHardmodeItems();
        }
        else if (npcType == NPCID.WitchDoctor)
        {
            shopItems = GetWitchDoctorItems();
        }

        // Generate RawData (Format paket Terraria untuk toko)
        var rawData = new byte[40][];
        for (int i = 0; i < 40; i++)
        {
            if (i < shopItems.Count)
            {
                var item = shopItems[i];
                var idBytes = BitConverter.GetBytes((short)item.itemId);
                var stackBytes = BitConverter.GetBytes((short)1);
                var priceBytes = BitConverter.GetBytes(item.price);
                
                rawData[i] = new byte[]
                {
                    14, 0, 104, (byte)i, idBytes[0], idBytes[1], stackBytes[0], stackBytes[1], 0,
                    priceBytes[0], priceBytes[1], priceBytes[2], priceBytes[3], 0
                };
            }
            else
            {
                rawData[i] = new byte[] { 14, 0, 104, (byte)i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            }
        }

        // Kirim paket toko custom secara terus menerus selama toko kebuka
        var originalNpcID = npcID;
        Task.Run(() =>
        {
            while (plr.TPlayer.talkNPC == originalNpcID && !VanillaShopPlayers.Contains(plr.Index))
            {
                foreach (var data in rawData)
                {
                    plr.SendRawData(data);
                }
                Task.Delay(500).Wait();
            }
        });
    }

    private List<(int itemId, int price)> GetDyeTraderPreHardmodeItems()
    {
        return new List<(int, int)>
        {
            (ItemID.LesserHealingPotion, Item.buyPrice(0, 0, 1, 0)),
            (ItemID.LesserManaPotion, Item.buyPrice(0, 0, 1, 0)),
            (ItemID.HealingPotion, Item.buyPrice(0, 0, 3, 0)),
            (ItemID.ManaPotion, Item.buyPrice(0, 0, 3, 0)),
            (ItemID.IronskinPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.SwiftnessPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.RegenerationPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.NightOwlPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.ShinePotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.GillsPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.MiningPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.BuilderPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.CalmingPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.TrapsightPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.ArcheryPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.AmmoReservationPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.WarmthPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.WaterWalkingPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.FeatherfallPotion, Item.buyPrice(0, 0, 8, 0)),
            (ItemID.ObsidianSkinPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.SpelunkerPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.HunterPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.BattlePotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.HeartreachPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.ThornsPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.InvisibilityPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.MagicPowerPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.ManaRegenerationPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.SummoningPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.TitanPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.FlipperPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.FishingPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.CratePotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.GravitationPotion, Item.buyPrice(0, 0, 15, 0)),
            (ItemID.EndurancePotion, Item.buyPrice(0, 0, 20, 0)),
            (ItemID.WrathPotion, Item.buyPrice(0, 0, 20, 0)),
            (ItemID.RagePotion, Item.buyPrice(0, 0, 20, 0)),
            (ItemID.InfernoPotion, Item.buyPrice(0, 0, 20, 0))
        };
    }

    private List<(int itemId, int price)> GetDyeTraderHardmodeItems()
    {
        var items = new List<(int, int)>
        {
            (ItemID.GreaterHealingPotion, Item.buyPrice(0, 0, 30, 0)),
            (ItemID.GreaterManaPotion, Item.buyPrice(0, 0, 30, 0)),
            (ItemID.LifeforcePotion, Item.buyPrice(0, 0, 75, 0))
        };

        if (NPC.downedTowerSolar && NPC.downedTowerVortex && NPC.downedTowerNebula && NPC.downedTowerStardust)
        {
            items.Add((ItemID.SuperHealingPotion, Item.buyPrice(0, 1, 50, 0)));
            items.Add((ItemID.SuperManaPotion, Item.buyPrice(0, 1, 50, 0)));
        }

        return items;
    }

    private List<(int itemId, int price)> GetWitchDoctorItems()
    {
        var items = new List<(int, int)>
        {
            (ItemID.FlaskofFire, Item.buyPrice(0, 0, 20, 0)),
            (ItemID.FlaskofPoison, Item.buyPrice(0, 0, 20, 0))
        };

        if (Main.hardMode)
        {
            items.Add((ItemID.FlaskofGold, Item.buyPrice(0, 0, 50, 0)));
            items.Add((ItemID.FlaskofIchor, Item.buyPrice(0, 0, 50, 0)));
            items.Add((ItemID.FlaskofCursedFlames, Item.buyPrice(0, 0, 50, 0)));
            items.Add((ItemID.FlaskofParty, Item.buyPrice(0, 0, 20, 0)));
        }

        if (NPC.downedPlantBoss)
        {
            items.Add((ItemID.FlaskofNanites, Item.buyPrice(0, 0, 60, 0)));
            items.Add((ItemID.FlaskofVenom, Item.buyPrice(0, 0, 75, 0)));
        }

        return items;
    }
}
