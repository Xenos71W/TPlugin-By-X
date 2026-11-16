using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks; // <-- INI YANG PENTING BIAR PLAYERSHOPEVENTARGS KEBACA

namespace PotionShop;

[ApiVersion(2, 1)]
public class PotionShopPlugin : TerrariaPlugin
{
    public override string Name => "PotionShop";
    public override string Author => "Brok";
    public override Version Version => new(1, 0, 0);
    public override string Description => "Menambahkan Potion ke Dye Trader dan Flask ke Witch Doctor secara seimbang.";

    private static readonly Dictionary<int, bool> PlayerHardmodeShop = new();
    private static readonly HashSet<int> VanillaShopPlayers = new();

    public PotionShopPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        // Daftarin event pakai TShockAPI.Hooks
        PlayerHooks.OnShop += OnShop;
        
        Commands.ChatCommands.Add(new Command("potion.shop.hm", ToggleHardmodeShop, "hm"));
        Commands.ChatCommands.Add(new Command("potion.shop.switch", SwitchShopCmd, "switchshop", "switch"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PlayerHooks.OnShop -= OnShop;
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
            plr.SendErrorMessage("Dunia ini masih mode Pre-Hardmode! Kamu belum bisa mengakses toko Hardmode.");
            return;
        }

        if (!PlayerHardmodeShop.TryGetValue(plr.Index, out var isHm))
        {
            isHm = false;
        }

        PlayerHardmodeShop[plr.Index] = !isHm;
        plr.SendSuccessMessage($"Toko Dye Trader dialihkan ke mode: [{(!isHm ? "Hardmode" : "Pre-Hardmode")}]. Silakan buka kembali toko NPC.");
    }

    private void SwitchShopCmd(CommandArgs args)
    {
        var plr = args.Player;
        if (VanillaShopPlayers.Contains(plr.Index))
        {
            VanillaShopPlayers.Remove(plr.Index);
            plr.SendSuccessMessage("Toko diubah ke mode: [Custom PotionShop]. Silakan buka kembali toko NPC.");
        }
        else
        {
            VanillaShopPlayers.Add(plr.Index);
            plr.SendSuccessMessage("Toko diubah ke mode: [Vanilla/Asli]. Silakan buka kembali toko NPC.");
        }
    }

    // PASTIKAN PARAMETERNYA ADALAH PlayerShopEventArgs DARI TShockAPI.Hooks
    private void OnShop(PlayerShopEventArgs e)
    {
        var npcType = e.Npc.type;
        
        if (npcType != NPCID.DyeTrader && npcType != NPCID.WitchDoctor) return;
        
        if (VanillaShopPlayers.Contains(e.Player.Index)) return;

        // Bersihin toko asli
        for (int i = 0; i < 40; i++)
        {
            e.ShopInventory.item[i] = new Item();
        }

        // Isi toko sesuai NPC
        if (npcType == NPCID.DyeTrader)
        {
            if (!PlayerHardmodeShop.TryGetValue(e.Player.Index, out var isHm))
            {
                isHm = false;
            }

            if (isHm && !Main.hardMode)
            {
                isHm = false;
                PlayerHardmodeShop[e.Player.Index] = false;
                e.Player.SendErrorMessage("Dunia ini masih Pre-Hardmode! Toko otomatis dialihkan ke Pre-Hardmode.");
            }

            if (isHm) FillDyeTraderHardmode(e.ShopInventory);
            else FillDyeTraderPreHardmode(e.ShopInventory);
        }
        else if (npcType == NPCID.WitchDoctor)
        {
            FillWitchDoctor(e.ShopInventory);
        }
    }

    private void AddItem(Chest shop, int itemId, int price)
    {
        for (int i = 0; i < 40; i++)
        {
            if (shop.item[i].type == 0 || shop.item[i].stack == 0)
            {
                shop.item[i].SetDefaults(itemId);
                shop.item[i].shopCustomPrice = price; // Harga dalam Copper
                break;
            }
        }
    }

    private void FillDyeTraderPreHardmode(Chest shop)
    {
        AddItem(shop, ItemID.LesserHealingPotion, Item.buyPrice(0, 0, 1, 0));
        AddItem(shop, ItemID.LesserManaPotion, Item.buyPrice(0, 0, 1, 0));
        AddItem(shop, ItemID.HealingPotion, Item.buyPrice(0, 0, 3, 0));
        AddItem(shop, ItemID.ManaPotion, Item.buyPrice(0, 0, 3, 0));
        
        AddItem(shop, ItemID.IronskinPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.SwiftnessPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.RegenerationPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.NightOwlPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.ShinePotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.GillsPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.MiningPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.BuilderPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.CalmingPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.TrapsightPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.ArcheryPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.AmmoReservationPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.WarmthPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.WaterWalkingPotion, Item.buyPrice(0, 0, 8, 0));
        AddItem(shop, ItemID.FeatherfallPotion, Item.buyPrice(0, 0, 8, 0));

        AddItem(shop, ItemID.ObsidianSkinPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.SpelunkerPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.HunterPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.BattlePotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.HeartreachPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.ThornsPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.InvisibilityPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.MagicPowerPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.ManaRegenerationPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.SummoningPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.TitanPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.FlipperPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.FishingPotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.CratePotion, Item.buyPrice(0, 0, 15, 0));
        AddItem(shop, ItemID.GravitationPotion, Item.buyPrice(0, 0, 15, 0));

        AddItem(shop, ItemID.EndurancePotion, Item.buyPrice(0, 0, 20, 0));
        AddItem(shop, ItemID.WrathPotion, Item.buyPrice(0, 0, 20, 0));
        AddItem(shop, ItemID.RagePotion, Item.buyPrice(0, 0, 20, 0));
        AddItem(shop, ItemID.InfernoPotion, Item.buyPrice(0, 0, 20, 0));
    }

    private void FillDyeTraderHardmode(Chest shop)
    {
        AddItem(shop, ItemID.GreaterHealingPotion, Item.buyPrice(0, 0, 30, 0));
        AddItem(shop, ItemID.GreaterManaPotion, Item.buyPrice(0, 0, 30, 0));
        AddItem(shop, ItemID.LifeforcePotion, Item.buyPrice(0, 0, 75, 0));

        if (NPC.downedTowerSolar && NPC.downedTowerVortex && NPC.downedTowerNebula && NPC.downedTowerStardust)
        {
            AddItem(shop, ItemID.SuperHealingPotion, Item.buyPrice(0, 1, 50, 0));
            AddItem(shop, ItemID.SuperManaPotion, Item.buyPrice(0, 1, 50, 0));
        }
    }

    private void FillWitchDoctor(Chest shop)
    {
        AddItem(shop, ItemID.FlaskofFire, Item.buyPrice(0, 0, 20, 0));
        AddItem(shop, ItemID.FlaskofPoison, Item.buyPrice(0, 0, 20, 0));

        if (Main.hardMode)
        {
            AddItem(shop, ItemID.FlaskofGold, Item.buyPrice(0, 0, 50, 0));
            AddItem(shop, ItemID.FlaskofIchor, Item.buyPrice(0, 0, 50, 0));
            AddItem(shop, ItemID.FlaskofCursedFlames, Item.buyPrice(0, 0, 50, 0));
            AddItem(shop, ItemID.FlaskofParty, Item.buyPrice(0, 0, 20, 0));
        }

        if (NPC.downedPlantBoss)
        {
            AddItem(shop, ItemID.FlaskofNanites, Item.buyPrice(0, 0, 60, 0));
            AddItem(shop, ItemID.FlaskofVenom, Item.buyPrice(0, 0, 75, 0));
        }
    }
}
