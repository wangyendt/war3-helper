using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace War3Helper
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
        // 魔兽默认的物品栏快捷键(界面顺序: 物品1~3=左列, 4~6=右列)。
        // 实际用哪几个键由 Scheme.ItemSlotDst 决定，这里只是默认值。
        public static readonly int[] ItemSlotVk = new int[] { 0x67, 0x64, 0x61, 0x68, 0x65, 0x62 };

        public static bool IsNumpadVk(int vk) { return vk >= 0x60 && vk <= 0x69; }

        // ---- 滚轮节流 ----
        // 不少鼠标一格物理刻度会连着发好几条 WM_MOUSEWHEEL(实测同方向间隔只有 78~94ms)，
        // 结果一次滚动触发了两次改键。按方向各自记时间，间隔不够就丢弃。
        // 上下分开计时，这样"上滚紧接着下滚"这种有意的方向切换不会被误伤。
        static uint _lastWheelUp = uint.MaxValue - 100000;
        static uint _lastWheelDown = uint.MaxValue - 100000;

        // 返回 true 表示这一次滚轮可以放行；false 表示间隔太近，应当丢弃
        public static bool TryConsumeWheel(int src)
        {
            int min = (Cfg != null) ? Cfg.WheelMinIntervalMs : 0;
            if (min <= 0) return true;
            uint now = (uint)Environment.TickCount;
            if (src == Native.VK_WHEELUP)
            {
                if (now - _lastWheelUp < (uint)min) return false;
                _lastWheelUp = now;
            }
            else
            {
                if (now - _lastWheelDown < (uint)min) return false;
                _lastWheelDown = now;
            }
            return true;
        }

        public static void ResetWheelThrottle()
        {
            _lastWheelUp = uint.MaxValue - 100000;
            _lastWheelDown = uint.MaxValue - 100000;
        }

        public const int VK_ALT = 0x12;
        public const int VK_F1 = 0x70;

        public static AppConfig Cfg;

        public static event Action HotToggleRemap;      // Ctrl+F2
        public static event Action HotNextScheme;       // Ctrl+F3
        public static event Action HotToggleLock;       // Ctrl+F4
        // Ctrl+F5(血条常显)已取消：血条常显改用魔兽自己的设置，只能在游戏关闭时改，
        // 局内热键没有意义了。
        public static event Action HotToggleApm;        // Ctrl+F7
        public static event Action HotTimerReset;       // Ctrl+F8

        static IntPtr _kbHook = IntPtr.Zero;
        static IntPtr _msHook = IntPtr.Zero;
        static Native.HookProc _kbProc;
        static Native.HookProc _msProc;

        static Dictionary<int, int> _map = new Dictionary<int, int>();
        static HashSet<int> _itemSrc = new HashSet<int>();      // 映射到物品栏的源键
        static HashSet<int> _shopKeep = new HashSet<int>();     // 商店模式下仍生效的源键
        static Dictionary<int, string> _chatMap = new Dictionary<int, string>();
        static readonly HashSet<int> _swallowedUp = new HashSet<int>();
        static readonly bool[] _phyDown = new bool[256];
        static volatile bool _sendingChat = false;

        // 血条常显(合成Alt)状态

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
            HashSet<int> keep = new HashSet<int>();
            Dictionary<int, string> cm = new Dictionary<int, string>();
            if (Cfg != null)
            {
                Scheme s = Cfg.ActiveScheme;
                int[] slotDst = s.ItemSlotDst;      // 方案自己的物品栏目标键，不再写死小键盘
                for (int i = 0; i < 6; i++)
                    if (s.ItemKeys[i] != 0)
                    {
                        m[s.ItemKeys[i]] = slotDst[i];
                        item.Add(s.ItemKeys[i]);
                        if (s.ItemKeysKeepInShop) keep.Add(s.ItemKeys[i]);
                    }
                // 内置改键：S(停止) 改为 H(原地不动)。DOTA里常用，H能打断攻击后摇而S不能。
                // 玩家在自定义列表里显式改了S的话以他的为准，所以放在自定义之前。
                if (Cfg.BuiltinStopAsHold) m[(int)'S'] = (int)'H';

                foreach (KeyMapEntry e in s.Maps)
                    if (e.Src != 0 && e.Dst != 0 && e.Src != e.Dst)
                    {
                        m[e.Src] = e.Dst;
                        if (Array.IndexOf(slotDst, e.Dst) >= 0) item.Add(e.Src);
                        else item.Remove(e.Src);
                        if (e.KeepInShop) keep.Add(e.Src); else keep.Remove(e.Src);
                    }
                if (Cfg.ChatEnabled && Cfg.Chats != null)
                    foreach (ChatItem c in Cfg.Chats)
                        if (c.Key != 0 && !string.IsNullOrEmpty(c.Text))
                            cm[ChatKey(c.Mods, c.Key)] = c.Text;
            }
            _map = m;
            _itemSrc = item;
            _shopKeep = keep;
            _chatMap = cm;

            // 源键和目标键都要看：NumLock 关闭时小键盘键在系统层面是方向键/Home/End，
            // 所以既发不出去(当目标)，也认不出来(当源)。
            bool numpad = false;
            foreach (KeyValuePair<int, int> kv in m)
                if (IsNumpadVk(kv.Key) || IsNumpadVk(kv.Value)) { numpad = true; break; }
            if (!numpad && Cfg != null && Cfg.Chats != null)
                foreach (ChatItem c in Cfg.Chats)
                    if (c.Key != 0 && IsNumpadVk(c.Key)) { numpad = true; break; }
            _mapsToNumpad = numpad;
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

        // ---- 打字状态 ----
        // 按回车打开聊天栏后，空格、数字这些键都是在输入文字，这时候还改键就会把
        // 打的字变成别的键(比如空格变成小键盘2)。游戏不会告诉外部"聊天栏开着没"，
        // 但这个状态完全由玩家自己的按键决定：回车开、回车发出去、Esc 取消，
        // 所以不用猜游戏状态也能准确跟踪。
        const int VK_RETURN = 0x0D;
        const int VK_ESCAPE = 0x1B;
        const int TypingIdleResetMs = 30000;   // 兜底：长时间没动静就复位，免得卡死在打字态

        static volatile bool _typing;
        static uint _typingLastKey;

        public static bool Typing { get { return _typing; } }

        public static event Action TypingChanged;

        static void SetTyping(bool on)
        {
            if (_typing == on) return;
            _typing = on;
            _typingLastKey = (uint)Environment.TickCount;
            Diag.Note(on ? "检测到打开聊天栏，改键已暂停" : "聊天栏已关闭，改键恢复");
            Action h = TypingChanged;
            if (h != null) { try { h(); } catch { } }
        }

        public static void ResetTyping() { SetTyping(false); }

        // 仅供测试
        public static void FeedTypingKeyForTest(int vk) { TrackTyping(vk); }

        static void TrackTyping(int vk)
        {
            uint now = (uint)Environment.TickCount;
            if (vk == VK_RETURN) { SetTyping(!_typing); _typingLastKey = now; return; }
            if (vk == VK_ESCAPE) { if (_typing) SetTyping(false); return; }
            if (_typing) _typingLastKey = now;
        }

        static void TickTypingTimeout()
        {
            if (!_typing) return;
            if ((uint)Environment.TickCount - _typingLastKey > TypingIdleResetMs) SetTyping(false);
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

        // ---- 商店模式 ----
        // 自动判断"当前选中的是不是商店"必须读游戏内存，本工具不做。
        // 改成由玩家自己的操作来切换：按下"进入键"(比如滚轮)后挂起全部改键，
        // 按下"恢复键"(默认F1，也就是重新选中英雄)后恢复。
        static volatile bool _shopMode;

        public static bool ShopMode { get { return _shopMode; } }

        public static event Action ShopModeChanged;

        static void SetShopMode(bool on)
        {
            if (_shopMode == on) return;
            _shopMode = on;
            Diag.Note(on ? "进入商店模式(改键已挂起)" : "退出商店模式(改键已恢复)");
            Action h = ShopModeChanged;
            if (h != null) { try { h(); } catch { } }
        }

        public static void ExitShopMode() { SetShopMode(false); }

        // 仅供测试使用
        public static void EnterShopModeForTest() { SetShopMode(true); }

        // 商店模式下，这个源键的改键是否仍然生效
        static bool ShopExempt(int src)
        {
            if (Cfg == null) return false;
            // 触发键必须永远生效！否则用滚轮进入商店模式后，滚轮自己的改键也被挂起，
            // 第二次滚轮就直接落到游戏里变成视角缩放了。
            if (Cfg.ShopEnterOnWheel && (src == Native.VK_WHEELUP || src == Native.VK_WHEELDOWN)) return true;
            if (Cfg.ShopEnterKey != 0 && src == Cfg.ShopEnterKey) return true;
            if (Cfg.ShopExitKey != 0 && src == Cfg.ShopExitKey) return true;
            return _shopKeep.Contains(src);   // 用户逐条勾了"商店模式下仍生效"
        }

        // 这个源键的改键当前是否被挂起
        static bool SuspendedFor(int src)
        {
            if (SuspendHeld) return true;                 // 按住停用键 = 全部挂起
            return _shopMode && !ShopExempt(src);
        }

        public static bool IsSuspendedFor(int src) { return SuspendedFor(src); }

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

        // 血条常显已改用魔兽自己的设置（见 War3Prefs），不再合成 Alt。
        // 按住 Alt 的副作用太大：F4 变 Alt+F4 直接关游戏、Alt+Enter 切全屏、
        // Alt+点击 在DOTA里是发信号、改键注入的键也全带上 Alt。

        // ---- NumLock ----
        // 魔兽的物品栏快捷键是小键盘 7/8/4/5/1/2。NumLock 关闭时，这些键在系统层面
        // 就是 Home/↑/←/Clear/End/↓ —— 游戏收到的是方向键，物品栏改键自然全部落空。
        // 所以只要有改键指向小键盘，就得保证 NumLock 是开的。
        public static bool NumLockOn
        {
            get { return (Native.GetKeyState(0x90) & 1) != 0; }
        }

        static bool _mapsToNumpad;

        public static bool MapsToNumpad { get { return _mapsToNumpad; } }

        public static bool NumLockProblem
        {
            get { return _mapsToNumpad && !NumLockOn; }
        }

        public static void EnsureNumLock()
        {
            if (!NumLockProblem) return;
            SendVk(0x90, true);
            SendVk(0x90, false);
            Diag.Note("NumLock 原为关闭，已自动打开(物品栏小键盘改键需要)");
        }

        public static void TickNumLock()
        {
            if (Cfg == null || !Cfg.AutoNumLock) return;
            if (!War3Foreground()) return;
            EnsureNumLock();
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
                if (_typing) SetTyping(false);
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
            }

            // 打字期间(聊天栏开着)一律原样放行，否则空格会被改成小键盘键，字都打不出来
            if (Cfg.SuspendWhileTyping)
            {
                if (down) TrackTyping(vk);
                TickTypingTimeout();
                if (_typing)
                    return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
            }
            else if (_typing) SetTyping(false);

            // 商店模式的进入/退出键（在改键判断之前处理，这两个键本身照常放行）
            if (down && Cfg.ShopModeEnabled)
            {
                bool isEnter = (Cfg.ShopEnterKey != 0 && vk == Cfg.ShopEnterKey);
                bool isExit = (Cfg.ShopExitKey != 0 && vk == Cfg.ShopExitKey);
                // 两个键设成同一个时当成开关用，否则退出优先会导致永远进不去
                if (isEnter && isExit) SetShopMode(!_shopMode);
                else if (isExit) SetShopMode(false);
                else if (isEnter) SetShopMode(true);
            }

            // 按住"临时停用"键时连喊话热键一起放行
            if (SuspendHeld)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            if (down && !repeat) CountApm();

            int mods = PhysMods;

            // 控制热键 Ctrl+F2/F3/F4/F7/F8
            if (down && PhysCtrl && !PhysAlt && !PhysShift)
            {
                Action fired = null;
                if (vk == 0x71) fired = HotToggleRemap;
                else if (vk == 0x72) fired = HotNextScheme;
                else if (vk == 0x73) fired = HotToggleLock;
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
                    if (SuspendedFor(vk))       // 商店模式下这条被挂起 -> 原样放行
                        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
                    EmitMapped(vk, dst, down, repeat);
                    return new IntPtr(1);
                }
            }

            if (down && Diag.Enabled) Diag.Log(0, vk, 0, true, true, false, 0);
            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // 连按同一个物品键时，第二次不能再插"选中英雄"。
        // 原因：F1 是一条选择指令，会取消"指定目标"状态。魔兽里连按两次物品键
        // 是对自己施法，中间插一次 F1 就把目标状态清掉了，双击永远生效不了。
        // 而且第一次已经把英雄选中了，紧接着的第二次本来也不需要再选。
        const int HeroSelectSkipWindowMs = 600;
        static readonly Dictionary<int, uint> _lastItemPress = new Dictionary<int, uint>();

        // 返回 true 表示这次物品键按下需要先补一个"选中英雄"
        public static bool ConsumeItemPress(int src)
        {
            uint now = (uint)Environment.TickCount;
            uint last;
            bool recent = _lastItemPress.TryGetValue(src, out last)
                          && (now - last) < HeroSelectSkipWindowMs;
            _lastItemPress[src] = now;
            return !recent;
        }

        public static void ResetItemPressTimes() { _lastItemPress.Clear(); }

        // 注入"选中英雄"时必须先把玩家按住的修饰键临时松开。
        // 否则按 Shift+物品键 时，游戏收到的是 Shift+F1 —— 魔兽里那是"切换"选中状态，
        // 英雄本来选着就被取消了。发完再按回去，后面的物品键照样带 Shift，
        // Shift+物品(排队使用)不受影响。
        static readonly int[] SuppressibleMods = new int[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };
        static readonly int[] _suppressBuf = new int[6];

        static int SuppressHeldModifiers()
        {
            int n = 0;
            for (int i = 0; i < SuppressibleMods.Length; i++)
            {
                int vk = SuppressibleMods[i];
                if (IsPhysicallyHeld(vk))
                {
                    _suppressBuf[n++] = vk;
                    SendVk(vk, false);
                }
            }
            return n;
        }

        static void RestoreModifiers(int n)
        {
            for (int i = n - 1; i >= 0; i--) SendVk(_suppressBuf[i], true);
        }

        // 发出改键结果。物品栏键可选先选中英雄，这样在商店/小兵被选中时
        // 也不会误买东西 —— 按下去永远作用在自己英雄身上。
        static void EmitMapped(int src, int dst, bool down, bool repeat)
        {
            if (down && !repeat && Cfg.ItemKeySelectHeroFirst && _itemSrc.Contains(src)
                && ConsumeItemPress(src))
            {
                int hero = Cfg.HeroSelectKey != 0 ? Cfg.HeroSelectKey : VK_F1;
                int suppressed = SuppressHeldModifiers();
                SendVk(hero, true);
                SendVk(hero, false);
                RestoreModifiers(suppressed);
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

            if (_sendingChat || Cfg == null || !War3Foreground() || _typing)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            // 滚轮进入商店模式。滚轮自己的改键属于触发键，ShopExempt 会让它一直生效。
            bool wheelEvent = (msg == Native.WM_MOUSEWHEEL);
            bool enterShopAfter = (wheelEvent && Cfg.ShopModeEnabled && Cfg.ShopEnterOnWheel);
            // 屏蔽滚轮视角缩放：无论有没有改键，都不让原始滚轮事件落到游戏里
            bool eatWheel = (wheelEvent && Cfg.BlockWheelZoom);

            if (SuspendHeld)
            {
                if (enterShopAfter) SetShopMode(true);
                if (eatWheel) return new IntPtr(1);
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
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
                if (_map.TryGetValue(src, out dst)
                    && (Cfg.ApplyToCombo || PhysMods == 0)
                    && !SuspendedFor(src))
                {
                    if (wheel)
                    {
                        if (!TryConsumeWheel(src))
                        {
                            // 间隔太近，判定为同一格刻度的重复事件：不发按键，
                            // 但仍然吞掉，免得这一条漏到游戏里变成视角缩放
                            if (Diag.Enabled) Diag.Note("滚轮间隔过短，已忽略重复的一次");
                            if (enterShopAfter) SetShopMode(true);
                            return new IntPtr(1);
                        }
                        uint r = SendVk(dst, true);
                        SendVk(dst, false);
                        Diag.Log(1, src, dst, true, true, true, r);
                    }
                    else
                    {
                        uint r = SendVk(dst, down);
                        Diag.Log(1, src, dst, down, true, true, r);
                    }
                    if (enterShopAfter) SetShopMode(true);
                    return new IntPtr(1);
                }
                if (Diag.Enabled) Diag.Log(1, src, 0, down, true, false, 0);
            }
            if (enterShopAfter) SetShopMode(true);
            if (eatWheel) return new IntPtr(1);
            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        // ================= 输入合成 =================
        // 测试接缝：录制模式下只记录要发的按键，不真的注入。
        // 用来确定性地验证"发出去的按键顺序"，这类 bug(比如 Shift 没松开导致 Shift+F1)
        // 光靠肉眼看代码很难发现。
        static List<int> _recorded;

        // 物理按键是否按住。测试里可替换，免得依赖真实键盘状态。
        public static Func<int, bool> IsPhysicallyHeld =
            delegate(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; };

        public static void BeginRecord() { _recorded = new List<int>(); }

        // 返回录到的序列：正数=按下，负数=抬起
        public static int[] EndRecord()
        {
            int[] r = (_recorded == null) ? new int[0] : _recorded.ToArray();
            _recorded = null;
            return r;
        }

        public static void EmitMappedForTest(int src, int dst, bool down, bool repeat)
        {
            EmitMapped(src, dst, down, repeat);
        }

        // 返回 SendInput 的结果：0 表示注入被系统拒绝(通常是权限/UIPI问题)
        public static uint SendVk(int vk, bool down)
        {
            if (_recorded != null) { _recorded.Add(down ? vk : -vk); return 1; }
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
