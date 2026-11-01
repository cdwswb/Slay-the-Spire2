using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Concurrent;
using System.Text.Json;
using SpireCompanion.Core;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace SpireCompanion;

internal sealed class CompanionPanel
{
    /// <summary>Screens the companion can act on. One list for both the card and auto mode so they cannot drift.</summary>
    private static readonly HashSet<string> ExecutableKinds =
        ["Reward", "Rest", "Shop", "Select", "Event", "Map", "Relic", "Rewards", "Treasure"];

    private readonly ConcurrentQueue<Action> mainQueue = new();
    private ChatPanel chat = null!;
    private PanelContainer panel = null!;
    private Label status = null!;
    private Label stateLabel = null!;
    private readonly SceneTree tree;
    private ScrollContainer chatScroll = null!;
    private ScrollContainer settingsScroll = null!;
    private ScrollContainer gameScroll = null!;
    private bool dragging;
    private Vector2 dragOffset;
    private Button pause = null!;
    private Button autoToggle = null!;
    private bool autoEnabled;
    private CancellationTokenSource? autoCts;
    private string autoPending = "";
    private string[] autoPendingIds = [];
    private readonly HashSet<string> autoSelectedIds = [];
    private int autoActionFails;
    private string pendingShopId = "";
    private string pendingShopName = "";
    private double pendingShopWait;
    private string lastCombatKey = "";
    private bool lowHpWarned;
    private double quipCooldown;
    private bool quipBusy;
    private bool autoSuspended;
    private Button resumeAuto = null!;
    private double autoWait;
    private string autoActedKey = "";
    private bool autoSelectPendingConfirm;
    private string shopOpenedFor = "";
    private string windowStatePath = "";
    private Label autoLabel = null!;
    private PanelContainer decisionCard = null!;
    private VBoxContainer decisionButtons = null!;
    private Label decisionStatus = null!;
    private string decisionSignature = "";
    private Button toggle = null!;
    private Button solverStart = null!;
    private Button solverToggle = null!;
    private bool solverAuto;
    private bool solverSuspended;
    private bool voiceEnabled;
    private Button voiceToggle = null!;
    private Label voiceStatus = null!;
    private readonly VoiceClient voice = new();
    private readonly RunPlan plan = new();
    private Label planLabel = null!;
    private string planPath = "";
    private bool reviewInFlight;
    private string pinnedName = "";
    private string lastDirective = "";
    private double pinnedAt;
    private double solverRetry;
    private bool defeatNotified;
    private bool reportedReady;
    private bool layoutReported;
    private int ticks;
    private TabContainer tabs = null!;
    private double remaining;
    private bool f9Down;
    private bool wDown;
    private double lastWTap = -10;
    private string message = "只读建议：不会自动出牌，自动执行交给求解器。";

