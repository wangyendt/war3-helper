using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace War3Helper
{
    // 主界面需要向局内菜单暴露的操作
    public interface IHelperActions
    {
        void ActToggleRemap();
        void ActSelectScheme(int index);
        void ActToggleApm();
        void ActStartTimer();
        void ActStopTimer();
        void ActToggleReminder(int index);
        void ActBorderless();
        void ActRestoreBorder();
        void ActToggleLock();
        void ActSendChat(string text);
        void ActOpenReplayDir();
        void ActBackupReplayNow();
        void ActShowMain();
        void ActExit();
    }

    // 局内悬浮图标：左上角可拖动的半透明图标，单击弹出分级菜单
    public class InGameIconForm : Form
    {
        public AppConfig Cfg;
        public IHelperActions Actions;

        Point _dragOrigin;
        Point _winOrigin;
        bool _mouseDown;
        bool _dragged;
        bool _hover;
        bool _menuOpen;
        int _iconPx = 40;

        public InGameIconForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "WSH";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        public void Init()
        {
            float k;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            _iconPx = (int)Math.Round(40 * k);
            Size = new Size(_iconPx, _iconPx);
            Location = new Point(Cfg.IconX, Cfg.IconY);
            Redraw();
        }

        public void Redraw()
        {
            if (!IsHandleCreated) return;
            int alpha = (int)Math.Round(255 * (Cfg.IconOpacity / 100.0));
            if (_hover || _menuOpen) alpha = 255;
            alpha = Math.Max(30, Math.Min(255, alpha));

            using (Bitmap src = IconGen.Render(_iconPx))
            using (Bitmap pm = Premultiply(src))
                SetLayered(pm, (byte)alpha);
        }

        static Bitmap Premultiply(Bitmap src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            BitmapData sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int count = src.Width * src.Height;
                byte[] buf = new byte[count * 4];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, buf, 0, buf.Length);
                for (int i = 0; i < buf.Length; i += 4)
                {
                    int a = buf[i + 3];
                    buf[i] = (byte)(buf[i] * a / 255);
                    buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                    buf[i + 2] = (byte)(buf[i + 2] * a / 255);
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, dd.Scan0, buf.Length);
            }
            finally
            {
                src.UnlockBits(sd);
                dst.UnlockBits(dd);
            }
            return dst;
        }

        void SetLayered(Bitmap premultiplied, byte constAlpha)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = premultiplied.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                Native.SIZE size; size.cx = premultiplied.Width; size.cy = premultiplied.Height;
                Native.POINT srcPt; srcPt.x = 0; srcPt.y = 0;
                Native.POINT dstPt; dstPt.x = Left; dstPt.y = Top;
                Native.BLENDFUNCTION bf;
                bf.BlendOp = Native.AC_SRC_OVER;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = constAlpha;
                bf.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size,
                    memDc, ref srcPt, 0, ref bf, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBitmap);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Redraw();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Redraw();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            _mouseDown = true;
            _dragged = false;
            _dragOrigin = Cursor.Position;
            _winOrigin = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown) return;
            Point now = Cursor.Position;
            int dx = now.X - _dragOrigin.X, dy = now.Y - _dragOrigin.Y;
            if (!_dragged && Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
            _dragged = true;
            Location = new Point(_winOrigin.X + dx, _winOrigin.Y + dy);
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_mouseDown) return;
            _mouseDown = false;
            if (_dragged)
            {
                Cfg.IconX = Left;
                Cfg.IconY = Top;
                Cfg.Save();
            }
            else
            {
                ShowMenu();
            }
        }

        void ShowMenu()
        {
            ContextMenuStrip m = BuildMenu();
            _menuOpen = true;
            Redraw();
            m.Closed += delegate { _menuOpen = false; Redraw(); };
            m.Show(this, new Point(0, Height));
        }

        ToolStripMenuItem Item(string text, bool isChecked, EventHandler onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Checked = isChecked;
            it.CheckOnClick = false;
            if (onClick != null) it.Click += onClick;
            return it;
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = true;
            m.Font = new Font("Microsoft YaHei UI", 9F);

            // --- 改键 ---
            ToolStripMenuItem mRemap = new ToolStripMenuItem("改键");
            mRemap.DropDownItems.Add(Item(Cfg.RemapEnabled ? "已启用 (点击关闭)" : "已关闭 (点击启用)",
                Cfg.RemapEnabled, delegate { Actions.ActToggleRemap(); }));
            mRemap.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < Cfg.Schemes.Count; i++)
            {
                int idx = i;
                mRemap.DropDownItems.Add(Item("方案: " + Cfg.Schemes[i].Name, i == Cfg.CurrentScheme,
                    delegate { Actions.ActSelectScheme(idx); }));
            }
            m.Items.Add(mRemap);

            // --- 显示 ---
            ToolStripMenuItem mShow = new ToolStripMenuItem("显示");
            mShow.DropDownItems.Add(Item("APM 实时显示", Cfg.ShowApm, delegate { Actions.ActToggleApm(); }));
            // 血条常显走的是魔兽自己的设置，只能在游戏关闭时改，局内切不了
            m.Items.Add(mShow);

            // --- 计时 ---
            ToolStripMenuItem mTimer = new ToolStripMenuItem("计时提醒");
            mTimer.DropDownItems.Add(Item("开始 / 重新计时", false, delegate { Actions.ActStartTimer(); }));
            mTimer.DropDownItems.Add(Item("停止计时", false, delegate { Actions.ActStopTimer(); }));
            if (Cfg.Reminders.Count > 0)
            {
                mTimer.DropDownItems.Add(new ToolStripSeparator());
                for (int i = 0; i < Cfg.Reminders.Count; i++)
                {
                    int idx = i;
                    Reminder r = Cfg.Reminders[i];
                    mTimer.DropDownItems.Add(Item(string.Format("{0} ({1}秒)", r.Name, r.Interval),
                        r.Enabled, delegate { Actions.ActToggleReminder(idx); }));
                }
            }
            m.Items.Add(mTimer);

            // --- 窗口 ---
            ToolStripMenuItem mWin = new ToolStripMenuItem("窗口");
            mWin.DropDownItems.Add(Item("伪全屏 (去边框铺满)", false, delegate { Actions.ActBorderless(); }));
            mWin.DropDownItems.Add(Item("恢复窗口边框", false, delegate { Actions.ActRestoreBorder(); }));
            mWin.DropDownItems.Add(Item("鼠标锁定在游戏内", Cfg.AutoLockMouse, delegate { Actions.ActToggleLock(); }));
            m.Items.Add(mWin);

            // --- 喊话 ---
            if (Cfg.Chats.Count > 0)
            {
                ToolStripMenuItem mChat = new ToolStripMenuItem("快捷喊话");
                foreach (ChatItem c in Cfg.Chats)
                {
                    string text = c.Text;
                    string label = text.Length > 24 ? text.Substring(0, 24) + "..." : text;
                    if (c.Key != 0) label += "   [" + ChatKeyLabel(c) + "]";
                    mChat.DropDownItems.Add(Item(label, false, delegate { Actions.ActSendChat(text); }));
                }
                m.Items.Add(mChat);
            }

            // --- 录像 ---
            ToolStripMenuItem mRep = new ToolStripMenuItem("录像");
            mRep.DropDownItems.Add(Item("打开录像目录", false, delegate { Actions.ActOpenReplayDir(); }));
            mRep.DropDownItems.Add(Item("立即备份最近一局", false, delegate { Actions.ActBackupReplayNow(); }));
            m.Items.Add(mRep);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("打开助手主界面", false, delegate { Actions.ActShowMain(); }));
            m.Items.Add(Item("退出助手", false, delegate { Actions.ActExit(); }));
            return m;
        }

        public static string ChatKeyLabel(ChatItem c)
        {
            string s = "";
            if ((c.Mods & 1) != 0) s += "Ctrl+";
            if ((c.Mods & 2) != 0) s += "Alt+";
            if ((c.Mods & 4) != 0) s += "Shift+";
            return s + KeyNames.Name(c.Key);
        }

        // 由主界面定时调用：魔兽窗口存在时显示图标
        public void Sync()
        {
            if (Cfg == null || !IsHandleCreated) return;
            // 用缓存版本：本方法由UI定时器调用，而钩子回调也在同一线程上，
            // 这里不能做进程枚举那种耗时操作
            bool want = Cfg.InGameIcon && !War3Ctl.BossHidden && War3Ctl.CachedMainWindow() != IntPtr.Zero;
            if (want && !Visible)
            {
                Show();
                Redraw();
            }
            else if (!want && Visible && !_menuOpen)
            {
                Hide();
            }
        }
    }
}
