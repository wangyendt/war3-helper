using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace War3Helper
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
        public int[] ItemSlotDst { get; set; }       // 6个物品栏在游戏里的实际快捷键
        public bool ItemKeysKeepInShop { get; set; } // 商店模式下物品栏键仍然生效
        public List<KeyMapEntry> Maps { get; set; }

        // 魔兽物品栏是 2列x3行，对应小键盘左边那 2 列:
        //     物品1(小键盘7)  物品4(小键盘8)
        //     物品2(小键盘4)  物品5(小键盘5)
        //     物品3(小键盘1)  物品6(小键盘2)
        // 界面上物品1~3 是左列、4~6 是右列(竖着数)，所以顺序是 7 4 1 8 5 2，
        // 不是横着数的 7 8 4 5 1 2。
        // 用了 War3 自定义快捷键(CustomKeys.txt)的话物品栏可能是别的键，所以做成可改的。
        public static int[] DefaultItemSlotDst()
        {
            return new int[] { 0x67, 0x64, 0x61, 0x68, 0x65, 0x62 };
        }

        // 3.x 之前用的横向顺序，用来识别需要迁移的旧配置
        public static int[] LegacyRowMajorItemSlotDst()
        {
            return new int[] { 0x67, 0x68, 0x64, 0x65, 0x61, 0x62 };
        }

        public Scheme()
        {
            Name = "默认方案";
            ItemKeys = new int[6];
            ItemSlotDst = DefaultItemSlotDst();
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
        public int WheelMinIntervalMs { get; set; }        // 同方向滚轮的最小间隔(0=不限制)
        public int ShopEnterKey { get; set; }              // 额外的进入键(0=未设)
        public int ShopExitKey { get; set; }               // 恢复键(默认F1)
        public int HeroSelectKey { get; set; }             // 选中自己英雄的键(默认F1)
        public bool BuiltinStopAsHold { get; set; }        // 内置: S 键改为原地不动(H)
        public bool SuspendWhileTyping { get; set; }       // 聊天栏打开时暂停改键
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

        // 这两个是派生属性，不写进 json（否则 ActiveScheme 会把方案整份重复存一遍）
        [ScriptIgnore]
        public LaunchMode Launch
        {
            get { return (LaunchMode)LaunchModeValue; }
            set { LaunchModeValue = (int)value; }
        }

        // 配置默认存到 %APPDATA%\War3Helper\config.json —— 这样删掉 bin\、重新编译、
        // 重新 clone 仓库都不会丢改键方案。
        // 如果 exe 旁边有 portable.txt，配置就跟着程序走(绿色版)。
        static string _configPath;

        const string AppDataDirName = "War3Helper";
        const string LegacyAppDataDirName = "WshHelper";   // 改名前的目录

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

                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(roaming))
                {
                    // 只有连 %APPDATA% 都拿不到时才退回 exe 目录
                    _configPath = portable;
                    return _configPath;
                }

                string dir = Path.Combine(roaming, AppDataDirName);
                string appData = Path.Combine(dir, "config.json");

                // 先把位置定死。下面的迁移全是"锦上添花"，任何一步失败都不能改变配置位置 ——
                // 之前这里出异常会悄悄退回 exe 目录，而那儿没有配置文件，
                // 结果就是加载出一份全新的默认配置，用户看着就像"设置全没了"。
                // 触发条件很常见：两个实例几乎同时启动时，File.Copy(..., overwrite:false)
                // 会因为对方刚建好文件而抛异常。
                _configPath = appData;

                try { Directory.CreateDirectory(dir); }
                catch { }

                if (!File.Exists(appData))
                {
                    // 程序改名前配置在 %APPDATA%\WshHelper
                    TryImport(Path.Combine(Path.Combine(roaming, LegacyAppDataDirName), "config.json"), appData);
                }
                if (!File.Exists(appData) && File.Exists(portable))
                {
                    // 更早的版本把配置放在 exe 目录
                    if (TryImport(portable, appData))
                    {
                        try { File.Move(portable, portable + ".migrated"); }
                        catch { }
                    }
                }
                return _configPath;
            }
        }

        // 把 from 搬到 to。失败(包括并发下对方已经建好)都不算错，
        // 只要 to 最终存在就当成功。
        static bool TryImport(string from, string to)
        {
            try
            {
                if (File.Exists(from)) File.Copy(from, to, false);
            }
            catch { }
            return File.Exists(to);
        }

        public static bool IsPortableConfig
        {
            get
            {
                return string.Equals(Path.GetDirectoryName(ConfigPath).TrimEnd('\\'),
                                     ExeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
        }

        // 配置文件存在但读不出来时的提示，由主界面在启动后显示
        public static string LoadWarning;

        // 从一个文件读配置。读不出来返回 null，不抛异常。
        static AppConfig TryLoadFrom(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json.Trim())) { error = "文件是空的"; return null; }
                JavaScriptSerializer ser = new JavaScriptSerializer();
                AppConfig c = ser.Deserialize<AppConfig>(json);
                if (c == null) { error = "反序列化返回 null"; return null; }
                c.FixUp();
                return c;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // 加载顺序：主文件 -> .bak -> 默认值。
        // 关键点是绝不能"主文件读不到就直接用默认值"然后被下一次保存写回去 ——
        // 那样一次偶发的读取失败就会把用户的改键方案永久抹掉。
        public static AppConfig Load()
        {
            LoadWarning = null;
            string path = ConfigPath;
            string bak = path + ".bak";

            string err;
            AppConfig c = TryLoadFrom(path, out err);
            if (c != null) return c;

            bool mainExisted = File.Exists(path);

            // 主文件缺失或损坏，先试备份
            string bakErr;
            AppConfig fromBak = TryLoadFrom(bak, out bakErr);
            if (fromBak != null)
            {
                if (mainExisted)
                {
                    string saved = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                    try { File.Move(path, saved); }
                    catch { saved = path; }
                    LoadWarning = "配置文件读取失败，已从备份恢复。\r\n\r\n" +
                                  "读不出来的原文件保留为:\r\n" + saved +
                                  (err != null ? "\r\n\r\n错误: " + err : "");
                }
                else
                {
                    LoadWarning = "配置文件不见了，已从备份恢复:\r\n" + bak;
                }
                try { fromBak.Save(); }
                catch { }
                return fromBak;
            }

            // 备份也没有/也坏了，才退到默认值。主文件存在的话留证，绝不覆盖。
            if (mainExisted)
            {
                string saved = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                try { File.Move(path, saved); }
                catch { saved = path; }
                LoadWarning = "配置文件读取失败，备份也无法恢复，已改用默认设置。\r\n\r\n" +
                              "原文件没有被覆盖，已保留为:\r\n" + saved +
                              (err != null ? "\r\n\r\n错误: " + err : "");
            }

            AppConfig d = new AppConfig();
            d.SetDefaults();
            d.FixUp();      // 补齐派生项(如版本包目录)并做一次范围校验
            return d;
        }

        // 原子写入。
        // 原来是 复制到.bak -> 删除主文件 -> 重命名临时文件，中间有一段"主文件不存在"的窗口；
        // 另一个实例正好在那一刻启动，Load() 就会认为没有配置而套用默认值，
        // 接着它自己一保存，用户的改键方案就被永久抹掉了。
        // File.Replace 是原子的：替换和生成 .bak 一步完成，全程不存在主文件缺失的瞬间。
        public void Save()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(this);
                string path = ConfigPath;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                if (File.Exists(path))
                    File.Replace(tmp, path, path + ".bak", true);
                else
                    File.Move(tmp, path);
            }
            catch { }
        }

        public const int CurrentConfigVersion = 7;

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
            HeroSelectKey = 0x70;
            BlockWheelZoom = true;
            BuiltinStopAsHold = true;
            SuspendWhileTyping = true;
            WheelMinIntervalMs = 300;
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
                if (s.ItemSlotDst == null || s.ItemSlotDst.Length != 6)
                {
                    int[] def = Scheme.DefaultItemSlotDst();
                    if (s.ItemSlotDst != null)
                        for (int i = 0; i < Math.Min(6, s.ItemSlotDst.Length); i++)
                            if (s.ItemSlotDst[i] != 0) def[i] = s.ItemSlotDst[i];
                    s.ItemSlotDst = def;
                }
                for (int i = 0; i < 6; i++)
                    if (s.ItemSlotDst[i] == 0) s.ItemSlotDst[i] = Scheme.DefaultItemSlotDst()[i];
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
            if (HeroSelectKey == 0) HeroSelectKey = 0x70;
            // ConfigVersion 6: 滚轮最小间隔。0 是"不限制"的合法取值，所以不能靠范围校验补默认值，
            // 只能在这里给老配置补上。
            if (ConfigVersion < 6) WheelMinIntervalMs = 300;
            // ConfigVersion 7: 打字时暂停改键
            if (ConfigVersion < 7) SuspendWhileTyping = true;
            if (WheelMinIntervalMs < 0 || WheelMinIntervalMs > 2000) WheelMinIntervalMs = 300;

            // ConfigVersion 5:
            //  a) 物品栏目标键顺序修正 —— 之前是横向的 7 8 4 5 1 2，而界面上
            //     物品1~3 是左列、4~6 是右列，正确顺序应为 7 4 1 8 5 2
            //  b) S->H 变成内置可勾选项，把手动添加的同款映射清掉，避免重复
            if (ConfigVersion < 5)
            {
                BuiltinStopAsHold = true;
                WheelMinIntervalMs = 300;
                int[] legacy = Scheme.LegacyRowMajorItemSlotDst();
                foreach (Scheme s in Schemes)
                {
                    bool isLegacy = true;
                    for (int i = 0; i < 6; i++)
                        if (s.ItemSlotDst[i] != legacy[i]) { isLegacy = false; break; }
                    if (isLegacy) s.ItemSlotDst = Scheme.DefaultItemSlotDst();
                    s.Maps.RemoveAll(delegate(KeyMapEntry e)
                    {
                        return e.Src == (int)'S' && e.Dst == (int)'H';
                    });
                }
            }

            if (ConfigVersion < CurrentConfigVersion) ConfigVersion = CurrentConfigVersion;
            if (LaunchModeValue < 0 || LaunchModeValue > 2) LaunchModeValue = (int)LaunchMode.BorderlessFullscreen;
            if (string.IsNullOrEmpty(VerSourceDir)) VerSourceDir = War3Version.DefaultSourceDir(War3Path);
        }

        [ScriptIgnore]
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
