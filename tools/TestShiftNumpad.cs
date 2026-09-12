using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using War3Helper;

// Opt-in Windows integration test: exercises the real hook -> SendInput -> hook path.
// Item/navigation events are swallowed by the capture hook, so they never reach an app.
static class ShiftNumpadTests
{
    static Native.HookProc captureProc = Capture;
    static Native.HookProc captureMouseProc = CaptureMouse;
    static IntPtr captureHook;
    static IntPtr captureMouseHook;
    static bool capturing;
    static int itemDowns, wrongKeys, unshiftedItems;
    static int rightClicks, leftClicks, unshiftedClicks;
    static int failures;
    static readonly IntPtr TestMagic = new IntPtr(0x57535432);

    static IntPtr Capture(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && capturing)
        {
            Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(Native.KBDLLHOOKSTRUCT));
            bool down = msg.ToInt32() == Native.WM_KEYDOWN || msg.ToInt32() == Native.WM_SYSKEYDOWN;
            // Windows can remove dwExtraInfo from the matching key-up.
            if (k.vkCode == 0x62 || k.vkCode == 0x28 || k.vkCode == 0x20 || k.vkCode == 0x70)
            {
                if (down)
                {
                    if (k.vkCode == 0x62)
                    {
                        itemDowns++;
                        if ((Native.GetAsyncKeyState(0x10) & 0x8000) == 0) unshiftedItems++;
                    }
                    else wrongKeys++;
                }
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(captureHook, code, msg, data);
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
    }

    static IntPtr CaptureMouse(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && capturing && Native.ReadExtraInfo(data, Native.MouseExtraInfoOffset) == TestMagic)
        {
            int message = msg.ToInt32();
            if (message == Native.WM_RBUTTONDOWN || message == Native.WM_LBUTTONDOWN)
            {
                if (message == Native.WM_RBUTTONDOWN) rightClicks++; else leftClicks++;
                if ((Native.GetAsyncKeyState(0x10) & 0x8000) == 0) unshiftedClicks++;
            }
            // Never deliver test clicks to the user's foreground window.
            return new IntPtr(1);
        }
        return Native.CallNextHookEx(captureMouseHook, code, msg, data);
    }

    static void Click(bool right)
    {
        Native.INPUT[] input = new Native.INPUT[1];
        input[0].u.mi.dwFlags = right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN;
        input[0].u.mi.dwExtraInfo = TestMagic;
        int expected = right ? rightClicks + 1 : leftClicks + 1;
        if (Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT))) != 1)
            throw new InvalidOperationException("Mouse SendInput failed");
        // Mouse hook delivery can lag the SendInput call; wait for the actual capture.
        for (int i = 0; i < 20 && (right ? rightClicks : leftClicks) < expected; i++) Pump();
        input[0].u.mi.dwFlags = right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP;
        if (Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT))) != 1)
            throw new InvalidOperationException("Mouse SendInput release failed");
        Pump();
    }

    static void Input(int vk, bool down)
    {
        Native.INPUT[] input = new Native.INPUT[1];
        input[0].type = 1;
        input[0].u.ki.wVk = (ushort)vk;
        input[0].u.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
        input[0].u.ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;
        input[0].u.ki.dwExtraInfo = TestMagic;
        if (Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT))) != 1)
            throw new InvalidOperationException("SendInput failed");
        Pump();
    }

    static void Check(bool condition, string message)
    {
        Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + message);
        if (!condition) failures++;
    }

    public static int Run()
    {
        Console.WriteLine("===== SHIFT + NUMPAD INPUT INTEGRATION =====");
        IntPtr foreground = Native.GetForegroundWindow();
        if (War3Ctl.IsWar3WindowFast(foreground) || !Engine.NumLockOn
            || (Native.GetAsyncKeyState(0x10) & 0x8000) != 0
            || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0
            || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0)
        {
            Console.WriteLine("SKIP: requires NumLock on, no held modifiers, and a non-game foreground window.");
            return 1;
        }
        AppConfig saved = Engine.Cfg;
        AppConfig cfg = new AppConfig();
        cfg.SetDefaults();
        cfg.ActiveScheme.ItemKeys[5] = 0x20;
        Engine.Cfg = cfg;
        Engine.Rebuild();
        // Keep real event dispatch and OS input processing; only game detection is substituted.
        typeof(Engine).GetField("_fgMemoHwnd", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, foreground);
        typeof(Engine).GetField("_fgMemoResult", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true);
        captureHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, captureProc, Native.GetModuleHandle(null), 0);
        if (captureHook == IntPtr.Zero) throw new InvalidOperationException("Capture hook install failed");
        try
        {
            captureMouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, captureMouseProc, Native.GetModuleHandle(null), 0);
            if (captureMouseHook == IntPtr.Zero) throw new InvalidOperationException("Mouse capture hook install failed");
            Engine.Install(); // Installed last: Engine handles each event before Capture.
            capturing = true;
            for (int mode = 0; mode <= 1; mode++)
                foreach (int shift in new int[] { 0xA0, 0xA1 })
                    foreach (bool heroFirst in new bool[] { false, true })
                    {
                        cfg.InjectMode = mode;
                        cfg.ItemKeySelectHeroFirst = heroFirst;
                        Engine.ResetItemPressTimes();
                        itemDowns = wrongKeys = unshiftedItems = 0;
                        Input(shift, true);
                        Input(0x20, true);
                        Input(0x20, true); // Held-key repeat.
                        Input(0x20, false);
                        Input(0x20, true); // Second tap while Shift remains held.
                        Input(0x20, false);
                        Input(shift, false);
                        string label = "mode=" + mode + " shift=" + shift.ToString("X2") + " heroFirst=" + heroFirst;
                        Check(itemDowns == 3 && wrongKeys == 0 && unshiftedItems == 0,
                            label + ": 3 queued item presses, no Space/Down/F1 (items=" + itemDowns
                            + ", wrong=" + wrongKeys + ", unshifted=" + unshiftedItems + ")");
                        Check((Native.GetAsyncKeyState(0x10) & 0x8000) == 0, label + ": Shift releases normally");

                        // User's full sequence: hold Shift -> move order -> item -> target click.
                        Engine.ResetItemPressTimes();
                        itemDowns = wrongKeys = unshiftedItems = 0;
                        rightClicks = leftClicks = unshiftedClicks = 0;
                        Input(shift, true);
                        Click(true);
                        Input(0x20, true);
                        Input(0x20, false);
                        Click(false);
                        Input(shift, false);
                        Check(itemDowns == 1 && wrongKeys == 0 && unshiftedItems == 0
                              && rightClicks == 1 && leftClicks == 1 && unshiftedClicks == 0,
                            label + ": Shift preserved through right-click -> Space -> left-click"
                            + " (items=" + itemDowns + ", wrong=" + wrongKeys + ", unshiftedItems=" + unshiftedItems
                            + ", right=" + rightClicks + ", left=" + leftClicks + ", unshiftedClicks=" + unshiftedClicks + ")");
                        Check((Native.GetAsyncKeyState(0x10) & 0x8000) == 0,
                            label + ": Shift releases after target click");
                    }
        }
        finally
        {
            Input(0xA0, false);
            Input(0xA1, false);
            Engine.Uninstall();
            capturing = false;
            Native.UnhookWindowsHookEx(captureHook);
            if (captureMouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(captureMouseHook);
            Engine.Cfg = saved;
            Engine.Rebuild();
            Engine.InvalidateForegroundMemo();
        }
        return failures;
    }
}
