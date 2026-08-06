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
              "side button 2 -> item slot 5 (numpad 1)");
        Check(Engine.IsItemSlotSource(Native.VK_XBUTTON2), "side button 2 recognised as an item-slot key");
        Check(!Engine.IsItemSlotSource(Native.VK_XBUTTON1), "side button 1 is not an item-slot key");
        Check(!Engine.TryGetMapping((int)'Q', out dst), "unmapped key stays unmapped");

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

        // --- 3) 滚轮方向解码：mouseData 高16位是有符号short ---
        Console.WriteLine("\n[3] wheel delta decoding");
        Check(DecodeWheel(0x00780000) > 0, "mouseData 0x00780000 (+120) decodes as wheel UP");
        Check(DecodeWheel(unchecked((int)0xFF880000)) < 0, "mouseData 0xFF880000 (-120) decodes as wheel DOWN");
        Check(DecodeXButton(0x00010000) == 1, "mouseData 0x00010000 decodes as XBUTTON1");
        Check(DecodeXButton(0x00020000) == 2, "mouseData 0x00020000 decodes as XBUTTON2");

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
