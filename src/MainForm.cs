using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace War3Helper
{
    // 按键采集框：点击后按任意键/鼠标侧键采集，双击清除
    public class CaptureBox : TextBox
    {
        int _vk;
        public event Action VkChanged;
        public bool AllowMouse = true;
        public bool IgnoreModifiers = false;

        public CaptureBox()
        {
            ReadOnly = true;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        public int Vk
        {
            get { return _vk; }
            set { _vk = value; Text = KeyNames.Name(value); }
        }

        static bool IsMod(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5);
        }

        void SetVk(int vk)
        {
            if (IgnoreModifiers && IsMod(vk)) return;
            _vk = vk;
            Text = KeyNames.Name(vk);
            if (VkChanged != null) VkChanged();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Focused)
            {
                int vk = (int)(keyData & Keys.KeyCode);
                if (vk != 0) { SetVk(vk); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            int vk = (int)e.KeyCode;
            if (vk != 0) SetVk(vk);
        }

        protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!AllowMouse) return;
            if (e.Button == MouseButtons.Middle) SetVk(Native.VK_MBUTTON);
            else if (e.Button == MouseButtons.XButton1) SetVk(Native.VK_XBUTTON1);
            else if (e.Button == MouseButtons.XButton2) SetVk(Native.VK_XBUTTON2);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (AllowMouse && Focused)
                SetVk(e.Delta > 0 ? Native.VK_WHEELUP : Native.VK_WHEELDOWN);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            SetVk(0);
        }
    }

    public class MainForm : Form, IHelperActions
    {
        AppConfig cfg;
        OverlayForm overlay;
        InGameIconForm iconForm;
        ReplayWatcher replayWatcher;
        NotifyIcon tray;
        bool reallyExit = false;
        bool loading = true;

        // 改键页
        CheckBox chkRemap, chkCombo, chkWin, chkHeroFirst;
        CaptureBox[] capItems = new CaptureBox[6];
        Label[] lblSlotDst = new Label[6];
        CheckBox chkStopAsHold;
        ListView lvMaps;
        CaptureBox capSrc, capDst, capSuspend, capShopEnter, capShopExit;
        CheckBox chkAutoNumLock, chkShopMode, chkShopWheel, chkBlockWheel, chkItemKeepShop, chkTyping;
        Label lblNumLock;
        ComboBox cmbSchemes, cmbInject;
        CheckBox chkDiag;
        // 喊话页
        CheckBox chkChat;
        ListView lvChats;
        CaptureBox capChatKey;
        CheckBox chkMCtrl, chkMAlt, chkMShift;
        TextBox txtChat, txtChatNote;
        NumericUpDown numEnterDelay, numCharDelay, numWheelGap;
        // 窗口页
        TextBox txtPath;
        ComboBox cmbRes, cmbLaunch;
        CheckBox chkOpenGL, chkLock, chkAlwaysBars, chkIcon;
        bool _barsOn;
        CaptureBox capBoss;
        TrackBar trackOpacity, trackIconOpacity;
        // 录像页
        ListView lvReplays;
        Label lblReplayStatus;
        CheckBox chkReplay, chkIgnoreShort;
        // 版本页
        Label lblCurVer;
        TextBox txtVerDir;
        ListView lvVersions;
        ProgressBar barVer;
        Label lblVerStatus;
        Button btnSwitch, btnDownload;
        List<VersionPackage> versionPkgs = new List<VersionPackage>();
        string currentLabel = "";
        // 提醒页
        CheckBox chkApm;
        NumericUpDown numOx, numOy, numWarn;
        ListView lvRem;
        TextBox txtRemName;
        NumericUpDown numRemSec;
        // 状态栏
        Label lblStatus;
        System.Windows.Forms.Timer timerMain;

        const int HOTKEY_BOSS = 1;

        public MainForm()
        {
            cfg = AppConfig.Load();
            Engine.Cfg = cfg;

            Text = "War3助手 — 魔兽争霸3 改键 / 喊话 / 版本切换";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(720, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = IconGen.AppIcon();

            BuildUi();
            Util.DpiScale(this);
            LoadCfgToUi();
            loading = false;

            overlay = new OverlayForm();
            overlay.Cfg = cfg;

            iconForm = new InGameIconForm();
            iconForm.Cfg = cfg;
            iconForm.Actions = this;
            iconForm.CreateControl();
            iconForm.Init();

            Engine.Install();
            Engine.Rebuild();
            Engine.HotToggleRemap += delegate { BeginInvoke((Action)ActToggleRemap); };
            Engine.HotNextScheme += delegate { BeginInvoke((Action)NextSchemeHot); };
            Engine.HotToggleLock += delegate { BeginInvoke((Action)ActToggleLock); };
            Engine.HotToggleApm += delegate { BeginInvoke((Action)ActToggleApm); };
            Engine.HotTimerReset += delegate { BeginInvoke((Action)ActStartTimer); };
            Engine.ShopModeChanged += delegate
            {
                BeginInvoke((Action)delegate
                {
                    overlay.FlashMessage(Engine.ShopMode ? "商店模式: 改键已挂起" : "改键已恢复", 1800);
                });
            };

            replayWatcher = new ReplayWatcher();
            replayWatcher.Saved += delegate(string s)
            {
                BeginInvoke((Action)delegate
                {
                    lblReplayStatus.Text = string.Format("[{0:HH:mm}] {1}", DateTime.Now, s);
                    if (tray != null) tray.ShowBalloonTip(2000, "War3助手", s, ToolTipIcon.Info);
                    ReloadReplays();
                });
            };
            ApplyReplayWatcher();

            SetupTray();
            RegisterBoss();
            // 启动时只在游戏没开着的时候补写一次。游戏开着时写了也会被它退出时覆盖，
            // 而且玩家可能刚在游戏里自己改过，不该被我们无条件盖掉。
            if (cfg.AlwaysHealthBars && !War3Ctl.GetAlwaysHealthBars())
            {
                string barErr;
                War3Ctl.SetAlwaysHealthBars(true, out barErr);
            }
            War3Ctl.StartWindowCacheRefresher();   // 进程枚举放后台，别堵住UI线程上的钩子回调

            timerMain = new System.Windows.Forms.Timer();
            timerMain.Interval = 300;
            timerMain.Tick += OnMainTick;
            timerMain.Start();

            if (AppConfig.LoadWarning != null)
            {
                string w = AppConfig.LoadWarning;
                BeginInvoke((Action)delegate
                {
                    MessageBox.Show(this, w, "配置读取失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_BOSS)
            {
                War3Ctl.ToggleBoss();
                return;
            }
            // 又双击了一次程序 -> 把本窗口叫到前台，而不是让用户以为没反应
            if (Program.WM_SHOW_EXISTING != 0 && m.Msg == (int)Program.WM_SHOW_EXISTING)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        void RegisterBoss()
        {
            Native.UnregisterHotKey(Handle, HOTKEY_BOSS);
            if (cfg.BossKey != 0)
                Native.RegisterHotKey(Handle, HOTKEY_BOSS, 0, (uint)cfg.BossKey);
        }

        // ================= IHelperActions =================
        public void ActToggleRemap()
        {
            chkRemap.Checked = !chkRemap.Checked;
            overlay.FlashMessage(cfg.RemapEnabled ? "改键: 开" : "改键: 关", 1500);
        }

        public void ActSelectScheme(int index)
        {
            if (index < 0 || index >= cfg.Schemes.Count) return;
            cmbSchemes.SelectedIndex = index;
            overlay.FlashMessage("方案: " + cfg.ActiveScheme.Name, 1800);
        }

        void NextSchemeHot()
        {
            if (cfg.Schemes.Count < 2) { overlay.FlashMessage("只有一个方案", 1500); return; }
            ActSelectScheme((cfg.CurrentScheme + 1) % cfg.Schemes.Count);
        }

        public void ActToggleLock()
        {
            chkLock.Checked = !chkLock.Checked;
            overlay.FlashMessage(cfg.AutoLockMouse ? "锁定鼠标: 开" : "锁定鼠标: 关", 1500);
        }

        public void ActToggleApm()
        {
            chkApm.Checked = !chkApm.Checked;
            overlay.FlashMessage(cfg.ShowApm ? "APM显示: 开" : "APM显示: 关", 1200);
        }

        public void ActStartTimer() { overlay.ResetGameTimer(); }

        public void ActStopTimer()
        {
            overlay.StopGameTimer();
            overlay.FlashMessage("计时已停止", 1500);
        }

        public void ActToggleReminder(int index)
        {
            if (index < 0 || index >= cfg.Reminders.Count) return;
            cfg.Reminders[index].Enabled = !cfg.Reminders[index].Enabled;
            cfg.Save();
            ReloadReminders();
            overlay.FlashMessage(cfg.Reminders[index].Name +
                (cfg.Reminders[index].Enabled ? " 提醒开" : " 提醒关"), 1500);
        }

        public void ActBorderless()
        {
            if (!War3Ctl.MakeBorderless()) MessageBox.Show(this, "未找到魔兽窗口，请先窗口化启动魔兽");
        }

        public void ActRestoreBorder()
        {
            if (!War3Ctl.RestoreBorder(cfg.WinW, cfg.WinH)) MessageBox.Show(this, "未找到魔兽窗口");
        }

        public void ActSendChat(string text) { Engine.SendChatAsync(text); }

        public void ActOpenReplayDir()
        {
            string dir = War3Ctl.AutoSaveDir(cfg.War3Path);
            try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
            catch { }
        }

        public void ActBackupReplayNow()
        {
            if (replayWatcher != null) replayWatcher.BackupNow();
        }

        public void ActShowMain() { RestoreFromTray(); }

        public void ActExit() { reallyExit = true; Close(); }

        // ================= 主循环 =================
        int tickCount = 0;

        // 注意：低层钩子回调跑在本线程(UI线程)上，这里做的任何耗时操作都会把钩子回调堵住，
        // 超过 LowLevelHooksTimeout(300ms) 钩子就会被系统摘掉。所以这里只能做廉价的事，
        // 进程枚举一律交给 War3Ctl 的后台刷新线程。
        void OnMainTick(object s, EventArgs e)
        {
            tickCount++;
            if (tickCount % 3 == 0) Engine.InvalidateForegroundMemo();
            Engine.WatchdogTick();      // 钩子被系统摘掉时自动重装
            War3Ctl.MaintainClip(cfg.AutoLockMouse);
            if (tickCount % 7 == 0) Engine.TickNumLock();
            if (lblNumLock != null)
                lblNumLock.Text = Engine.NumLockProblem
                    ? "⚠ NumLock 是关的，小键盘物品栏改键会失效！"
                    : (Engine.MapsToNumpad ? "NumLock 已开启，物品栏改键可正常工作" : "");
            if (iconForm != null) iconForm.Sync();
            bool found = War3Ctl.CachedMainWindow() != IntPtr.Zero;
            // 每 30 拍(约9秒)读一次注册表就够了，别每拍都读
            if (tickCount % 30 == 1) _barsOn = War3Ctl.GetAlwaysHealthBars();
            string bars = _barsOn ? "开" : "关";
            string hook = Engine.ReinstallCount > 0 ? "  钩子已自动重装" + Engine.ReinstallCount + "次" : "";
            lblStatus.Text = string.Format("魔兽: {0}    改键: {1}    方案: {2}    血条常显: {3}    老板键: {4}{5}",
                found ? (Engine.War3Foreground() ? "游戏中" : "已找到") : "未运行",
                Engine.Typing ? "打字中(已暂停)"
                    : (Engine.ShopMode ? "商店模式(已挂起)"
                    : (Engine.SuspendHeld ? "已临时停用" : (cfg.RemapEnabled ? "开" : "关"))),
                cfg.ActiveScheme.Name,
                bars,
                KeyNames.Name(cfg.BossKey),
                hook);
        }

        // ================= UI构建 =================
        void BuildUi()
        {
            TabControl tabs = new TabControl();
            tabs.Bounds = new Rectangle(0, 0, 720, 615);
            Controls.Add(tabs);

            lblStatus = new Label();
            lblStatus.Bounds = new Rectangle(8, 619, 710, 20);
            lblStatus.ForeColor = Color.DimGray;
            Controls.Add(lblStatus);

            TabPage tp1 = new TabPage("改键");
            TabPage tp2 = new TabPage("快捷喊话");
            TabPage tp3 = new TabPage("窗口/控制");
            TabPage tp4 = new TabPage("录像");
            TabPage tp5 = new TabPage("版本切换");
            TabPage tp6 = new TabPage("提醒/APM");
            TabPage tp7 = new TabPage("使用说明");
            tabs.TabPages.AddRange(new TabPage[] { tp1, tp2, tp3, tp4, tp5, tp6, tp7 });

            BuildTabRemap(tp1);
            BuildTabChat(tp2);
            BuildTabWindow(tp3);
            BuildTabReplay(tp4);
            BuildTabVersion(tp5);
            BuildTabMisc(tp6);
            BuildTabHelp(tp7);
        }

        void BuildTabRemap(TabPage tp)
        {
            chkRemap = new CheckBox();
            chkRemap.Text = "启用改键 (Ctrl+F2)";
            chkRemap.Bounds = new Rectangle(12, 10, 150, 22);
            chkRemap.CheckedChanged += delegate { if (loading) return; cfg.RemapEnabled = chkRemap.Checked; SaveRebuild(); };
            tp.Controls.Add(chkRemap);

            chkCombo = new CheckBox();
            chkCombo.Text = "按住Ctrl/Alt/Shift时仍改键";
            chkCombo.Bounds = new Rectangle(166, 10, 196, 22);
            chkCombo.CheckedChanged += delegate { if (loading) return; cfg.ApplyToCombo = chkCombo.Checked; SaveRebuild(); };
            tp.Controls.Add(chkCombo);

            chkWin = new CheckBox();
            chkWin.Text = "游戏中屏蔽Win键";
            chkWin.Bounds = new Rectangle(366, 10, 130, 22);
            chkWin.CheckedChanged += delegate { if (loading) return; cfg.BlockWinKey = chkWin.Checked; SaveRebuild(); };
            tp.Controls.Add(chkWin);

            chkStopAsHold = new CheckBox();
            chkStopAsHold.Text = "内置: S = 原地不动(H)";
            chkStopAsHold.Bounds = new Rectangle(502, 10, 200, 22);
            chkStopAsHold.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.BuiltinStopAsHold = chkStopAsHold.Checked;
                SaveRebuild();
            };
            tp.Controls.Add(chkStopAsHold);

            GroupBox gItems = new GroupBox();
            gItemsBox = gItems;
            gItems.Text = "物品栏快捷键 (物品1~6 = 小键盘 7 8 4 5 1 2)";
            gItems.Bounds = new Rectangle(12, 40, 330, 130);
            tp.Controls.Add(gItems);
            // 排列和游戏里的物品栏一致：2列x3行，物品1~3 左列、4~6 右列
            for (int i = 0; i < 6; i++)
            {
                int col = i / 3, row = i % 3;
                Label l = new Label();
                l.Text = "物品" + (i + 1);
                l.Bounds = new Rectangle(15 + col * 160, 28 + row * 32, 45, 20);
                gItems.Controls.Add(l);
                CaptureBox cb = new CaptureBox();
                cb.Bounds = new Rectangle(62 + col * 160, 24 + row * 32, 68, 24);
                int idx = i;
                cb.VkChanged += delegate
                {
                    if (loading) return;
                    cfg.ActiveScheme.ItemKeys[idx] = cb.Vk;
                    SaveRebuild();
                };
                capItems[i] = cb;
                gItems.Controls.Add(cb);

                // 直接标出这一格在游戏里的实际快捷键，避免顺序看不明白
                Label dl = new Label();
                dl.Bounds = new Rectangle(132 + col * 160, 28 + row * 32, 26, 20);
                dl.ForeColor = Color.FromArgb(120, 120, 130);
                lblSlotDst[i] = dl;
                gItems.Controls.Add(dl);
            }

            Button bSlotAdv = new Button();
            bSlotAdv.Text = "目标键...";
            bSlotAdv.Bounds = new Rectangle(240, 172, 100, 26);
            bSlotAdv.Click += delegate { ShowItemSlotDialog(); };
            tp.Controls.Add(bSlotAdv);

            chkHeroFirst = new CheckBox();
            chkHeroFirst.Text = "物品键先选中英雄";
            chkHeroFirst.Bounds = new Rectangle(14, 174, 220, 22);
            chkHeroFirst.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.ItemKeySelectHeroFirst = chkHeroFirst.Checked;
                SaveRebuild();
            };
            tp.Controls.Add(chkHeroFirst);

            chkTyping = new CheckBox();
            chkTyping.Text = "打字时暂停改键（按回车开聊天栏后自动识别）";
            chkTyping.Bounds = new Rectangle(14, 198, 336, 22);
            chkTyping.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.SuspendWhileTyping = chkTyping.Checked;
                if (!cfg.SuspendWhileTyping) Engine.ResetTyping();
                cfg.Save();
            };
            tp.Controls.Add(chkTyping);

            lblNumLock = new Label();
            lblNumLock.Bounds = new Rectangle(14, 224, 336, 20);
            lblNumLock.ForeColor = Color.FromArgb(180, 60, 0);
            tp.Controls.Add(lblNumLock);

            chkAutoNumLock = new CheckBox();
            chkAutoNumLock.Text = "自动开启 NumLock (物品栏改键必需)";
            chkAutoNumLock.Bounds = new Rectangle(14, 244, 300, 22);
            chkAutoNumLock.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.AutoNumLock = chkAutoNumLock.Checked;
                cfg.Save();
                if (cfg.AutoNumLock) Engine.EnsureNumLock();
            };
            tp.Controls.Add(chkAutoNumLock);

            GroupBox gShop = new GroupBox();
            gShop.Text = "商店模式 (进商店时挂起全部改键)";
            gShop.Bounds = new Rectangle(12, 270, 330, 160);
            tp.Controls.Add(gShop);

            chkShopMode = new CheckBox();
            chkShopMode.Text = "启用商店模式";
            chkShopMode.Bounds = new Rectangle(14, 22, 120, 22);
            chkShopMode.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.ShopModeEnabled = chkShopMode.Checked;
                if (!cfg.ShopModeEnabled) Engine.ExitShopMode();
                cfg.Save();
            };
            gShop.Controls.Add(chkShopMode);

            chkBlockWheel = new CheckBox();
            chkBlockWheel.Text = "屏蔽滚轮调整视角";
            chkBlockWheel.Bounds = new Rectangle(14, 100, 180, 22);
            chkBlockWheel.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.BlockWheelZoom = chkBlockWheel.Checked;
                cfg.Save();
            };
            gShop.Controls.Add(chkBlockWheel);

            Label lwi = new Label();
            lwi.Text = "滚轮最小间隔:";
            lwi.Bounds = new Rectangle(14, 130, 92, 20);
            gShop.Controls.Add(lwi);
            numWheelGap = new NumericUpDown();
            numWheelGap.Minimum = 0; numWheelGap.Maximum = 2000; numWheelGap.Increment = 50;
            numWheelGap.Bounds = new Rectangle(108, 126, 64, 24);
            numWheelGap.ValueChanged += delegate
            {
                if (loading) return;
                cfg.WheelMinIntervalMs = (int)numWheelGap.Value;
                Engine.ResetWheelThrottle();
                cfg.Save();
            };
            gShop.Controls.Add(numWheelGap);
            Label lwi2 = new Label();
            lwi2.Text = "毫秒 (防一格触发两次)";
            lwi2.Bounds = new Rectangle(178, 130, 150, 20);
            lwi2.ForeColor = Color.DimGray;
            gShop.Controls.Add(lwi2);

            chkItemKeepShop = new CheckBox();
            chkItemKeepShop.Text = "物品栏键仍生效";
            chkItemKeepShop.Bounds = new Rectangle(190, 100, 130, 22);
            chkItemKeepShop.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.ActiveScheme.ItemKeysKeepInShop = chkItemKeepShop.Checked;
                SaveRebuild();
            };
            gShop.Controls.Add(chkItemKeepShop);

            chkShopWheel = new CheckBox();
            chkShopWheel.Text = "滚轮上/下 进入";
            chkShopWheel.Bounds = new Rectangle(146, 22, 140, 22);
            chkShopWheel.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.ShopEnterOnWheel = chkShopWheel.Checked;
                cfg.Save();
            };
            gShop.Controls.Add(chkShopWheel);

            Label lse = new Label(); lse.Text = "另一个进入键:"; lse.Bounds = new Rectangle(14, 54, 90, 20);
            gShop.Controls.Add(lse);
            capShopEnter = new CaptureBox();
            capShopEnter.Bounds = new Rectangle(108, 50, 90, 24);
            capShopEnter.VkChanged += delegate
            {
                if (loading) return;
                cfg.ShopEnterKey = capShopEnter.Vk; cfg.Save();
            };
            gShop.Controls.Add(capShopEnter);

            Label lsx = new Label(); lsx.Text = "恢复改键的键:"; lsx.Bounds = new Rectangle(14, 84, 90, 20);
            gShop.Controls.Add(lsx);
            capShopExit = new CaptureBox();
            capShopExit.Bounds = new Rectangle(108, 80, 90, 24);
            capShopExit.VkChanged += delegate
            {
                if (loading) return;
                cfg.ShopExitKey = capShopExit.Vk; cfg.Save();
            };
            gShop.Controls.Add(capShopExit);
            Label lsx2 = new Label();
            lsx2.Text = "(默认F1=重选英雄)";
            lsx2.Bounds = new Rectangle(204, 84, 120, 20);
            lsx2.ForeColor = Color.DimGray;
            gShop.Controls.Add(lsx2);

            Label lsus = new Label();
            lsus.Text = "或按住此键临时停用:";
            lsus.Bounds = new Rectangle(14, 440, 140, 20);
            tp.Controls.Add(lsus);
            capSuspend = new CaptureBox();
            capSuspend.Bounds = new Rectangle(156, 436, 100, 24);
            capSuspend.VkChanged += delegate
            {
                if (loading) return;
                cfg.SuspendKey = capSuspend.Vk;
                cfg.Save();
            };
            tp.Controls.Add(capSuspend);

            GroupBox gScheme = new GroupBox();
            gScheme.Text = "改键方案 (Ctrl+F3 游戏中切换)";
            gScheme.Bounds = new Rectangle(12, 468, 330, 100);
            tp.Controls.Add(gScheme);

            cmbSchemes = new ComboBox();
            cmbSchemes.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSchemes.Bounds = new Rectangle(15, 26, 300, 24);
            cmbSchemes.SelectedIndexChanged += delegate
            {
                if (loading || cmbSchemes.SelectedIndex < 0) return;
                cfg.CurrentScheme = cmbSchemes.SelectedIndex;
                ReloadSchemeUi();
                SaveRebuild();
            };
            gScheme.Controls.Add(cmbSchemes);

            Button bNew = new Button(); bNew.Text = "新建"; bNew.Bounds = new Rectangle(15, 58, 70, 28);
            bNew.Click += delegate
            {
                string name = Prompt("新方案名称:", "方案" + (cfg.Schemes.Count + 1));
                if (string.IsNullOrEmpty(name)) return;
                Scheme s = new Scheme(); s.Name = name;
                cfg.Schemes.Add(s);
                cfg.CurrentScheme = cfg.Schemes.Count - 1;
                ReloadSchemeList();
                SaveRebuild();
            };
            gScheme.Controls.Add(bNew);

            Button bRen = new Button(); bRen.Text = "重命名"; bRen.Bounds = new Rectangle(93, 58, 70, 28);
            bRen.Click += delegate
            {
                string name = Prompt("方案名称:", cfg.ActiveScheme.Name);
                if (string.IsNullOrEmpty(name)) return;
                cfg.ActiveScheme.Name = name;
                ReloadSchemeList();
                cfg.Save();
            };
            gScheme.Controls.Add(bRen);

            Button bDel = new Button(); bDel.Text = "删除"; bDel.Bounds = new Rectangle(171, 58, 70, 28);
            bDel.Click += delegate
            {
                if (cfg.Schemes.Count <= 1) { MessageBox.Show(this, "至少保留一个方案"); return; }
                if (MessageBox.Show(this, "删除方案 [" + cfg.ActiveScheme.Name + "] ?", "确认",
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                cfg.Schemes.RemoveAt(cfg.CurrentScheme);
                if (cfg.CurrentScheme >= cfg.Schemes.Count) cfg.CurrentScheme = 0;
                ReloadSchemeList();
                SaveRebuild();
            };
            gScheme.Controls.Add(bDel);

            GroupBox gMaps = new GroupBox();
            gMaps.Text = "自定义改键 (任意键 → 任意键)";
            gMaps.Bounds = new Rectangle(354, 40, 350, 420);
            tp.Controls.Add(gMaps);

            lvMaps = new ListView();
            lvMaps.View = View.Details;
            lvMaps.FullRowSelect = true;
            lvMaps.HideSelection = false;
            lvMaps.CheckBoxes = true;
            lvMaps.Bounds = new Rectangle(12, 24, 326, 286);
            lvMaps.Columns.Add("按下", 130);
            lvMaps.Columns.Add("实际生效", 120);
            lvMaps.Columns.Add("商店", 60);
            lvMaps.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (loading) return;
                int i = e.Item.Index;
                if (i >= 0 && i < cfg.ActiveScheme.Maps.Count)
                {
                    cfg.ActiveScheme.Maps[i].KeepInShop = e.Item.Checked;
                    e.Item.SubItems[2].Text = e.Item.Checked ? "仍生效" : "挂起";
                    SaveRebuild();
                }
            };
            gMaps.Controls.Add(lvMaps);

            Label lmk = new Label();
            lmk.Text = "勾选 = 商店模式下这条改键仍然生效（触发键自动豁免）";
            lmk.Bounds = new Rectangle(12, 312, 326, 20);
            lmk.ForeColor = Color.DimGray;
            gMaps.Controls.Add(lmk);

            capSrc = new CaptureBox();
            capSrc.Bounds = new Rectangle(12, 344, 110, 24);
            gMaps.Controls.Add(capSrc);
            Label arrow = new Label(); arrow.Text = "→"; arrow.Bounds = new Rectangle(128, 348, 22, 20);
            gMaps.Controls.Add(arrow);
            capDst = new CaptureBox();
            capDst.Bounds = new Rectangle(152, 344, 110, 24);
            gMaps.Controls.Add(capDst);

            Button bAdd = new Button(); bAdd.Text = "添加"; bAdd.Bounds = new Rectangle(12, 378, 100, 30);
            bAdd.Click += delegate
            {
                if (capSrc.Vk == 0 || capDst.Vk == 0) { MessageBox.Show(this, "请先设置两个按键"); return; }
                if (capSrc.Vk == capDst.Vk) { MessageBox.Show(this, "两个键相同，无需改键"); return; }
                KeyMapEntry en = new KeyMapEntry(); en.Src = capSrc.Vk; en.Dst = capDst.Vk;
                cfg.ActiveScheme.Maps.RemoveAll(delegate(KeyMapEntry x) { return x.Src == en.Src; });
                cfg.ActiveScheme.Maps.Add(en);
                capSrc.Vk = 0; capDst.Vk = 0;
                ReloadMaps();
                SaveRebuild();
            };
            gMaps.Controls.Add(bAdd);

            Button bDelMap = new Button(); bDelMap.Text = "删除选中"; bDelMap.Bounds = new Rectangle(120, 378, 100, 30);
            bDelMap.Click += delegate
            {
                if (lvMaps.SelectedIndices.Count == 0) return;
                cfg.ActiveScheme.Maps.RemoveAt(lvMaps.SelectedIndices[0]);
                ReloadMaps();
                SaveRebuild();
            };
            gMaps.Controls.Add(bDelMap);

            GroupBox gDiag = new GroupBox();
            gDiag.Text = "改键不生效时用这里排查";
            gDiag.Bounds = new Rectangle(354, 470, 350, 108);
            tp.Controls.Add(gDiag);

            Label lim = new Label();
            lim.Text = "注入方式:";
            lim.Bounds = new Rectangle(14, 28, 66, 20);
            gDiag.Controls.Add(lim);
            cmbInject = new ComboBox();
            cmbInject.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbInject.Bounds = new Rectangle(82, 24, 230, 24);
            cmbInject.Items.AddRange(new object[] { "虚拟键 + 扫描码 (默认)", "只发扫描码 (老游戏兼容)" });
            cmbInject.SelectedIndexChanged += delegate
            {
                if (loading) return;
                cfg.InjectMode = cmbInject.SelectedIndex;
                cfg.Save();
            };
            gDiag.Controls.Add(cmbInject);

            chkDiag = new CheckBox();
            chkDiag.Text = "记录诊断日志";
            chkDiag.Bounds = new Rectangle(14, 54, 130, 22);
            chkDiag.CheckedChanged += delegate
            {
                if (loading) return;
                if (chkDiag.Checked) Diag.Start(); else Diag.Stop();
            };
            gDiag.Controls.Add(chkDiag);

            Button bOpenLog = new Button();
            bOpenLog.Text = "打开日志";
            bOpenLog.Bounds = new Rectangle(150, 51, 80, 27);
            bOpenLog.Click += delegate
            {
                try
                {
                    if (!File.Exists(Diag.LogPath))
                    {
                        MessageBox.Show(this, "还没有日志。请先勾选\"记录诊断日志\"，进游戏按几下改键，再回来打开。");
                        return;
                    }
                    System.Diagnostics.Process.Start("notepad.exe", Diag.LogPath);
                }
                catch { }
            };
            gDiag.Controls.Add(bOpenLog);

            Button bReinstall = new Button();
            bReinstall.Text = "重装钩子";
            bReinstall.Bounds = new Rectangle(236, 51, 80, 27);
            bReinstall.Click += delegate
            {
                Engine.Install();
                MessageBox.Show(this, "键鼠钩子已重新安装。");
            };
            gDiag.Controls.Add(bReinstall);

            Label ldh = new Label();
            ldh.Text = "日志含发出的键和 SendInput 返回值(0=注入失败)";
            ldh.Bounds = new Rectangle(14, 82, 312, 20);
            ldh.ForeColor = Color.DimGray;
            gDiag.Controls.Add(ldh);
        }

        void BuildTabChat(TabPage tp)
        {
            chkChat = new CheckBox();
            chkChat.Text = "启用快捷喊话（按热键自动发送聊天）";
            chkChat.Bounds = new Rectangle(12, 10, 280, 22);
            chkChat.CheckedChanged += delegate { if (loading) return; cfg.ChatEnabled = chkChat.Checked; SaveRebuild(); };
            tp.Controls.Add(chkChat);

            Button bCfgDir = new Button();
            bCfgDir.Text = "配置文件位置";
            bCfgDir.Bounds = new Rectangle(400, 6, 120, 28);
            bCfgDir.Click += delegate
            {
                MessageBox.Show(this,
                    "改键方案等配置保存在:\r\n\r\n" + AppConfig.ConfigPath + "\r\n\r\n" +
                    "重新编译助手、删除 bin 目录都不会影响它。\r\n" +
                    "想让配置跟着程序走(绿色版)，在 exe 旁边放一个空的 portable.txt 即可。",
                    "配置文件位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { System.Diagnostics.Process.Start("explorer.exe",
                        "/select,\"" + AppConfig.ConfigPath + "\""); }
                catch { }
            };
            tp.Controls.Add(bCfgDir);

            Button bReset = new Button();
            bReset.Text = "恢复默认DOTA命令";
            bReset.Bounds = new Rectangle(540, 6, 160, 28);
            bReset.Click += delegate
            {
                if (MessageBox.Show(this, "将补回所有默认的DOTA命令条目（已有的同内容条目不会重复添加）。继续？",
                    "确认", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                foreach (ChatItem c in AppConfig.DefaultChats())
                {
                    bool exists = false;
                    foreach (ChatItem old in cfg.Chats) if (old.Text == c.Text) { exists = true; break; }
                    if (!exists) cfg.Chats.Add(c);
                }
                ReloadChats();
                SaveRebuild();
            };
            tp.Controls.Add(bReset);

            lvChats = new ListView();
            lvChats.View = View.Details;
            lvChats.FullRowSelect = true;
            lvChats.HideSelection = false;
            lvChats.Bounds = new Rectangle(12, 40, 690, 320);
            lvChats.Columns.Add("热键", 120);
            lvChats.Columns.Add("发送内容", 240);
            lvChats.Columns.Add("说明", 300);
            lvChats.SelectedIndexChanged += delegate
            {
                if (loading || lvChats.SelectedIndices.Count == 0) return;
                ChatItem c = cfg.Chats[lvChats.SelectedIndices[0]];
                loading = true;
                capChatKey.Vk = c.Key;
                chkMCtrl.Checked = (c.Mods & Mods.Ctrl) != 0;
                chkMAlt.Checked = (c.Mods & Mods.Alt) != 0;
                chkMShift.Checked = (c.Mods & Mods.Shift) != 0;
                txtChat.Text = c.Text;
                txtChatNote.Text = c.Note == null ? "" : c.Note;
                loading = false;
            };
            tp.Controls.Add(lvChats);

            Label l1 = new Label(); l1.Text = "热键:"; l1.Bounds = new Rectangle(12, 374, 40, 20);
            tp.Controls.Add(l1);
            chkMCtrl = new CheckBox(); chkMCtrl.Text = "Ctrl"; chkMCtrl.Bounds = new Rectangle(54, 372, 52, 22);
            tp.Controls.Add(chkMCtrl);
            chkMAlt = new CheckBox(); chkMAlt.Text = "Alt"; chkMAlt.Bounds = new Rectangle(108, 372, 46, 22);
            tp.Controls.Add(chkMAlt);
            chkMShift = new CheckBox(); chkMShift.Text = "Shift"; chkMShift.Bounds = new Rectangle(156, 372, 56, 22);
            tp.Controls.Add(chkMShift);
            capChatKey = new CaptureBox();
            capChatKey.IgnoreModifiers = true;
            capChatKey.Bounds = new Rectangle(214, 370, 86, 24);
            tp.Controls.Add(capChatKey);

            Label l2 = new Label(); l2.Text = "内容:"; l2.Bounds = new Rectangle(12, 404, 40, 20);
            tp.Controls.Add(l2);
            txtChat = new TextBox();
            txtChat.Bounds = new Rectangle(54, 400, 246, 24);
            tp.Controls.Add(txtChat);

            Label l3 = new Label(); l3.Text = "说明:"; l3.Bounds = new Rectangle(310, 404, 40, 20);
            tp.Controls.Add(l3);
            txtChatNote = new TextBox();
            txtChatNote.Bounds = new Rectangle(352, 400, 200, 24);
            tp.Controls.Add(txtChatNote);

            Button bAdd = new Button(); bAdd.Text = "添加"; bAdd.Bounds = new Rectangle(310, 369, 58, 27);
            bAdd.Click += delegate
            {
                if (string.IsNullOrEmpty(txtChat.Text)) { MessageBox.Show(this, "请先填写发送内容"); return; }
                ChatItem c = new ChatItem();
                c.Key = capChatKey.Vk; c.Mods = CurrentChatMods();
                c.Text = txtChat.Text; c.Note = txtChatNote.Text;
                cfg.Chats.Add(c);
                ReloadChats();
                SaveRebuild();
            };
            tp.Controls.Add(bAdd);

            Button bUpd = new Button(); bUpd.Text = "修改选中"; bUpd.Bounds = new Rectangle(374, 369, 76, 27);
            bUpd.Click += delegate
            {
                if (lvChats.SelectedIndices.Count == 0) { MessageBox.Show(this, "请先在列表中选中一条"); return; }
                ChatItem c = cfg.Chats[lvChats.SelectedIndices[0]];
                c.Key = capChatKey.Vk; c.Mods = CurrentChatMods();
                c.Text = txtChat.Text; c.Note = txtChatNote.Text;
                ReloadChats();
                SaveRebuild();
            };
            tp.Controls.Add(bUpd);

            Button bDel = new Button(); bDel.Text = "删除"; bDel.Bounds = new Rectangle(456, 369, 58, 27);
            bDel.Click += delegate
            {
                if (lvChats.SelectedIndices.Count == 0) return;
                cfg.Chats.RemoveAt(lvChats.SelectedIndices[0]);
                ReloadChats();
                SaveRebuild();
            };
            tp.Controls.Add(bDel);

            Button bTest = new Button(); bTest.Text = "测试发送"; bTest.Bounds = new Rectangle(520, 369, 76, 27);
            bTest.Click += delegate
            {
                if (string.IsNullOrEmpty(txtChat.Text)) return;
                if (War3Ctl.MainWindow() == IntPtr.Zero) { MessageBox.Show(this, "魔兽未运行"); return; }
                Native.SetForegroundWindow(War3Ctl.MainWindow());
                string t = txtChat.Text;
                System.Windows.Forms.Timer d = new System.Windows.Forms.Timer();
                d.Interval = 600;
                d.Tick += delegate { d.Stop(); d.Dispose(); Engine.SendChatAsync(t); };
                d.Start();
            };
            tp.Controls.Add(bTest);

            Label lsp = new Label();
            lsp.Text = "发送速度 —— 回车后等待:";
            lsp.Bounds = new Rectangle(12, 432, 160, 20);
            tp.Controls.Add(lsp);
            numEnterDelay = new NumericUpDown();
            numEnterDelay.Minimum = 30; numEnterDelay.Maximum = 2000; numEnterDelay.Increment = 10;
            numEnterDelay.Bounds = new Rectangle(174, 428, 70, 24);
            numEnterDelay.ValueChanged += delegate { if (loading) return; cfg.ChatEnterDelay = (int)numEnterDelay.Value; cfg.Save(); };
            tp.Controls.Add(numEnterDelay);
            Label lsp2 = new Label();
            lsp2.Text = "毫秒，每字间隔:";
            lsp2.Bounds = new Rectangle(250, 432, 100, 20);
            tp.Controls.Add(lsp2);
            numCharDelay = new NumericUpDown();
            numCharDelay.Minimum = 1; numCharDelay.Maximum = 200;
            numCharDelay.Bounds = new Rectangle(352, 428, 60, 24);
            numCharDelay.ValueChanged += delegate { if (loading) return; cfg.ChatCharDelay = (int)numCharDelay.Value; cfg.Save(); };
            tp.Controls.Add(numCharDelay);
            Label lsp3 = new Label();
            lsp3.Text = "毫秒（发不出来就把两个值调大）";
            lsp3.Bounds = new Rectangle(418, 432, 260, 20);
            lsp3.ForeColor = Color.DimGray;
            tp.Controls.Add(lsp3);

            Label hint = new Label();
            hint.Text = "· 默认DOTA命令用 Alt+数字 触发（War3本身不占用Alt+数字），可自行改成任意组合键。\r\n" +
                        "· 发送到的频道 = 你在游戏里最后一次聊天选的频道（默认队伍/全部）。开局模式命令请先切到全部。\r\n" +
                        "· 英文命令按真实键盘按键逐字敲入；中文靠Unicode注入，部分版本的魔兽可能不接受。";
            hint.Bounds = new Rectangle(12, 456, 690, 60);
            hint.ForeColor = Color.DimGray;
            tp.Controls.Add(hint);
        }

        // 物品栏"目标键"= 这6个格子在游戏里真正的快捷键。默认是魔兽自带的小键盘 7 8 4 5 1 2，
        // 用了 War3 自定义快捷键(CustomKeys.txt)的话就不是小键盘了，所以做成可改。
        void ShowItemSlotDialog()
        {
            Form f = new Form();
            f.Text = "物品栏目标键 / 英雄选择键";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.ClientSize = new Size(430, 300);
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = f.MaximizeBox = false;
            f.Font = new Font("Microsoft YaHei UI", 9F);
            f.Icon = IconGen.AppIcon();

            Label top = new Label();
            top.Text = "这6个格子在游戏里真正的快捷键。魔兽默认是小键盘 7 8 4 5 1 2，\r\n" +
                       "如果你用了 War3 自定义快捷键(CustomKeys.txt)，改成你实际用的键。";
            top.Bounds = new Rectangle(12, 10, 406, 40);
            top.ForeColor = Color.DimGray;
            f.Controls.Add(top);

            CaptureBox[] caps = new CaptureBox[6];
            int[] work = (int[])cfg.ActiveScheme.ItemSlotDst.Clone();
            for (int i = 0; i < 6; i++)
            {
                int col = i / 3, row = i % 3;
                Label l = new Label();
                l.Text = "物品" + (i + 1);
                l.Bounds = new Rectangle(20 + col * 200, 62 + row * 32, 48, 20);
                f.Controls.Add(l);
                CaptureBox cb = new CaptureBox();
                cb.Bounds = new Rectangle(70 + col * 200, 58 + row * 32, 110, 24);
                cb.Vk = work[i];
                int idx = i;
                cb.VkChanged += delegate { work[idx] = cb.Vk; };
                caps[i] = cb;
                f.Controls.Add(cb);
            }

            Label lh = new Label();
            lh.Text = "选中自己英雄的键:";
            lh.Bounds = new Rectangle(20, 172, 120, 20);
            f.Controls.Add(lh);
            CaptureBox capHero = new CaptureBox();
            capHero.Bounds = new Rectangle(142, 168, 110, 24);
            capHero.Vk = cfg.HeroSelectKey;
            f.Controls.Add(capHero);
            Label lh2 = new Label();
            lh2.Text = "(默认F1，供\"物品键先选中英雄\"用)";
            lh2.Bounds = new Rectangle(20, 196, 400, 20);
            lh2.ForeColor = Color.DimGray;
            f.Controls.Add(lh2);

            Label lck = new Label();
            lck.Bounds = new Rectangle(20, 220, 400, 20);
            lck.ForeColor = Color.FromArgb(180, 60, 0);
            try
            {
                string[] ck = Directory.GetFiles(cfg.War3Path, "CustomKeys*.txt", SearchOption.AllDirectories);
                lck.Text = ck.Length > 0
                    ? "检测到 CustomKeys 文件，物品栏快捷键可能不是小键盘，请核对上面的设置"
                    : "未检测到 CustomKeys 文件，用魔兽默认的小键盘即可";
            }
            catch { lck.Text = ""; }
            f.Controls.Add(lck);

            Button bDef = new Button();
            bDef.Text = "恢复默认(小键盘)";
            bDef.Bounds = new Rectangle(20, 252, 140, 30);
            bDef.Click += delegate
            {
                int[] d = Scheme.DefaultItemSlotDst();
                for (int i = 0; i < 6; i++) { work[i] = d[i]; caps[i].Vk = d[i]; }
                capHero.Vk = 0x70;
            };
            f.Controls.Add(bDef);

            Button ok = new Button(); ok.Text = "确定"; ok.DialogResult = DialogResult.OK;
            ok.Bounds = new Rectangle(250, 252, 80, 30);
            f.Controls.Add(ok);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel;
            cancel.Bounds = new Rectangle(338, 252, 80, 30);
            f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            Util.DpiScale(f);

            if (f.ShowDialog(this) != DialogResult.OK) return;
            for (int i = 0; i < 6; i++)
                if (work[i] != 0) cfg.ActiveScheme.ItemSlotDst[i] = work[i];
            if (capHero.Vk != 0) cfg.HeroSelectKey = capHero.Vk;
            SaveRebuild();
            UpdateItemSlotLabel();
        }

        void UpdateItemSlotLabel()
        {
            if (gItemsBox == null) return;
            int[] d = cfg.ActiveScheme.ItemSlotDst;
            for (int i = 0; i < 6; i++)
                if (lblSlotDst[i] != null)
                {
                    string n = KeyNames.Name(d[i]);
                    if (n.StartsWith("小键盘")) n = n.Substring(3);   // 只留数字，够窄
                    lblSlotDst[i].Text = "→" + n;
                }
            bool isDefault = true;
            int[] def = Scheme.DefaultItemSlotDst();
            for (int i = 0; i < 6; i++) if (d[i] != def[i]) { isDefault = false; break; }
            gItemsBox.Text = isDefault
                ? "物品栏快捷键 (右侧 → 是游戏里的小键盘键)"
                : "物品栏快捷键 (目标键已自定义)";
        }

        GroupBox gItemsBox;

        int CurrentChatMods()
        {
            int m = 0;
            if (chkMCtrl.Checked) m |= Mods.Ctrl;
            if (chkMAlt.Checked) m |= Mods.Alt;
            if (chkMShift.Checked) m |= Mods.Shift;
            return m;
        }

        static readonly string[] CommonRes = new string[]
        {
            "800x600","1024x768","1280x720","1280x960","1366x768","1440x900",
            "1600x900","1680x1050","1920x1080","1920x1200","2560x1080",
            "2560x1440","3440x1440","3840x2160"
        };

        void BuildTabWindow(TabPage tp)
        {
            Label l1 = new Label(); l1.Text = "魔兽路径:"; l1.Bounds = new Rectangle(12, 16, 70, 20);
            tp.Controls.Add(l1);
            txtPath = new TextBox();
            txtPath.Bounds = new Rectangle(84, 12, 500, 24);
            txtPath.TextChanged += delegate { if (loading) return; cfg.War3Path = txtPath.Text; cfg.Save(); ApplyReplayWatcher(); };
            tp.Controls.Add(txtPath);
            Button bBrowse = new Button(); bBrowse.Text = "浏览"; bBrowse.Bounds = new Rectangle(594, 10, 70, 27);
            bBrowse.Click += delegate
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.SelectedPath = cfg.War3Path;
                if (d.ShowDialog(this) == DialogResult.OK) txtPath.Text = d.SelectedPath;
            };
            tp.Controls.Add(bBrowse);

            Label l2 = new Label(); l2.Text = "启动模式:"; l2.Bounds = new Rectangle(12, 52, 70, 20);
            tp.Controls.Add(l2);
            cmbLaunch = new ComboBox();
            cmbLaunch.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLaunch.Bounds = new Rectangle(84, 48, 200, 24);
            cmbLaunch.Items.AddRange(new object[] { "独占全屏", "窗口化", "无边框全屏 (推荐)" });
            cmbLaunch.SelectedIndexChanged += delegate
            {
                if (loading) return;
                cfg.LaunchModeValue = cmbLaunch.SelectedIndex;
                cmbRes.Enabled = (cfg.Launch == LaunchMode.Windowed);
                cfg.Save();
            };
            tp.Controls.Add(cmbLaunch);

            Label l2b = new Label(); l2b.Text = "窗口分辨率:"; l2b.Bounds = new Rectangle(300, 52, 80, 20);
            tp.Controls.Add(l2b);
            cmbRes = new ComboBox();
            cmbRes.DropDownStyle = ComboBoxStyle.DropDown;
            cmbRes.Bounds = new Rectangle(382, 48, 130, 24);
            cmbRes.Items.AddRange(CommonRes);
            cmbRes.TextChanged += delegate
            {
                if (loading) return;
                int w, h;
                if (ParseRes(cmbRes.Text, out w, out h)) { cfg.WinW = w; cfg.WinH = h; cfg.Save(); }
            };
            tp.Controls.Add(cmbRes);

            chkOpenGL = new CheckBox();
            chkOpenGL.Text = "OpenGL模式";
            chkOpenGL.Bounds = new Rectangle(528, 50, 120, 22);
            chkOpenGL.CheckedChanged += delegate { if (loading) return; cfg.UseOpenGL = chkOpenGL.Checked; cfg.Save(); };
            tp.Controls.Add(chkOpenGL);

            Button bLaunch = new Button();
            bLaunch.Text = "启动魔兽";
            bLaunch.Bounds = new Rectangle(12, 86, 130, 36);
            bLaunch.Font = new Font(Font, FontStyle.Bold);
            bLaunch.Click += delegate
            {
                string err = War3Ctl.Launch(cfg, cfg.Launch);
                if (err != null) MessageBox.Show(this, err);
            };
            tp.Controls.Add(bLaunch);

            Button bBorder = new Button(); bBorder.Text = "立即伪全屏"; bBorder.Bounds = new Rectangle(152, 86, 120, 36);
            bBorder.Click += delegate { ActBorderless(); };
            tp.Controls.Add(bBorder);

            Button bRestore = new Button(); bRestore.Text = "恢复窗口边框"; bRestore.Bounds = new Rectangle(282, 86, 120, 36);
            bRestore.Click += delegate { ActRestoreBorder(); };
            tp.Controls.Add(bRestore);

            GroupBox gBars = new GroupBox();
            gBars.Text = "血条 / 蓝条";
            gBars.Bounds = new Rectangle(12, 134, 690, 96);
            tp.Controls.Add(gBars);

            chkAlwaysBars = new CheckBox();
            chkAlwaysBars.Text = "血条/蓝条常显（写入魔兽自带的\"生命条\"设置）";
            chkAlwaysBars.Bounds = new Rectangle(14, 24, 320, 22);
            chkAlwaysBars.CheckedChanged += delegate
            {
                if (loading) return;
                string err;
                if (!War3Ctl.SetAlwaysHealthBars(chkAlwaysBars.Checked, out err))
                {
                    // 写不进去就把勾选状态退回注册表里的真实值，别让界面撒谎
                    MessageBox.Show(this, err, "血条常显", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    loading = true;
                    chkAlwaysBars.Checked = War3Ctl.GetAlwaysHealthBars();
                    loading = false;
                    return;
                }
                cfg.AlwaysHealthBars = chkAlwaysBars.Checked;
                cfg.Save();
            };
            gBars.Controls.Add(chkAlwaysBars);

            Label lBars = new Label();
            lBars.Text = "直接改魔兽自己的设置（注册表 Gameplay\\healthbars），等同于在游戏里 选项→游戏性 勾上\"生命条\"。\r\n" +
                         "不注入游戏、不占用按键。魔兽退出时会把设置整个写回注册表，所以要先退出游戏再改，改完下次启动生效。";
            lBars.Bounds = new Rectangle(14, 50, 670, 40);
            lBars.ForeColor = Color.DimGray;
            gBars.Controls.Add(lBars);

            chkLock = new CheckBox();
            chkLock.Text = "鼠标锁定在魔兽窗口内 (Ctrl+F4 开/关)";
            chkLock.Bounds = new Rectangle(12, 240, 300, 22);
            chkLock.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.AutoLockMouse = chkLock.Checked;
                if (!chkLock.Checked) War3Ctl.ReleaseClip();
                cfg.Save();
            };
            tp.Controls.Add(chkLock);

            Label l3 = new Label(); l3.Text = "老板键:"; l3.Bounds = new Rectangle(330, 242, 56, 20);
            tp.Controls.Add(l3);
            capBoss = new CaptureBox();
            capBoss.Bounds = new Rectangle(388, 238, 100, 24);
            capBoss.VkChanged += delegate
            {
                if (loading) return;
                cfg.BossKey = capBoss.Vk;
                RegisterBoss();
                cfg.Save();
            };
            tp.Controls.Add(capBoss);
            Label l3b = new Label();
            l3b.Text = "全局有效，一键隐藏/恢复魔兽";
            l3b.Bounds = new Rectangle(498, 242, 200, 20);
            l3b.ForeColor = Color.DimGray;
            tp.Controls.Add(l3b);

            GroupBox gIcon = new GroupBox();
            gIcon.Text = "局内悬浮图标";
            gIcon.Bounds = new Rectangle(12, 272, 690, 96);
            tp.Controls.Add(gIcon);

            chkIcon = new CheckBox();
            chkIcon.Text = "显示局内悬浮图标（可拖动，单击弹出菜单）";
            chkIcon.Bounds = new Rectangle(14, 24, 310, 22);
            chkIcon.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.InGameIcon = chkIcon.Checked;
                cfg.Save();
                if (iconForm != null) iconForm.Sync();
            };
            gIcon.Controls.Add(chkIcon);

            Label lio = new Label(); lio.Text = "透明度:"; lio.Bounds = new Rectangle(330, 26, 56, 20);
            gIcon.Controls.Add(lio);
            trackIconOpacity = new TrackBar();
            trackIconOpacity.Minimum = 20; trackIconOpacity.Maximum = 100; trackIconOpacity.TickFrequency = 10;
            trackIconOpacity.Bounds = new Rectangle(388, 20, 180, 40);
            trackIconOpacity.ValueChanged += delegate
            {
                if (loading) return;
                cfg.IconOpacity = trackIconOpacity.Value;
                if (iconForm != null) iconForm.Redraw();
                cfg.Save();
            };
            gIcon.Controls.Add(trackIconOpacity);

            Button bResetIcon = new Button();
            bResetIcon.Text = "回到左上角";
            bResetIcon.Bounds = new Rectangle(578, 24, 100, 28);
            bResetIcon.Click += delegate
            {
                cfg.IconX = 8; cfg.IconY = 8; cfg.Save();
                if (iconForm != null) { iconForm.Location = new Point(8, 8); iconForm.Redraw(); }
            };
            gIcon.Controls.Add(bResetIcon);

            Label lIconHint = new Label();
            lIconHint.Text = "拖动可移动位置，单击弹出分级菜单（改键/显示/计时/窗口/喊话/录像）。独占全屏模式下无法显示，请用无边框全屏。";
            lIconHint.Bounds = new Rectangle(14, 62, 670, 30);
            lIconHint.ForeColor = Color.DimGray;
            gIcon.Controls.Add(lIconHint);

            Label l4 = new Label(); l4.Text = "助手窗口透明度:"; l4.Bounds = new Rectangle(12, 378, 110, 20);
            tp.Controls.Add(l4);
            trackOpacity = new TrackBar();
            trackOpacity.Minimum = 40; trackOpacity.Maximum = 100; trackOpacity.TickFrequency = 10;
            trackOpacity.Bounds = new Rectangle(124, 372, 200, 40);
            trackOpacity.ValueChanged += delegate
            {
                if (loading) return;
                cfg.OpacityPercent = trackOpacity.Value;
                Opacity = trackOpacity.Value / 100.0;
                cfg.Save();
            };
            tp.Controls.Add(trackOpacity);

            Label tips = new Label();
            tips.Text = "无边框全屏 = 以桌面分辨率窗口化启动，游戏窗口出现后自动去边框铺满屏幕。\r\n" +
                        "Alt+Tab 切换不卡顿、悬浮图标和APM都能正常显示，画面效果与全屏一致。";
            tips.Bounds = new Rectangle(12, 418, 690, 44);
            tips.ForeColor = Color.DimGray;
            tp.Controls.Add(tips);
        }

        static bool ParseRes(string s, out int w, out int h)
        {
            w = h = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int i = s.IndexOfAny(new char[] { 'x', 'X', '*', '×' });
            if (i <= 0) return false;
            return int.TryParse(s.Substring(0, i).Trim(), out w)
                && int.TryParse(s.Substring(i + 1).Trim(), out h)
                && w >= 640 && h >= 480;
        }

        void BuildTabReplay(TabPage tp)
        {
            chkReplay = new CheckBox();
            chkReplay.Text = "每局结束自动备份录像";
            chkReplay.Bounds = new Rectangle(12, 10, 190, 22);
            chkReplay.CheckedChanged += delegate { if (loading) return; cfg.AutoSaveReplay = chkReplay.Checked; cfg.Save(); ApplyReplayWatcher(); };
            tp.Controls.Add(chkReplay);

            chkIgnoreShort = new CheckBox();
            chkIgnoreShort.Text = "忽略不足5分钟的录像";
            chkIgnoreShort.Bounds = new Rectangle(210, 10, 180, 22);
            chkIgnoreShort.CheckedChanged += delegate { if (loading) return; cfg.IgnoreShortReplay = chkIgnoreShort.Checked; cfg.Save(); ApplyReplayWatcher(); };
            tp.Controls.Add(chkIgnoreShort);

            Button bRefresh = new Button(); bRefresh.Text = "刷新"; bRefresh.Bounds = new Rectangle(400, 6, 70, 28);
            bRefresh.Click += delegate { ReloadReplays(); };
            tp.Controls.Add(bRefresh);

            Button bBackupNow = new Button(); bBackupNow.Text = "立即备份最近一局"; bBackupNow.Bounds = new Rectangle(478, 6, 140, 28);
            bBackupNow.Click += delegate { ActBackupReplayNow(); };
            tp.Controls.Add(bBackupNow);

            Button bOpenDir = new Button(); bOpenDir.Text = "打开目录"; bOpenDir.Bounds = new Rectangle(624, 6, 78, 28);
            bOpenDir.Click += delegate { ActOpenReplayDir(); };
            tp.Controls.Add(bOpenDir);

            lvReplays = new ListView();
            lvReplays.View = View.Details;
            lvReplays.FullRowSelect = true;
            lvReplays.HideSelection = false;
            lvReplays.GridLines = true;
            lvReplays.Bounds = new Rectangle(12, 40, 690, 390);
            lvReplays.Columns.Add("录像文件", 230);
            lvReplays.Columns.Add("时长", 110);
            lvReplays.Columns.Add("大小", 100);
            lvReplays.Columns.Add("时间", 160);
            lvReplays.Columns.Add("位置", 90);
            lvReplays.DoubleClick += delegate { PlaySelectedReplay(); };
            tp.Controls.Add(lvReplays);

            Button bPlay = new Button(); bPlay.Text = "播放录像"; bPlay.Bounds = new Rectangle(12, 436, 100, 30);
            bPlay.Click += delegate { PlaySelectedReplay(); };
            tp.Controls.Add(bPlay);

            Button bReveal = new Button(); bReveal.Text = "在资源管理器中显示"; bReveal.Bounds = new Rectangle(120, 436, 150, 30);
            bReveal.Click += delegate
            {
                ReplayInfo r = SelectedReplay();
                if (r == null) return;
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + r.Path + "\""); }
                catch { }
            };
            tp.Controls.Add(bReveal);

            Button bRename = new Button(); bRename.Text = "重命名"; bRename.Bounds = new Rectangle(278, 436, 90, 30);
            bRename.Click += delegate
            {
                ReplayInfo r = SelectedReplay();
                if (r == null) return;
                string name = Prompt("新文件名:", Path.GetFileNameWithoutExtension(r.Name));
                if (string.IsNullOrEmpty(name)) return;
                try
                {
                    string dst = Path.Combine(Path.GetDirectoryName(r.Path), name + ".w3g");
                    File.Move(r.Path, dst);
                    ReloadReplays();
                }
                catch (Exception ex) { MessageBox.Show(this, "重命名失败: " + ex.Message); }
            };
            tp.Controls.Add(bRename);

            Button bDelete = new Button(); bDelete.Text = "删除"; bDelete.Bounds = new Rectangle(376, 436, 90, 30);
            bDelete.Click += delegate
            {
                ReplayInfo r = SelectedReplay();
                if (r == null) return;
                if (MessageBox.Show(this, "确定删除录像 [" + r.Name + "] ？\r\n将移入回收站。",
                    "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(r.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ReloadReplays();
                }
                catch (Exception ex) { MessageBox.Show(this, "删除失败: " + ex.Message); }
            };
            tp.Controls.Add(bDelete);

            lblReplayStatus = new Label();
            lblReplayStatus.Text = "自动备份位置: Replay\\AutoSave\\Auto_日期_时长.w3g";
            lblReplayStatus.Bounds = new Rectangle(12, 472, 690, 20);
            lblReplayStatus.ForeColor = Color.DimGray;
            tp.Controls.Add(lblReplayStatus);
        }

        ReplayInfo SelectedReplay()
        {
            if (lvReplays.SelectedItems.Count == 0) return null;
            return lvReplays.SelectedItems[0].Tag as ReplayInfo;
        }

        void PlaySelectedReplay()
        {
            ReplayInfo r = SelectedReplay();
            if (r == null) return;
            if (War3Ctl.MainWindow() != IntPtr.Zero)
            {
                MessageBox.Show(this, "魔兽正在运行，请先退出后再播放录像。");
                return;
            }
            string exe = War3Ctl.Exe(cfg.War3Path);
            if (exe == null) { MessageBox.Show(this, "找不到 War3.exe"); return; }
            try
            {
                string rel = r.Path;
                string root = cfg.War3Path.TrimEnd('\\') + "\\";
                if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(root.Length);
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(exe, "-loadfile \"" + rel + "\"");
                psi.WorkingDirectory = cfg.War3Path;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(this, "播放失败: " + ex.Message); }
        }

        void BuildTabVersion(TabPage tp)
        {
            lblCurVer = new Label();
            lblCurVer.Font = new Font(Font, FontStyle.Bold);
            lblCurVer.Bounds = new Rectangle(12, 12, 690, 22);
            tp.Controls.Add(lblCurVer);

            Label l1 = new Label(); l1.Text = "版本包目录:"; l1.Bounds = new Rectangle(12, 44, 80, 20);
            tp.Controls.Add(l1);
            txtVerDir = new TextBox();
            txtVerDir.Bounds = new Rectangle(94, 40, 450, 24);
            txtVerDir.TextChanged += delegate { if (loading) return; cfg.VerSourceDir = txtVerDir.Text; cfg.Save(); };
            tp.Controls.Add(txtVerDir);
            Button bBrowse = new Button(); bBrowse.Text = "浏览"; bBrowse.Bounds = new Rectangle(552, 38, 66, 27);
            bBrowse.Click += delegate
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.SelectedPath = cfg.VerSourceDir;
                if (d.ShowDialog(this) == DialogResult.OK) { txtVerDir.Text = d.SelectedPath; ReloadVersions(); }
            };
            tp.Controls.Add(bBrowse);
            Button bScan = new Button(); bScan.Text = "扫描"; bScan.Bounds = new Rectangle(626, 38, 66, 27);
            bScan.Click += delegate { ReloadVersions(); };
            tp.Controls.Add(bScan);

            lvVersions = new ListView();
            lvVersions.View = View.Details;
            lvVersions.FullRowSelect = true;
            lvVersions.HideSelection = false;
            lvVersions.GridLines = true;
            lvVersions.Bounds = new Rectangle(12, 72, 690, 280);
            lvVersions.Columns.Add("版本", 120);
            lvVersions.Columns.Add("状态", 130);
            lvVersions.Columns.Add("包大小", 100);
            lvVersions.Columns.Add("版本包文件", 320);
            tp.Controls.Add(lvVersions);

            btnSwitch = new Button();
            btnSwitch.Text = "切换到选中版本";
            btnSwitch.Bounds = new Rectangle(12, 360, 150, 34);
            btnSwitch.Font = new Font(Font, FontStyle.Bold);
            btnSwitch.Click += delegate { DoSwitchVersion(); };
            tp.Controls.Add(btnSwitch);

            btnDownload = new Button();
            btnDownload.Text = "从网址下载版本包...";
            btnDownload.Bounds = new Rectangle(172, 360, 170, 34);
            btnDownload.Click += delegate { DoDownloadVersion(); };
            tp.Controls.Add(btnDownload);

            Button bStore = new Button();
            bStore.Text = "打开版本仓库";
            bStore.Bounds = new Rectangle(352, 360, 130, 34);
            bStore.Click += delegate
            {
                string d = Path.Combine(cfg.War3Path, War3Version.StoreDirName);
                try { Directory.CreateDirectory(d); System.Diagnostics.Process.Start("explorer.exe", d); }
                catch { }
            };
            tp.Controls.Add(bStore);

            barVer = new ProgressBar();
            barVer.Bounds = new Rectangle(12, 402, 690, 18);
            tp.Controls.Add(barVer);

            lblVerStatus = new Label();
            lblVerStatus.Bounds = new Rectangle(12, 424, 690, 20);
            lblVerStatus.ForeColor = Color.DimGray;
            tp.Controls.Add(lblVerStatus);

            Label warn = new Label();
            warn.Text =
