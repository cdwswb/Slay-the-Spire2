namespace SpireCompanion.Core;

/// <summary>
/// Per-screen instructions appended to the system prompt. The snapshot tells the model
/// what is on screen; this tells it what the player actually needs decided there.
/// </summary>
public static class ScreenGuidance
{
    public static string Combat =>
        "当前是战斗界面：先给这一手最该做的动作（出哪张牌、打哪个目标、是否用药），再给一句理由；不要罗列所有手牌，不要预测隐藏抽牌或未显示的敌人意图。";

    public static string General =>
        "当前没有可读的界面选项：只回答能从状态确认的内容；缺少界面数据时直接说明缺什么，不要凭常识补全选项或价格。";

    public static string For(GameSnapshot snapshot) => snapshot.Room?.Kind switch
    {
        "Event" =>
            "当前是事件界面：依据 Room.Title、Room.Description 与 Room.Options 逐项比较代价和收益，明确推荐一个选项并说明理由；" +
            "选项没写清的效果不要补全，Locked 表示当前不可选，Proceed 类选项表示离开。",
        "Reward" =>
            "当前是卡牌奖励界面：把 Room.Options 里的候选与“跳过”放在一起比较，结合牌组费用曲线、已有协同和即时战力给一个选择；" +
            "可以建议跳过，并说明跳过的代价（少一张牌）与收益（不稀释牌组）。",
        "Shop" =>
            "当前是商店界面：先看金币与 Room.Options 里每件的价格，按“现在最缺什么”排序；" +
            "价格高于金币或已售出的项要明确指出，允许建议这次不买、把钱留给删牌或下一个商店。",
        "Rest" => RestGuidance(snapshot),
        "Select" =>
            "当前是选牌界面（锻造、删牌或事件要求选择卡牌）：从 Room.Options 里挑一张，说明它会带来什么、放弃其他候选的代价；" +
            "选中后还需要确认，没确认前可以改选。只推荐列表内的牌。",
        "Treasure" =>
            "当前是宝箱房：先打开宝箱，再挑一个遗物；说明它对你当前牌组的作用。没有更好的就选「离开」。",
        "Rewards" =>
            "当前是战斗奖励结算：可以按任意顺序领取金币、药水、遗物和卡牌奖励，也可以直接离开放弃剩余奖励；" +
            "说明先拿哪个、为什么，药水栏满时提醒先丢一瓶。",
        "Relic" =>
            "当前是遗物选择界面：比较候选遗物对你当前牌组与资源的作用，说明放弃另一个的代价；" +
            "有「跳过」时才考虑跳过。只推荐列表内出现的遗物。",
        "Map" =>
            "当前是地图界面：只比较 Room.Options 里列出的可达节点，说明各自风险与收益（精英换遗物、商店补牌、篝火回血），" +
            "不要推断未列出的节点或未探索区域。",
        _ => snapshot.Cards.Length > 0 || snapshot.Enemies.Length > 0 ? Combat : General
    };

    /// <summary>Rest-site advice must follow the HP ratio, otherwise the model heals at full health.</summary>
    private static string RestGuidance(GameSnapshot snapshot)
    {
        var ratio = snapshot.MaxHp > 0 ? (double)snapshot.Hp / snapshot.MaxHp : 1;
        var hint = ratio >= 0.6
            ? $"当前血量 {snapshot.Hp}/{snapshot.MaxHp}，血量健康——不要为了回血放弃升级，优先升级卡组里最关键、升级收益最大的牌。"
            : ratio <= 0.4
                ? $"当前血量 {snapshot.Hp}/{snapshot.MaxHp}，血线吃紧——优先休息回血，除非接下来没有战斗或容错足够。"
                : $"当前血量 {snapshot.Hp}/{snapshot.MaxHp}，回血和升级都可以——看升级收益是否明显大于多出的血量，以及接下来是否要打精英或 Boss。";
        return "当前是休息点界面：" + hint + "只推荐 Room.Options 里可用的选项。";
    }
}
