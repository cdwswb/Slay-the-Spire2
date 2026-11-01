using SpireCompanion.Core;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireCompanion;

internal static class GameReader
{
    private static readonly string[] ConflictingControllers = ["SpireBuddy", "STS2AIAgent", "CombatSolver"];

    public static GameSnapshot Read()
    {
        var manager = RunManager.Instance;
        var run = manager.IsInProgress ? manager.DebugOnlyGetState() : null;
        if (run == null) return new("主菜单或无跑局", "无", 0, 0, 0, [], "无", false, "尚未进入游戏");
        var player = LocalContext.GetMe(run);
        if (player == null) return new("状态未就绪", "未知", 0, 0, 0, [], "无", false, "没有本地角色");
        var combat = CombatManager.Instance;
        var state = combat.IsInProgress ? combat.DebugOnlyGetState() : null;
        var handUi = NCombatRoom.Instance?.Ui?.Hand;
        var conflict = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name).FirstOrDefault(n => ConflictingControllers.Contains(n));
        var reason = run.Players.Count != 1 ? "当前仅支持单人" :
            conflict != null ? $"检测到 {conflict}，本阶段仅只读，避免双重控制" :
            manager.NetService?.IsGameLoading == true ? "游戏加载中" :
            state == null ? "当前不在战斗中" :
            state.CurrentSide != CombatSide.Player || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play ? "不是玩家可操作阶段" :
            combat.PlayerActionsDisabled ? "玩家动作已禁用" :
            manager.ActionExecutor.IsRunning || !manager.ActionQueueSet.IsEmpty ? "动作正在结算" :
            NOverlayStack.Instance?.Peek() != null ? "请先关闭覆盖界面或完成选牌" :
            handUi == null || handUi.InCardPlay || handUi.CurrentMode != NPlayerHand.Mode.Play ? "手牌尚未就绪" : "";
        return new(run.CurrentRoom?.GetType().Name ?? "未知场景", Text(player.Character.Title),
            player.Creature.CurrentHp, player.Creature.MaxHp, player.Gold,
            state != null ? player.PlayerCombatState?.Hand.Cards.Select(c => Text(c.Title)).ToArray() ?? [] : [],
            state != null ? $"{state.RoundNumber}:{state.CurrentSide}:{player.PlayerCombatState?.Phase}" : "无",
            reason.Length == 0, reason,
            $"{RuntimeHelpers.GetHashCode(run)}:{(state != null ? RuntimeHelpers.GetHashCode(state) : 0)}:" +
            $"{player.Creature.Block}:{(state != null ? player.PlayerCombatState?.Energy : 0)}:" +
            string.Join(";", state?.Enemies.Select(e => $"{RuntimeHelpers.GetHashCode(e)}:{e.CurrentHp}:{e.Block}") ?? []))
        {
            Energy = state != null ? player.PlayerCombatState?.Energy : null,
            Block = player.Creature.Block,
            Cards = state != null ? player.PlayerCombatState?.Hand.Cards.Select((c, i) => Card(c, i, true)).ToArray() ?? [] : [],
            Deck = player.Deck.Cards.Select((c, i) => Card(c, i, false)).ToArray(),
            Relics = player.Relics.Select((r, i) => new ItemView(i, r.Id.Entry, Text(r.Title), Shorten(Plain(Text(r.DynamicDescription)), 80))).ToArray(),
            Potions = player.PotionSlots.Select((p, i) => p == null ? null : new ItemView(i, p.Id.Entry, Text(p.Title), Shorten(Plain(Text(p.DynamicDescription)), 80)))
                .OfType<ItemView>().ToArray(),
            Room = ReadRoom(run),
            Floor = run.TotalFloor,
            Ascension = run.AscensionLevel,
            Enemies = state?.Enemies.Select((e, i) => new EnemyView(i, e.Monster?.Id.Entry ?? "unknown",
                e.Monster != null ? Text(e.Monster.Title) : "未知敌人", e.CurrentHp, e.MaxHp, e.Block,
                IntentOf(e))).ToArray() ?? []
        };
    }

    public static void EndTurn()
    {
        // Read again immediately before mutation; never retain a live Player in background work.
        var snapshot = Read();
        if (!snapshot.CanEndTurn) throw new InvalidOperationException(snapshot.BlockReason);
        var run = RunManager.Instance.DebugOnlyGetState()!;
        Player player = LocalContext.GetMe(run) ?? throw new InvalidOperationException("本地角色已失效");
        PlayerCmd.EndTurn(player, canBackOut: false);
    }

    /// <summary>
    /// Out-of-combat screens. Event, shop and rest site expose plain model lists, so they can be
    /// read without touching UI text; the card reward and map are the two exceptions noted below.
    /// </summary>
    private static RoomView? ReadRoom(RunState run)
    {
        // The map is an overlay that can be opened from any room, not only from MapRoom.
        if (NMapScreen.Instance is { IsOpen: true }) return MapView(run);
        if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen selectScreen)
        {
            // The prompt may be a Label or a RichTextLabel; read the "text" property generically.
            var promptNode = selectScreen.GetNodeOrNull<Godot.Control>("%BottomLabel");
            var prompt = promptNode?.Get("text").AsString() ?? "";
            if (string.IsNullOrWhiteSpace(prompt)) prompt = "选择一张牌";
            var cards = SortedGridHolders(selectScreen).Select(holder => holder.CardModel).OfType<CardModel>().ToArray();
            var options = RoomOptions.MergeDuplicates(cards.Select((card, index) => new RoomOptionView(
                index.ToString(), Text(card.Title),
                $"费用 {(card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString())}" +
                (card.IsUpgraded ? " · 已升级" : "") + "：" + Shorten(CardText(card), 100), false)).ToArray());
            return new RoomView("Select", StripTags(prompt), "选中后需要确认", options)
                { Id = ScreenId(selectScreen), Required = ScreenPrompt.RequiredCards(StripTags(prompt)) };
        }
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewardsScreen)
        {
            var buttons = FindAll<NRewardButton>(rewardsScreen).Where(button => button.Reward != null && button.IsEnabled).ToList();
            var state = run;
            var me = state != null ? LocalContext.GetMe(state) : null;
            var potionsFull = me != null && me.PotionSlots.All(slot => slot != null);
            var options = buttons.Select((button, index) =>
            {
                var reward = button.Reward!;
                var replaces = potionsFull && reward is PotionReward;
                return new RoomOptionView(index.ToString(), RewardName(reward),
                    Text(reward.Description) + (replaces ? "（药水栏已满，将替换最右侧药水）" : ""), false);
            }).ToList();
            if (FindFirst<NProceedButton>(rewardsScreen)?.IsEnabled == true)
                options.Add(new RoomOptionView("proceed", "离开奖励（继续）", "剩余奖励将放弃", false));
            return new RoomView("Rewards", "战斗奖励", "领取想要的东西，或离开", options.ToArray()) { Id = ScreenId(rewardsScreen) };
        }
        if (NOverlayStack.Instance?.Peek() is NChooseARelicSelection relicScreen)
        {
            var holders = FindAll<NRelicBasicHolder>(relicScreen).ToList();
            var options = holders.Select((holder, index) => new RoomOptionView(index.ToString(),
                Text(holder.Relic?.Model?.Title), Text(holder.Relic?.Model?.DynamicDescription), false)).ToList();
            var skip = relicScreen.GetNodeOrNull<NClickableControl>("SkipButton");
            if (skip is { IsEnabled: true, Visible: true })
                options.Add(new RoomOptionView("skip", "跳过这个遗物", "不拿遗物，继续前进", false));
            return new RoomView("Relic", "选择遗物", "只能选一个，选中后立即获得", options.ToArray()) { Id = ScreenId(relicScreen) };
        }
        if (NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen rewardScreen)
        {
            var cards = SortedCardHolders(rewardScreen).Select(holder => holder.CardModel).OfType<CardModel>().ToArray();
            if (cards.Length > 0)
            {
                var rewardOptions = cards.Select((card, index) => new RoomOptionView(index.ToString(), Text(card.Title),
                    $"费用 {(card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString())}" +
                    (card.IsUpgraded ? " · 已升级" : "") + "：" + Shorten(CardText(card), 100), false)).ToList();
                // The skip button is a real, clickable action; without it the model can only ever add a card.
                if (CanSkipReward()) rewardOptions.Add(new RoomOptionView("skip", "跳过这张奖励", "不加入牌组，继续前进", false));
                return new RoomView("Reward", "卡牌奖励", "选择一张加入牌组，或跳过", rewardOptions.ToArray())
                    { Id = ScreenId(rewardScreen) };
            }
        }
        switch (run.CurrentRoom)
        {
            case EventRoom eventRoom:
                // Reading mutable event models throws CanonicalModelException for some events;
                // the UI buttons are the reliable source, and a failure must not kill the snapshot.
                try
                {
                    var canonical = eventRoom.CanonicalEvent;
                    var title = canonical == null ? "事件" : Text(canonical.Title);
                    var body = canonical == null ? "" : Text(canonical.Description);
                    var ui = NEventRoom.Instance;
                    var options = ui == null
                        ? []
                        : FindAll<NEventOptionButton>(ui).Select((button, index) =>
                        {
                            var option = button.Option;
                            // Ancient-event relics are options carrying a relic model: show the relic itself.
                            var relic = option.Relic;
                            return new RoomOptionView(
                                index.ToString(),
                                relic != null ? Text(relic.Title) : Text(option.Title),
                                (relic != null ? Text(relic.DynamicDescription) : Text(option.Description)) +
                                (option.IsProceed ? "（离开）" : ""),
                                option.IsLocked);
                        }).ToArray();
                    return new RoomView("Event", title, body, options) { Id = ScreenId(eventRoom) };
                }
                catch (Exception error)
                {
                    Godot.GD.PushWarning($"[SpireCompanion] event_state: {error.Message}");
                    return new RoomView("Event", "事件（内容暂不可读）", "", []) { Id = ScreenId(eventRoom) };
                }
            case TreasureRoom:
            {
                var root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
                var ui = FindFirst<NTreasureRoom>(root);
                if (ui == null) return new RoomView("Treasure", "宝箱房", "等待界面加载", []) { Id = ScreenId(run) };
                var options = new List<RoomOptionView>();
                var collection = ui.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
                var relicCount = RunManager.Instance?.TreasureRoomRelicSynchronizer?.CurrentRelics?.Count ?? 0;
                if (collection?.Visible != true)
                {
                    // An empty chest makes the game's own award animation throw; leave instead of opening.
                    if (relicCount == 0 && ui.ProceedButton?.IsEnabled == true)
                    {
                        Godot.GD.Print("[SpireCompanion] treasure_empty");
                        options.Add(new RoomOptionView("proceed", "离开宝箱房（继续）", "这个宝箱没有遗物", false));
                    }
                    else options.Add(new RoomOptionView("open", "打开宝箱", "打开后可以拿取遗物", false));
                }
                else
                {
                    var holders = FindAll<NTreasureRoomRelicHolder>(collection).Where(holder => holder.IsEnabled && holder.Visible).ToList();
                    options.AddRange(holders.Select((holder, index) => new RoomOptionView(index.ToString(),
                        Text(holder.Relic?.Model?.Title), Text(holder.Relic?.Model?.DynamicDescription), false)));
                    if (ui.ProceedButton?.IsEnabled == true)
                        options.Add(new RoomOptionView("proceed", "离开宝箱房（继续）", "不拿剩余遗物", false));
                }
                return new RoomView("Treasure", "宝箱房", "先打开，再挑一个遗物", options.ToArray()) { Id = ScreenId(ui) };
            }
            case MerchantRoom shop:
                var inventory = shop.GetLocalInventory();
                var proceed = LeaveOption(NMerchantRoom.Instance?.ProceedButton?.IsEnabled == true);
                // Stock is only interactive after the inventory UI is open (same rule Spire Buddy follows).
                if (inventory == null || NMerchantRoom.Instance?.Inventory?.IsOpen != true)
                    return new RoomView("Shop", "商店", "先打开商店界面", [new RoomOptionView("open", "查看商品", "打开商店界面", false), proceed])
                        { Id = ScreenId(shop) };
                var shopState = run;
                var shopMe = shopState != null ? LocalContext.GetMe(shopState) : null;
                var potionsFull = shopMe != null && shopMe.PotionSlots.All(slot => slot != null);
                var items = inventory.AllEntries.Select((entry, index) =>
                {
                    var (name, detail) = Entry(entry);
                    if (entry is MerchantPotionEntry && potionsFull)
                        detail += "（药水栏已满，将替换最右侧）";
                    return new RoomOptionView(index.ToString(), name, detail, !entry.IsStocked || !entry.EnoughGold);
                }).ToList();
                items.Add(new RoomOptionView("close", "关闭商品界面", "关闭后才能离开商店", false));
                if (NMerchantRoom.Instance?.ProceedButton?.IsEnabled == true) items.Add(proceed);
                return new RoomView("Shop", "商店", "金币有限，先买最缺的；不想买了就关界面再离开", items.ToArray())
                    { Id = ScreenId(shop) };
            case RestSiteRoom rest:
                return new RoomView("Rest", "休息点", "选择休息或升级",
                    rest.Options.Select((option, index) => new RoomOptionView(
                        string.IsNullOrEmpty(option.OptionId) ? index.ToString() : option.OptionId,
                        Text(option.Title), Text(option.Description), !option.IsEnabled))
                    .Append(LeaveOption(NRestSiteRoom.Instance?.ProceedButton?.IsEnabled == true)).ToArray())
                { Id = ScreenId(rest) };
            case MapRoom:
                return MapView(run);
            default:
                return null;
        }
    }

    /// <summary>Only reachable nodes. With the map open, use the travelable UI nodes so ids match click order.</summary>
    private static RoomView MapView(RunState run)
    {
        var screen = NMapScreen.Instance;
        if (screen is { IsOpen: true } && TravelableNodes(screen) is { Count: > 0 } travelable)
            return new RoomView("Map", "地图", "选择下一层路线",
                travelable.Select((node, index) => new RoomOptionView(index.ToString(),
                    PointName(node.Point!.PointType), $"第 {node.Point.coord.col} 列", false)).ToArray())
                { Id = ScreenId(screen) };
        var point = run.CurrentMapPoint ?? run.Map?.StartingMapPoint;
        var options = point?.Children.OrderBy(child => child.PointType.ToString())
            .Select((child, index) => new RoomOptionView(index.ToString(), PointName(child.PointType), "可达节点", false))
            .ToArray() ?? [];
        return new RoomView("Map", "地图", options.Length == 0 ? "当前没有可前进的节点" : "选择下一层路线", options)
            { Id = ScreenId(NMapScreen.Instance ?? (object)run) };
    }

    private static List<NMapPoint> TravelableNodes(Godot.Node screen) => FindAll<NMapPoint>(screen)
        .Where(node => node.State == MapPointState.Travelable && node.Point != null)
        .OrderBy(node => node.Point!.coord.col).ToList();

    private static (string Name, string Detail) Entry(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card when card.CreationResult?.Card is { } model =>
            (Text(model.Title), $"{card.Cost} 金币{(card.IsOnSale ? " · 打折" : "")}{(card.IsStocked ? "" : " · 已售出")}：" +
                Shorten(CardText(model), 100)),
        MerchantRelicEntry relic when relic.Model is { } model =>
            (Text(model.Title), $"{relic.Cost} 金币{(relic.IsStocked ? "" : " · 已售出")}：" +
                Shorten(Plain(Text(model.DynamicDescription)), 80)),
        MerchantPotionEntry potion when potion.Model is { } model =>
            (Text(model.Title), $"{potion.Cost} 金币{(potion.IsStocked ? "" : " · 已售出")}"),
        MerchantCardRemovalEntry removal => ("删牌服务", $"{removal.Cost} 金币{(removal.Used ? " · 已使用" : "")}"),
        _ => ("商品", $"{entry.Cost} 金币")
    };

    private static string PointName(MapPointType type) => type switch
    {
        MapPointType.Monster => "小怪",
        MapPointType.Elite => "精英",
        MapPointType.Boss => "Boss",
        MapPointType.Shop => "商店",
        MapPointType.RestSite => "休息点",
        MapPointType.Treasure => "宝箱",
        MapPointType.Ancient => "远古",
        _ => "未知"
    };

    /// <summary>Visual order, because the reward screen's tree order is not the order the player sees.</summary>
    private static List<NCardHolder> SortedCardHolders(Godot.Node root) => CardHolders(root)
        .OrderBy(holder => holder.GlobalPosition.Y).ThenBy(holder => holder.GlobalPosition.X).ToList();

    /// <summary>Rest site buttons carry the model option, so match by id instead of trusting list order.</summary>
    public static void SelectRestOption(string optionId)
    {
        var room = NRestSiteRoom.Instance ?? throw new InvalidOperationException("休息点界面已关闭。");
        var buttons = FindAll<NRestSiteButton>(room).ToList();
        var button = buttons.FirstOrDefault(candidate => string.Equals(candidate.Option.OptionId, optionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"该选项已不在界面中（界面有 {buttons.Count} 个选项）。");
        if (!button.Option.IsEnabled) throw new InvalidOperationException("该选项当前不可用。");
        button.ForceClick();
    }

    public static void CloseShop()
    {
        var room = NMerchantRoom.Instance ?? throw new InvalidOperationException("商店界面已关闭。");
        var back = FindAll<NBackButton>(room).LastOrDefault(button => button.IsEnabled && button.IsVisibleInTree())
            ?? throw new InvalidOperationException("找不到关闭商品界面的按钮。");
        back.ForceClick();
    }

    public static void OpenShop()
    {
        var room = NMerchantRoom.Instance ?? throw new InvalidOperationException("商店界面已关闭。");
        var button = room.MerchantButton ?? throw new InvalidOperationException("找不到商店按钮。");
        if (!button.IsEnabled) throw new InvalidOperationException("商店按钮当前不可用。");
        button.ForceClick();
    }

    public static void Purchase(string entryId)
    {
        if (!int.TryParse(entryId, out var index)) throw new InvalidOperationException("商品编号无效。");
        var room = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom as MerchantRoom
            ?? throw new InvalidOperationException("当前不在商店。");
        var entry = room.GetLocalInventory()?.AllEntries.ElementAtOrDefault(index)
            ?? throw new InvalidOperationException("商品已下架，请重新打开商店。");
        if (!entry.IsStocked) throw new InvalidOperationException("这件商品已售出。");
        if (!entry.EnoughGold) throw new InvalidOperationException($"金币不足：需要 {entry.Cost}。");
        var inventory = room.GetLocalInventory() ?? throw new InvalidOperationException("商店库存未就绪。");
        if (entry is MerchantPotionEntry)
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            var player = run != null ? LocalContext.GetMe(run) : null;
            if (player != null && player.PotionSlots.All(slot => slot != null))
            {
                // Buying a potion with a full belt would stall on the game's discard prompt.
                var slot = Enumerable.Range(0, player.PotionSlots.Count).Last(i => player.PotionSlots[i] != null);
                var potion = player.PotionSlots[slot] ?? throw new InvalidOperationException("药水栏读取失败。");
                Godot.GD.Print("[SpireCompanion] shop_potion_replace");
                _ = DiscardThenPurchase(potion, inventory, entry);
                return;
            }
        }
        // Fire and forget, same call the game's own purchase button uses.
        _ = entry.OnTryPurchaseWrapper(inventory);
    }

    public static bool CanSkipReward() =>
        NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen screen && SkipButton(screen) != null;

    private static NCardRewardAlternativeButton? SkipButton(Godot.Node root) => FindFirst<NCardRewardAlternativeButton>(root);

    private static IEnumerable<T> FindAll<T>(Godot.Node node) where T : Godot.Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static T? FindFirst<T>(Godot.Node node) where T : Godot.Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T match) return match;
            var nested = FindFirst<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static List<NGridCardHolder> SortedGridHolders(Godot.Node root) => FindAll<NGridCardHolder>(root)
        .OrderBy(holder => holder.GlobalPosition.Y).ThenBy(holder => holder.GlobalPosition.X).ToList();

    private static RoomOptionView LeaveOption(bool available) =>
        new("proceed", "离开 / 继续前进", available ? "不在这里继续操作" : "当前还不能离开", !available);

    /// <summary>LocString text can carry BBCode; the model should read plain prose.</summary>
    private static string Plain(string value) => System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", ""), "\\[(?:/|lb|rb)[^\\]]*\\]|\\[[a-z]+[^\\]]*\\]", "");

    private static string Shorten(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";

    private static string ScreenId(object instance) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance).ToString();

    private static string StripTags(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", "");

    private static string RewardName(Reward reward) => reward switch
    {
        GoldReward gold => $"金币 {gold.Amount}",
        PotionReward potion when potion.Potion != null => $"药水：{Text(potion.Potion.Title)}",
        RelicReward relic when relic.Relic != null => $"遗物：{Text(relic.Relic.Title)}",
        CardReward => "卡牌奖励",
        CardRemovalReward => "删牌奖励",
        _ => "奖励"
    };

    public static void ClaimReward(int index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NRewardsScreen
            ?? throw new InvalidOperationException("奖励界面已关闭。");
        var buttons = FindAll<NRewardButton>(screen).Where(button => button.Reward != null && button.IsEnabled).ToList();
        if (index < 0 || index >= buttons.Count) throw new InvalidOperationException("该奖励已被领取。");
        var reward = buttons[index].Reward!;
        var button = buttons[index];
        if (reward is PotionReward)
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            var player = runState != null ? LocalContext.GetMe(runState) : null;
            if (player != null && player.PotionSlots.All(slot => slot != null))
            {
                // Default policy: drop the rightmost potion, then claim the reward.
                var slot = Enumerable.Range(0, player.PotionSlots.Count).Last(i => player.PotionSlots[i] != null);
                var potion = player.PotionSlots[slot] ?? throw new InvalidOperationException("药水栏读取失败。");
                _ = DiscardThenClaim(potion, button);
                return;
            }
        }
        button.ForceClick();
    }

    private static async Task DiscardThenPurchase(PotionModel potion, MerchantInventory inventory, MerchantEntry entry)
    {
        try { await PotionCmd.Discard(potion); }
        catch (Exception error)
        {
            Godot.GD.PushError($"[SpireCompanion] shop_potion_discard: {error.Message}");
            return;
        }
        Godot.Callable.From(() =>
        {
            try
            {
                if (entry.IsStocked && entry.EnoughGold) _ = entry.OnTryPurchaseWrapper(inventory);
            }
            catch (Exception error) { Godot.GD.PushError($"[SpireCompanion] shop_potion_buy: {error.Message}"); }
        }).CallDeferred();
    }

    private static async Task DiscardThenClaim(PotionModel potion, NRewardButton button)
    {
        try { await PotionCmd.Discard(potion); }
        catch (Exception error)
        {
            Godot.GD.PushError($"[SpireCompanion] potion_discard: {error.Message}");
            return;
        }
        Godot.Callable.From(() =>
        {
            if (Godot.GodotObject.IsInstanceValid(button)) button.ForceClick();
        }).CallDeferred();
    }

    public static void OpenTreasure()
    {
        var root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
        var button = FindFirst<NTreasureButton>(root) ?? throw new InvalidOperationException("宝箱按钮不可用。");
        button.ForceClick();
    }

    public static void ClaimTreasureRelic(int index)
    {
        var root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
        var ui = FindFirst<NTreasureRoom>(root) ?? throw new InvalidOperationException("宝箱房已关闭。");
        var collection = ui.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection")
            ?? throw new InvalidOperationException("宝箱还没有打开。");
        var holders = FindAll<NTreasureRoomRelicHolder>(collection).Where(holder => holder.IsEnabled && holder.Visible).ToList();
        if (index < 0 || index >= holders.Count) throw new InvalidOperationException("该遗物已不可拿取。");
        holders[index].ForceClick();
    }

    public static void SelectRelic(int index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseARelicSelection
            ?? throw new InvalidOperationException("遗物选择界面已关闭。");
        var holders = FindAll<NRelicBasicHolder>(screen).ToList();
        if (index < 0 || index >= holders.Count) throw new InvalidOperationException("该遗物已不在候选中。");
        holders[index].ForceClick();
    }

    public static void SkipRelic()
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseARelicSelection
            ?? throw new InvalidOperationException("遗物选择界面已关闭。");
        var skip = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        if (skip is not { IsEnabled: true }) throw new InvalidOperationException("这个遗物选择不能跳过。");
        skip.ForceClick();
    }

    public static void SelectEventOption(int index)
    {
        var room = NEventRoom.Instance ?? throw new InvalidOperationException("事件界面已关闭。");
        var buttons = FindAll<NEventOptionButton>(room).ToList();
        if (index < 0 || index >= buttons.Count) throw new InvalidOperationException("该事件选项已不在界面中。");
        if (buttons[index].Option.IsLocked) throw new InvalidOperationException("该事件选项当前不可选。");
        buttons[index].ForceClick();
    }

    public static void ChooseMapNode(int index)
    {
        var screen = NMapScreen.Instance ?? throw new InvalidOperationException("地图未打开。");
        var travelable = TravelableNodes(screen);
        if (index < 0 || index >= travelable.Count) throw new InvalidOperationException("该节点当前不可前往。");
        screen.OnMapPointSelectedLocally(travelable[index]);
    }

    private static string IntentOf(Creature creature)
    {
        try
        {
            if (creature.Monster?.NextMove is not { } move) return "";
            var targets = creature.CombatState?.PlayerCreatures;
            var labels = new List<string>();
            foreach (var intent in move.Intents)
            {
                try
                {
                    if (targets != null) labels.Add(StripTags(intent.GetIntentLabel(targets, creature).GetFormattedText()));
                }
                catch { }
            }
            return string.Join(" / ", labels);
        }
        catch { return ""; }
    }

    /// <summary>Read-only card lookup for the chat tool. Never mutates game state.</summary>
    public static string? LookupCardText(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var cards = ModelDb.AllCards.ToList();
            CardModel? exact = null;
            CardModel? partial = null;
            foreach (var card in cards)
            {
                var title = Text(card.Title);
                if (string.Equals(title, name, StringComparison.Ordinal)) { exact = card; break; }
                if (partial == null && (title.Contains(name, StringComparison.Ordinal) || name.Contains(title, StringComparison.Ordinal)))
                    partial = card;
            }
            var found = exact ?? partial;
            if (found == null) return null;
            var cost = found.EnergyCost.CostsX ? "X" : found.EnergyCost.GetAmountToSpend().ToString();
            return $"{Text(found.Title)}（{cost} 费）：{Shorten(CardText(found), 200)}";
        }
        catch (Exception error)
        {
            Godot.GD.PushWarning($"[SpireCompanion] lookup_card: {error.Message}");
            return null;
        }
    }

    public static string? LookupRelicText(string name) => LookupCatalog(name, ModelDb.AllRelics.Select(relic => (Text(relic.Title), Plain(Text(relic.DynamicDescription)))));
    public static string? LookupPotionText(string name) => LookupCatalog(name, ModelDb.AllPotions.Select(potion => (Text(potion.Title), Plain(Text(potion.DynamicDescription)))));

    private static string? LookupCatalog(string name, IEnumerable<(string Title, string Text)> entries)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            (string Title, string Text)? exact = null;
            (string Title, string Text)? partial = null;
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Title, name, StringComparison.Ordinal)) { exact = entry; break; }
                if (partial == null && (entry.Title.Contains(name, StringComparison.Ordinal) || name.Contains(entry.Title, StringComparison.Ordinal)))
                    partial = entry;
            }
            var found = exact ?? partial;
            return found == null ? null : $"{found.Value.Title}：{Shorten(found.Value.Text, 200)}";
        }
        catch (Exception error)
        {
            Godot.GD.PushWarning($"[SpireCompanion] catalog_lookup: {error.Message}");
            return null;
        }
    }

    /// <summary>Read-only live state slices for the chat tools.</summary>
    public static string QueryState(string scope)
    {
        var snapshot = Read();
        var text = (scope ?? "screen").Trim().ToLowerInvariant() switch
        {
            "hand" => "手牌：" + (snapshot.Cards.Length == 0 ? "无" : string.Join(" | ", snapshot.Cards.Select(card => $"{card.Name}({card.Cost})：{Shorten(card.Text, 100)}"))),
            "deck" => $"牌组 {snapshot.Deck.Length} 张：" + string.Join("、", snapshot.Deck.Select(card => card.Name)),
            "relics" => "遗物：" + (snapshot.Relics.Length == 0 ? "无" : string.Join(" | ", snapshot.Relics.Select(relic => $"{relic.Name}：{Shorten(relic.Text, 80)}"))),
            "potions" => "药水：" + (snapshot.Potions.Length == 0 ? "无" : string.Join(" | ", snapshot.Potions.Select(potion => $"{potion.Name}：{Shorten(potion.Text, 80)}"))),
            "enemies" => "敌人：" + (snapshot.Enemies.Length == 0 ? "无" : string.Join("、", snapshot.Enemies.Select(enemy => $"{enemy.Name} {enemy.Hp}/{enemy.MaxHp} 格挡{enemy.Block}" + (enemy.Intent.Length > 0 ? $" 意图[{enemy.Intent}]" : "")))),
            "run" => $"场景 {snapshot.Scene}；{snapshot.Character} A{snapshot.Ascension} 第 {snapshot.Floor} 层 HP {snapshot.Hp}/{snapshot.MaxHp}；金币 {snapshot.Gold}；能量 {snapshot.Energy?.ToString() ?? "—"}；格挡 {snapshot.Block}；战斗阶段 {snapshot.CombatTurn}",
            "map" or "screen" => SnapshotBrief.Describe(snapshot),
            _ => SnapshotBrief.Describe(snapshot)
        };
        return Shorten(text, 1500);
    }

    public static bool IsGameOver() => NOverlayStack.Instance?.Peek() is NGameOverScreen;

    public static bool CanProceed()
    {
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards && FindAll<NProceedButton>(rewards).Any(button => button.IsEnabled)) return true;
        if (NRestSiteRoom.Instance?.ProceedButton is { IsEnabled: true }) return true;
        if (NMerchantRoom.Instance?.ProceedButton is { IsEnabled: true }) return true;
        return false;
    }

    public static void Proceed()
    {
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards &&
            FindAll<NProceedButton>(rewards).FirstOrDefault(button => button.IsEnabled) is { } rewardProceed)
        {
            rewardProceed.ForceClick();
            return;
        }
        if (NRestSiteRoom.Instance?.ProceedButton is { IsEnabled: true } rest)
        {
            rest.ForceClick();
            return;
        }
        if (NMerchantRoom.Instance?.ProceedButton is { IsEnabled: true } shop)
        {
            shop.ForceClick();
            return;
        }
        var root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
        if (FindFirst<NTreasureRoom>(root) is { ProceedButton.IsEnabled: true } treasure)
        {
            treasure.ProceedButton.ForceClick();
            return;
        }
        throw new InvalidOperationException("当前界面没有可用的继续按钮。");
    }

    /// <summary>Toggles one card in a grid selection screen (upgrade, remove, transform).</summary>
    public static void SelectGridCard(int index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen
            ?? throw new InvalidOperationException("选牌界面已关闭。");
        var holders = SortedGridHolders(screen);
        if (index < 0 || index >= holders.Count) throw new InvalidOperationException("该卡牌已不在候选里。");
        var grid = FindFirst<NCardGrid>(screen) ?? throw new InvalidOperationException("找不到选牌网格。");
        grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holders[index]);
    }

    public static bool CanConfirmSelection() =>
        NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen screen &&
        FindAll<NConfirmButton>(screen).Any(button => button.IsEnabled && button.IsVisibleInTree());

    public static void ConfirmSelection()
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen
            ?? throw new InvalidOperationException("选牌界面已关闭。");
        var confirm = FindAll<NConfirmButton>(screen).FirstOrDefault(button => button.IsEnabled && button.IsVisibleInTree())
            ?? throw new InvalidOperationException("还没有可以确认的选牌。");
        confirm.ForceClick();
    }

    /// <summary>Picks one reward card; the caller must have just shown the player the same index.</summary>
    public static void SelectReward(int index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen
            ?? throw new InvalidOperationException("卡牌奖励界面已关闭，请重新选择。");
        var holders = SortedCardHolders(screen);
        if (index < 0 || index >= holders.Count) throw new InvalidOperationException("该卡牌已不在奖励中。");
        var holder = holders[index];
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
    }

    public static void SkipReward()
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen
            ?? throw new InvalidOperationException("卡牌奖励界面已关闭，请重新选择。");
        var button = SkipButton(screen) ?? throw new InvalidOperationException("这个奖励没有跳过选项。");
        button.ForceClick();
    }

    // The card reward screen exposes cards only through its holders; there is no model list to read.
    private static IEnumerable<NCardHolder> CardHolders(Godot.Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is NCardHolder holder) yield return holder;
            foreach (var nested in CardHolders(child)) yield return nested;
        }
    }

    private static CardView Card(CardModel card, int index, bool inHand) => new(index, card.Id.Entry, Text(card.Title),
        card.EnergyCost.CostsX ? "X" : inHand ? card.EnergyCost.GetAmountToSpend().ToString() : "未在手牌，不计算动态费用",
        card.IsUpgraded, Shorten(CardText(card), inHand ? 140 : 80));

    /// <summary>CardModel.Description is a template; the pile-aware call renders the real numbers.</summary>
    private static string CardText(CardModel card)
    {
        try { return Shorten(Plain(card.GetDescriptionForPile(PileType.Hand)).Replace("\n", " "), 140); }
        catch (Exception error)
        {
            Godot.GD.PushWarning($"[SpireCompanion] card_text: {error.Message}");
            try { return Shorten(Plain(Text(card.Description)).Replace("\n", " "), 140); }
            catch { return ""; }
        }
    }

    private static string Text(object? value) => value == null ? "未知" : value is LocString loc ? loc.GetFormattedText() : value.ToString() ?? "未知";
}
