using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WshHelper
{
    // 键盘/鼠标底层钩子 + 改键引擎 + 喊话 + APM统计 + 血条常显
    public static class Engine
    {
        // 物品栏1~6对应小键盘 7 8 4 5 1 2
        public static readonly int[] ItemSlotVk = new int[] { 0x67, 0x68, 0x64, 0x65, 0x61, 0x62 };

        public const int VK_ALT = 0x12;

        public static AppConfig Cfg;

        public static event Action HotToggleRemap;      // Ctrl+F2
        public static event Action HotNextScheme;       // Ctrl+F3
        public static event Action HotToggleLock;       // Ctrl+F4
        public static event Action HotToggleBars;       // Ctrl+F5
        public static event Action HotToggleApm;        // Ctrl+F7
        public static event Action HotTimerReset;       // Ctrl+F8

        static IntPtr _kbHook = IntPtr.Zero;
        static IntPtr _msHook = IntPtr.Zero;
        static Native.HookProc _kbProc;
        static Native.HookProc _msProc;

        static Dictionary<int, int> _map = new Dictionary<int, int>();
        // 喊话表: key = vk | (mods << 16)
        static Dictionary<int, string> _chatMap = new Dictionary<int, string>();
        static readonly HashSet<int> _swallowedUp = new HashSet<int>();
        static readonly bool[] _phyDown = new bool[256];
        static volatile bool _sendingChat = false;

        // 血条常显(合成Alt)状态
        static volatile bool _synthAlt = false;
        static uint _altResumeAt = 0;

        // war3前台状态缓存
        static bool _fgCached = false;
        static uint _fgCheckedAt = 0;

        // APM
        static readonly object _apmLock = new object();
        static readonly Queue<uint> _apmTicks = new Queue<uint>();

        public static void Install()
        {
            _kbProc = KbProc;
            _msProc = MsProc;
            IntPtr hMod = Native.GetModuleHandle(null);
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hMod, 0);
        }

        public static void Uninstall()
        {
            ReleaseSynthAlt();
            if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
        }

        public static int ChatKey(int mods, int vk) { return (vk & 0xFFFF) | (mods << 16); }

        // 根据当前方案重建映射表
        public static void Rebuild()
        {
            Dictionary<int, int> m = new Dictionary<int, int>();
            Dictionary<int, string> cm = new Dictionary<int, string>();
            if (Cfg != null)
            {
                Scheme s = Cfg.ActiveScheme;
                for (int i = 0; i < 6; i++)
                    if (s.ItemKeys[i] != 0) m[s.ItemKeys[i]] = ItemSlotVk[i];
                foreach (KeyMapEntry e in s.Maps)
                    if (e.Src != 0 && e.Dst != 0 && e.Src != e.Dst) m[e.Src] = e.Dst;
                if (Cfg.ChatEnabled && Cfg.Chats != null)
                    foreach (ChatItem c in Cfg.Chats)
                        if (c.Key != 0 && !string.IsNullOrEmpty(c.Text))
                            cm[ChatKey(c.Mods, c.Key)] = c.Text;
            }
            _map = m;
            _chatMap = cm;
        }

        public static bool War3Foreground()
        {
            uint now = (uint)Environment.TickCount;
            if (now - _fgCheckedAt < 250) return _fgCached;
            _fgCheckedAt = now;
            _fgCached = War3Ctl.IsWar3Window(Native.GetForegroundWindow());
            return _fgCached;
        }

        // ---- 物理修饰键状态(不受合成按键影响) ----
        static bool PhysCtrl { get { return _phyDown[0x11] || _phyDown[0xA2] || _phyDown[0xA3]; } }
        static bool PhysAlt { get { return _phyDown[0x12] || _phyDown[0xA4] || _phyDown[0xA5]; } }
        static bool PhysShift { get { return _phyDown[0x10] || _phyDown[0xA0] || _phyDown[0xA1]; } }

        static int PhysMods
        {
            get
            {
                int m = 0;
                if (PhysCtrl) m |= Mods.Ctrl;
                if (PhysAlt) m |= Mods.Alt;
                if (PhysShift) m |= Mods.Shift;
                return m;
            }
        }

        static bool IsModifierVk(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12
                || (vk >= 0xA0 && vk <= 0xA5);
        }

        static void CountApm()
        {
            uint now = (uint)Environment.TickCount;
            lock (_apmLock)
            {
                _apmTicks.Enqueue(now);
                while (_apmTicks.Count > 0 && now - _apmTicks.Peek() > 60000) _apmTicks.Dequeue();
            }
        }

        public static int CurrentApm()
        {
            uint now = (uint)Environment.TickCount;
            lock (_apmLock)
            {
                while (_apmTicks.Count > 0 && now - _apmTicks.Peek() > 60000) _apmTicks.Dequeue();
                return _apmTicks.Count;
            }
        }

        // ---- 血条/蓝条常显：持续保持Alt按下 ----
        // 玩家真正操作(按键/点击)时先松开Alt，避免产生 Alt+点击(信号) / Alt+键 组合，
        // 停手约0.35秒后重新按住，血条随即恢复显示。
        public static bool BarsActive { get { return _synthAlt; } }

        static void PressSynthAlt()
        {
            if (_synthAlt) return;
            _synthAlt = true;
            SendVk(VK_ALT, true);
        }

        public static void ReleaseSynthAlt()
        {
            if (!_synthAlt) return;
            _synthAlt = false;
            SendVk(VK_ALT, false);
        }

        static void SuspendBars()
        {
            _altResumeAt = (uint)Environment.TickCount + 350;
            ReleaseSynthAlt();
        }

        // 由主界面定时器调用
        public static void TickBars()
        {
            if (Cfg == null) return;
            bool want = Cfg.ShowHpBars && !_sendingChat && War3Foreground()
                        && !PhysAlt && !PhysCtrl && !PhysShift
                        && (uint)Environment.TickCount >= _altResumeAt;
            if (want) PressSynthAlt();
            else if (!Cfg.ShowHpBars || !War3Foreground()) ReleaseSynthAlt();
        }

        static IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
            Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if (k.dwExtraInfo == Native.InjectMagic)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
            bool up = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
            int vk = (int)k.vkCode;

            // 物理按键状态始终跟踪(即使不在游戏中)，供修饰键判断使用
            bool repeat = false;
            if (vk < 256)
            {
                if (down) { repeat = _phyDown[vk]; _phyDown[vk] = true; }
                else if (up) _phyDown[vk] = false;
            }

            if (_sendingChat || Cfg == null)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            if (!War3Foreground())
            {
                if (_synthAlt) ReleaseSynthAlt();
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
            }

            // 玩家有真实按键动作 -> 暂时收起合成Alt
            if (down && _synthAlt) SuspendBars();
            else if (down) _altResumeAt = (uint)Environment.TickCount + 350;

            // APM统计（去掉长按自动重复）
            if (down && !repeat) CountApm();

            int mods = PhysMods;

            // 控制热键 Ctrl+F2/F3/F4/F5/F7/F8
            if (down && PhysCtrl && !PhysAlt && !PhysShift)
            {
                Action fired = null;
                if (vk == 0x71) fired = HotToggleRemap;
                else if (vk == 0x72) fired = HotNextScheme;
                else if (vk == 0x73) fired = HotToggleLock;
                else if (vk == 0x74) fired = HotToggleBars;
                else if (vk == 0x76) fired = HotToggleApm;
                else if (vk == 0x77) fired = HotTimerReset;
                if (fired != null)
                {
                    _swallowedUp.Add(vk);
                    try { fired(); } catch { }
                    return new IntPtr(1);
                }
            }

            // 屏蔽Win键
            if (Cfg.BlockWinKey && (vk == 0x5B || vk == 0x5C))
                return new IntPtr(1);

            // 快捷喊话（修饰键组合必须完全匹配）
            if (down && !IsModifierVk(vk))
            {
                string text;
                if (_chatMap.TryGetValue(ChatKey(mods, vk), out text))
                {
                    _swallowedUp.Add(vk);
                    SendChatAsync(text);
                    return new IntPtr(1);
                }
            }

            if (up && _swallowedUp.Contains(vk))
            {
                _swallowedUp.Remove(vk);
                return new IntPtr(1);
            }

            // 改键
            if (Cfg.RemapEnabled && (down || up))
            {
                int dst;
                if (_map.TryGetValue(vk, out dst))
                {
                    if (!Cfg.ApplyToCombo && mods != 0)
                        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
                    SendVk(dst, down);
                    return new IntPtr(1);
                }
            }

            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        static IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
            Native.MSLLHOOKSTRUCT m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            if (m.dwExtraInfo == Native.InjectMagic)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            if (_sendingChat || Cfg == null || msg == Native.WM_MOUSEMOVE || !War3Foreground())
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            bool anyDown = (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                            msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN);

            // 点击前先松开合成Alt，否则会变成 Alt+点击(在DOTA里是发信号)
            if (anyDown)
            {
                if (_synthAlt) SuspendBars();
                else _altResumeAt = (uint)Environment.TickCount + 350;
            }

            if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN) CountApm();

            if (!Cfg.RemapEnabled)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            int src = 0; bool down = false, wheel = false;
            switch (msg)
            {
                case Native.WM_MBUTTONDOWN: src = Native.VK_MBUTTON; down = true; break;
                case Native.WM_MBUTTONUP: src = Native.VK_MBUTTON; down = false; break;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    {
                        int which = (int)(m.mouseData >> 16) & 0xFFFF;
                        src = (which == 1) ? Native.VK_XBUTTON1 : Native.VK_XBUTTON2;
                        down = (msg == Native.WM_XBUTTONDOWN);
                        break;
                    }
                case Native.WM_MOUSEWHEEL:
                    {
                        short delta = (short)((m.mouseData >> 16) & 0xFFFF);
                        src = delta > 0 ? Native.VK_WHEELUP : Native.VK_WHEELDOWN;
                        wheel = true;
                        break;
                    }
            }
            if (src != 0)
            {
                int dst;
                if (_map.TryGetValue(src, out dst))
                {
                    if (!Cfg.ApplyToCombo && PhysMods != 0)
                        return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
                    if (wheel) { SendVk(dst, true); SendVk(dst, false); }
                    else SendVk(dst, down);
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        // 发送一个按键(支持鼠标伪键码)
        public static void SendVk(int vk, bool down)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            if (vk == Native.VK_LBUTTON || vk == Native.VK_RBUTTON || vk == Native.VK_MBUTTON)
            {
                inp[0].type = 0;
                uint f;
                if (vk == Native.VK_LBUTTON) f = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP;
                else if (vk == Native.VK_RBUTTON) f = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP;
                else f = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP;
                inp[0].u.mi.dwFlags = f;
                inp[0].u.mi.dwExtraInfo = Native.InjectMagic;
            }
            else
            {
                inp[0].type = 1;
                inp[0].u.ki.wVk = (ushort)vk;
                inp[0].u.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
                inp[0].u.ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;
                if (IsExtended(vk)) inp[0].u.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
                inp[0].u.ki.dwExtraInfo = Native.InjectMagic;
            }
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static bool IsExtended(int vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:
                case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2D: case 0x2E: case 0x6F: case 0x90:
                case 0xA3: case 0xA5:
                    return true;
            }
            return false;
        }

        public static void TapVk(int vk)
        {
            SendVk(vk, true);
            SendVk(vk, false);
        }

        static void SendUnicodeChar(char ch)
        {
            Native.INPUT[] inp = new Native.INPUT[2];
            inp[0].type = 1;
            inp[0].u.ki.wScan = ch;
            inp[0].u.ki.dwFlags = Native.KEYEVENTF_UNICODE;
            inp[0].u.ki.dwExtraInfo = Native.InjectMagic;
            inp[1].type = 1;
            inp[1].u.ki.wScan = ch;
            inp[1].u.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
            inp[1].u.ki.dwExtraInfo = Native.InjectMagic;
            Native.SendInput(2, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // 游戏内发送聊天: 回车 -> 逐字输入 -> 回车
        public static void SendChatAsync(string text)
        {
            if (_sendingChat) return;
            _sendingChat = true;
            ReleaseSynthAlt();
            _altResumeAt = (uint)Environment.TickCount + 2000;
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    // 等玩家松开触发喊话的修饰键，避免 Ctrl/Alt 影响输入
                    for (int i = 0; i < 40 && PhysMods != 0; i++) Thread.Sleep(25);
                    TapVk(0x0D);
                    Thread.Sleep(120);
                    foreach (char ch in text)
                    {
                        SendUnicodeChar(ch);
                        Thread.Sleep(3);
                    }
                    Thread.Sleep(60);
                    TapVk(0x0D);
                    Thread.Sleep(80);
                }
                catch { }
                finally { _sendingChat = false; }
            });
        }
    }
}