    public CompanionPanel(SceneTree tree)
    {
        this.tree = tree;
        var layer = new CanvasLayer { Layer = 90 };
        panel = new PanelContainer { Position = new Vector2(24, 70) };
        // Use game fonts, never depend on operating-system font enumeration.
        panel.Theme = new Theme();
        UiStyle.Apply(panel.Theme);
        var windowStyle = UiStyle.Box(UiStyle.Background, UiStyle.Border, 20, 16);
        windowStyle.ShadowColor = new Color(0.42f, 0.26f, 0.04f, 0.18f);
        windowStyle.ShadowSize = 10;
        panel.AddThemeStyleboxOverride("panel", windowStyle);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        layout.AddChild(header);
        var title = new Label
        {
            Text = "尖塔 AI 陪玩", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Stop
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.GuiInput += input =>
        {
            if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left && mouse.Pressed)
            { dragging = true; dragOffset = title.GetGlobalMousePosition() - panel.Position; }
        };
        header.AddChild(title);
        if (UiStyle.Avatar is { } face)
            header.AddChild(new TextureRect
            {
                Texture = face, CustomMinimumSize = new Vector2(44, 44),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
            });
        stateLabel = new Label { Text = "就绪", VerticalAlignment = VerticalAlignment.Center };
        stateLabel.AddThemeColorOverride("font_color", UiStyle.Good);
        header.AddChild(stateLabel);
        autoLabel = new Label { Text = "自动：关", VerticalAlignment = VerticalAlignment.Center };
        autoLabel.AddThemeColorOverride("font_color", UiStyle.Muted);
        header.AddChild(autoLabel);
        var close = new Button { Text = "关闭" }; close.Pressed += () => panel.Hide();
        UiStyle.Size(close, 40, 84); header.AddChild(close);

        tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; layout.AddChild(tabs);
        chatScroll = Page("聊天", out var chatBody);
        settingsScroll = Page("设置", out var settingsBody);
        gameScroll = Page("游戏状态", out var body);
        UiStyle.StyleTabs(tabs);

        // Executable choices go first: they are the only thing the player must act on.
        var decisionBody = UiStyle.Card(body, "可以执行", out decisionCard);
        decisionCard.Visible = false;
        decisionBody.AddChild(UiStyle.Hint("只执行你点下的选择，不会自动选或自动购买。"));
        decisionStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        decisionStatus.AddThemeColorOverride("font_color", UiStyle.AccentDeep);
        decisionBody.AddChild(decisionStatus);
        decisionButtons = new VBoxContainer();
        decisionButtons.AddThemeConstantOverride("separation", 6);
        decisionBody.AddChild(decisionButtons);

        var statusCard = UiStyle.Card(body, "当前局面");
        status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 80) };
        statusCard.AddChild(status);
        var controlCard = UiStyle.Card(body, "托管与求解器");
        windowStatePath = Path.Combine(ProjectSettings.GlobalizePath("user://spire-companion"), "window-state.json");
        var saved = WindowState.Load(windowStatePath);
        autoEnabled = saved.AutoEnabled;
        solverAuto = saved.SolverAuto;
        voiceEnabled = saved.VoiceEnabled;
        autoToggle = new Button { Text = autoEnabled ? "局外自动操作：开" : "局外自动操作：关" };
        autoToggle.Pressed += ToggleAuto;
        UiStyle.Secondary(autoToggle); UiStyle.Size(autoToggle, 44);
        controlCard.AddChild(autoToggle);
        controlCard.AddChild(UiStyle.Hint("开启后由模型在奖励结算/休息点/商店/选牌/事件/地图/遗物/宝箱里选一项并执行，同一界面状态只操作一次（选牌会自动确认）；战斗仍归求解器。发消息或取消会暂停自动，点「恢复自动执行」继续。"));
        resumeAuto = new Button { Text = "恢复自动执行", Visible = false };
        resumeAuto.Pressed += ResumeAuto;
        voiceToggle = new Button { Text = voiceEnabled ? "语音输入：开" : "语音输入：关" };
        voiceToggle.Pressed += ToggleVoice;
        UiStyle.Secondary(voiceToggle); UiStyle.Size(voiceToggle, 40);
        controlCard.AddChild(voiceToggle);
        voiceStatus = new Label { Text = voiceEnabled ? "语音：等待连接" : "语音：未开启" };
        voiceStatus.AddThemeColorOverride("font_color", UiStyle.Muted);
        controlCard.AddChild(voiceStatus);
        voice.StatusChanged += status => mainQueue.Enqueue(() => { if (voiceStatus != null) voiceStatus.Text = "语音：" + status; });
        voice.TextReceived += (id, text) => mainQueue.Enqueue(() => HandleVoiceText(id, text));
        voice.IntentReceived += (id, intent) => mainQueue.Enqueue(() => HandleVoiceIntent(id, intent));
        UiStyle.Primary(resumeAuto); UiStyle.Size(resumeAuto, 42);
        controlCard.AddChild(resumeAuto);
        solverToggle = new Button { Text = solverAuto ? "自动接管战斗：开" : "自动接管战斗：关" };
        solverToggle.Pressed += ToggleSolverAuto;
        UiStyle.Secondary(solverToggle); UiStyle.Size(solverToggle, 44);
        controlCard.AddChild(solverToggle);
        solverStart = new Button { Text = "交给 CombatSolver 打本场战斗" };
        solverStart.Pressed += StartSolver;
        UiStyle.Primary(solverStart); UiStyle.Size(solverStart, 44);
        controlCard.AddChild(solverStart);
        pause = new Button { Text = "暂停所有本模块动作" };
        pause.Pressed += PauseAll;
        UiStyle.Secondary(pause); UiStyle.Size(pause, 44);
        controlCard.AddChild(pause);
        controlCard.AddChild(UiStyle.Hint("快捷键：双击 W 依次切换「关 → 开 → 暂停 → 恢复」；暂停时同时停止已接管的求解器；聊天输入时不会触发。"));

        var debugCard = UiStyle.Card(body, "调试");
        var export = new Button { Text = "导出当前状态" };
        export.Pressed += ExportSnapshot;
        UiStyle.Ghost(export); UiStyle.Size(export, 40);
        debugCard.AddChild(export);

        chat = new ChatPanel(chatBody, settingsBody, mainQueue.Enqueue);
        chat.PlanProvider = () => plan.Describe();
        chat.Interrupted += () => SuspendAuto("你在和搭子讨论，自动操作已暂停；点「恢复自动执行」继续。");
        chat.UserSaid += NoteDirective;
        planLabel = new Label { Text = "本局计划：未设置", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        planLabel.AddThemeColorOverride("font_color", UiStyle.Muted);
        controlCard.AddChild(planLabel);
        planPath = Path.Combine(ProjectSettings.GlobalizePath("user://spire-companion"), "run-plan.json");
        CharacterCatalog.UserDirectory = Path.Combine(ProjectSettings.GlobalizePath("user://spire-companion"), "characters");
        LoadPlan();

        layer.AddChild(panel);
        toggle = new Button
        {
            Text = "AI 搭子", Icon = UiStyle.SmallAvatar, Position = new Vector2(16, 16),
            TooltipText = "显示或隐藏窗口（F9）", Theme = panel.Theme
        };
        UiStyle.Size(toggle, 44, 108);
        toggle.Pressed += () => panel.Visible = !panel.Visible;
        layer.AddChild(toggle);
        autoLabel.Text = autoEnabled ? "自动：开" : "自动：关";
        solverToggle.Text = solverAuto ? "自动接管战斗：开" : "自动接管战斗：关";
        tree.Root.AddChild(layer);
        if (voiceEnabled) StartVoice();
        tree.ProcessFrame += () => Tick(tree.Root.GetProcessDeltaTime());
        tree.Root.TreeExiting += () =>
        {
            autoCts?.Cancel();
            chat.Dispose();
            try { SolverBridge.StopOwned(); }
            catch (Exception error) { GD.PushError($"[SpireCompanion] shutdown_stop: {error}"); }
        };
        Refresh();
    }

    private ScrollContainer Page(string name, out VBoxContainer body)
    {
        var page = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        page.AddChild(body);
        tabs.AddChild(page);
        return page;
    }

    private void Tick(double delta)
    {
        while (mainQueue.TryDequeue(out var update)) update();
        var viewport = tree.Root.GetVisibleRect().Size;
        var width = Math.Clamp(viewport.X - 40, 360, 620);
        var height = Math.Clamp(viewport.Y - 100, 300, 720);
        chatScroll.CustomMinimumSize = settingsScroll.CustomMinimumSize = gameScroll.CustomMinimumSize =
            new Vector2(width - 40, 140);
        panel.Size = new Vector2(width, height);
        if (dragging)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) dragging = false;
            else panel.Position = tree.Root.GetMousePosition() - dragOffset;
        }
        panel.Position = panel.Position.Clamp(Vector2.Zero, (viewport - panel.Size).Max(Vector2.Zero));
        ticks++;
        if (!layoutReported && ticks > 5 && panel.IsVisibleInTree())
        {
            // Report the real laid-out geometry once so a broken window is diagnosable from the log alone.
            layoutReported = true;
            var titles = string.Join("|", Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle));
            var page = tabs.GetCurrentTabControl();
            GD.Print($"[SpireCompanion] PANEL_LAYOUT viewport=({viewport.X},{viewport.Y}) " +
                $"rect=({panel.Position.X},{panel.Position.Y},{panel.Size.X},{panel.Size.Y}) " +
                $"tabs={tabs.GetTabCount()} titles={titles} current={tabs.CurrentTab} " +
                $"scroll=({chatScroll.Size.X},{chatScroll.Size.Y}) page=({page?.Size.X},{page?.Size.Y}) " +
                $"font_ready={panel.Theme.DefaultFont != null} visible={panel.Visible}");
        }
        autoWait = Math.Max(0, autoWait - delta);
        solverRetry = Math.Max(0, solverRetry - delta);
        quipCooldown = Math.Max(0, quipCooldown - delta);
        if (pendingShopId.Length > 0)
        {
            pendingShopWait -= delta;
            if (pendingShopWait <= 0)
            {
                var id = pendingShopId;
                var name = pendingShopName;
                pendingShopId = "";
                var shopNow = GameReader.Read().Room;
                var stillThere = shopNow is { Kind: "Shop" } && int.TryParse(id, out var slot) &&
                    slot >= 0 && slot < shopNow.Options.Length && shopNow.Options[slot].Name == name;
                if (stillThere) ExecutePurchase(id);
                else { message = "商店已经变化，这次购买取消。"; decisionStatus.Text = message; autoActedKey = ""; }

            }
        }
        bool pressed = Input.IsPhysicalKeyPressed(Key.F9);
        if (pressed && !f9Down) panel.Visible = !panel.Visible;
        f9Down = pressed;
        var focus = panel.GetViewport()?.GuiGetFocusOwner();
        if (focus is not (LineEdit or TextEdit))
        {
            bool wPressed = Input.IsPhysicalKeyPressed(Key.W);
            if (wPressed && !wDown)
            {
                var now = Time.GetTicksMsec() / 1000.0;
                if (now - lastWTap < 0.45) { lastWTap = -10; HotkeyToggleAuto(); }
                else lastWTap = now;
            }
            wDown = wPressed;
        }
        else { wDown = false; lastWTap = -10; }
        remaining -= delta;
        if (remaining <= 0) { remaining = 0.25; Refresh(); }
    }

    private void ExportSnapshot()
    {
        try
        {
            var fresh = GameReader.Read();
            var directory = ProjectSettings.GlobalizePath("user://spire-companion/captures");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"state-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema_version = 1, captured_at = DateTimeOffset.UtcNow,
                fingerprint = fresh.Fingerprint, state = fresh }, new JsonSerializerOptions { WriteIndented = true }));
            message = $"已导出到 {path}";
            GD.Print($"[SpireCompanion] snapshot_saved path={path}");
        }
        catch (Exception error)
        {
            message = "状态导出失败，详见日志。";
            GD.PushError($"[SpireCompanion] snapshot_export: {error}");
        }
        Refresh();
    }

    /// <summary>Offer executable choices only for screens we can actually act on.</summary>
    private void UpdateDecision(GameSnapshot next)
    {
        var room = next.Room;
        if (room is null || room.Options.Length == 0 || !ExecutableKinds.Contains(room.Kind))
        {
            decisionCard.Visible = false;
            decisionSignature = "";
            return;
        }
        // Selection itself may not change the snapshot fingerprint, so include whether confirm is available.
        var canConfirm = room.Kind == "Select" && GameReader.CanConfirmSelection();
        var signature = next.Fingerprint + "|" + room.Kind + "|" + canConfirm + "|" +
            string.Join("|", room.Options.Select(option => option.Id + option.Name + option.Locked));
        if (signature != decisionSignature)
        {
            decisionSignature = signature;
            foreach (var child in decisionButtons.GetChildren()) child.Free();
            switch (room.Kind)
            {
                case "Reward": BuildRewardButtons(room); break;
                case "Rest": BuildRestButtons(room); break;
                case "Select": BuildSelectButtons(room); break;
                case "Event": BuildEventButtons(room); break;
                case "Map": BuildMapButtons(room); break;
                case "Relic": BuildRelicButtons(room); break;
                case "Rewards": BuildLootButtons(room); break;
                case "Treasure": BuildTreasureButtons(room); break;
                default: BuildShopButtons(room); break;
            }
            decisionStatus.Text = $"检测到 {room.Options.Length} 个可选项，点击后立即执行。";
            var promptPeek = room.Title.Length > 40 ? room.Title[..40] : room.Title;
            GD.Print($"[SpireCompanion] decision kind={room.Kind} options={room.Options.Length} required={room.Required} prompt={promptPeek} " +
                $"names={string.Join("|", room.Options.Select(option => $"{option.Id}:{option.Name}:{(option.Locked ? "locked" : "open")}"))}");
        }
        decisionCard.Visible = true;
    }

    private void BuildRewardButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            if (!int.TryParse(option.Id, out var index)) continue;
            var button = new Button { Text = $"选「{option.Name}」 · {option.Detail}" };
            button.Pressed += () => ExecuteReward(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
        if (GameReader.CanSkipReward())
        {
            var skip = new Button { Text = "跳过这张奖励" };
            skip.Pressed += ExecuteSkipReward;
            UiStyle.Ghost(skip); UiStyle.Size(skip, 40);
            decisionButtons.AddChild(skip);
        }
    }

    private void BuildRestButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            var button = new Button { Text = $"选「{option.Name}」 · {option.Detail}", Disabled = option.Locked };
            var id = option.Id;
            button.Pressed += id == "proceed" ? ExecuteProceed : () => ExecuteRest(id);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildSelectButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            if (!int.TryParse(option.Id, out var index)) continue;
            var button = new Button { Text = $"选「{option.Name}」 · {option.Detail}" };
            button.Pressed += () => ExecuteGridCard(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
        if (GameReader.CanConfirmSelection())
        {
            var confirm = new Button { Text = "确认选择" };
            confirm.Pressed += ExecuteConfirm;
            UiStyle.Primary(confirm); UiStyle.Size(confirm, 42);
            decisionButtons.AddChild(confirm);
        }
    }

    private void BuildEventButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            if (!int.TryParse(option.Id, out var index)) continue;
            var button = new Button { Text = $"选「{option.Name}」 · {option.Detail}", Disabled = option.Locked };
            button.Pressed += () => ExecuteEvent(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildTreasureButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            var isOpen = option.Id == "open";
            var isProceed = option.Id == "proceed";
            if (!isOpen && !isProceed && !int.TryParse(option.Id, out _)) continue;
            var button = new Button { Text = isOpen ? option.Name : isProceed ? option.Name : $"拿「{option.Name}」" };
            if (isOpen) button.Pressed += ExecuteOpenTreasure;
            else if (isProceed) button.Pressed += ExecuteProceed;
            else if (int.TryParse(option.Id, out var index)) button.Pressed += () => ExecuteClaimTreasure(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildLootButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            var isProceed = option.Id == "proceed";
            if (!isProceed && !int.TryParse(option.Id, out _)) continue;
            var button = new Button { Text = isProceed ? option.Name : $"领取「{option.Name}」" };
            if (isProceed) button.Pressed += ExecuteProceed;
            else if (int.TryParse(option.Id, out var index)) button.Pressed += () => ExecuteLoot(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildRelicButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            var isSkip = option.Id == "skip";
            if (!isSkip && !int.TryParse(option.Id, out _)) continue;
            var detail = option.Detail.Length > 60 ? option.Detail[..60] + "…" : option.Detail;
            var button = new Button { Text = isSkip ? option.Name : $"拿「{option.Name}」 · {detail}" };
            if (isSkip) button.Pressed += ExecuteSkipRelic;
            else if (int.TryParse(option.Id, out var index)) button.Pressed += () => ExecuteRelic(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildMapButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            if (!int.TryParse(option.Id, out var index)) continue;
            var button = new Button { Text = $"前往「{option.Name}」 · {option.Detail}", Disabled = option.Locked };
            button.Pressed += () => ExecuteMap(index);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void BuildShopButtons(RoomView room)
    {
        foreach (var option in room.Options)
        {
            var id = option.Id;
            var isOpen = id == "open";
            var isClose = id == "close";
            var button = new Button { Text = isOpen || isClose || id == "proceed" ? option.Name : $"买「{option.Name}」 · {option.Detail}", Disabled = option.Locked };
            button.Pressed += isOpen ? ExecuteOpenShop : isClose ? ExecuteCloseShop : id == "proceed" ? ExecuteProceed : () => ExecutePurchase(id);
            UiStyle.Secondary(button); UiStyle.Size(button, 42);
            decisionButtons.AddChild(button);
        }
    }

    private void ExecuteCloseShop()
    {
        try { GameReader.CloseShop(); message = "已关闭商品界面。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=close_shop ok"); }
        catch (Exception error)
        {
            message = $"关闭商店失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] shop_close: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteOpenShop()
    {
        try { GameReader.OpenShop(); message = "已打开商店界面。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=open_shop ok"); }
        catch (Exception error)
        {
            message = $"打开商店失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] shop_open: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteRest(string optionId)
    {
        try { GameReader.SelectRestOption(optionId); message = "已选择该休息点选项。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=rest id={optionId} ok"); }
        catch (Exception error)
        {
            message = $"选择失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] rest_option: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecutePurchase(string entryId)
    {
        try { GameReader.Purchase(entryId); message = "已购买该商品，等待结算。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=shop id={entryId} ok"); }
        catch (Exception error)
        {
            message = $"购买失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] purchase: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    private void ToggleAuto()
    {
        autoEnabled = !autoEnabled;
        autoToggle.Text = autoEnabled ? "局外自动操作：开" : "局外自动操作：关";
        message = autoEnabled
            ? "已开启局外自动操作：奖励、休息点、商店、选牌、事件、地图、遗物、宝箱各替你操作一次。"
            : "已关闭局外自动操作。";
        if (!autoEnabled) StopAuto();
        else { autoActedKey = ""; autoWait = 0; }
        autoLabel.Text = autoEnabled ? "自动：开" : "自动：关";
        new WindowState(autoEnabled, solverAuto, voiceEnabled).Save(windowStatePath);
        Refresh();
    }

    /// <summary>One short in-character remark per combat, plus a single low-HP warning; quiet mode keeps only the warning.</summary>
    private void ProactiveStep(GameSnapshot next)
    {
        if (quipBusy || quipCooldown > 0) return;
        if (next.Enemies.Length == 0)
        {
            lastCombatKey = "";
            lowHpWarned = false;
            return;
        }
        var low = next.Hp * 100 <= next.MaxHp * 30;
        if (low && !lowHpWarned)
        {
            lowHpWarned = true;
            AskQuip(next, "血量已经很低，压力很大");
            return;
        }
        var key = next.Scene + "|" + string.Join(",", next.Enemies.Select(enemy => enemy.Name + enemy.MaxHp));
        if (chat.TalkMode == "quiet" || key == lastCombatKey) return;
        lastCombatKey = key;
        AskQuip(next, "战斗刚开始，看到这些敌人");
    }

    private void AskQuip(GameSnapshot snapshot, string occasion)
    {
        quipBusy = true;
        quipCooldown = 25;
        var (config, apiKey) = chat.RequestContext;
        var token = new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await chat.CommentAsync(config, apiKey, snapshot, occasion, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text)) mainQueue.Enqueue(() => chat.SayProactive(text.Trim()));
            }
            catch (Exception error) { GD.PushWarning($"[SpireCompanion] quip_failed: {error.Message}"); }
            finally { mainQueue.Enqueue(() => quipBusy = false); }
        });
    }

    /// <summary>Any interruption hands control back to the player until they explicitly resume.</summary>
    private void SuspendAuto(string why)
    {
        if (!autoEnabled || autoSuspended) return;
        autoSuspended = true;
        solverSuspended = true;
        autoCts?.Cancel();
        autoPending = "";
        message = why;
        if (decisionStatus != null) decisionStatus.Text = why;
        if (autoLabel != null) autoLabel.Text = "自动：暂停";
        chat.Notify(why);
        GD.Print("[SpireCompanion] auto_suspended");
        Refresh();
    }

    private void ResumeAuto()
    {
        if (!autoEnabled) return;
        autoSuspended = false;
        solverSuspended = false;
        message = "自动执行已恢复。";
        if (autoLabel != null) autoLabel.Text = "自动：开";
        if (decisionStatus != null) decisionStatus.Text = message;
        GD.Print("[SpireCompanion] auto_resumed");
        Refresh();
    }

    private void ToggleVoice()
    {
        voiceEnabled = !voiceEnabled;
        voiceToggle.Text = voiceEnabled ? "语音输入：开" : "语音输入：关";
        new WindowState(autoEnabled, solverAuto, voiceEnabled).Save(windowStatePath);
        if (voiceEnabled) StartVoice(); else { voice.Stop(); voiceStatus.Text = "语音：未开启"; }
    }

    private void StartVoice()
    {
        try
        {
            var directory = ProjectSettings.GlobalizePath("user://spire-companion");
            Directory.CreateDirectory(directory);
            var token = Guid.NewGuid().ToString("N");
            var sessionPath = Path.Combine(directory, "voice-session.json");
            File.WriteAllText(sessionPath, System.Text.Json.JsonSerializer.Serialize(new { port = 17860, token, url = "ws://127.0.0.1:17860" }));
            voice.Start(17860, token);
            message = $"语音输入已开启，会话信息写入 {sessionPath}；启动 mock 或语音服务后会自动连接。";
            GD.Print($"[SpireCompanion] voice_start path={sessionPath}");
        }
        catch (Exception error)
        {
            voiceEnabled = false;
            voiceToggle.Text = "语音输入：关";
            message = $"语音启动失败：{error.Message}";
            GD.PushError($"[SpireCompanion] voice_start: {error}");
        }
        if (decisionStatus != null) decisionStatus.Text = message;
    }

    private void HandleVoiceText(string inputId, string text)
    {
        if (chat.Busy)
        {
            message = "语音输入被忽略：上一条还在回复。";
            _ = voice.AckAsync(inputId, "rejected");
        }
        else
        {
            chat.SubmitExternal(text);
            message = $"语音输入：{text}";
            _ = voice.AckAsync(inputId, "accepted");
        }
        if (decisionStatus != null) decisionStatus.Text = message;
        GD.Print($"[SpireCompanion] voice_input id={inputId} text={text}");
    }

    private void HandleVoiceIntent(string inputId, string intent)
    {
        var handled = true;
        switch (intent)
        {
            case "pause":
                if (autoEnabled) SuspendAuto("语音：已暂停自动操作，说「恢复」继续。");
                if (SolverBridge.Owned) { solverSuspended = true; try { SolverBridge.StopOwned(); } catch (Exception e) { GD.PushError($"[SpireCompanion] voice_stop_solver: {e}"); } }
                break;
            case "resume":
                ResumeAuto();
                chat.Notify("语音：已恢复自动执行。");
                break;
            case "handoff":
                try { if (!SolverBridge.Owned && SolverBridge.CanStart) SolverBridge.Start(); }
                catch (Exception error) { GD.PushWarning($"[SpireCompanion] voice_handoff: {error.Message}"); handled = false; }
                break;
            default: handled = false; break;
        }
        _ = voice.AckAsync(inputId, handled ? "accepted" : "ignored");
        GD.Print($"[SpireCompanion] voice_intent intent={intent} handled={handled}");
    }

    private void ToggleSolverAuto()
    {
        solverAuto = !solverAuto;
        if (solverAuto) solverSuspended = false;
        solverToggle.Text = solverAuto ? "自动接管战斗：开" : "自动接管战斗：关";
        message = solverAuto ? "已开启自动接管：每场战斗开始后自动交给 CombatSolver。" : "已关闭自动接管，战斗需要手动点。";
        new WindowState(autoEnabled, solverAuto, voiceEnabled).Save(windowStatePath);
        solverRetry = 0;
        Refresh();
    }

    /// <summary>Hand every new combat to the solver while the switch is on; auto mode stays untouched.</summary>
    private void SolverAutoStep(GameSnapshot next)
    {
        if (!solverAuto || solverSuspended || SolverBridge.Owned || solverRetry > 0) return;
        if (next.Enemies.Length == 0 || !SolverBridge.CanStart) return;
        try
        {
            SolverBridge.Start();
            message = "已自动交给 CombatSolver 打本场战斗。";
            GD.Print("[SpireCompanion] solver_auto_start");
        }
        catch (Exception error)
        {
            message = $"自动接管失败：{error.Message}";
            GD.PushWarning($"[SpireCompanion] solver_auto_failed: {error.Message}");
        }
        solverRetry = 5;
    }

    private void StopAuto()
    {
        autoEnabled = false;
        if (autoToggle != null) autoToggle.Text = "局外自动操作：关";
        if (autoLabel != null) autoLabel.Text = "自动：关";
        if (windowStatePath.Length > 0) new WindowState(false, solverAuto, voiceEnabled).Save(windowStatePath);
        autoPending = "";
        autoCts?.Cancel();
        autoCts = null;
    }

    /// <summary>Auto mode only ever acts on an executable screen, one decision per observed state.</summary>
    private void AutoStep(GameSnapshot next)
    {
        if (!autoEnabled || autoSuspended || SolverBridge.Owned) return;
        if (autoPending.Length > 0 || autoWait > 0)
        {
            if (autoWait > 0 && next.Room is { } waiting && waiting.Options.Length > 0)
                GD.Print($"[SpireCompanion] auto_wait wait={autoWait:F2} kind={waiting.Kind}");
            return;
        }
        var room = next.Room;
        if (room is null || room.Kind != "Shop") pendingShopId = "";
        if (room is null) { autoSelectPendingConfirm = false; return; }
        if (room.Kind != "Select") { autoSelectPendingConfirm = false; autoPendingIds = []; autoSelectedIds.Clear(); }
        if (autoSelectPendingConfirm)
        {
            if (GameReader.CanConfirmSelection())
            {
                try { GameReader.ConfirmSelection(); message = "自动操作：已确认选牌。"; autoWait = 1.0; autoPendingIds = []; autoSelectedIds.Clear(); }
                catch (Exception error) { message = $"自动确认失败：{error.Message}"; StopAuto(); }
                autoSelectPendingConfirm = false;
                return;
            }
            // Multi-card screens (e.g. remove two) need more picks before confirm unlocks.
            while (autoPendingIds.Length > 0)
            {
                var pick = autoPendingIds[0];
                autoPendingIds = autoPendingIds[1..];
                if (!int.TryParse(pick, out var pickIndex)) continue;
                if (!room.Options.Any(option => option.Id == pick && !option.Locked)) continue;
                try
                {
                    GameReader.SelectGridCard(pickIndex);
                    autoSelectedIds.Add(pick);
                    message = $"自动操作：继续选下一张（{autoSelectedIds.Count}/{Math.Max(1, room.Required)}）。";
                    autoWait = 0.6;
                }
                catch (Exception error) { message = $"自动选牌失败：{error.Message}"; GD.PushError($"[SpireCompanion] select_next: {error}"); }
                return;
            }
            // Model gave too few ids: fill the remaining picks from unlocked, not-yet-selected cards.
            if (autoSelectedIds.Count < Math.Max(1, room.Required))
            {
                var filler = room.Options.FirstOrDefault(option => !option.Locked && !autoSelectedIds.Contains(option.Id) && int.TryParse(option.Id, out _));
                if (filler != null && int.TryParse(filler.Id, out var fillerIndex))
                {
                    try
                    {
                        GameReader.SelectGridCard(fillerIndex);
                        autoSelectedIds.Add(filler.Id);
                        message = $"自动操作：补选第 {autoSelectedIds.Count} 张（共 {Math.Max(1, room.Required)} 张）。";
                        GD.Print($"[SpireCompanion] select_fill id={filler.Id} count={autoSelectedIds.Count}/{room.Required}");
                        autoWait = 0.6;
                    }
                    catch (Exception error) { message = $"补选失败：{error.Message}"; GD.PushError($"[SpireCompanion] select_fill: {error}"); }
                    return;
                }
            }
            StopAutoWith($"选牌界面需要选 {Math.Max(1, room.Required)} 张，但无法继续完成，已停止自动操作。");
            return;
        }
        // One action per screen state: identical options must not re-trigger, but a new dialogue page may.
        var optionKey = room.Id + "|" + string.Join(",", room.Options.Select(option => option.Id + option.Name + option.Locked));
        if (optionKey == autoActedKey) return;
        if (!ExecutableKinds.Contains(room.Kind))
        {
            GD.Print($"[SpireCompanion] auto_skip_unsupported kind={room.Kind}");
            return;
        }
        if (room.Options.All(option => option.Locked)) return;
        // Shop mechanics: open once, close when nothing is worth buying, then leave.
        if (room.Kind == "Shop")
        {
            var inventoryOpen = room.Options.Any(option => option.Id == "close");
            var actionable = room.Options.Any(option => !option.Locked && option.Id is not ("open" or "close" or "proceed"));
            if (!inventoryOpen && shopOpenedFor != room.Id && room.Options.Any(option => option.Id == "open"))
            {
                shopOpenedFor = room.Id;
                autoActedKey = optionKey;
                autoWait = 0.8;
                ExecuteOpenShop();
                return;
            }
            if (!actionable)
            {
                if (inventoryOpen)
                {
                    autoActedKey = optionKey;
                    autoWait = 0.8;
                    ExecuteCloseShop();
                    return;
                }
                if (room.Options.Any(option => option.Id == "proceed" && !option.Locked))
                {
                    autoActedKey = optionKey;
                    autoWait = 0.8;
                    ExecuteProceed();
                    return;
                }
            }
        }
        if (TryApplyPin(next, room, optionKey)) return;
        autoPending = optionKey;
        GD.Print($"[SpireCompanion] auto_request kind={room.Kind} options={room.Options.Length} room={room.Id} plan={plan.Describe()}");
        autoCts = new CancellationTokenSource();
        autoCts.CancelAfter(TimeSpan.FromSeconds(30));
        if (decisionStatus != null) decisionStatus.Text = "自动决策中…（模型可能需要几秒）";
        _ = DecideAsync(next, room.Options, room.Required, lastDirective, plan.Describe(), chat.RecentExchange, autoCts.Token);
    }

    private async Task DecideAsync(GameSnapshot snapshot, RoomOptionView[] options, int required, string directive, string runPlan, string playerContext, CancellationToken token)
    {
        var ids = options.Select(option => option.Id).ToArray();
        var skipVetoed = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var reply = await chat.DecideAsync(snapshot, options, required, directive, token, runPlan, playerContext).ConfigureAwait(false);
                var (id, ranked, reason) = ChoiceParser.ParseWithIds(reply, ids);
                if (id != null)
                {
                    // Guard rail: a thin deck must never decline a card reward. The model reads the
                    // "30 card cap" as "pick as little as possible" and skips every early reward,
                    // leaving the run with no damage. Re-ask once with skip removed from the options.
                    if (id == "skip" && !skipVetoed && ShouldVetoRewardSkip(snapshot))
                    {
                        skipVetoed = true;
                        options = options.Where(option => option.Id != "skip").ToArray();
                        ids = options.Select(option => option.Id).ToArray();
                        GD.Print($"[SpireCompanion] reward_skip_veto deck={snapshot.Deck.Length} reason={reason}");
                        attempt--;
                        continue;
                    }
                    // Multi-card screens: ask for the remaining picks instead of stopping at one.
                    if (required > 1 && ranked.Length < required)
                    {
                        var remaining = options.Where(option => !ranked.Contains(option.Id)).ToArray();
                        if (remaining.Length > 0)
                            ranked = await CompleteMultiPick(snapshot, remaining, required - ranked.Length, ranked, token, runPlan).ConfigureAwait(false);
                    }
                    mainQueue.Enqueue(() => ApplyAuto(snapshot, ranked, reason));
                    return;
                }
                // One retry: a malformed reply is usually noise, not a broken config.
                GD.PushWarning($"[SpireCompanion] auto_invalid attempt={attempt} reply={Truncate(reply)}");
            }
            catch (OperationCanceledException)
            {
                mainQueue.Enqueue(() => StopAutoWith("自动决策超时，已停止自动操作。可以手动点，或重新开启。"));
                return;
            }
            catch (Exception error)
            {
                if (attempt == 0 && error is ModelReplyException)
                {
                    GD.PushWarning($"[SpireCompanion] auto_retry error={error.Message}");
                    continue;
                }
                GD.PushError($"[SpireCompanion] auto_failed error={error.Message}");
                mainQueue.Enqueue(() => StopAutoWith($"自动决策失败：{error.Message}"));
                return;
            }
        }
        mainQueue.Enqueue(() => StopAutoWith("自动决策两次都没给出合法选项，已停止自动操作。"));
    }

    private async Task<string[]> CompleteMultiPick(GameSnapshot snapshot, RoomOptionView[] remaining, int need,
        string[] chosen, CancellationToken token, string runPlan)
    {
        try
        {
            var reply = await chat.DecideAsync(snapshot, remaining, need, "", token, runPlan, "").ConfigureAwait(false);
            var (next, ranked, _) = ChoiceParser.ParseWithIds(reply, remaining.Select(option => option.Id).ToArray());
            if (next == null) return chosen;
            var merged = chosen.ToList();
            foreach (var id in ranked) if (!merged.Contains(id)) merged.Add(id);
            GD.Print($"[SpireCompanion] multi_pick chosen={merged.Count} required={need + chosen.Length}");
            return merged.ToArray();
        }
        catch (Exception error)
        {
            GD.PushWarning($"[SpireCompanion] multi_pick_failed: {error.Message}");
            return chosen;
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200];

    /// <summary>Reward skip stays a real option, but never while the deck is still thin (0 = unknown, don't judge).</summary>
    private static bool ShouldVetoRewardSkip(GameSnapshot snapshot) =>
        snapshot.Room is { Kind: "Reward" } && snapshot.Deck.Length is > 0 and < RewardSkipFloor;

    private const int RewardSkipFloor = 20;

    private void StopAutoWith(string notice)
    {
        message = notice;
        if (decisionStatus != null) decisionStatus.Text = notice;
        chat.Notify(notice);
        GD.PushWarning($"[SpireCompanion] auto_stop notice={notice}");
        StopAuto();
    }

    private void ApplyAuto(GameSnapshot snapshot, string[] ids, string reason)
    {
        autoPending = "";
        if (!autoEnabled) return;
        var id = ids.Length > 0 ? ids[0] : null;
        autoPendingIds = ids.Length > 1 ? ids[1..] : [];
        if (id == null)
        {
            StopAutoWith("自动决策未返回合法选项，已停止自动操作。");
            return;
        }
        var fresh = GameReader.Read();
        var room = fresh.Room;
        var snapshotRoom = snapshot.Room;
        if (room is null || snapshotRoom is null || room.Kind != snapshotRoom.Kind)
        {
            message = "界面已切换，已丢弃这条自动决策。";
            GD.PushWarning($"[SpireCompanion] auto_discard reason=screen_changed was={snapshotRoom?.Kind} now={room?.Kind}");
            return;
        }
        if (room.Id.Length > 0 && snapshotRoom.Id.Length > 0 && room.Id != snapshotRoom.Id)
        {
            message = "界面已刷新，已丢弃这条自动决策。";
            GD.PushWarning($"[SpireCompanion] auto_discard reason=screen_id was={snapshotRoom.Id} now={room.Id}");
            return;
        }
        if (!room.Options.Any(option => option.Id == id && !option.Locked))
        {
            message = "选项已失效，已丢弃这条自动决策。";
            GD.PushWarning($"[SpireCompanion] auto_discard reason=option_missing id={id} kind={room.Kind} options={string.Join("|", room.Options.Select(option => option.Id))}");
            return;
        }
        autoActedKey = room.Id + "|" + string.Join(",", room.Options.Select(option => option.Id + option.Name + option.Locked));
        autoWait = 1.0;
        var chosen = room.Options.First(option => option.Id == id);
        var why = string.IsNullOrWhiteSpace(reason) ? "" : $"理由：{reason}";
        chat.Notify($"我选了「{chosen.Name}」，{why}".TrimEnd('，'));
        decisionStatus.Text = $"自动选择：「{chosen.Name}」 {why}";
        GD.Print($"[SpireCompanion] auto_action kind={room.Kind} id={id} room={room.Id} reason={reason}");
        switch (room.Kind)
        {
            case "Reward" when id == "skip": ExecuteSkipReward(); break;
            case "Reward" when int.TryParse(id, out var index): ExecuteReward(index); break;
            case "Rest" or "Shop" when id == "proceed": ExecuteProceed(); break;
            case "Shop" when id == "open": ExecuteOpenShop(); break;
            case "Shop" when id == "close": ExecuteCloseShop(); break;
            case "Rest": ExecuteRest(id); break;
            case "Shop":
                pendingShopId = id;
                pendingShopName = chosen.Name;
                pendingShopWait = 1.8;
                chat.Notify($"准备买「{chosen.Name}」，{pendingShopWait:0.0} 秒后执行，让你先看清楚。");
                decisionStatus.Text = $"即将购买「{chosen.Name}」…";
                return;
            case "Event" when int.TryParse(id, out var eventIndex): ExecuteEvent(eventIndex); break;
            case "Map" when int.TryParse(id, out var mapIndex): ExecuteMap(mapIndex); break;
            case "Treasure" when id == "open": ExecuteOpenTreasure(); break;
            case "Treasure" when id == "proceed": ExecuteProceed(); break;
            case "Treasure" when int.TryParse(id, out var treasureIndex): ExecuteClaimTreasure(treasureIndex); break;
            case "Rewards" when id == "proceed": ExecuteProceed(); break;
            case "Rewards" when int.TryParse(id, out var lootIndex): ExecuteLoot(lootIndex); break;
            case "Relic" when id == "skip": ExecuteSkipRelic(); break;
            case "Relic" when int.TryParse(id, out var relicIndex): ExecuteRelic(relicIndex); break;
            case "Select" when int.TryParse(id, out var cardIndex):
                autoSelectPendingConfirm = true;
                autoSelectedIds.Clear();
                autoSelectedIds.Add(id);
                autoWait = 0.4;
                ExecuteGridCard(cardIndex, room.Options.First(option => option.Id == id).Name);
                break;
        }
    }

    private void ExecuteGridCard(int index, string? name = null)
    {
        try
        {
            GameReader.SelectGridCard(index);
            message = name == null ? "已选中该卡牌，需要时再点确认。" : $"已选中「{name}」，确认中…";
            decisionStatus.Text = message;
            GD.Print($"[SpireCompanion] decision_result action=select_grid id={index} name={name ?? "manual"} ok");
        }
        catch (Exception error)
        {
            message = $"选牌失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] select_grid: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteConfirm()
    {
        try { GameReader.ConfirmSelection(); message = "已确认选牌。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=confirm_select ok"); }
        catch (Exception error)
        {
            message = $"确认失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] confirm_select: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    /// <summary>A failed action must not lock the screen forever: retry a couple of times, then stop.</summary>
    private void NoteAutoFailure()
    {
        autoActionFails++;
        autoActedKey = "";
        autoWait = 3.0;
        if (autoActionFails >= 3) StopAutoWith("自动操作连续失败三次，已停止自动操作。");
    }

    private void ExecuteOpenTreasure()
    {
        try { GameReader.OpenTreasure(); message = "已打开宝箱。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=open_treasure ok"); }
        catch (Exception error)
        {
            message = $"打开宝箱失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] treasure_open: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteClaimTreasure(int index)
    {
        try { GameReader.ClaimTreasureRelic(index); message = "已拿取宝箱遗物。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=treasure_relic id={index} ok"); }
        catch (Exception error)
        {
            message = $"拿取宝箱遗物失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] treasure_relic: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteLoot(int index)
    {
        try { GameReader.ClaimReward(index); message = "已领取该奖励。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=loot id={index} ok"); }
        catch (Exception error)
        {
            message = $"领取失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] claim_reward: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteRelic(int index)
    {
        try { GameReader.SelectRelic(index); message = "已选择该遗物。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=relic id={index} ok"); }
        catch (Exception error)
        {
            message = $"遗物选择失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] relic_select: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteSkipRelic()
    {
        try { GameReader.SkipRelic(); message = "已跳过该遗物。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=skip_relic ok"); }
        catch (Exception error)
        {
            message = $"跳过遗物失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] relic_skip: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteEvent(int index)
    {
        try { GameReader.SelectEventOption(index); message = "已选择该事件选项。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=event id={index} ok"); }
        catch (Exception error)
        {
            message = $"事件选择失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] event_option: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteMap(int index)
    {
        try { GameReader.ChooseMapNode(index); message = "已选择路线，等待移动。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=map id={index} ok"); }
        catch (Exception error)
        {
            message = $"选择路线失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] map_node: {error}");
            NoteAutoFailure();
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteProceed()
    {
        try { GameReader.Proceed(); message = "已继续前进。"; decisionStatus.Text = message; GD.Print("[SpireCompanion] decision_result action=proceed ok"); }
        catch (Exception error)
        {
            message = $"继续失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] proceed: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteReward(int index)
    {
        try { GameReader.SelectReward(index); message = "已选择奖励卡牌。"; decisionStatus.Text = message; GD.Print($"[SpireCompanion] decision_result action=reward id={index} ok"); }
        catch (Exception error)
        {
            message = $"选择失败：{error.Message}";
            decisionStatus.Text = message;
            GD.PushError($"[SpireCompanion] select_reward: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    private void ExecuteSkipReward()
    {
        try { GameReader.SkipReward(); message = "已跳过这张奖励。"; }
        catch (Exception error)
        {
            message = $"跳过失败：{error.Message}";
            GD.PushError($"[SpireCompanion] skip_reward: {error}");
        }
        decisionSignature = "";
        Refresh();
    }

    /// <summary>Remember an explicit instruction ("选重锤") so the next matching decision obeys it.</summary>
    private void LoadPlan()
    {
        try
        {
            if (!File.Exists(planPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(planPath));
            if (saved == null) return;
            plan.Restore(saved.Goal, saved.Needs, saved.Rules, saved.LastReview);
            plan.Floor = saved.Floor;
            plan.RunKey = saved.RunKey;
            UpdatePlanLabel();
        }
        catch (Exception error) { GD.PushWarning($"[SpireCompanion] plan_load: {error.Message}"); }
    }

    private void SavePlan()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            File.WriteAllText(planPath, System.Text.Json.JsonSerializer.Serialize(new PlanFile(
                plan.Goal, plan.Needs.ToArray(), plan.Rules.ToArray(), plan.LastReview, plan.Floor, plan.RunKey)));
        }
        catch (Exception error) { GD.PushWarning($"[SpireCompanion] plan_save: {error.Message}"); }
    }

    private sealed record PlanFile(string Goal, string[] Needs, string[] Rules, string LastReview, int Floor, string RunKey);

    private void UpdatePlanLabel()
    {
        if (planLabel == null) return;
        var described = plan.Describe();
        planLabel.Text = "本局计划：" + (described.Length == 0 ? "未设置" : described);
    }

    /// <summary>Load/persist the run plan and request one short review when a new floor starts.</summary>
    private void SyncPlan(GameSnapshot next)
    {
        var manager = RunManager.Instance;
        var run = manager.IsInProgress ? manager.DebugOnlyGetState() : null;
        if (run == null) return;
        var me = LocalContext.GetMe(run);
        var title = me?.Character?.Title;
        var key = $"{(title != null ? title.GetFormattedText() : "?")}|{run.AscensionLevel}";
        if (plan.RunKey != key || (run.TotalFloor <= 1 && plan.Floor > 1))
        {
            plan.Clear();
            plan.RunKey = key;
            plan.Floor = run.TotalFloor;
            SavePlan();
            UpdatePlanLabel();
            return;
        }
        if (run.TotalFloor == plan.Floor) return;
        var previous = plan.Floor;
        plan.Floor = run.TotalFloor;
        SavePlan();
        if (previous > 0 && run.TotalFloor > previous && HasContent)
            RequestFloorReview(next, run.TotalFloor);
    }

    private bool HasContent => plan.Goal.Length > 0 || plan.Needs.Count > 0 || plan.Rules.Count > 0;

    private void RequestFloorReview(GameSnapshot snapshot, int floor)
    {
        if (reviewInFlight) return;
        reviewInFlight = true;
        var (config, apiKey) = chat.RequestContext;
        var occasion = $"刚进入第 {floor} 层，当前计划：{plan.Describe()}。用不超过 50 字指出本层最需要注意的事，并给一条计划调整建议。";
        var token = new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await chat.CommentAsync(config, apiKey, snapshot, occasion, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) return;
                mainQueue.Enqueue(() =>
                {
                    plan.SetReview(text);
                    SavePlan();
                    UpdatePlanLabel();
                    chat.SayProactive($"第 {floor} 层复盘：{text}");
                    GD.Print($"[SpireCompanion] floor_review floor={floor} text={text}");
                });
            }
            catch (Exception error) { GD.PushWarning($"[SpireCompanion] floor_review_failed: {error.Message}"); }
            finally { mainQueue.Enqueue(() => reviewInFlight = false); }
        });
    }

    private void NoteDirective(string text)
    {
        lastDirective = text.Length > 80 ? text[..80] : text;
        if (plan.UpdateFrom(text))
        {
            var described = plan.Describe();
            if (planLabel != null) planLabel.Text = "本局计划：" + (described.Length == 0 ? "未设置" : described);
            chat.Notify("已更新本局计划：" + described);
            SavePlan();
            GD.Print($"[SpireCompanion] plan_update {described}");
        }
        var target = UserDirective.PinTarget(text);
        var source = "verb";
        if (target.Length == 0)
        {
            // If the player just names an option on this screen ("重锤"), pin it directly.
            try
            {
                var room = GameReader.Read().Room;
                var desire = text.Contains('选') || text.Contains('拿') || text.Contains('买') || text.Contains('要') ||
                    text.Contains('想') || text.Contains('就') || text.Contains("一定") || text.Contains("必须") || text.Contains("听我的");
                if (room != null)
                {
                    foreach (var option in room.Options)
                    {
                        if (option.Name.Length < 2 || !text.Contains(option.Name, StringComparison.Ordinal)) continue;
                        if (!desire && text.Trim() != option.Name) continue;
                        if (text.Contains("别" + option.Name, StringComparison.Ordinal) ||
                            text.Contains("不要" + option.Name, StringComparison.Ordinal) ||
                            text.Contains("不选" + option.Name, StringComparison.Ordinal)) continue;
                        target = option.Name;
                        source = "option";
                        break;
                    }
                }
            }
            catch { }
        }
        if (target.Length == 0) return;
        pinnedName = target;
        pinnedAt = Time.GetTicksMsec() / 1000.0;
        if (plan.AddPreference(pinnedName)) { SavePlan(); UpdatePlanLabel(); }
        chat.Notify($"已记住：下一屏优先选「{pinnedName}」，并计入本局偏好；恢复自动执行后生效。");
        GD.Print($"[SpireCompanion] directive pin={pinnedName} source={source} text={text}");
    }

    /// <summary>Apply a pinned instruction deterministically instead of asking the model again.</summary>
    private bool TryApplyPin(GameSnapshot next, RoomView room, string optionKey)
    {
        if (pinnedName.Length == 0) return false;
        if (Time.GetTicksMsec() / 1000.0 - pinnedAt > 180) { pinnedName = ""; return false; }
        if (!ExecutableKinds.Contains(room.Kind)) return false;
        var option = room.Options.FirstOrDefault(candidate => !candidate.Locked &&
            (candidate.Name.Contains(pinnedName) || pinnedName.Contains(candidate.Name)));
        if (option == null) return false;
        var name = option.Name;
        pinnedName = "";
        autoActedKey = optionKey;
        autoWait = 1.0;
        GD.Print($"[SpireCompanion] pin_apply kind={room.Kind} id={option.Id} name={name}");
        ApplyAuto(next, [option.Id], "按你的要求");
        return true;
    }

    /// <summary>Double-tap W toggles: pause (with solver stop) when running, resume when suspended.</summary>
    private void HotkeyToggleAuto()
    {
        if (!autoEnabled)
        {
            if (SolverBridge.Owned)
            {
                solverSuspended = true;
                try { SolverBridge.StopOwned(); message = "快捷键 WW：已停止求解器。"; }
                catch (Exception error) { message = "求解器停止未确认，请手动停止。"; GD.PushError($"[SpireCompanion] hotkey_solver_stop: {error}"); }
                chat.Notify(message);
                GD.Print("[SpireCompanion] hotkey_toggle action=solver_stop");
                return;
            }
            // Off -> on: the shortcut should always do something useful.
            autoEnabled = true;
            autoSuspended = false;
            solverSuspended = false;
            autoToggle.Text = "局外自动操作：开";
            autoLabel.Text = "自动：开";
            new WindowState(autoEnabled, solverAuto, voiceEnabled).Save(windowStatePath);
            message = "快捷键 WW：已开启自动操作。";
            decisionStatus.Text = message;
            chat.Notify(message);
            GD.Print("[SpireCompanion] hotkey_toggle action=enable");
            Refresh();
            return;
        }
        if (autoSuspended)
        {
            ResumeAuto();
            chat.Notify("快捷键：已恢复自动执行。");
            GD.Print("[SpireCompanion] hotkey_toggle action=resume");
            return;
        }
        SuspendAuto("快捷键 WW：已暂停自动操作，再双击 W 恢复。");
        if (SolverBridge.Owned)
        {
            try { SolverBridge.StopOwned(); message = "快捷键 WW：已暂停自动并停止求解器，再双击 W 恢复。"; }
            catch (Exception error)
            {
                message = "快捷键 WW：自动已暂停，但求解器停止未确认，请手动停止。";
                GD.PushError($"[SpireCompanion] hotkey_solver_stop: {error}");
            }
        }
        if (decisionStatus != null && message.Length > 0) decisionStatus.Text = message;
        chat.Notify("快捷键：已暂停自动操作，再双击 W 恢复。");
        GD.Print("[SpireCompanion] hotkey_toggle action=pause");
        Refresh();
    }

    private void PauseAll()
    {
        solverSuspended = true;
        StopAuto();
        chat.Cancel();
        try { SolverBridge.StopOwned(); message = "本模块已暂停，已接管的求解器也已停止。"; }
        catch (Exception error)
        {
            message = "本模块已暂停，但求解器停止未确认，请手动停止求解器。";
            GD.PushError($"[SpireCompanion] solver_stop: {error}");
        }
        Refresh();
    }

    private void StartSolver()
    {
        try { SolverBridge.Start(); message = "已交给 CombatSolver；暂停按钮可停止接管。"; }
        catch (Exception error)
        {
            message = "求解器未成功接管，详见日志；不会改用模型出牌。";
            GD.PushError($"[SpireCompanion] solver_start: {error}");
        }
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            var font = FontManager.GetSubstituteFont("zhs", FontType.Regular);
            if (font != null && panel.Theme.DefaultFont != font)
            {
                panel.Theme.DefaultFont = font;
                // The launcher is a sibling of the window: it must share its CJK theme explicitly.
                toggle.Theme = panel.Theme;
                toggle.AddThemeFontOverride("font", font);
                foreach (var type in new[] { "Button", "Label", "LineEdit", "TextEdit", "OptionButton", "CheckButton", "TabBar", "PopupMenu", "TooltipLabel" })
                    panel.Theme.SetFont("font", type, font);
                // RichTextLabel uses its own font items; without these the reply falls back to the game's light theme font.
                foreach (var name in new[] { "normal_font", "bold_font", "italics_font", "mono_font" })
                    panel.Theme.SetFont(name, "RichTextLabel", font);
                foreach (var name in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "mono_font_size" })
                    panel.Theme.SetFontSize(name, "RichTextLabel", 18);
            }
            SolverBridge.ObserveLifecycle();
            var next = GameReader.Read();
            var solver = SolverBridge.Status();
            UpdateDecision(next);
            if (GameReader.IsGameOver())
            {
                if (!defeatNotified)
                {
                    defeatNotified = true;
                    chat.Notify(Personality.Comfort(chat.TalkMode));
                    GD.Print("[SpireCompanion] defeat_notice");
                }
            }
            else defeatNotified = false;
            SolverAutoStep(next);
            AutoStep(next);
            ProactiveStep(next);
            SyncPlan(next);
            stateLabel.Text = SolverBridge.Owned ? "求解器接管" : "只读建议";
            stateLabel.AddThemeColorOverride("font_color", SolverBridge.Owned ? UiStyle.AccentDeep : UiStyle.Good);
            status.Text = $"场景：{next.Scene} · {next.CombatTurn}\n角色：{next.Character} · HP {next.Hp}/{next.MaxHp} · 金币 {next.Gold}\n能量：{next.Energy?.ToString() ?? "—"} · 格挡：{next.Block}\n手牌：{string.Join("、", next.Cards.Select(c => $"{c.Name}({c.Cost})"))}\n敌人：{string.Join("、", next.Enemies.Select(e => $"{e.Name} {e.Hp}/{e.MaxHp}"))}\n牌组 {next.Deck.Length} · 遗物 {next.Relics.Length} · 药水 {next.Potions.Length}\n阶段：{(next.CanEndTurn ? "可操作" : next.BlockReason)}\n求解器：{solver.Description}\n{message}";
            solverStart.Disabled = !solver.Available || solver.FullAuto || solver.Deploying || SolverBridge.Owned || !SolverBridge.CanStart;
            if (!reportedReady)
            {
                reportedReady = true;
                GD.Print($"[SpireCompanion] PANEL_READY scene={next.Scene} font_ready={font != null} fingerprint={next.Fingerprint[..16]}");
            }
        }
        catch (Exception error)
        {
            solverStart.Disabled = true;
            status.Text = "状态读取失败。详见游戏日志。";
            GD.PushError($"[SpireCompanion] read_state: {error}");
            // Avoid emitting a failing read every frame.
            remaining = 5;
        }
    }
}
