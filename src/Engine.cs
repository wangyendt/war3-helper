using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WshHelper
{
    // 键盘/鼠标底层钩子 + 改键引擎 + 喊话 + APM统计 + 血条常显
    //
    // 低层钩子的硬性约束：回调必须在 LowLevelHooksTimeout(默认300ms)内返回，
    // 否则 Windows 会**静默移除**这个钩子，之后所有事件都不再回调。
    // 鼠标钩子每秒会被调用上千次(鼠标移动)，所以回调里：
    //   - 不做 Marshal.PtrToStructure(会装箱分配，制造GC压力)
    //   - 不查进程信息(Process.GetProcessById 很贵)
    //   - 鼠标移动在读取任何字段之前就直接返回
    // 另外 WatchdogTick() 会检测钩子是否已被摘掉并自动重装。
    public static class Engine
    {
        // 物品栏1~6对应小键盘 7 8 4 5 1 2
        public static readonly int[] ItemSlotVk = new int[] { 0x67, 0x68, 0x64, 0x65, 0x61, 0x62 };

        public const int VK_ALT = 0x12;
        public const int VK_F1 = 0x70;

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
        static HashSet<int> _itemSrc = new HashSet<int>();      // 映射到物品栏的源键
        static Dictionary<int, string> _chatMap = new Dictionary<int, string>();
        static readonly HashSet<int> _swallowedUp = new HashSet<int>();
        static readonly bool[] _phyDown = new bool[256];
        static volatile bool _sendingChat = false;

        // 血条常显(合成Alt)状态
        static volatile bool _synthAlt = false;
        static uint _altResumeAt = 0;

        // 钩子存活计数（看门狗用）
        static int _msTicks = 0;
        static int _kbTicks = 0;
        public static int MouseHookTicks { get { return _msTicks; } }
        public static int ReinstallCount { get { return _reinstalls; } }
        static int _reinstalls = 0;

        // APM
        static readonly object _apmLock = new object();
        static readonly Queue<uint> _apmTicks = new Queue<uint>();

        public static void Install()
        {
            Uninstall();
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

        // ---- 看门狗：钩子被系统摘掉后自动重装 ----
        static Native.POINT _lastCursor;
        static int _lastMsTicks;
        static uint _suspectSince;

        // 键盘钩子没有"鼠标在动"这种天然信号，所以主动打一个心跳：
        // 注入一个未定义键(0xFF)的抬起事件，钩子活着就一定会被回调到
        // (计数在 InjectMagic 判断之前)。下一拍如果计数没变，就说明钩子已经死了。
        const int HeartbeatVk = 0xFF;
        static int _kbTicksAtBeat = -1;
        static bool _beatPending;

        public static void WatchdogTick()
        {
            bool dead = false;

            // --- 鼠标钩子：鼠标在动但回调没被触发 ---
            Native.POINT cur;
            if (Native.GetCursorPos(out cur))
            {
                bool moved = (cur.x != _lastCursor.x || cur.y != _lastCursor.y);
                _lastCursor = cur;
                if (moved && _msTicks == _lastMsTicks)
                {
                    uint now = (uint)Environment.TickCount;
                    if (_suspectSince == 0) _suspectSince = now;
                    else if (now - _suspectSince > 1500) dead = true;
                }
                else _suspectSince = 0;
                _lastMsTicks = _msTicks;
            }

            // --- 键盘钩子：心跳 ---
            if (_beatPending)
            {
                if (_kbTicks == _kbTicksAtBeat) dead = true;
                _beatPending = false;
            }
            else
            {
                _kbTicksAtBeat = _kbTicks;
                _beatPending = true;
                SendVk(HeartbeatVk, false);
            }

            if (dead)
            {
                _reinstalls++;
                Install();
                _suspectSince = 0;
                _beatPending = false;
            }
        }

        public static int ChatKey(int mods, int vk) { return (vk & 0xFFFF) | (mods << 16); }

        // 根据当前方案重建映射表
        public static void Rebuild()
        {
            Dictionary<int, int> m = new Dictionary<int, int>();
            HashSet<int> item = new HashSet<int>();
            Dictionary<int, string> cm = new Dictionary<int, string>();
            if (Cfg != null)
            {
                Scheme s = Cfg.ActiveScheme;
                for (int i = 0; i < 6; i++)
                    if (s.ItemKeys[i] != 0) { m[s.ItemKeys[i]] = ItemSlotVk[i]; item.Add(s.ItemKeys[i]); }
                foreach (KeyMapEntry e in s.Maps)
                    if (e.Src != 0 && e.Dst != 0 && e.Src != e.Dst)
                    {
                        m[e.Src] = e.Dst;
                        if (Array.IndexOf(ItemSlotVk, e.Dst) >= 0) item.Add(e.Src);
                        else item.Remove(e.Src);
                    }
                if (Cfg.ChatEnabled && Cfg.Chats != null)
                    foreach (ChatItem c in Cfg.Chats)
                        if (c.Key != 0 && !string.IsNullOrEmpty(c.Text))
                            cm[ChatKey(c.Mods, c.Key)] = c.Text;
            }
            _map = m;
            _itemSrc = item;
            _chatMap = cm;
        }

        // 查询当前生效的映射（诊断/测试用）
        public static bool TryGetMapping(int src, out int dst)
        {
            return _map.TryGetValue(src, out dst);
        }

        public static bool IsItemSlotSource(int src) { return _itemSrc.Contains(src); }

        public static bool TryGetChat(int mods, int vk, out string text)
        {
            return _chatMap.TryGetValue(ChatKey(mods, vk), out text);
        }

        // 判断一个字符会走"真实按键"路径还是 Unicode 注入路径（诊断/测试用）
        public static bool CanTypeAsRealKey(char ch)
        {
            if (ch >= 128) return false;
            short vs = Native.VkKeyScan(ch);
            if (vs == -1) return false;
            int vk = vs & 0xFF;
            int state = (vs >> 8) & 0xFF;
            return vk != 0 && (state & 2) == 0 && (state & 4) == 0;
        }

        // ---- 前台判定：按窗口句柄记忆，命中时只是一次指针比较 ----
        static IntPtr _fgMemoHwnd = new IntPtr(-1);
        static bool _fgMemoResult;

        public static bool War3Foreground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == _fgMemoHwnd) return _fgMemoResult;
            _fgMemoHwnd = fg;
            _fgMemoResult = War3Ctl.IsWar3WindowFast(fg);
            return _fgMemoResult;
        }

        // 前台窗口没变但归属可能变了(如魔兽刚启动)，由UI定时器调用使记忆失效
        public static void InvalidateForegroundMemo()
        {
            _fgMemoHwnd = new IntPtr(-1);
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
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5);
        }

        // 是否正按住"临时停用改键"键
        public static bool SuspendHeld
        {
            get
            {
                if (Cfg == null) return false;
                int k = Cfg.SuspendKey;
                if (k <= 0 || k >= 256) return false;
                return _phyDown[k];
            }
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

        public static void TickBars()
        {
            if (Cfg == null) return;
            bool fg = War3Foreground();
            bool want = Cfg.ShowHpBars && !_sendingChat && fg
                        && !PhysAlt && !PhysCtrl && !PhysShift
                        && (uint)Environment.TickCount >= _altResumeAt;
            if (want) PressSynthAlt();
            else if (!Cfg.ShowHpBars || !fg) ReleaseSynthAlt();
        }

        // ================= 键盘钩子 =================
        static IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
            _kbTicks++;

            if (Native.ReadExtraInfo(lParam, Native.KbdExtraInfoOffset) == Native.InjectMagic)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
            bool up = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
            int vk = Native.ReadInt(lParam, Native.KbdVkCodeOffset);

            // 物理按键状态始终跟踪(即使不在游戏中)，供修饰键判断使用
            bool repeat = false;
            if (vk >= 0 && vk < 256)
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

            // 按住"临时停用"键时，所有改键和喊话热键原样放行。
            // 用途：在商店里让 S 还是 S（改键会把 S 变成别的键，导致买不了树枝）。
            if (SuspendHeld)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            if (down && _synthAlt) SuspendBars();
            else if (down) _altResumeAt = (uint)Environment.TickCount + 350;

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
                    EmitMapped(vk, dst, down, repeat);
                    return new IntPtr(1);
                }
            }

            if (down && Diag.Enabled) Diag.Log(0, vk, 0, true, true, false, 0);
            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // 发出改键结果。物品栏键可选先按F1选中英雄，这样在商店/小兵被选中时
        // 也不会误买东西 —— 按下去永远作用在自己英雄身上。
        static void EmitMapped(int src, int dst, bool down, bool repeat)
        {
            if (down && !repeat && Cfg.ItemKeySelectHeroFirst && _itemSrc.Contains(src))
            {
                SendVk(VK_F1, true);
                SendVk(VK_F1, false);
            }
            uint r = SendVk(dst, down);
            Diag.Log(0, src, dst, down, true, true, r);
        }

        // ================= 鼠标钩子 =================
        static IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            _msTicks++;

            // 热路径：鼠标移动在读取任何字段之前就返回
            if (msg == Native.WM_MOUSEMOVE)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            if (Native.ReadExtraInfo(lParam, Native.MouseExtraInfoOffset) == Native.InjectMagic)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            if (_sendingChat || Cfg == null || !War3Foreground() || SuspendHeld)
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
                        int data = Native.ReadInt(lParam, Native.MouseDataOffset);
                        int which = (data >> 16) & 0xFFFF;
                        src = (which == 1) ? Native.VK_XBUTTON1 : Native.VK_XBUTTON2;
                        down = (msg == Native.WM_XBUTTONDOWN);
                        break;
                    }
                case Native.WM_MOUSEWHEEL:
                    {
                        int data = Native.ReadInt(lParam, Native.MouseDataOffset);
                        short delta = (short)((data >> 16) & 0xFFFF);
                        if (delta == 0) break;
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
                    if (wheel)
                    {
                        uint r = SendVk(dst, true);
                        SendVk(dst, false);
                        Diag.Log(1, src, dst, true, true, true, r);
                    }
                    else
                    {
                        uint r = SendVk(dst, down);
                        Diag.Log(1, src, dst, down, true, true, r);
                    }
                    return new IntPtr(1);
                }
                if (Diag.Enabled) Diag.Log(1, src, 0, down, true, false, 0);
            }
            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        // ================= 输入合成 =================
        // 返回 SendInput 的结果：0 表示注入被系统拒绝(通常是权限/UIPI问题)
        public static uint SendVk(int vk, bool down)
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
                ushort scan = (ushort)Native.MapVirtualKey((uint)vk, 0);
                bool scanOnly = (Cfg != null && Cfg.InjectMode == 1 && scan != 0);
                inp[0].type = 1;
                // 方式1(默认): 带虚拟键+扫描码。方式2: 只发扫描码 —— 有些用
                // DirectInput/RawInput 读键盘的老游戏只认扫描码。
                inp[0].u.ki.wVk = scanOnly ? (ushort)0 : (ushort)vk;
                inp[0].u.ki.wScan = scan;
                inp[0].u.ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;
                if (scanOnly) inp[0].u.ki.dwFlags |= Native.KEYEVENTF_SCANCODE;
                if (IsExtended(vk)) inp[0].u.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
                inp[0].u.ki.dwExtraInfo = Native.InjectMagic;
            }
            return Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
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

        // 逐字输入。ASCII 走真实的虚拟键+扫描码(和真人敲键盘一样)，
        // 因为魔兽这类游戏未必接受 KEYEVENTF_UNICODE 合成的 VK_PACKET；
        // 非ASCII(中文)只能退回 Unicode 方式。
        static void TypeChar(char ch)
        {
            if (ch < 128)
            {
                short vs = Native.VkKeyScan(ch);
                if (vs != -1)
                {
                    int vk = vs & 0xFF;
                    int state = (vs >> 8) & 0xFF;
                    bool shift = (state & 1) != 0;
                    bool ctrl = (state & 2) != 0;
                    bool alt = (state & 4) != 0;
                    if (vk != 0 && !ctrl && !alt)
                    {
                        if (shift) SendVk(0xA0, true);
                        SendVk(vk, true);
                        SendVk(vk, false);
                        if (shift) SendVk(0xA0, false);
                        return;
                    }
                }
            }
            SendUnicodeChar(ch);
        }

        static readonly int[] AllModVks = new int[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x10, 0x11, 0x12 };

        // 强制释放所有修饰键。必须做：喊话热键是 Alt+数字，
        // 如果 Alt 还按着就发回车，魔兽会把 Alt+Enter 当成切换全屏/窗口。
        static void ForceReleaseModifiers()
        {
            foreach (int vk in AllModVks)
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                    SendVk(vk, false);
        }

        // 游戏内发送聊天: 回车 -> 逐字输入 -> 回车
        public static void SendChatAsync(string text)
        {
            if (_sendingChat) return;
            _sendingChat = true;
            ReleaseSynthAlt();
            _altResumeAt = (uint)Environment.TickCount + 3000;

            int enterDelay = Cfg != null ? Cfg.ChatEnterDelay : 150;
            int charDelay = Cfg != null ? Cfg.ChatCharDelay : 12;

            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    // 等玩家松开触发键，最多等 600ms
                    for (int i = 0; i < 24 && PhysMods != 0; i++) Thread.Sleep(25);
                    ForceReleaseModifiers();
                    Thread.Sleep(30);

                    TapVk(0x0D);                       // 打开聊天栏
                    Thread.Sleep(enterDelay);
                    foreach (char ch in text)
                    {
                        TypeChar(ch);
                        Thread.Sleep(charDelay);
                    }
                    Thread.Sleep(enterDelay / 2);
                    TapVk(0x0D);                       // 发送
                    Thread.Sleep(60);
                }
                catch { }
                finally { _sendingChat = false; }
            });
        }
    }
}
