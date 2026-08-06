using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WshHelper
{
    public class KeyMapEntry
    {
        public int Src { get; set; }
        public int Dst { get; set; }
        public bool KeepInShop { get; set; }   // 商店模式下这条改键仍然生效
    }

    public class Scheme
    {
        public string Name { get; set; }
        public int[] ItemKeys { get; set; }          // 6个物品栏触发键, 0=未设置
        public bool ItemKeysKeepInShop { get; set; } // 商店模式下物品栏键仍然生效
        public List<KeyMapEntry> Maps { get; set; }

        public Scheme()
        {
            Name = "默认方案";
            ItemKeys = new int[6];
            Maps = new List<KeyMapEntry>();
        }
    }

    public class ChatItem
    {
        public int Key { get; set; }
        public int Mods { get; set; }     // 位掩码: 1=Ctrl 2=Alt 4=Shift
        public string Text { get; set; }
        public string Note { get; set; }
    }

    public class Reminder
    {
        public string Name { get; set; }
        public int Interval { get; set; }   // 秒
        public bool Enabled { get; set; }
    }

    public class VersionSource
    {
        public string Name { get; set; }    // 版本号，如 1.26
        public string Url { get; set; }
    }

    public static class Mods
    {
        public const int Ctrl = 1;
        public const int Alt = 2;
        public const int Shift = 4;

        public static string Label(int mods)
        {
            string s = "";
            if ((mods & Ctrl) != 0) s += "Ctrl+";
            if ((mods & Alt) != 0) s += "Alt+";
            if ((mods & Shift) != 0) s += "Shift+";
            return s;
        }
    }

    // 启动模式
    public enum LaunchMode
    {
        ExclusiveFullscreen = 0,
        Windowed = 1,
        BorderlessFullscreen = 2
    }

    public class AppConfig
    {
        public int ConfigVersion { get; set; }
        public bool RemapEnabled { get; set; }
        public bool ApplyToCombo { get; set; }
        public bool BlockWinKey { get; set; }
        public bool ChatEnabled { get; set; }
        public bool AutoLockMouse { get; set; }
        public bool AutoSaveReplay { get; set; }
        public bool IgnoreShortReplay { get; set; }
        public bool ShowApm { get; set; }
        public bool UseOpenGL { get; set; }
        public bool ShowHpBars { get; set; }        // 持续按住Alt显示血条/蓝条
        public bool AlwaysHealthBars { get; set; }  // 注册表 healthbars 开关
        public bool InGameIcon { get; set; }
        public bool ItemKeySelectHeroFirst { get; set; }   // 物品键先按F1选英雄
        public int SuspendKey { get; set; }                // 按住它时临时停用改键(0=未设)
        public int InjectMode { get; set; }                // 0=虚拟键+扫描码(默认) 1=纯扫描码
        public bool AutoNumLock { get; set; }              // 有小键盘改键时自动开启NumLock
        public bool ShopModeEnabled { get; set; }          // 启用商店模式(挂起改键)
        public bool ShopEnterOnWheel { get; set; }         // 滚轮上/下进入商店模式
        public bool BlockWheelZoom { get; set; }           // 屏蔽滚轮的视角缩放
        public int ShopEnterKey { get; set; }              // 额外的进入键(0=未设)
        public int ShopExitKey { get; set; }               // 恢复键(默认F1)
        public int ChatEnterDelay { get; set; }            // 回车后等待毫秒
        public int ChatCharDelay { get; set; }             // 每个字符间隔毫秒
        public int IconX { get; set; }
        public int IconY { get; set; }
        public int IconOpacity { get; set; }
        public int LaunchModeValue { get; set; }
        public int BossKey { get; set; }
        public string War3Path { get; set; }
        public string VerSourceDir { get; set; }
        public int WinW { get; set; }
        public int WinH { get; set; }
        public int OverlayX { get; set; }
        public int OverlayY { get; set; }
        public int OpacityPercent { get; set; }
        public int WarnAhead { get; set; }
        public int CurrentScheme { get; set; }
        public List<Scheme> Schemes { get; set; }
        public List<ChatItem> Chats { get; set; }
        public List<Reminder> Reminders { get; set; }
        public List<VersionSource> VersionSources { get; set; }

        public LaunchMode Launch
        {
            get { return (LaunchMode)LaunchModeValue; }
            set { LaunchModeValue = (int)value; }
        }

        // 配置默认存到 %APPDATA%\WshHelper\config.json —— 这样删掉 bin\、重新编译、
        // 重新 clone 仓库都不会丢改键方案。
        // 如果 exe 旁边已经有 config.json(旧版本或绿色版用法)，就继续用那个。
        static string _configPath;

        static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string ConfigPath
        {
            get
            {
                if (_configPath != null) return _configPath;
                string portable = Path.Combine(ExeDir, "config.json");

                // 绿色版模式：exe 旁放一个 portable.txt，配置就跟着程序走
                if (File.Exists(Path.Combine(ExeDir, "portable.txt")))
                {
                    _configPath = portable;
                    return _configPath;
                }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WshHelper");
                string appData = Path.Combine(dir, "config.json");
                try
                {
                    Directory.CreateDirectory(dir);
                    // 旧版把配置放在 exe 目录，首次运行时搬过来，避免重新编译/删bin丢方案
                    if (!File.Exists(appData) && File.Exists(portable))
                    {
                        File.Copy(portable, appData, false);
                        try { File.Move(portable, portable + ".migrated"); }
                        catch { }
                    }
                }
                catch { _configPath = portable; return _configPath; }

                _configPath = appData;
                return _configPath;
            }
        }

        public static bool IsPortableConfig
        {
            get
            {
                return string.Equals(Path.GetDirectoryName(ConfigPath).TrimEnd('\\'),
                                     ExeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    AppConfig c = ser.Deserialize<AppConfig>(json);
                    if (c != null) { c.FixUp(); return c; }
                }
            }
            catch { }
            AppConfig d = new AppConfig();
            d.SetDefaults();
            d.FixUp();      // 补齐派生项(如版本包目录)并做一次范围校验
            return d;
        }

        // 原子写入：先写临时文件再替换，避免写到一半崩溃导致配置损坏丢方案
        public void Save()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(this);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                if (File.Exists(ConfigPath))
                {
                    string bak = ConfigPath + ".bak";
                    try { File.Copy(ConfigPath, bak, true); }
                    catch { }
                    File.Delete(ConfigPath);
                }
                File.Move(tmp, ConfigPath);
            }
            catch { }
        }

        public const int CurrentConfigVersion = 4;

        public void SetDefaults()
        {
            ConfigVersion = CurrentConfigVersion;
            RemapEnabled = true;
            ApplyToCombo = true;
            BlockWinKey = true;
            ChatEnabled = true;
            AutoLockMouse = false;
            AutoSaveReplay = true;
            IgnoreShortReplay = true;
            ShowApm = false;
            UseOpenGL = false;
            ShowHpBars = false;
            AlwaysHealthBars = true;
            BossKey = 0x13; // Pause
            War3Path = FindWar3Dir();
            WinW = 1600;
            WinH = 900;
            OverlayX = 20;
            OverlayY = 20;
            OpacityPercent = 100;
            WarnAhead = 10;
            CurrentScheme = 0;
            ApplyV2Defaults();

            Schemes = new List<Scheme>();
            Schemes.Add(new Scheme());

            Chats = DefaultChats();

            Reminders = new List<Reminder>();
            Reminder r1 = new Reminder(); r1.Name = "神符"; r1.Interval = 120; r1.Enabled = false; Reminders.Add(r1);
            Reminder r2 = new Reminder(); r2.Name = "野怪"; r2.Interval = 60; r2.Enabled = false; Reminders.Add(r2);
        }

        // 新增功能的默认值(升级旧配置时也会套用)
        void ApplyV2Defaults()
        {
            Launch = LaunchMode.BorderlessFullscreen;   // 默认无边框全屏
            InGameIcon = true;
            IconX = 8;
            IconY = 8;
            IconOpacity = 65;
            AlwaysHealthBars = true;
            ItemKeySelectHeroFirst = false;
            ChatEnterDelay = 150;
            ChatCharDelay = 12;
            AutoNumLock = true;
            ShopModeEnabled = false;
            ShopEnterOnWheel = false;
            ShopEnterKey = 0;
            ShopExitKey = 0x70;   // F1 = 重新选中英雄
            BlockWheelZoom = true;
        }

        static ChatItem Chat(int mods, int key, string text, string note)
        {
            ChatItem c = new ChatItem();
            c.Mods = mods; c.Key = key; c.Text = text; c.Note = note;
            return c;
        }

        // 默认喊话：DOTA 常用开局/查询命令 (Alt+数字，War3本身不占用Alt+数字)
        public static List<ChatItem> DefaultChats()
        {
            List<ChatItem> l = new List<ChatItem>();
            l.Add(Chat(Mods.Alt, '1', "-aphehg", "全阵营选人+换英雄+快速金钱"));
            l.Add(Chat(Mods.Alt, '2', "-apemhg", "全阵营选人+简单模式+快速金钱"));
            l.Add(Chat(Mods.Alt, '3', "-arem", "随机英雄+简单模式"));
            l.Add(Chat(Mods.Alt, '4', "-test", "测试模式"));
            l.Add(Chat(Mods.Alt, '5', "-ii", "显示物品信息"));
            l.Add(Chat(Mods.Alt, '6', "-di", "显示补刀数"));
            l.Add(Chat(Mods.Alt, '7', "-ma", "显示双方英雄"));
            l.Add(Chat(Mods.Alt, '8', "-cson", "开启正反补统计"));
            l.Add(Chat(Mods.Alt, '9', "-random", "随机英雄"));
            l.Add(Chat(Mods.Alt, '0', "-repick", "重选英雄"));
            l.Add(Chat(0, 0, "-clear", "清屏"));
            l.Add(Chat(0, 0, "-swaphero", "交换英雄"));
            l.Add(Chat(0, 0, "-unstuck", "卡住时脱离"));
            l.Add(Chat(0, 0, "快！都来这里Gank！", ""));
            l.Add(Chat(0, 0, "希望大家来了就打完，不要抛弃队友！", ""));
            return l;
        }

        public static string FindWar3Dir()
        {
            // 优先读注册表安装路径
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Blizzard Entertainment\Warcraft III"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallPathX") ?? k.GetValue("InstallPath");
                        if (v != null)
                        {
                            string p = v.ToString().TrimEnd('\\');
                            if (Directory.Exists(p)) return p;
                        }
                    }
                }
            }
            catch { }
            return @"D:\Games\Warcraft III Frozen Throne";
        }

        public void FixUp()
        {
            if (Schemes == null || Schemes.Count == 0)
            {
                Schemes = new List<Scheme>();
                Schemes.Add(new Scheme());
            }
            foreach (Scheme s in Schemes)
            {
                if (s.ItemKeys == null || s.ItemKeys.Length != 6)
                {
                    int[] nk = new int[6];
                    if (s.ItemKeys != null)
                        for (int i = 0; i < Math.Min(6, s.ItemKeys.Length); i++) nk[i] = s.ItemKeys[i];
                    s.ItemKeys = nk;
                }
                if (s.Maps == null) s.Maps = new List<KeyMapEntry>();
                if (string.IsNullOrEmpty(s.Name)) s.Name = "未命名";
            }
            if (CurrentScheme < 0 || CurrentScheme >= Schemes.Count) CurrentScheme = 0;
            if (Chats == null) Chats = new List<ChatItem>();
            if (Reminders == null) Reminders = new List<Reminder>();
            if (VersionSources == null) VersionSources = new List<VersionSource>();
            if (string.IsNullOrEmpty(War3Path)) War3Path = FindWar3Dir();
            if (WinW <= 0) WinW = 1600;
            if (WinH <= 0) WinH = 900;
            if (OpacityPercent < 40 || OpacityPercent > 100) OpacityPercent = 100;
            if (WarnAhead <= 0) WarnAhead = 10;
            if (BossKey == 0) BossKey = 0x13;

            // 旧版配置升级：补上新功能的默认值，并追加默认喊话条目
            if (ConfigVersion < 2)
            {
                ApplyV2Defaults();
                foreach (ChatItem c in DefaultChats())
                {
                    bool exists = false;
                    foreach (ChatItem old in Chats)
                        if (old.Text == c.Text) { exists = true; break; }
                    if (!exists) Chats.Add(c);
                }
                ConfigVersion = CurrentConfigVersion;
            }

            if (IconOpacity < 20 || IconOpacity > 100) IconOpacity = 65;
            if (ChatEnterDelay < 30 || ChatEnterDelay > 2000) ChatEnterDelay = 150;
            if (ChatCharDelay < 1 || ChatCharDelay > 200) ChatCharDelay = 12;
            if (InjectMode < 0 || InjectMode > 1) InjectMode = 0;

            // ConfigVersion 3: 自动NumLock 与 商店模式
            if (ConfigVersion < 3)
            {
                AutoNumLock = true;
                if (ShopExitKey == 0) ShopExitKey = 0x70;
            }
            // ConfigVersion 4: 默认屏蔽滚轮的视角缩放
            if (ConfigVersion < 4)
            {
                BlockWheelZoom = true;
            }
            if (ConfigVersion < CurrentConfigVersion) ConfigVersion = CurrentConfigVersion;
            if (LaunchModeValue < 0 || LaunchModeValue > 2) LaunchModeValue = (int)LaunchMode.BorderlessFullscreen;
            if (string.IsNullOrEmpty(VerSourceDir)) VerSourceDir = War3Version.DefaultSourceDir(War3Path);
        }

        public Scheme ActiveScheme
        {
            get { return Schemes[CurrentScheme]; }
        }
    }

    public static class KeyNames
    {
        public static string Name(int vk)
        {
            if (vk == 0) return "";
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);
            switch (vk)
            {
                case Native.VK_LBUTTON: return "鼠标左键";
                case Native.VK_RBUTTON: return "鼠标右键";
                case Native.VK_MBUTTON: return "鼠标中键";
                case Native.VK_XBUTTON1: return "鼠标侧键1";
                case Native.VK_XBUTTON2: return "鼠标侧键2";
                case Native.VK_WHEELUP: return "滚轮上";
                case Native.VK_WHEELDOWN: return "滚轮下";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x14: return "CapsLock";
                case 0x1B: return "Esc";
                case 0x20: return "空格";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrtSc";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x6A: return "小键盘*";
                case 0x6B: return "小键盘+";
                case 0x6D: return "小键盘-";
                case 0x6E: return "小键盘.";
                case 0x6F: return "小键盘/";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                case 0xA0: return "左Shift";
                case 0xA1: return "右Shift";
                case 0xA2: return "左Ctrl";
                case 0xA3: return "右Ctrl";
                case 0xA4: return "左Alt";
                case 0xA5: return "右Alt";
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
            }
            return "VK" + vk.ToString("X2");
        }
    }
}
