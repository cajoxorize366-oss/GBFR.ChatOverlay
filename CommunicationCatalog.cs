namespace GBFR.ChatOverlay.Configuration;

public readonly record struct CommunicationCatalogEntry(
    int Id,
    int NativeValue,
    int SortOrder,
    string ChineseName,
    string EnglishName)
{
    public string GetDisplayName(UiLanguage language) =>
        UiLocalization.Select(language, ChineseName, EnglishName);
}

/// <summary>
/// Relink 2.0.3's official TAB communication catalog. IDs and order come from
/// communication_stamp.tbl, communication_fixedPhrase.tbl and
/// communication_emotion.tbl; labels come from text_communication.yml.
/// </summary>
public static class CommunicationCatalog
{
    public static IReadOnlyList<CommunicationCatalogEntry> GetEntries(QuickActionKind kind) => kind switch
    {
        QuickActionKind.Stamp => StampEntries,
        QuickActionKind.FixedPhrase => FixedPhraseEntries,
        QuickActionKind.Emotion => EmotionEntries,
        _ => Array.Empty<CommunicationCatalogEntry>(),
    };

    public static bool TryGetEntry(
        QuickActionKind kind,
        int id,
        out CommunicationCatalogEntry entry)
    {
        foreach (var candidate in GetEntries(kind))
        {
            if (candidate.Id != id)
                continue;
            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    private static readonly CommunicationCatalogEntry[] StampEntries =
    [
        new(16, 16, 0, "谢谢", "Thanks!"),
        new(17, 17, 1, "太好啦！", "Hurray!"),
        new(18, 18, 2, "撤退……", "I give up..."),
        new(19, 19, 3, "我会努力的！", "I'll never surrender!"),
        new(20, 20, 4, "辛苦了", "Good work"),
        new(21, 21, 5, "离开一下", "Gotta run!"),
        new(6, 6, 6, "我来啦～", "Hey!"),
        new(7, 7, 7, "加油加油", "Go, go!"),
        new(8, 8, 8, "我！", "OK!"),
        new(9, 9, 9, "对不起", "Sorry..."),
        new(10, 10, 10, "慌张……", "Oh my..."),
        new(13, 13, 11, "恭喜！", "Congrats!"),
        new(12, 12, 12, "锵锵锵", "Gong, gong!"),
        new(11, 11, 13, "咻", "Drool"),
        new(14, 14, 14, "蕨饼！", "Vyrncakes!"),
        new(15, 15, 15, "GO！", "Go!"),
        new(0, 0, 16, "冲呀！", "Here goes!"),
        new(1, 1, 17, "我不行了……", "Totally crushed..."),
        new(2, 2, 18, "盯……", "Heyo"),
        new(3, 3, 19, "（碧晶球）", "(Vy-ball)"),
        new(4, 4, 20, "ZZZ……", "Zzz..."),
        new(5, 5, 21, "沙袋碧鼠！", "Vyrnzilla!"),
        new(22, 22, 22, "参战", "Reporting in!"),
        new(23, 23, 23, "拜托了！", "Please!"),
        new(24, 24, 24, "请多关照", "I'm counting on you"),
        new(25, 25, 25, "发起猛攻！", "Go wild!"),
        new(26, 26, 26, "干得好！", "Awesome!"),
        new(27, 27, 27, "等一下！", "Wait a minute!"),
        new(28, 28, 28, "交给我吧！", "Got ya covered!"),
        new(29, 29, 29, "没事啦！", "Don't mention it!"),
        new(30, 30, 30, "好机会～", "Let's do it!"),
        new(31, 31, 31, "？！", "!?"),
        new(36, 36, 32, "哇啊啊啊啊啊！", "Nooooo!"),
        new(37, 37, 33, "有一手啊！", "Not bad!"),
        new(48, 48, 34, "哈哈哈", "Gahaha"),
        new(49, 49, 35, "这不算什么！", "That was nothing!"),
        new(32, 32, 36, "紧张冒汗", "Yikes!"),
        new(33, 33, 37, "哇啊", "Whoa"),
        new(38, 38, 38, "需要帮忙吗？", "Can I help?"),
        new(39, 39, 39, "一起玩吧！", "Come play with me!"),
        new(40, 40, 40, "出发！", "Let's go!"),
        new(41, 41, 41, "这里交给我！", "I'll call the shots!"),
        new(42, 42, 42, "冷静点！", "Calm down!"),
        new(43, 43, 43, "菲德拉赫！", "Feendra-yay!"),
        new(44, 44, 44, "什么？！", "How could that be!"),
        new(45, 45, 45, "准你当我家臣！", "Be my vassal!"),
        new(46, 46, 46, "都给我跟上！", "Follow me!"),
        new(47, 47, 47, "给我萝卜", "One radish, please"),
        new(34, 34, 48, "上咯～", "Here I go!"),
        new(35, 35, 49, "诶嘿★", "Tee-hee"),
        new(54, 54, 50, "嘿嘿嘿嘿！", "A-hyuk-hyuk!"),
        new(55, 55, 51, "潜力不错", "Spry one, ain'tcha?"),
        new(50, 50, 52, "哈？！", "Wh-what!?"),
        new(51, 51, 53, "再见咯！", "See ya!"),
        new(52, 52, 54, "话太多了", "I've said too much..."),
        new(53, 53, 55, "下一个是谁？", "Who's next?"),
        new(56, 56, 56, "90秒搞定", "I'll end this in 90 sec."),
        new(57, 57, 57, "（碰拳）", "(Fist Bump)"),
        new(58, 58, 58, "咀嚼……", "Nom, nom, nom..."),
        new(59, 59, 59, "贵安呀", "How do you do?"),
        new(60, 60, 60, "赐予我救赎！", "Salvation!"),
        new(61, 61, 61, "汝等？", "How dare you?!"),
        new(62, 62, 62, "明智的决定", "Wise decision."),
        new(63, 63, 63, "快刀斩乱麻！", "One for the books!"),
        new(64, 64, 64, "一切皆有可能", "Nothing's impossible"),
        new(65, 65, 65, "欢迎光临～", "Welcome!"),
        new(66, 66, 66, "故事……就是这样的", "There you have it..."),
        new(67, 67, 67, "哇啊……超帅的！", "So cool!"),
        new(68, 68, 68, "嚯 厉害啊！", "Wow!"),
        new(69, 69, 69, "所有人集合！", "Skyfarers, assemble!"),
        new(70, 70, 70, "不错啊！", "Nice!"),
        new(71, 71, 71, "这很索恩", "Maximum Punnagement"),
        new(72, 72, 72, "不会让你跑掉的", "Target locked!"),
        new(73, 73, 73, "出发", "Going out for a bit"),
        new(74, 74, 74, "思考中", "Thinking..."),
        new(75, 75, 75, "（炸大虾）", "(SHRIMP)"),
        new(76, 76, 76, "（黄金炸大虾）", "(G. SHRIMP)"),
        new(77, 77, 77, "很好！", "I Like!"),
        new(78, 78, 78, "（金枪鱼）", "(Albacore)"),
        new(79, 79, 79, "（肌肉碧）", "(Macho Vyrn)"),
        new(80, 80, 80, "赢啦……", "Gottem"),
        new(81, 81, 81, "哼！那是当然！", "Heheh! Got That Right!"),
        new(82, 82, 82, "任务完成", "Mission accomplished"),
        new(83, 83, 83, "嗯嗯……", "Nod..."),
        new(84, 84, 84, "最强！！", "I'm the strongest!"),
        new(85, 85, 85, "优雅华丽！", "Gracefully now!"),
        new(86, 86, 86, "真让人害羞呢", "How embarrassing..."),
        new(87, 87, 87, "再来一次吧！", "Let's go again!"),
        new(88, 88, 88, "是吗？", "Really?"),
        new(89, 89, 89, "让我瞧瞧！", "Ooh, show me!"),
        new(90, 90, 90, "吾乃至高之王！", "I shall reign supreme!"),
        new(91, 91, 91, "请再来一次！", "One more time, pretty please!"),
        new(92, 92, 92, "不用在意！", "No worries!"),
        new(93, 93, 93, "啪！", "Thwack"),
    ];

    private static readonly CommunicationCatalogEntry[] FixedPhraseEntries =
    [
        new(5, 5, 0, "请多关照！", "Let's have fun!"),
        new(0, 0, 1, "辛苦了", "Good work"),
        new(3, 3, 2, "初次见面", "Nice to meet you"),
        new(2, 2, 3, "再见", "See you later"),
        new(1, 1, 4, "你好", "Hello"),
        new(4, 4, 5, "下次再会！", "Until next time!"),
        new(47, 47, 6, "谢谢", "Thanks"),
        new(50, 50, 7, "对不起", "Sorry"),
        new(46, 46, 8, "好的", "Roger that"),
        new(55, 55, 9, "搞错了", "Oops, my bad"),
        new(9, 9, 10, "稍微离开一下", "Be right back"),
        new(10, 10, 11, "去吧！", "See you soon!"),
        new(7, 7, 12, "我回来了！", "I'm back!"),
        new(8, 8, 13, "欢迎回来！", "Welcome back!"),
        new(20, 20, 14, "真不错！", "Nice!"),
        new(21, 21, 15, "恭喜！", "Congrats!"),
        new(14, 14, 16, "太好了！", "We did it!"),
        new(18, 18, 17, "真遗憾……", "Shucks..."),
        new(15, 15, 18, "嘿嘿……", "Hehehe..."),
        new(17, 17, 19, "我生气了～", "Grrr!"),
        new(16, 16, 20, "诶？！", "Huh?!"),
        new(19, 19, 21, "别在意！", "No worries!"),
        new(48, 48, 22, "是", "Yes"),
        new(49, 49, 23, "不", "No"),
        new(51, 51, 24, "我明白了", "Understood"),
        new(53, 53, 25, "不懂", "I'm not sure"),
        new(60, 60, 26, "加油！", "Let's give it our all!"),
        new(57, 57, 27, "不客气！", "You're welcome!"),
        new(11, 11, 28, "出发！", "Let's go!"),
        new(6, 6, 29, "继续挑战吧！", "One more quest?"),
        new(13, 13, 30, "开始任务", "Starting the quest"),
        new(12, 12, 31, "拜托了", "Please!"),
        new(58, 58, 32, "准备完毕！", "All ready!"),
        new(43, 43, 33, "准备好了吗？", "All ready?"),
        new(59, 59, 34, "求做这个任务！", "Let's do this quest"),
        new(61, 61, 35, "我第一次挑战！", "It's my first time!"),
        new(52, 52, 36, "下次再一起玩吧", "Let's play again"),
        new(56, 56, 37, "今天就到这里", "I'm done for the day"),
        new(54, 54, 38, "我用这个角色！", "I'll use this character!"),
        new(40, 40, 39, "你用哪个角色参加？", "Who will you use?"),
        new(34, 34, 40, "我去强化一下！", "Buffing now!"),
        new(33, 33, 41, "强化一下再挑战吧", "Let's apply buffs"),
        new(42, 42, 42, "加个好友吧？", "Want to be friends?"),
        new(45, 45, 43, "稍等一下", "Hold on a sec"),
        new(41, 41, 44, "再来一次怎么样？", "Want to go again?"),
        new(44, 44, 45, "打完下一局我就撤", "Next one's my last"),
        new(29, 29, 46, "这边！", "This way!"),
        new(25, 25, 47, "救命！", "Help me!"),
        new(23, 23, 48, "我要用奥义了！", "Using my SBA!"),
        new(37, 37, 49, "好机会！", "Now's our chance!"),
        new(31, 31, 50, "用奥义攻击！", "Use your SBAs!"),
        new(32, 32, 51, "奥义留着之后用吧！", "Save your SBAs!"),
        new(36, 36, 52, "我来上debuff！", "Debuffing now!"),
        new(35, 35, 53, "给敌人上debuff！", "Use debuffs first!"),
        new(24, 24, 54, "一起攻击！", "Go all out!"),
        new(38, 38, 55, "小心行事", "Be careful"),
        new(28, 28, 56, "远离敌人！", "Get back!"),
        new(27, 27, 57, "小心敌人的技能！", "Watch out!"),
        new(39, 39, 58, "能帮我治疗吗？", "I need healing!"),
        new(30, 30, 59, "请掩护我", "Cover me"),
        new(22, 22, 60, "发现道具", "Item spotted"),
        new(26, 26, 61, "发现稀有道具！", "A rare item!"),
    ];

    private static readonly CommunicationCatalogEntry[] EmotionEntries =
    [
        new(1, 1, 0, "打招呼", "Greet"),
        new(0, 0, 1, "鞠躬", "Bow"),
        new(2, 2, 2, "点头", "Nod"),
        new(3, 3, 3, "摇头", "Shake head"),
        new(6, 6, 4, "高兴", "Rejoice"),
        new(5, 5, 5, "不甘", "Regret"),
        new(4, 4, 6, "拍手", "Applaud"),
        new(10, 10, 7, "呼喊", "Call"),
        new(8, 8, 8, "耍酷", "Strike pose"),
        new(9, 9, 9, "胜利", "Victory pose"),
        new(7, 7, 10, "坐下", "Sit"),
        new(11, 11, 11, "猜拳", "Rock Paper Scissors"),
        new(12, 17, 12, "热身", "Shadowbox"),
        new(13, 15, 13, "鼓舞士气", "Inspire"),
        new(14, 14, 14, "俯卧撑", "Push-ups"),
        new(15, 22, 15, "仰卧起坐", "Sit-ups"),
        new(16, 23, 16, "深蹲", "Squats"),
        new(22, 24, 17, "测运势", "Try Your Luck"),
        new(17, 16, 18, "吃饭团", "Eat Rice Ball"),
        new(18, 18, 19, "跳舞", "Dance"),
        new(19, 19, 20, "赛马娘蹦跳传说1", "Umapyoi Legend 1"),
        new(20, 20, 21, "赛马娘蹦跳传说2", "Umapyoi Legend 2"),
        new(21, 21, 22, "赛马娘蹦跳传说3", "Umapyoi Legend 3"),
    ];
}
