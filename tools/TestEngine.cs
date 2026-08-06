using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WshHelper;

// 引擎测试：改键表构建(含鼠标侧键/滚轮)、钩子结构体偏移、喊话逐字输入路径。
// 这些都不需要前台窗口，可以在锁屏/无游戏时跑。
static class EngineTests
{
    static int failures = 0;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + what);
        if (!cond) failures++;
    }

    public static int Run()
    {
        Console.WriteLine("\n===== ENGINE TESTS =====");

        // --- 1) 低层钩子里按偏移直读字段，偏移必须和结构体布局一致 ---
        Console.WriteLine("\n[1] hook struct offsets");
        int ptrSize = IntPtr.Size;
        Console.WriteLine("  (running as " + (ptrSize == 8 ? "x64" : "x86") + ")");
        Check(Native.KbdVkCodeOffset == 0, "KBDLLHOOKSTRUCT.vkCode offset = 0 (got " + Native.KbdVkCodeOffset + ")");
        int expectKbdExtra = (ptrSize == 8) ? 16 : 16;
        Check(Native.KbdExtraInfoOffset == expectKbdExtra,
              "KBDLLHOOKSTRUCT.dwExtraInfo offset = " + expectKbdExtra + " (got " + Native.KbdExtraInfoOffset + ")");
        Check(Native.MouseDataOffset == 8,
              "MSLLHOOKSTRUCT.mouseData offset = 8 (got " + Native.MouseDataOffset + ")");
        int expectMsExtra = (ptrSize == 8) ? 24 : 20;
        Check(Native.MouseExtraInfoOffset == expectMsExtra,
              "MSLLHOOKSTRUCT.dwExtraInfo offset = " + expectMsExtra + " (got " + Native.MouseExtraInfoOffset + ")");
        Check(Marshal.SizeOf(typeof(Native.INPUT)) == (ptrSize == 8 ? 40 : 28),
              "INPUT struct size correct for SendInput");

        // --- 2) 改键表：重点是鼠标侧键/中键/滚轮能进表 ---
        Console.WriteLine("\n[2] remap table (mouse buttons + wheel)");
        AppConfig cfg = new AppConfig();
        cfg.SetDefaults();
        cfg.FixUp();
        Scheme s = cfg.ActiveScheme;
        // 复刻用户实际配置的那套映射
        s.ItemKeys[0] = (int)'1';
        s.ItemKeys[4] = Native.VK_XBUTTON2;              // 物品5 = 鼠标侧键2
        s.Maps.Clear();
        s.Maps.Add(NewMap(Native.VK_XBUTTON1, (int)'O'));   // 侧键1 -> O
        s.Maps.Add(NewMap(Native.VK_MBUTTON, (int)'P'));    // 中键  -> P
        s.Maps.Add(NewMap(Native.VK_WHEELUP, (int)'6'));    // 滚轮上 -> 6
        s.Maps.Add(NewMap(Native.VK_WHEELDOWN, (int)'7'));  // 滚轮下 -> 7
        Engine.Cfg = cfg;
        Engine.Rebuild();

        int dst;
        Check(Engine.TryGetMapping(Native.VK_XBUTTON1, out dst) && dst == 'O', "side button 1 -> O");
        Check(Engine.TryGetMapping(Native.VK_MBUTTON, out dst) && dst == 'P', "middle button -> P");
        Check(Engine.TryGetMapping(Native.VK_WHEELUP, out dst) && dst == '6', "wheel up -> 6");
        Check(Engine.TryGetMapping(Native.VK_WHEELDOWN, out dst) && dst == '7', "wheel down -> 7");
        Check(Engine.TryGetMapping(Native.VK_XBUTTON2, out dst) && dst == Engine.ItemSlotVk[4],
              "side button 2 -> item slot 5");

        // 物品栏 2列x3行，界面上 物品1~3 是左列、4~6 是右列，
        // 所以目标键顺序必须是竖着数的 7 4 1 8 5 2，不是横着数的 7 8 4 5 1 2
        Console.WriteLine("\n[2a] item slot layout matches the on-screen 2x3 grid");
        int[] slot = Scheme.DefaultItemSlotDst();
        Check(slot[0] == 0x67 && slot[1] == 0x64 && slot[2] == 0x61,
              "left column top-to-bottom = numpad 7 / 4 / 1");
        Check(slot[3] == 0x68 && slot[4] == 0x65 && slot[5] == 0x62,
              "right column top-to-bottom = numpad 8 / 5 / 2");
        for (int i = 0; i < 6; i++)
            if (Engine.ItemSlotVk[i] != slot[i])
                Check(false, "Engine default order matches Scheme default order");
        Check(true, "Engine default order matches Scheme default order");

        // 旧配置(横向顺序)升级时要被改正
        AppConfig legacyCfg = new AppConfig();
        legacyCfg.SetDefaults();
        legacyCfg.ConfigVersion = 4;
        legacyCfg.Schemes[0].ItemSlotDst = Scheme.LegacyRowMajorItemSlotDst();
        legacyCfg.Schemes[0].Maps.Add(NewMap((int)'S', (int)'H'));
        legacyCfg.FixUp();
        Check(legacyCfg.Schemes[0].ItemSlotDst[1] == 0x64,
              "upgrading a v4 config fixes the row-major slot order");
        bool stillHasManualSH = false;
        foreach (KeyMapEntry e in legacyCfg.Schemes[0].Maps)
            if (e.Src == 'S' && e.Dst == 'H') stillHasManualSH = true;
        Check(!stillHasManualSH, "upgrading removes the manual S->H now that it is built in");
        Check(legacyCfg.BuiltinStopAsHold, "upgrading turns the built-in S->H on");
        Check(Engine.IsItemSlotSource(Native.VK_XBUTTON2), "side button 2 recognised as an item-slot key");
        Check(!Engine.IsItemSlotSource(Native.VK_XBUTTON1), "side button 1 is not an item-slot key");
        Check(!Engine.TryGetMapping((int)'Q', out dst), "unmapped key stays unmapped");

        // --- 2a2) 内置 S->H：默认开，可关，玩家显式改S时以玩家的为准 ---
        Console.WriteLine("\n[2a2] built-in S -> H");
        Check(cfg.BuiltinStopAsHold, "built-in S->H defaults to on");
        Engine.Rebuild();
        Check(Engine.TryGetMapping((int)'S', out dst) && dst == 'H', "built-in gives S -> H");
        s.Maps.Add(NewMap((int)'S', (int)'P'));
        Engine.Rebuild();
        Check(Engine.TryGetMapping((int)'S', out dst) && dst == 'P',
              "an explicit S mapping overrides the built-in");
        s.Maps.RemoveAt(s.Maps.Count - 1);
        cfg.BuiltinStopAsHold = false;
        Engine.Rebuild();
        Check(!Engine.TryGetMapping((int)'S', out dst), "unchecking the built-in removes S -> H");
        cfg.BuiltinStopAsHold = true;
        Engine.Rebuild();

        // --- 2b) 必须能识别出"有改键指向小键盘" ---
        // NumLock 关闭时小键盘键在系统层面是 Home/方向键/End，魔兽的物品栏快捷键
        // 就收不到，所以助手要靠这个标志决定是否自动打开 NumLock。
        Check(Engine.MapsToNumpad, "detects that the scheme targets numpad keys");
        Scheme noNum = new Scheme();
        noNum.Maps.Add(NewMap((int)'S', (int)'H'));
        cfg.Schemes.Add(noNum);
        cfg.CurrentScheme = cfg.Schemes.Count - 1;
        Engine.Rebuild();
        Check(!Engine.MapsToNumpad, "a scheme without numpad targets does not ask for NumLock");
        cfg.CurrentScheme = 0;
        Engine.Rebuild();
        Check(Engine.MapsToNumpad, "flag follows the active scheme");

        // --- 2c) 商店模式：触发键必须豁免，否则滚轮进商店后自己就被挂起了 ---
        Console.WriteLine("\n[2c] shop mode exemptions");
        cfg.ShopModeEnabled = true;
        cfg.ShopEnterOnWheel = true;
        cfg.ShopExitKey = 0x70;
        Engine.Rebuild();
        Engine.ExitShopMode();
        Check(!Engine.IsSuspendedFor((int)'S'), "not suspended before entering shop mode");
        Engine.EnterShopModeForTest();
        Check(Engine.IsSuspendedFor((int)'S'), "S is suspended in shop mode");
        Check(!Engine.IsSuspendedFor(Native.VK_WHEELUP), "wheel up stays active (it is the trigger)");
        Check(!Engine.IsSuspendedFor(Native.VK_WHEELDOWN), "wheel down stays active (it is the trigger)");
        Check(Engine.IsSuspendedFor(Native.VK_XBUTTON2), "item-slot side button is suspended in shop mode");
        Engine.ExitShopMode();
        Check(!Engine.IsSuspendedFor((int)'S'), "S active again after leaving shop mode");

        // 逐条勾"商店模式下仍生效"
        s.Maps[0].KeepInShop = true;      // 侧键1 -> O
        s.ItemKeysKeepInShop = true;
        Engine.Rebuild();
        Engine.EnterShopModeForTest();
        Check(!Engine.IsSuspendedFor(Native.VK_XBUTTON1), "per-entry KeepInShop keeps that mapping active");
        Check(!Engine.IsSuspendedFor(Native.VK_XBUTTON2), "ItemKeysKeepInShop keeps item slots active");
        Check(Engine.IsSuspendedFor((int)'S'), "unflagged mapping is still suspended");
        Engine.ExitShopMode();
        s.Maps[0].KeepInShop = false;
        s.ItemKeysKeepInShop = false;
        cfg.ShopModeEnabled = false;
        Engine.Rebuild();

        // --- 2d) 换一套完全不同的配置，规则应当同样自洽 ---
        Console.WriteLine("\n[2d] a completely different config still behaves consistently");
        AppConfig alt = new AppConfig();
        alt.SetDefaults();
        alt.FixUp();
        Scheme a = alt.ActiveScheme;

        // 用了 War3 自定义快捷键的人：物品栏根本不是小键盘，而是 QWERASDF 那种
        a.ItemSlotDst = new int[] { (int)'Z', (int)'X', (int)'C', (int)'V', (int)'B', (int)'N' };
        a.ItemKeys[0] = (int)'Q';
        a.ItemKeys[1] = Native.VK_MBUTTON;
        // 链式：A->B 且 B->C，两条互不影响(注入的键带魔数，不会再进钩子)
        a.Maps.Add(NewMap((int)'A', (int)'B'));
        a.Maps.Add(NewMap((int)'B', (int)'C'));
        alt.Chats.Clear();
        ChatItem cc = new ChatItem(); cc.Mods = 0; cc.Key = (int)'A'; cc.Text = "-ma";
        alt.Chats.Add(cc);
        alt.BuiltinStopAsHold = false;      // 这套配置不用内置项
        Engine.Cfg = alt;
        Engine.Rebuild();

        Check(Engine.TryGetMapping((int)'Q', out dst) && dst == 'Z',
              "item slot honours the scheme's own target key, not hardcoded numpad");
        Check(Engine.TryGetMapping(Native.VK_MBUTTON, out dst) && dst == 'X',
              "middle button drives item slot 2 with a custom target");
        Check(Engine.IsItemSlotSource((int)'Q'), "custom item target still recognised as an item-slot key");
        Check(!Engine.MapsToNumpad, "no numpad anywhere -> does not force NumLock on");

        Check(Engine.TryGetMapping((int)'A', out dst) && dst == 'B', "chain: A maps to B");
        Check(Engine.TryGetMapping((int)'B', out dst) && dst == 'C', "chain: B maps to C");
        string ct;
        Check(Engine.TryGetChat(0, (int)'A', out ct) && ct == "-ma",
              "a chat hotkey on the same key as a remap is resolved first (documented precedence)");

        // 把小键盘键当"源"用：NumLock 关闭时它根本不是小键盘键，同样要强制打开
        Scheme np = new Scheme();
        np.Maps.Add(NewMap(0x64, (int)'Q'));      // 小键盘4 -> Q
        alt.Schemes.Add(np);
        alt.CurrentScheme = alt.Schemes.Count - 1;
        Engine.Rebuild();
        Check(Engine.MapsToNumpad, "numpad used as a SOURCE also forces NumLock (was only checking targets)");

        // 进入键和恢复键设成同一个键 -> 当成开关，不能变成永远进不去
        alt.CurrentScheme = 0;
        alt.ShopModeEnabled = true;
        alt.ShopEnterOnWheel = false;
        alt.ShopEnterKey = (int)'G';
        alt.ShopExitKey = (int)'G';
        Engine.Rebuild();
        Engine.ExitShopMode();
        Check(!Engine.IsSuspendedFor((int)'A'), "same enter/exit key: starts inactive");
        Engine.EnterShopModeForTest();
        Check(Engine.IsSuspendedFor((int)'A'), "same enter/exit key: toggles on");
        Check(!Engine.IsSuspendedFor((int)'G'), "the toggle key itself is never suspended");
        Engine.ExitShopMode();

        Engine.Cfg = cfg;     // 还原给后面的用例
        cfg.CurrentScheme = 0;
        Engine.Rebuild();

        // --- 3) 滚轮方向解码：mouseData 高16位是有符号short ---
        Console.WriteLine("\n[3] wheel delta decoding");
        Check(DecodeWheel(0x00780000) > 0, "mouseData 0x00780000 (+120) decodes as wheel UP");
        Check(DecodeWheel(unchecked((int)0xFF880000)) < 0, "mouseData 0xFF880000 (-120) decodes as wheel DOWN");
        Check(DecodeXButton(0x00010000) == 1, "mouseData 0x00010000 decodes as XBUTTON1");
        Check(DecodeXButton(0x00020000) == 2, "mouseData 0x00020000 decodes as XBUTTON2");

        // --- 3b) 滚轮节流：一格刻度连发多次时只放行第一次 ---
        Console.WriteLine("\n[3b] wheel throttle");
        cfg.WheelMinIntervalMs = 200;
        Engine.Cfg = cfg;
        Engine.ResetWheelThrottle();
        Check(Engine.TryConsumeWheel(Native.VK_WHEELUP), "first wheel-up passes");
        System.Threading.Thread.Sleep(80);    // 实测日志里重复事件的间隔就是 78~94ms
        Check(!Engine.TryConsumeWheel(Native.VK_WHEELUP), "duplicate 80ms later is dropped");
        Check(Engine.TryConsumeWheel(Native.VK_WHEELDOWN),
              "the opposite direction is NOT blocked (separate timers)");
        System.Threading.Thread.Sleep(230);
        Check(Engine.TryConsumeWheel(Native.VK_WHEELUP), "passes again once the interval has elapsed");

        cfg.WheelMinIntervalMs = 0;
        Engine.ResetWheelThrottle();
        Check(Engine.TryConsumeWheel(Native.VK_WHEELUP), "0 means no throttling (1)");
        Check(Engine.TryConsumeWheel(Native.VK_WHEELUP), "0 means no throttling (2)");
        cfg.WheelMinIntervalMs = 300;
        Engine.ResetWheelThrottle();

        // --- 4) 喊话热键表(组合键) ---
        Console.WriteLine("\n[4] chat hotkey table");
        string text;
        Check(Engine.TryGetChat(Mods.Alt, (int)'4', out text) && text == "-test", "Alt+4 -> -test");
        Check(Engine.TryGetChat(Mods.Alt, (int)'1', out text) && text == "-aphehg", "Alt+1 -> -aphehg");
        Check(!Engine.TryGetChat(0, (int)'4', out text), "plain 4 does not trigger chat");
        Check(!Engine.TryGetChat(Mods.Ctrl, (int)'4', out text), "Ctrl+4 does not trigger the Alt+4 entry");

        // --- 5) 逐字输入：英文命令必须走真实按键，不能依赖Unicode注入 ---
        Console.WriteLine("\n[5] chat typing path");
        string[] cmds = new string[] { "-aphehg", "-apemhg", "-arem", "-test", "-ii",
                                       "-di", "-ma", "-cson", "-random", "-repick",
                                       "-clear", "-swaphero", "-unstuck" };
        bool allReal = true;
        foreach (string cmd in cmds)
            foreach (char ch in cmd)
                if (!Engine.CanTypeAsRealKey(ch)) { allReal = false; Console.WriteLine("      '" + ch + "' cannot"); }
        Check(allReal, "every char of every built-in DOTA command types as a real key press");
        Check(Engine.CanTypeAsRealKey('A') && Engine.CanTypeAsRealKey('z') && Engine.CanTypeAsRealKey('0'),
              "letters and digits type as real key presses");
        Check(!Engine.CanTypeAsRealKey('中'), "Chinese falls back to Unicode injection");

        // --- 6) 配置持久化：保存后重新载入，方案不能丢 ---
        Console.WriteLine("\n[6] config round trip");
        AppConfig reloaded = RoundTrip(cfg);
        Check(reloaded != null, "config serialises and deserialises");
        if (reloaded != null)
        {
            Scheme rs = reloaded.ActiveScheme;
            Check(rs.ItemKeys[4] == Native.VK_XBUTTON2, "item slot 5 side-button survives save/load");
            bool foundWheel = false;
            foreach (KeyMapEntry e in rs.Maps)
                if (e.Src == Native.VK_WHEELUP && e.Dst == '6') foundWheel = true;
            Check(foundWheel, "wheel mapping survives save/load");
            Check(reloaded.Chats.Count == cfg.Chats.Count, "chat entries survive save/load");
        }

        Console.WriteLine("\n" + (failures == 0 ? "ENGINE TESTS PASSED" : failures + " ENGINE TEST(S) FAILED"));
        return failures;
    }

    static KeyMapEntry NewMap(int src, int dst)
    {
        KeyMapEntry e = new KeyMapEntry();
        e.Src = src; e.Dst = dst;
        return e;
    }

    // 复刻 MsProc 里的解码，确保位运算正确
    static int DecodeWheel(int mouseData)
    {
        return (short)((mouseData >> 16) & 0xFFFF);
    }

    static int DecodeXButton(int mouseData)
    {
        return (mouseData >> 16) & 0xFFFF;
    }

    static AppConfig RoundTrip(AppConfig c)
    {
        try
        {
            System.Web.Script.Serialization.JavaScriptSerializer ser =
                new System.Web.Script.Serialization.JavaScriptSerializer();
            AppConfig back = ser.Deserialize<AppConfig>(ser.Serialize(c));
            if (back != null) back.FixUp();
            return back;
        }
        catch { return null; }
    }
}