"切换原理：换出前会把当前版本的全部相关文件（War3.exe / Game.dll / Storm.dll / War3Patch.mpq 等）完整快照到\r\n" +
"游戏目录的 VersionStore\\<版本>\\ 里，然后再应用目标版本。因此每次切换都可完整还原，不会因为版本包缺少\r\n" +
"某个文件而损坏安装。切换前请先完全退出魔兽；若游戏装在C盘等受保护目录，请以管理员身份运行助手。";
            warn.Bounds = new Rectangle(12, 448, 690, 60);
            warn.ForeColor = Color.FromArgb(150, 90, 0);
            tp.Controls.Add(warn);
        }

        void BuildTabMisc(TabPage tp)
        {
            GroupBox gApm = new GroupBox();
            gApm.Text = "APM 实时显示";
            gApm.Bounds = new Rectangle(12, 8, 690, 66);
            tp.Controls.Add(gApm);

            chkApm = new CheckBox();
            chkApm.Text = "显示APM悬浮窗 (Ctrl+F7)";
            chkApm.Bounds = new Rectangle(14, 26, 190, 22);
            chkApm.CheckedChanged += delegate { if (loading) return; cfg.ShowApm = chkApm.Checked; cfg.Save(); };
            gApm.Controls.Add(chkApm);

            Label lox = new Label(); lox.Text = "位置 X:"; lox.Bounds = new Rectangle(230, 28, 50, 20);
            gApm.Controls.Add(lox);
            numOx = new NumericUpDown(); numOx.Minimum = 0; numOx.Maximum = 10000; numOx.Bounds = new Rectangle(282, 24, 70, 24);
            numOx.ValueChanged += delegate { if (loading) return; cfg.OverlayX = (int)numOx.Value; cfg.Save(); };
            gApm.Controls.Add(numOx);
            Label loy = new Label(); loy.Text = "Y:"; loy.Bounds = new Rectangle(360, 28, 20, 20);
            gApm.Controls.Add(loy);
            numOy = new NumericUpDown(); numOy.Minimum = 0; numOy.Maximum = 10000; numOy.Bounds = new Rectangle(382, 24, 70, 24);
            numOy.ValueChanged += delegate { if (loading) return; cfg.OverlayY = (int)numOy.Value; cfg.Save(); };
            gApm.Controls.Add(numOy);
            Label lNote = new Label(); lNote.Text = "统计最近60秒键盘+鼠标操作数";
            lNote.Bounds = new Rectangle(470, 28, 210, 20);
            lNote.ForeColor = Color.DimGray;
            gApm.Controls.Add(lNote);

            GroupBox gRem = new GroupBox();
            gRem.Text = "计时提醒 (打野/神符等周期提醒)";
            gRem.Bounds = new Rectangle(12, 80, 690, 300);
            tp.Controls.Add(gRem);

            lvRem = new ListView();
            lvRem.View = View.Details;
            lvRem.FullRowSelect = true;
            lvRem.CheckBoxes = true;
            lvRem.HideSelection = false;
            lvRem.Bounds = new Rectangle(14, 24, 660, 170);
            lvRem.Columns.Add("启用/名称", 220);
            lvRem.Columns.Add("间隔(秒)", 100);
            lvRem.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (loading) return;
                int i = e.Item.Index;
                if (i >= 0 && i < cfg.Reminders.Count)
                {
                    cfg.Reminders[i].Enabled = e.Item.Checked;
                    cfg.Save();
                }
            };
            gRem.Controls.Add(lvRem);

            Label lrn = new Label(); lrn.Text = "名称:"; lrn.Bounds = new Rectangle(14, 208, 40, 20);
            gRem.Controls.Add(lrn);
            txtRemName = new TextBox(); txtRemName.Bounds = new Rectangle(56, 204, 120, 24);
            gRem.Controls.Add(txtRemName);
            Label lrs = new Label(); lrs.Text = "间隔秒:"; lrs.Bounds = new Rectangle(190, 208, 50, 20);
            gRem.Controls.Add(lrs);
            numRemSec = new NumericUpDown(); numRemSec.Minimum = 5; numRemSec.Maximum = 3600; numRemSec.Value = 120;
            numRemSec.Bounds = new Rectangle(242, 204, 70, 24);
            gRem.Controls.Add(numRemSec);

            Button bRemAdd = new Button(); bRemAdd.Text = "添加"; bRemAdd.Bounds = new Rectangle(324, 202, 60, 27);
            bRemAdd.Click += delegate
            {
                if (string.IsNullOrEmpty(txtRemName.Text)) return;
                Reminder r = new Reminder(); r.Name = txtRemName.Text; r.Interval = (int)numRemSec.Value; r.Enabled = true;
                cfg.Reminders.Add(r);
                txtRemName.Text = "";
                ReloadReminders();
                cfg.Save();
            };
            gRem.Controls.Add(bRemAdd);

            Button bRemDel = new Button(); bRemDel.Text = "删除选中"; bRemDel.Bounds = new Rectangle(392, 202, 80, 27);
            bRemDel.Click += delegate
            {
                if (lvRem.SelectedIndices.Count == 0) return;
                cfg.Reminders.RemoveAt(lvRem.SelectedIndices[0]);
                ReloadReminders();
                cfg.Save();
            };
            gRem.Controls.Add(bRemDel);

            Label lwa = new Label(); lwa.Text = "提前提醒秒数:"; lwa.Bounds = new Rectangle(520, 208, 90, 20);
            gRem.Controls.Add(lwa);
            numWarn = new NumericUpDown(); numWarn.Minimum = 3; numWarn.Maximum = 60;
            numWarn.Bounds = new Rectangle(610, 204, 60, 24);
            numWarn.ValueChanged += delegate { if (loading) return; cfg.WarnAhead = (int)numWarn.Value; cfg.Save(); };
            gRem.Controls.Add(numWarn);

            Label lrHint = new Label();
            lrHint.Text = "用法: 游戏正式开始的瞬间按 Ctrl+F8 开始计时（再按重新计时），悬浮窗会显示游戏时间，\r\n" +
                          "到达每个周期前提前N秒开始倒数并蜂鸣提醒。也可从局内悬浮图标菜单里开始/停止。";
            lrHint.Bounds = new Rectangle(14, 240, 660, 46);
            lrHint.ForeColor = Color.DimGray;
            gRem.Controls.Add(lrHint);
        }

        void BuildTabHelp(TabPage tp)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.Dock = DockStyle.Fill;
            t.Font = new Font("Microsoft YaHei UI", 9.5F);
            t.Text =
