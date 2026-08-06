using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace War3Helper
{
    public class ReplayInfo
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Time;
        public int DurationMs;
        public bool IsAutoSave;

        public string DurationText
        {
            get
            {
                if (DurationMs <= 0) return "-";
                int s = DurationMs / 1000;
                return string.Format("{0}:{1:D2}:{2:D2}", s / 3600, (s / 60) % 60, s % 60);
            }
        }

        public string SizeText { get { return string.Format("{0:n0} KB", Size / 1024); } }
    }

    // 魔兽窗口控制：查找/窗口化启动/无边框/锁鼠标/老板键隐藏
    public static class War3Ctl
    {
        // 完整判定：会查进程名。**不要在低层钩子回调里调用**（Process.GetProcessById 很贵，
        // 会让钩子超过 LowLevelHooksTimeout 而被系统摘掉）。
        public static bool IsWar3Window(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(hWnd, sb, 64);
            string cls = sb.ToString();
            if (cls == "Warcraft III") return true;
            uint pid;
            Native.GetWindowThreadProcessId(hWnd, out pid);
            try
            {
                Process p = Process.GetProcessById((int)pid);
                string n = p.ProcessName.ToLowerInvariant();
                return n == "war3" || n == "warcraft iii" || n == "frozen throne" || n == "16_war3";
            }
            catch { return false; }
        }

        // 钩子专用的快速判定：只做一次 GetClassName + 一个数组查找，不碰进程信息。
        // 进程名匹配的窗口由 RefreshWar3WindowCache() 在UI线程上预先算好。
        static IntPtr[] _war3HwndCache = new IntPtr[0];

        public static bool IsWar3WindowFast(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(32);
            Native.GetClassName(hWnd, sb, 32);
            if (sb.ToString() == "Warcraft III") return true;
            IntPtr[] cache = _war3HwndCache;
            for (int i = 0; i < cache.Length; i++)
                if (cache[i] == hWnd) return true;
            return false;
        }

        // 缓存刷新必须放在后台线程！
        // 低层钩子的回调跑在安装它的线程(=UI线程)上，而刷新会调 Process.GetProcesses()
        // 枚举系统全部进程(几十~几百毫秒)。放在UI线程上会把钩子回调堵在后面，
        // 一旦超过 LowLevelHooksTimeout(默认300ms)，Windows 就会把钩子静默摘掉。
        static System.Threading.Timer _cacheTimer;

        public static void StartWindowCacheRefresher()
        {
            StopWindowCacheRefresher();
            _cacheTimer = new System.Threading.Timer(delegate(object o)
            {
                try
                {
                    List<IntPtr> l = FindWar3Windows();
                    _war3HwndCache = l.ToArray();
                    _cachedMainWindow = l.Count > 0 ? PickVisible(l) : IntPtr.Zero;
                }
                catch { }
            }, null, 0, 1000);
        }

        public static void StopWindowCacheRefresher()
        {
            if (_cacheTimer != null) { _cacheTimer.Dispose(); _cacheTimer = null; }
        }

        static IntPtr PickVisible(List<IntPtr> l)
        {
            foreach (IntPtr h in l)
                if (Native.IsWindowVisible(h)) return h;
            return l.Count > 0 ? l[0] : IntPtr.Zero;
        }

        static IntPtr _cachedMainWindow = IntPtr.Zero;

        // 给UI线程/钩子用的廉价版本，读的是后台线程刷新好的结果
        public static IntPtr CachedMainWindow()
        {
            IntPtr h = _cachedMainWindow;
            if (h != IntPtr.Zero && !Native.IsWindow(h)) return IntPtr.Zero;
            return h;
        }

        static readonly string[] War3ProcNames = new string[] { "war3", "warcraft iii", "frozen throne", "16_war3" };

        static HashSet<uint> War3Pids()
        {
            HashSet<uint> pids = new HashSet<uint>();
            foreach (Process p in Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName.ToLowerInvariant(); }
                catch { continue; }
                foreach (string w in War3ProcNames)
                    if (n == w) { pids.Add((uint)p.Id); break; }
            }
            return pids;
        }

        public static List<IntPtr> FindWar3Windows()
        {
            List<IntPtr> list = new List<IntPtr>();
            HashSet<uint> pids = War3Pids();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                StringBuilder sb = new StringBuilder(64);
                Native.GetClassName(h, sb, 64);
                string cls = sb.ToString();
                if (cls == "Warcraft III") { list.Add(h); return true; }
                if (cls.IndexOf("IME", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid)) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static IntPtr MainWindow()
        {
            List<IntPtr> l = FindWar3Windows();
            foreach (IntPtr h in l)
                if (Native.IsWindowVisible(h)) return h;
            return l.Count > 0 ? l[0] : IntPtr.Zero;
        }

        // ---- 注册表设置 ----
        const string RegVideo = @"Software\Blizzard Entertainment\Warcraft III\Video";
        const string RegGameplay = @"Software\Blizzard Entertainment\Warcraft III\Gameplay";

        public static void SetRegistryResolution(int w, int h)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegVideo))
                {
                    k.SetValue("reswidth", w, RegistryValueKind.DWord);
                    k.SetValue("resheight", h, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // 游戏自带的"始终显示生命条"选项
        public static void SetAlwaysHealthBars(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegGameplay))
                    k.SetValue("healthbars", on ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool GetAlwaysHealthBars()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegGameplay))
                {
                    if (k == null) return false;
                    object v = k.GetValue("healthbars");
                    return v != null && Convert.ToInt32(v) != 0;
                }
            }
            catch { return false; }
        }

        public static string Exe(string war3Dir)
        {
            string exe = Path.Combine(war3Dir, "War3.exe");
            if (File.Exists(exe)) return exe;
            exe = Path.Combine(war3Dir, "Frozen Throne.exe");
            if (File.Exists(exe)) return exe;
            return null;
        }

        // 启动魔兽。borderless模式下会在窗口出现后自动去边框铺满。
        public static string Launch(AppConfig cfg, LaunchMode mode)
        {
            string exe = Exe(cfg.War3Path);
            if (exe == null) return "找不到 War3.exe，请检查魔兽路径";

            bool windowed = (mode != LaunchMode.ExclusiveFullscreen);
            if (mode == LaunchMode.BorderlessFullscreen)
            {
                System.Drawing.Rectangle b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                SetRegistryResolution(b.Width, b.Height);
            }
            else if (mode == LaunchMode.Windowed)
            {
                SetRegistryResolution(cfg.WinW, cfg.WinH);
            }

            string args = "";
            if (windowed) args += " -window";
            if (cfg.UseOpenGL) args += " -opengl";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args.Trim());
                psi.WorkingDirectory = cfg.War3Path;
                Process.Start(psi);
            }
            catch (Exception ex) { return "启动失败: " + ex.Message; }

            if (mode == LaunchMode.BorderlessFullscreen) BeginAutoBorderless();
            return null;
        }

        // 等游戏窗口出现后自动应用伪全屏
        static void BeginAutoBorderless()
        {
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                for (int i = 0; i < 240; i++)      // 最长等 2 分钟
                {
                    Thread.Sleep(500);
                    IntPtr h = MainWindow();
                    if (h == IntPtr.Zero || !Native.IsWindowVisible(h)) continue;
                    Thread.Sleep(1500);            // 等窗口尺寸稳定
                    try { MakeBorderless(); }
                    catch { }
                    return;
                }
            });
        }

        // 伪全屏：去边框并铺满当前显示器
        public static bool MakeBorderless()
        {
            IntPtr h = MainWindow();
            if (h == IntPtr.Zero) return false;
            uint style = Native.GetWindowLong(h, Native.GWL_STYLE);
            style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MINIMIZEBOX |
                       Native.WS_MAXIMIZEBOX | Native.WS_SYSMENU | Native.WS_BORDER | Native.WS_DLGFRAME);
            Native.SetWindowLong(h, Native.GWL_STYLE, style);
            IntPtr mon = Native.MonitorFromWindow(h, 2 /*MONITOR_DEFAULTTONEAREST*/);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            Native.SetWindowPos(h, IntPtr.Zero,
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
            return true;
        }

        // 恢复标准窗口边框
        public static bool RestoreBorder(int w, int h)
        {
            IntPtr hw = MainWindow();
            if (hw == IntPtr.Zero) return false;
            uint style = Native.GetWindowLong(hw, Native.GWL_STYLE);
            style |= (Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MINIMIZEBOX |
                      Native.WS_MAXIMIZEBOX | Native.WS_SYSMENU);
            Native.SetWindowLong(hw, Native.GWL_STYLE, style);
            if (w <= 0) w = 1280;
            if (h <= 0) h = 720;
            Native.SetWindowPos(hw, IntPtr.Zero, 60, 40, w, h,
                Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
            return true;
        }

        static bool _clipped = false;

        // 每次tick维持鼠标锁定（仅当魔兽前台）
        public static void MaintainClip(bool wantLock)
        {
            IntPtr fg = Native.GetForegroundWindow();
            bool active = wantLock && IsWar3Window(fg);
            if (active)
            {
                Native.RECT rc;
                Native.GetClientRect(fg, out rc);
                Native.POINT tl = new Native.POINT(); tl.x = rc.Left; tl.y = rc.Top;
                Native.POINT br = new Native.POINT(); br.x = rc.Right; br.y = rc.Bottom;
                Native.ClientToScreen(fg, ref tl);
                Native.ClientToScreen(fg, ref br);
                Native.RECT clip;
                clip.Left = tl.x; clip.Top = tl.y; clip.Right = br.x; clip.Bottom = br.y;
                Native.ClipCursor(ref clip);
                _clipped = true;
            }
            else if (_clipped)
            {
                Native.ClipCursor(IntPtr.Zero);
                _clipped = false;
            }
        }

        public static void ReleaseClip()
        {
            if (_clipped) { Native.ClipCursor(IntPtr.Zero); _clipped = false; }
        }

        static bool _bossHidden = false;
        static readonly List<IntPtr> _hiddenWindows = new List<IntPtr>();

        // 老板键：隐藏/恢复魔兽窗口
        public static bool ToggleBoss()
        {
            if (!_bossHidden)
            {
                List<IntPtr> ws = FindWar3Windows();
                if (ws.Count == 0) return false;
                _hiddenWindows.Clear();
                foreach (IntPtr h in ws)
                {
                    if (Native.IsWindowVisible(h))
                    {
                        _hiddenWindows.Add(h);
                        Native.ShowWindow(h, Native.SW_HIDE);
                    }
                }
                ReleaseClip();
                Engine.ReleaseSynthAlt();
                _bossHidden = _hiddenWindows.Count > 0;
                return _bossHidden;
            }
            else
            {
                foreach (IntPtr h in _hiddenWindows)
                {
                    if (Native.IsWindow(h))
                    {
                        Native.ShowWindow(h, Native.SW_SHOW);
                        Native.SetForegroundWindow(h);
                    }
                }
                _hiddenWindows.Clear();
                _bossHidden = false;
                return true;
            }
        }

        public static bool BossHidden { get { return _bossHidden; } }

        // ---- 录像 ----
        public static string ReplayDir(string war3Dir) { return Path.Combine(war3Dir, "Replay"); }
        public static string AutoSaveDir(string war3Dir) { return Path.Combine(ReplayDir(war3Dir), "AutoSave"); }

        public static List<ReplayInfo> ListReplays(string war3Dir)
        {
            List<ReplayInfo> list = new List<ReplayInfo>();
            AddReplays(list, ReplayDir(war3Dir), false);
            AddReplays(list, AutoSaveDir(war3Dir), true);
            list.Sort(delegate(ReplayInfo a, ReplayInfo b) { return b.Time.CompareTo(a.Time); });
            return list;
        }

        static void AddReplays(List<ReplayInfo> list, string dir, bool isAuto)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.w3g"))
            {
                try
                {
                    FileInfo fi = new FileInfo(f);
                    ReplayInfo r = new ReplayInfo();
                    r.Path = f;
                    r.Name = fi.Name;
                    r.Size = fi.Length;
                    r.Time = fi.LastWriteTime;
                    r.DurationMs = ReplayWatcher.ParseDuration(f);
                    r.IsAutoSave = isAuto;
                    list.Add(r);
                }
                catch { }
            }
        }
    }

    // 自动保存录像：监视 Replay\LastReplay.w3g
    public class ReplayWatcher : IDisposable
    {
        FileSystemWatcher _fsw;
        System.Windows.Forms.Timer _debounce;
        string _war3Dir;
        public event Action<string> Saved;   // 参数=提示文本

        public bool IgnoreShort = true;

        public void Start(string war3Dir)
        {
            Stop();
            _war3Dir = war3Dir;
            string dir = War3Ctl.ReplayDir(war3Dir);
            if (!Directory.Exists(dir)) return;
            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = 3000;
            _debounce.Tick += OnDebounce;
            _fsw = new FileSystemWatcher(dir, "LastReplay.w3g");
            _fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            _fsw.Changed += OnChanged;
            _fsw.Created += OnChanged;
            _fsw.Renamed += OnRenamed;
            _fsw.EnableRaisingEvents = true;
        }

        void OnRenamed(object s, RenamedEventArgs e) { Poke(); }
        void OnChanged(object s, FileSystemEventArgs e) { Poke(); }

        void Poke()
        {
            try
            {
                if (_debounce == null) return;
                // FileSystemWatcher 回调在线程池线程，Timer 操作转到UI线程
                System.Windows.Forms.Form f = System.Windows.Forms.Application.OpenForms.Count > 0
                    ? System.Windows.Forms.Application.OpenForms[0] : null;
                if (f != null && f.IsHandleCreated)
                    f.BeginInvoke((Action)delegate { _debounce.Stop(); _debounce.Start(); });
            }
            catch { }
        }

        void OnDebounce(object s, EventArgs e)
        {
            _debounce.Stop();
            try { DoSave(IgnoreShort); }
            catch { }
        }

        // 手动立即备份（忽略"太短"规则）
        public string BackupNow()
        {
            return DoSave(false);
        }

        string DoSave(bool applyShortFilter)
        {
            if (string.IsNullOrEmpty(_war3Dir)) return null;
            string src = Path.Combine(War3Ctl.ReplayDir(_war3Dir), "LastReplay.w3g");
            if (!File.Exists(src))
            {
                Report("没有找到 LastReplay.w3g");
                return null;
            }
            int durMs = ParseDuration(src);
            if (applyShortFilter && durMs > 0 && durMs < 5 * 60 * 1000)
            {
                Report("已忽略不足5分钟的录像");
                return null;
            }
            string dir = War3Ctl.AutoSaveDir(_war3Dir);
            Directory.CreateDirectory(dir);
            string durTxt = durMs > 0 ? string.Format("_{0}min", durMs / 60000) : "";
            string dst = Path.Combine(dir, string.Format("Auto_{0:yyyyMMdd_HHmmss}{1}.w3g", DateTime.Now, durTxt));
            try
            {
                File.Copy(src, dst, true);
                Report("录像已保存: " + Path.GetFileName(dst));
                return dst;
            }
            catch (Exception ex)
            {
                Report("保存失败: " + ex.Message);
                return null;
            }
        }

        void Report(string msg)
        {
            if (Saved != null) Saved(msg);
        }

        // w3g头(版本1)在0x3C处存有时长(毫秒)
        public static int ParseDuration(string file)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 0x44) return -1;
                    byte[] buf = new byte[0x44];
                    fs.Read(buf, 0, 0x44);
                    uint headerVer = BitConverter.ToUInt32(buf, 0x24);
                    if (headerVer == 1)
                        return (int)BitConverter.ToUInt32(buf, 0x3C);
                    return -1;
                }
            }
            catch { return -1; }
        }

        public void Stop()
        {
            if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
            if (_debounce != null) { _debounce.Stop(); _debounce.Dispose(); _debounce = null; }
        }

        public void Dispose() { Stop(); }
    }
}
