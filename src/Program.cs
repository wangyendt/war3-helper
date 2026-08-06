using System;
using System.Threading;
using System.Windows.Forms;

namespace WshHelper
{
    public static class Util
    {
        // 高DPI屏幕下按系统缩放比例放大控件布局(字体由GDI自动按DPI渲染)
        public static void DpiScale(Form form)
        {
            float k;
            using (System.Drawing.Graphics g = form.CreateGraphics())
                k = g.DpiX / 96f;
            if (Math.Abs(k - 1f) > 0.01f)
                form.Scale(new System.Drawing.SizeF(k, k));
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mtx = new Mutex(true, "WshHelper_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("WSH魔兽助手已在运行（请查看系统托盘）。", "提示");
                    return;
                }
                try { Native.SetProcessDPIAware(); }
                catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