"War3助手 — 魔兽争霸3 全能辅助 (U9WSH 复刻增强版)\r\n" +
"================================================\r\n\r\n" +
"【全局热键】(魔兽窗口前台时生效)\r\n" +
"  Ctrl+F2   改键开/关\r\n" +
"  Ctrl+F3   切换到下一个改键方案\r\n" +
"  Ctrl+F4   鼠标锁定开/关\r\n" +
"  Ctrl+F7   APM悬浮窗开/关\r\n" +
"  Ctrl+F8   计时提醒 开始/重新计时\r\n" +
"  老板键     默认Pause，任何时候可用，隐藏/恢复魔兽窗口\r\n\r\n" +
"【局内悬浮图标】\r\n" +
"  游戏运行时左上角会出现一个半透明图标：拖动可移位置，单击弹出分级菜单，\r\n" +
"  不用切出游戏就能改方案、开关APM、开始计时、伪全屏、发喊话、打开录像目录。\r\n" +
"  需要窗口化或无边框全屏模式（独占全屏下任何悬浮窗都无法显示）。\r\n\r\n" +
"【改键】\r\n" +
"  · 物品栏快捷键: 魔兽物品栏是2列x3行，默认对应小键盘左边两列(位置一一对应):\r\n" +
"        物品1→小键盘7   物品4→小键盘8\r\n" +
"        物品2→小键盘4   物品5→小键盘5\r\n" +
"        物品3→小键盘1   物品6→小键盘2\r\n" +
"    界面上每个格子右边直接标了目标键，对照游戏里的物品栏位置看即可。\r\n" +
"    用了War3自定义快捷键的话点\"目标键...\"改成你实际用的键。\r\n" +
"  · 内置\"S键改为原地不动(H)\": 默认开启。H能打断攻击后摇而S不能，DOTA里常用。\r\n" +
"    在自定义列表里显式给S设了别的映射时以你自己设的为准。\r\n" +
"  · \"物品键先选中英雄\"与双击: 连按同一个物品键(对自己施法)时不会重复插入\r\n" +
"    选择指令 —— 否则第二次按下前先选一次英雄，会把指定目标状态取消掉，\r\n" +
"    双击就永远生效不了。0.6秒内的连按算同一次，之后恢复正常。\r\n" +
"  · 注意: 商店模式生效期间物品栏键默认也被挂起。如果用滚轮进商店模式，\r\n" +
"    滚一下之后物品键要等按F1才恢复。想让物品键一直可用，\r\n" +
"    在商店模式里勾上\"物品栏键仍生效\"。\r\n" +
"  · 自定义改键: 任意键→任意键，支持鼠标中键/侧键/滚轮，也支持键盘键→鼠标左右键。\r\n" +
"  · 多方案: 不同地图用不同方案，游戏中 Ctrl+F3 或悬浮菜单一键切换。\r\n" +
"  · 改键只在魔兽窗口激活时生效，切出游戏自动失效。\r\n\r\n" +
"【打字时暂停改键】\r\n" +
"  默认开启。按回车打开聊天栏后自动暂停改键，再按回车发送或按 Esc 取消后恢复，\r\n" +
"  状态栏会显示\"打字中(已暂停)\"。否则打字时空格会被改成小键盘键，字都打不出来。\r\n" +
"  聊天栏开没开游戏不会告诉外部程序，但这个状态完全由你自己的按键决定\r\n" +
"  (回车开、回车发、Esc取消)，所以不用猜游戏状态也能准确跟踪。\r\n" +
"  兜底: 状态卡住时 30 秒没按键会自动复位，切出游戏也会复位。\r\n\r\n" +
"【快捷喊话】\r\n" +
"  按热键自动完成: 回车→输入文本→回车，支持组合键(Ctrl/Alt/Shift)。\r\n" +
"  已内置DOTA常用命令，默认绑到 Alt+1 ~ Alt+0:\r\n" +
"    Alt+1 -aphehg   Alt+2 -apemhg  Alt+3 -arem    Alt+4 -test   Alt+5 -ii\r\n" +
"    Alt+6 -di       Alt+7 -ma      Alt+8 -cson    Alt+9 -random Alt+0 -repick\r\n" +
"  英文按真实键盘按键逐字敲入(和你自己打字一样)，兼容性最好；\r\n" +
"  中文只能靠Unicode注入，部分魔兽版本可能收不到。\r\n" +
"  发不出来时，把喊话页下方的\"发送速度\"两个值调大再试。\r\n\r\n" +
"【NumLock —— 物品栏改键失效多半是这个原因】\r\n" +
"  魔兽的物品栏快捷键是小键盘 7 8 4 5 1 2。NumLock 关闭时，这几个键在系统层面\r\n" +
"  根本就是 Home/↑/←/Clear/End/↓，游戏收到的是方向键，物品栏改键自然全部落空。\r\n" +
"  助手默认勾选\"自动开启 NumLock\"，会替你把它打开 —— 键盘上没有NumLock键也没关系，\r\n" +
"  助手是用合成按键切换的。改键页会显示当前 NumLock 状态。\r\n\r\n" +
"【在商店里不想改键怎么办】\r\n" +
"  改键页有三个办法:\r\n" +
"  1. 商店模式(推荐): 勾\"启用商店模式\"，设好\"进入键\"(可以直接用滚轮上/下)和\r\n" +
"     \"恢复键\"(默认F1)。滚一下滚轮就挂起全部改键，S 就还是 S，能正常买树枝;\r\n" +
"     按 F1 重新选中英雄时自动恢复改键。状态栏和悬浮窗会提示当前处于商店模式。\r\n" +
"  2. \"按住此键临时停用改键\": 按住期间全部改键原样放行。\r\n" +
"  3. \"物品键先选中英雄(F1)\": 只影响物品栏6个键，按下时先按一下F1选中英雄。\r\n" +
"  说明: 真正去判断\"当前选中的是不是商店\"必须读游戏内存，本工具刻意不做那件事，\r\n" +
"  所以只能用上面这几种由你自己的操作来控制的方式。\r\n\r\n" +
"【改键不生效时怎么排查】\r\n" +
"  改键页最下方\"改键不生效时用这里排查\":\r\n" +
"  · 勾上\"记录诊断日志\"，进游戏按几下改键的键，回来点\"打开日志\"。\r\n" +
"    日志会写明每次按键有没有被改、发出了什么键、SendInput返回值是多少。\r\n" +
"    返回值是 0 就说明注入被系统拒绝了(基本都是权限问题，用管理员身份运行助手)。\r\n" +
"  · \"注入方式\"改成\"只发扫描码\": 有些老游戏用DirectInput读键盘，只认扫描码。\r\n" +
"  · \"重装钩子\": 手动重新挂接键鼠钩子。\r\n\r\n" +
"【血条/蓝条常显】\r\n" +
"  直接改魔兽自己的设置(注册表 Gameplay\\healthbars)，等同于在游戏里\r\n" +
"  选项→游戏性 勾上\"生命条\"。不注入游戏、不占用任何按键。\r\n" +
"  魔兽退出时会把自己的设置整个写回注册表，所以要先退出游戏再改，下次启动生效。\r\n" +
"  注: 旧版本用的是\"一直替你按住Alt\"，已经删掉了 —— 副作用太大:\r\n" +
"      按F4就成了Alt+F4直接关游戏，Alt+Enter切全屏，Alt+点击在DOTA里是发信号，\r\n" +
"      连改键注入的键都会带上Alt(滚轮改键本该发7、实际发Alt+7，编队根本选不中)。\r\n\r\n" +
"【版本切换】\r\n" +
"  扫描版本包目录里的 版本X.zip，一键切换魔兽版本。\r\n" +
"  换出前会把当前版本全部相关文件快照到 VersionStore\\<版本>\\，随时可切回来。\r\n" +
"  也支持从网址下载新的版本包。切换前必须完全退出魔兽。\r\n\r\n" +
"【录像】\r\n" +
"  每局结束自动备份 LastReplay.w3g 到 Replay\\AutoSave，文件名含日期与时长。\r\n" +
"  录像页可浏览全部录像(时长/大小/时间)、播放、重命名、删除(移入回收站)。\r\n\r\n" +
"【与原版U9WSH的差异】\r\n" +
"  本工具用系统级按键钩子实现，不注入游戏进程、不读写游戏内存:\r\n" +
"  · 兼容任意魔兽版本(1.20~1.28)，切换版本无需更新助手，无封号风险。\r\n" +
"  · 原版靠内存注入的 AI一键操作指令、JASS脚本引擎 未包含。\r\n" +
"  · 原版的显血显蓝靠内存补丁(原版自己也提示有封号风险)，本工具改用Alt方案。\r\n\r\n" +
"【提示】\r\n" +
"  · 关闭窗口 = 最小化到托盘继续工作，托盘右键→退出 才是真正退出。\r\n" +
"  · 如按键映射后游戏无反应，请把本助手也以管理员身份运行\r\n" +
"    (魔兽以管理员运行时，普通权限的助手无法向它发送按键)。\r\n" +
"  · 配置(含改键方案)保存在 %APPDATA%\\War3Helper\\config.json，\r\n" +
"    重新编译助手、删除bin目录都不会丢。喊话页有\"配置文件位置\"按钮可直接打开。\r\n" +
"    想让配置跟着程序走(绿色版)，在exe旁边放一个空的 portable.txt 即可。\r\n" +
"  · Windows 会把耗时过长的低层钩子静默摘掉(默认300ms超时)，助手带看门狗，\r\n" +
"    检测到钩子失效会自动重装，状态栏会显示重装次数。\r\n";
            tp.Controls.Add(t);
        }

        // ================= 版本切换 =================
        void ReloadVersions()
        {
            string cur = War3Version.DetectInstalledVersion(cfg.War3Path);
            versionPkgs = War3Version.Scan(cfg.VerSourceDir, cfg.War3Path);
            string matched = null;
            foreach (VersionPackage p in versionPkgs) if (p.Installed) matched = p.Name;
            lblCurVer.Text = "当前安装版本: " + cur +
                (matched != null ? "   (匹配版本包: " + matched + ")" : "   (未在版本包中找到对应项)");
            currentLabel = War3Version.CurrentLabel(cfg.War3Path, versionPkgs);

            lvVersions.Items.Clear();
            foreach (VersionPackage p in versionPkgs)
            {
                ListViewItem it = new ListViewItem(p.Name);
                string state = p.Installed ? "● 当前版本"
                    : (War3Version.HasSnapshot(cfg.War3Path, p.Name) ? "已快照，可切换" : "可切换");
                it.SubItems.Add(state);
                it.SubItems.Add(string.Format("{0:n1} MB", p.Size / 1048576.0));
                it.SubItems.Add(Path.GetFileName(p.ZipPath));
                it.Tag = p;
                if (p.Installed) it.Font = new Font(lvVersions.Font, FontStyle.Bold);
                lvVersions.Items.Add(it);
            }
            if (versionPkgs.Count == 0)
                lblVerStatus.Text = "版本包目录里没有找到 .zip 版本包。可以指向 war3ver 的 ver 目录，或用下载按钮获取。";
            else
                lblVerStatus.Text = string.Format("找到 {0} 个版本包。", versionPkgs.Count);
        }

        void SetVersionBusy(bool busy)
        {
            btnSwitch.Enabled = !busy;
            btnDownload.Enabled = !busy;
            lvVersions.Enabled = !busy;
        }

        void DoSwitchVersion()
        {
            if (lvVersions.SelectedItems.Count == 0) { MessageBox.Show(this, "请先选中一个版本"); return; }
            VersionPackage p = lvVersions.SelectedItems[0].Tag as VersionPackage;
            if (p == null) return;
            if (p.Installed) { MessageBox.Show(this, "已经是当前版本了。"); return; }
            if (War3Version.War3Running())
            {
                MessageBox.Show(this, "魔兽正在运行，请先完全退出游戏（包括对战平台里的魔兽）再切换版本。",
                    "无法切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string cur = currentLabel;
            if (MessageBox.Show(this,
                "即将把魔兽从 " + War3Version.DetectInstalledVersion(cfg.War3Path) +
                " 切换到 " + p.Name + "。\r\n\r\n" +
                "· 当前版本的全部相关文件会先快照到 VersionStore\\" + cur + "\\\r\n" +
                "· 然后应用 " + Path.GetFileName(p.ZipPath) + "\r\n" +
                "· 过程中请勿启动魔兽\r\n\r\n继续？",
                "确认切换版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetVersionBusy(true);
            barVer.Value = 0;
            List<VersionPackage> all = versionPkgs;
            string war3 = cfg.War3Path;
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                string err = War3Version.SwitchTo(war3, p, cur, all,
                    delegate(string msg, int pct)
                    {
                        try
                        {
                            BeginInvoke((Action)delegate
                            {
                                lblVerStatus.Text = msg;
                                barVer.Value = Math.Max(0, Math.Min(100, pct));
                            });
                        }
                        catch { }
                    });
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetVersionBusy(false);
                        if (err == null)
                        {
                            barVer.Value = 100;
                            lblVerStatus.Text = "切换完成，当前版本: " + War3Version.DetectInstalledVersion(war3);
                            MessageBox.Show(this, "版本切换完成！\r\n当前版本: " +
                                War3Version.DetectInstalledVersion(war3), "完成");
                        }
                        else
                        {
                            barVer.Value = 0;
                            lblVerStatus.Text = err.Replace("\r\n", " ");
                            MessageBox.Show(this, err, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        ReloadVersions();
                    });
                }
                catch { }
            });
        }

        void DoDownloadVersion()
        {
            string url = Prompt("版本包下载地址 (直链 .zip):", "");
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "请填写 http:// 或 https:// 开头的直链地址");
                return;
            }
            string ver = Prompt("这个包是哪个版本？(如 1.26):", "");
            if (string.IsNullOrEmpty(ver)) return;

            string dir = cfg.VerSourceDir;
            SetVersionBusy(true);
            barVer.Value = 0;
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                string saved;
                string err = War3Version.Download(url, dir, ver,
                    delegate(string msg, int pct)
                    {
                        try
                        {
                            BeginInvoke((Action)delegate
                            {
                                lblVerStatus.Text = msg;
                                barVer.Value = Math.Max(0, Math.Min(100, pct));
                            });
                        }
                        catch { }
                    }, out saved);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetVersionBusy(false);
                        if (err == null)
                        {
                            VersionSource vs = new VersionSource(); vs.Name = ver; vs.Url = url;
                            cfg.VersionSources.RemoveAll(delegate(VersionSource x) { return x.Name == ver; });
                            cfg.VersionSources.Add(vs);
                            cfg.Save();
                            lblVerStatus.Text = "已下载: " + Path.GetFileName(saved);
                        }
                        else
                        {
                            barVer.Value = 0;
                            lblVerStatus.Text = err;
                            MessageBox.Show(this, err, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        ReloadVersions();
                    });
                }
                catch { }
            });
        }

        // ================= 数据加载 =================
        void LoadCfgToUi()
        {
            chkRemap.Checked = cfg.RemapEnabled;
            chkCombo.Checked = cfg.ApplyToCombo;
            chkWin.Checked = cfg.BlockWinKey;
            chkChat.Checked = cfg.ChatEnabled;
            chkLock.Checked = cfg.AutoLockMouse;
            chkReplay.Checked = cfg.AutoSaveReplay;
            chkIgnoreShort.Checked = cfg.IgnoreShortReplay;
            chkApm.Checked = cfg.ShowApm;
            chkOpenGL.Checked = cfg.UseOpenGL;
            // 以注册表为准，不以我们自己的配置为准：玩家可能在游戏里自己改过
            chkAlwaysBars.Checked = War3Ctl.GetAlwaysHealthBars();
            chkIcon.Checked = cfg.InGameIcon;
            chkHeroFirst.Checked = cfg.ItemKeySelectHeroFirst;
            chkStopAsHold.Checked = cfg.BuiltinStopAsHold;
            capSuspend.Vk = cfg.SuspendKey;
            chkAutoNumLock.Checked = cfg.AutoNumLock;
            chkShopMode.Checked = cfg.ShopModeEnabled;
            chkShopWheel.Checked = cfg.ShopEnterOnWheel;
            capShopEnter.Vk = cfg.ShopEnterKey;
            capShopExit.Vk = cfg.ShopExitKey;
            chkBlockWheel.Checked = cfg.BlockWheelZoom;
            chkTyping.Checked = cfg.SuspendWhileTyping;
            numWheelGap.Value = Math.Max(0, Math.Min(2000, cfg.WheelMinIntervalMs));
            chkItemKeepShop.Checked = cfg.ActiveScheme.ItemKeysKeepInShop;
            cmbInject.SelectedIndex = cfg.InjectMode;
            chkDiag.Checked = Diag.Enabled;
            numEnterDelay.Value = Math.Max(30, Math.Min(2000, cfg.ChatEnterDelay));
            numCharDelay.Value = Math.Max(1, Math.Min(200, cfg.ChatCharDelay));
            txtPath.Text = cfg.War3Path;
            txtVerDir.Text = cfg.VerSourceDir;
            cmbLaunch.SelectedIndex = cfg.LaunchModeValue;
            cmbRes.Text = cfg.WinW + "x" + cfg.WinH;
            cmbRes.Enabled = (cfg.Launch == LaunchMode.Windowed);
            numOx.Value = Math.Max(0, Math.Min(10000, cfg.OverlayX));
            numOy.Value = Math.Max(0, Math.Min(10000, cfg.OverlayY));
            numWarn.Value = Math.Max(3, Math.Min(60, cfg.WarnAhead));
            capBoss.Vk = cfg.BossKey;
            trackOpacity.Value = cfg.OpacityPercent;
            trackIconOpacity.Value = cfg.IconOpacity;
            Opacity = cfg.OpacityPercent / 100.0;
            ReloadSchemeList();
            ReloadChats();
            ReloadReminders();
            ReloadReplays();
            ReloadVersions();
        }

        void ReloadSchemeList()
        {
            bool old = loading; loading = true;
            cmbSchemes.Items.Clear();
            foreach (Scheme s in cfg.Schemes) cmbSchemes.Items.Add(s.Name);
            cmbSchemes.SelectedIndex = cfg.CurrentScheme;
            loading = old;
            ReloadSchemeUi();
        }

        void ReloadSchemeUi()
        {
            bool old = loading; loading = true;
            for (int i = 0; i < 6; i++) capItems[i].Vk = cfg.ActiveScheme.ItemKeys[i];
            if (chkItemKeepShop != null) chkItemKeepShop.Checked = cfg.ActiveScheme.ItemKeysKeepInShop;
            loading = old;
            UpdateItemSlotLabel();
            ReloadMaps();
        }

        void ReloadMaps()
        {
            bool old = loading; loading = true;
            lvMaps.Items.Clear();
            foreach (KeyMapEntry e in cfg.ActiveScheme.Maps)
            {
                ListViewItem it = new ListViewItem(KeyNames.Name(e.Src));
                it.SubItems.Add(KeyNames.Name(e.Dst));
                it.SubItems.Add(e.KeepInShop ? "仍生效" : "挂起");
                it.Checked = e.KeepInShop;
                lvMaps.Items.Add(it);
            }
            loading = old;
        }

        void ReloadChats()
        {
            bool old = loading; loading = true;
            lvChats.Items.Clear();
            foreach (ChatItem c in cfg.Chats)
            {
                string hk = c.Key == 0 ? "(未设)" : Mods.Label(c.Mods) + KeyNames.Name(c.Key);
                ListViewItem it = new ListViewItem(hk);
                it.SubItems.Add(c.Text);
                it.SubItems.Add(c.Note == null ? "" : c.Note);
                if (c.Key == 0) it.ForeColor = Color.Gray;
                lvChats.Items.Add(it);
            }
            loading = old;
        }

        void ReloadReminders()
        {
            bool old = loading; loading = true;
            lvRem.Items.Clear();
            foreach (Reminder r in cfg.Reminders)
            {
                ListViewItem it = new ListViewItem(r.Name);
                it.SubItems.Add(r.Interval.ToString());
                it.Checked = r.Enabled;
                lvRem.Items.Add(it);
            }
            loading = old;
        }

        void ReloadReplays()
        {
            if (lvReplays == null) return;
            lvReplays.BeginUpdate();
            lvReplays.Items.Clear();
            try
            {
                foreach (ReplayInfo r in War3Ctl.ListReplays(cfg.War3Path))
                {
                    ListViewItem it = new ListViewItem(r.Name);
                    it.SubItems.Add(r.DurationText);
                    it.SubItems.Add(r.SizeText);
                    it.SubItems.Add(r.Time.ToString("yyyy-MM-dd HH:mm"));
                    it.SubItems.Add(r.IsAutoSave ? "AutoSave" : "Replay");
                    it.Tag = r;
                    lvReplays.Items.Add(it);
                }
            }
            catch { }
            lvReplays.EndUpdate();
        }

        void SaveRebuild()
        {
            cfg.Save();
            Engine.Rebuild();
        }

        void ApplyReplayWatcher()
        {
            if (replayWatcher == null) return;
            replayWatcher.IgnoreShort = cfg.IgnoreShortReplay;
            if (Directory.Exists(cfg.War3Path)) replayWatcher.Start(cfg.War3Path);
            else replayWatcher.Stop();
            if (!cfg.AutoSaveReplay) replayWatcher.Stop();
        }

        // ================= 托盘 =================
        void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Icon = IconGen.TrayIcon();
            tray.Text = "War3助手";
            tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);
            menu.Items.Add("显示主界面", null, delegate { RestoreFromTray(); });
            menu.Items.Add("启动魔兽", null, delegate
            {
                string err = War3Ctl.Launch(cfg, cfg.Launch);
                if (err != null) MessageBox.Show(err);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开录像目录", null, delegate { ActOpenReplayDir(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ActExit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1500, "War3助手", "已最小化到托盘，改键继续生效。右键托盘图标可退出。", ToolTipIcon.Info);
                return;
            }
            cfg.Save();
            Engine.Uninstall();
            War3Ctl.StopWindowCacheRefresher();
            War3Ctl.ReleaseClip();
            Native.UnregisterHotKey(Handle, HOTKEY_BOSS);
            if (iconForm != null) { iconForm.Hide(); iconForm.Dispose(); }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosing(e);
        }

        static string Prompt(string title, string defVal)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.ClientSize = new Size(380, 96);
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = f.MaximizeBox = false;
            f.Font = new Font("Microsoft YaHei UI", 9F);
            f.Icon = IconGen.AppIcon();
            TextBox tb = new TextBox();
            tb.Bounds = new Rectangle(12, 12, 356, 24);
            tb.Text = defVal;
            f.Controls.Add(tb);
            Button ok = new Button(); ok.Text = "确定"; ok.DialogResult = DialogResult.OK;
            ok.Bounds = new Rectangle(200, 52, 80, 30);
            f.Controls.Add(ok);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel;
            cancel.Bounds = new Rectangle(288, 52, 80, 30);
            f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            Util.DpiScale(f);
            return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }
}
