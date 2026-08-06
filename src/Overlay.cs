using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace War3Helper
{
    // 置顶穿透悬浮窗：APM显示 + 计时提醒 + 临时消息
    public class OverlayForm : Form
    {
        Label _lbl;
        System.Windows.Forms.Timer _timer;
        string _flashText = "";
        uint _flashUntil = 0;
        int _timerStart = 0;          // Environment.TickCount, 0=未计时
        readonly System.Collections.Generic.Dictionary<Reminder, int> _lastCycle =
            new System.Collections.Generic.Dictionary<Reminder, int>();

        public AppConfig Cfg;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            Size = new Size(340, 130);

            _lbl = new Label();
            _lbl.AutoSize = true;
            _lbl.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            _lbl.ForeColor = Color.FromArgb(80, 255, 120);
            _lbl.BackColor = Color.Black;
            _lbl.Location = new Point(0, 0);
            Controls.Add(_lbl);

            Util.DpiScale(this);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        public void FlashMessage(string text, int ms)
        {
            _flashText = text;
            _flashUntil = (uint)Environment.TickCount + (uint)ms;
            RefreshNow();
        }

        public void ResetGameTimer()
        {
            _timerStart = Environment.TickCount;
            _lastCycle.Clear();
            FlashMessage("计时开始", 1500);
        }

        public void StopGameTimer()
        {
            _timerStart = 0;
        }

        public bool TimerRunning { get { return _timerStart != 0; } }

        void OnTick(object s, EventArgs e) { RefreshNow(); }

        void RefreshNow()
        {
            if (Cfg == null) return;
            uint now = (uint)Environment.TickCount;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            bool war3fg = Engine.War3Foreground();
            bool anything = false;

            if (Cfg.ShowApm && war3fg)
            {
                sb.AppendLine("APM " + Engine.CurrentApm());
                anything = true;
            }

            if (_timerStart != 0)
            {
                int elapsed = (Environment.TickCount - _timerStart) / 1000;
                sb.AppendLine(string.Format("{0:D2}:{1:D2}", elapsed / 60, elapsed % 60));
                anything = true;
                foreach (Reminder r in Cfg.Reminders)
                {
                    if (!r.Enabled || r.Interval <= 0) continue;
                    int cycle = elapsed / r.Interval;
                    int last;
                    _lastCycle.TryGetValue(r, out last);
                    if (cycle > last && elapsed >= r.Interval)
                    {
                        _lastCycle[r] = cycle;
                        Beep();
                        FlashMessage(r.Name + " 时间到!", 3000);
                    }
                    int next = (cycle + 1) * r.Interval;
                    int remain = next - elapsed;
                    if (remain <= Cfg.WarnAhead)
                    {
                        sb.AppendLine(string.Format("{0} {1}s", r.Name, remain));
                        anything = true;
                    }
                }
            }

            if (_flashUntil > now && !string.IsNullOrEmpty(_flashText))
            {
                sb.AppendLine(_flashText);
                anything = true;
            }

            _lbl.Text = sb.ToString().TrimEnd();
            Location = new Point(Cfg.OverlayX, Cfg.OverlayY);
            bool shouldShow = anything && !War3Ctl.BossHidden;
            if (shouldShow && !Visible) Show();
            else if (!shouldShow && Visible) Hide();
        }

        static void Beep()
        {
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try { Console.Beep(1200, 160); Console.Beep(1500, 200); }
                catch { }
            });
        }
    }
}
