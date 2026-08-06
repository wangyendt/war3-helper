using System;
using System.Threading;
using System.Windows.Forms;

namespace War3Helper
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
        // 第二个实例启动时，广播这条消息把已在运行的窗口叫到前台，然后自己安静退出。
        // 以前是弹一个"已在运行"的框，用户双击后看不到窗口，会以为程序坏了。
        public static readonly uint WM_SHOW_EXISTING =
            Native.RegisterWindowMessage("War3Helper_ShowExistingWindow");

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mtx = new Mutex(true, "War3Helper_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Native.PostMessage(new IntPtr(Native.HWND_BROADCAST), WM_SHOW_EXISTING,
                                       IntPtr.Zero, IntPtr.Zero);
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
