using System;
using System.IO;
using System.Text;
using System.Threading;

namespace War3Helper
{
    // 诊断日志：钩子里只往环形缓冲区写一条定长记录(无分配、无IO)，
    // 由后台线程刷到文件。低层钩子回调超过 LowLevelHooksTimeout 会被系统摘掉，
    // 所以回调里绝对不能碰文件。
    public static class Diag
    {
        public struct Entry
        {
            public uint Tick;
            public byte Kind;      // 0=键盘 1=鼠标 2=备注
            public int Src;
            public int Dst;
            public bool Down;
            public bool Foreground;
            public bool Remapped;
            public uint SendResult; // SendInput 返回值，0 表示注入被拒绝
            public string Note;
        }

        const int Capacity = 8192;
        static readonly Entry[] _buf = new Entry[Capacity];
        static int _write;          // 单调递增
        static int _read;
        static volatile bool _enabled;
        static Timer _flusher;
        static string _path;

        public static bool Enabled { get { return _enabled; } }

        public static string LogPath
        {
            get
            {
                if (_path == null)
                {
                    string dir = Path.GetDirectoryName(AppConfig.ConfigPath);
                    _path = Path.Combine(dir, "diag.log");
                }
                return _path;
            }
        }

        public static void Start()
        {
            if (_enabled) return;
            _enabled = true;
            _read = _write;
            try { File.WriteAllText(LogPath, BuildHeader(), Encoding.UTF8); }
            catch { }
            _flusher = new Timer(delegate(object o) { Flush(); }, null, 1000, 1000);
        }

        public static void Stop()
        {
            if (!_enabled) return;
            _enabled = false;
            if (_flusher != null) { _flusher.Dispose(); _flusher = null; }
            Flush();
        }

        static string BuildHeader()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("War3助手 诊断日志  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("列: 时间 | 类型 | 按下的键 -> 发出的键 | 魔兽是否前台 | 是否改键 | SendInput返回值");
            sb.AppendLine("SendInput 返回 0 表示注入被系统拒绝(通常是权限问题)。");
            sb.AppendLine(new string('-', 78));
            return sb.ToString();
        }

        // 钩子里调用：只写内存，不分配、不阻塞
        public static void Log(byte kind, int src, int dst, bool down, bool fg, bool remapped, uint sendResult)
        {
            if (!_enabled) return;
            int i = Interlocked.Increment(ref _write) - 1;
            int slot = ((i % Capacity) + Capacity) % Capacity;
            _buf[slot].Tick = (uint)Environment.TickCount;
            _buf[slot].Kind = kind;
            _buf[slot].Src = src;
            _buf[slot].Dst = dst;
            _buf[slot].Down = down;
            _buf[slot].Foreground = fg;
            _buf[slot].Remapped = remapped;
            _buf[slot].SendResult = sendResult;
            _buf[slot].Note = null;
        }

        public static void Note(string text)
        {
            if (!_enabled) return;
            int i = Interlocked.Increment(ref _write) - 1;
            int slot = ((i % Capacity) + Capacity) % Capacity;
            _buf[slot].Kind = 2;
            _buf[slot].Tick = (uint)Environment.TickCount;
            _buf[slot].Note = text;
        }

        static void Flush()
        {
            int w = _write;
            if (w == _read) return;
            // 缓冲区被写爆时只保留最近的 Capacity 条
            if (w - _read > Capacity) _read = w - Capacity;

            StringBuilder sb = new StringBuilder();
            while (_read < w)
            {
                int slot = ((_read % Capacity) + Capacity) % Capacity;
                Entry e = _buf[slot];
                _read++;
                if (e.Kind == 2)
                {
                    sb.AppendLine(string.Format("[{0,10}] -- {1}", e.Tick, e.Note));
                    continue;
                }
                sb.AppendLine(string.Format("[{0,10}] {1} {2,-14} {3} {4,-14}  前台={5} 改键={6} SendInput={7}",
                    e.Tick,
                    e.Kind == 0 ? "键盘" : "鼠标",
                    KeyNames.Name(e.Src) + "(" + e.Src + ")",
                    e.Down ? "按下->" : "抬起->",
                    e.Remapped ? KeyNames.Name(e.Dst) + "(" + e.Dst + ")" : "(未改键,原样放行)",
                    e.Foreground ? "是" : "否",
                    e.Remapped ? "是" : "否",
                    e.Remapped ? e.SendResult.ToString() + (e.SendResult == 0 ? " <<< 注入失败!" : "") : "-"));
            }
            try { File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8); }
            catch { }
        }
    }
}
