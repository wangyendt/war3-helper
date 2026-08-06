using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace WshHelper
{
    public class VersionPackage
    {
        public string Name;          // "1.24e"
        public string ZipPath;       // 本地zip完整路径
        public long Size;
        public List<string> Entries = new List<string>();
        public long War3ExeSize = -1;   // 包内War3.exe的原始大小，用于识别当前版本
        public bool Installed;          // 是否与当前安装匹配
    }

    public static class Crc32
    {
        static readonly uint[] Table = Build();

        static uint[] Build()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        public static uint OfStream(Stream s)
        {
            uint crc = 0xFFFFFFFFu;
            byte[] buf = new byte[65536];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++)
                    crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint OfFile(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return OfStream(fs);
        }
    }

    // 魔兽版本检测 / 切换 / 下载
    // 切换采用"版本仓库"方案：换出前把当前版本的受管文件完整快照到 VersionStore\<版本>\，
    // 因此任何一次切换都可以完整还原，不会因为压缩包缺少某个文件(如War3Patch.mpq)而损坏安装。
    public static class War3Version
    {
        public const string StoreDirName = "VersionStore";

        // 无论压缩包里有没有，这些文件都纳入版本管理
        static readonly string[] ExtraManaged = new string[]
        {
            "War3Patch.mpq", "war3patch.mpq", "Game.dll", "Storm.dll", "War3.exe"
        };

        public static string DetectInstalledVersion(string war3Dir)
        {
            string exe = Path.Combine(war3Dir, "War3.exe");
            if (!File.Exists(exe)) exe = Path.Combine(war3Dir, "Frozen Throne.exe");
            if (!File.Exists(exe)) return "(未找到 War3.exe)";
            try
            {
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(exe);
                return string.Format("{0}.{1}.{2}", fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { return "(未知)"; }
        }

        // 当前安装对应的"版本仓库标签"。必须与版本包名一致，否则切回来时找不到快照。
        public static string CurrentLabel(string war3Dir, List<VersionPackage> packages)
        {
            if (packages != null)
                foreach (VersionPackage p in packages)
                    if (p.Installed) return p.Name;
            return "v" + DetectInstalledVersion(war3Dir);
        }

        public static string DefaultSourceDir(string war3Dir)
        {
            // 优先复用机器上已有的 war3ver 版本包目录
            try
            {
                foreach (string d in Directory.GetDirectories(war3Dir))
                {
                    string ver = Path.Combine(d, "ver");
                    if (Directory.Exists(ver) && Directory.GetFiles(ver, "*.zip").Length > 0)
                        return ver;
                }
            }
            catch { }
            return Path.Combine(war3Dir, "Versions");
        }

        // 从zip文件名提取版本号: "版本1.24e.zip" / "1.24e.zip" / "war3_1.26.zip" -> "1.24e"
        public static string VersionFromFileName(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path);
            int i = 0;
            while (i < n.Length && !char.IsDigit(n[i])) i++;
            string s = n.Substring(i).Trim();
            return s.Length > 0 ? s : n;
        }

        public static List<VersionPackage> Scan(string sourceDir, string war3Dir)
        {
            List<VersionPackage> list = new List<VersionPackage>();
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return list;

            long curSize = -1;
            uint curCrc = 0;
            bool curCrcDone = false;
            string curExe = Path.Combine(war3Dir, "War3.exe");
            if (File.Exists(curExe)) { try { curSize = new FileInfo(curExe).Length; } catch { } }

            foreach (string z in Directory.GetFiles(sourceDir, "*.zip"))
            {
                VersionPackage p = new VersionPackage();
                p.Name = VersionFromFileName(z);
                p.ZipPath = z;
                try { p.Size = new FileInfo(z).Length; }
                catch { }
                ZipArchiveEntry exeEntry = null;
                try
                {
                    using (ZipArchive a = ZipFile.OpenRead(z))
                    {
                        foreach (ZipArchiveEntry e in a.Entries)
                        {
                            if (e.FullName.EndsWith("/")) continue;
                            p.Entries.Add(e.FullName.Replace('/', '\\'));
                            if (string.Equals(Path.GetFileName(e.FullName), "War3.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                exeEntry = e;
                                p.War3ExeSize = e.Length;
                            }
                        }

                        // 先比大小(便宜)，大小一致时再解压算CRC确认，避免误判
                        if (exeEntry != null && curSize >= 0 && p.War3ExeSize == curSize)
                        {
                            if (!curCrcDone) { try { curCrc = Crc32.OfFile(curExe); } catch { } curCrcDone = true; }
                            try
                            {
                                using (Stream s = exeEntry.Open())
                                    p.Installed = (Crc32.OfStream(s) == curCrc);
                            }
                            catch { p.Installed = true; }   // 解压失败时退回按大小判断
                        }
                    }
                }
                catch { continue; }   // 损坏或非法zip直接跳过
                list.Add(p);
            }
            list.Sort(delegate(VersionPackage a, VersionPackage b) { return string.CompareOrdinal(a.Name, b.Name); });
            return list;
        }

        // 受管文件集合 = 所有版本包内出现过的相对路径 ∪ ExtraManaged
        public static HashSet<string> ManagedFiles(List<VersionPackage> all)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VersionPackage p in all)
                foreach (string e in p.Entries) set.Add(e);
            foreach (string e in ExtraManaged) set.Add(e);
            return set;
        }

        public static bool War3Running()
        {
            return War3Ctl.MainWindow() != IntPtr.Zero || IsAnyWar3Process();
        }

        static bool IsAnyWar3Process()
        {
            string[] names = new string[] { "war3", "16_war3", "frozen throne", "warcraft iii" };
            foreach (Process p in Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName.ToLowerInvariant(); }
                catch { continue; }
                foreach (string w in names) if (n == w) return true;
            }
            return false;
        }

        public delegate void Progress(string message, int percent);

        // 把当前安装的受管文件快照到 VersionStore\<label>\
        public static void Snapshot(string war3Dir, string label, HashSet<string> managed, Progress prog)
        {
            string store = Path.Combine(Path.Combine(war3Dir, StoreDirName), SanitizeName(label));
            Directory.CreateDirectory(store);
            int i = 0, total = managed.Count;
            foreach (string rel in managed)
            {
                i++;
                string src = Path.Combine(war3Dir, rel);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(store, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(src).Length)
                    continue;   // 已快照过且大小一致，跳过
                if (prog != null) prog("备份 " + rel, (int)(i * 100L / Math.Max(1, total)));
                File.Copy(src, dst, true);
            }
        }

        static string SanitizeName(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public static bool HasSnapshot(string war3Dir, string label)
        {
            string store = Path.Combine(Path.Combine(war3Dir, StoreDirName), SanitizeName(label));
            return Directory.Exists(store) && Directory.GetFiles(store, "*", SearchOption.AllDirectories).Length > 0;
        }

        // 切换到目标版本。返回null表示成功，否则返回错误信息。
        // 游戏运行时拒绝操作 —— 覆盖正在使用的 War3.exe/Game.dll 会损坏安装。
        public static string SwitchTo(string war3Dir, VersionPackage target, string currentLabel,
                                      List<VersionPackage> allPackages, Progress prog)
        {
            if (War3Running())
                return "魔兽正在运行，请先完全退出游戏再切换版本。";
            return ApplySwitch(war3Dir, target, currentLabel, allPackages, prog);
        }

        // 纯文件操作部分，不含"游戏是否在运行"的前置检查，便于沙箱测试。
        // 生产代码请走 SwitchTo。
        public static string ApplySwitch(string war3Dir, VersionPackage target, string currentLabel,
                                         List<VersionPackage> allPackages, Progress prog)
        {
            if (!Directory.Exists(war3Dir))
                return "魔兽目录不存在: " + war3Dir;
            if (!File.Exists(target.ZipPath))
                return "版本包不存在: " + target.ZipPath;

            HashSet<string> managed = ManagedFiles(allPackages);

            try
            {
                // 1) 快照当前版本（若尚未快照）
                if (prog != null) prog("备份当前版本 " + currentLabel + " ...", 0);
                Snapshot(war3Dir, currentLabel, managed, prog);

                // 2) 目标版本优先从仓库还原（仓库里是完整文件集，压缩包可能缺文件）
                string store = Path.Combine(Path.Combine(war3Dir, StoreDirName), SanitizeName(target.Name));
                HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (Directory.Exists(store))
                {
                    if (prog != null) prog("从版本仓库还原 " + target.Name + " ...", 40);
                    foreach (string f in Directory.GetFiles(store, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(store.Length).TrimStart('\\');
                        string dst = Path.Combine(war3Dir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(f, dst, true);
                        written.Add(rel);
                    }
                }

                // 3) 解压版本包（补齐仓库里没有的文件 / 首次切换）
                if (prog != null) prog("解压 " + Path.GetFileName(target.ZipPath) + " ...", 60);
                using (ZipArchive a = ZipFile.OpenRead(target.ZipPath))
                {
                    int i = 0, n = a.Entries.Count;
                    foreach (ZipArchiveEntry e in a.Entries)
                    {
                        i++;
                        if (e.FullName.EndsWith("/")) continue;
                        string rel = e.FullName.Replace('/', '\\');
                        if (written.Contains(rel)) continue;
                        string dst = Path.Combine(war3Dir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        if (prog != null) prog("解压 " + rel, 60 + (int)(i * 30L / Math.Max(1, n)));
                        e.ExtractToFile(dst, true);
                        written.Add(rel);
                    }
                }

                // 4) 目标版本没有、但当前版本有的受管文件 -> 移出安装目录(已在快照中保留)
                if (prog != null) prog("清理旧版本残留文件 ...", 92);
                string aside = Path.Combine(Path.Combine(war3Dir, StoreDirName), "_disabled");
                foreach (string rel in managed)
                {
                    if (written.Contains(rel)) continue;
                    string src = Path.Combine(war3Dir, rel);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(aside, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                }

                // 5) 落地校验：补丁包是版本的一部分，缺了游戏会起不来
                bool hasPatch = File.Exists(Path.Combine(war3Dir, "War3Patch.mpq"))
                             || File.Exists(Path.Combine(war3Dir, "war3patch.mpq"));
                if (!hasPatch)
                {
                    return "已应用 " + target.Name + " 的文件，但游戏目录里没有补丁包(War3Patch.mpq)，" +
                           "这个版本包可能不完整。\r\n\r\n上一个版本的完整文件已备份在 " +
                           StoreDirName + "\\" + SanitizeName(currentLabel) + "\\ ，" +
                           "可以把里面的文件复制回游戏目录还原。";
                }

                if (prog != null) prog("完成", 100);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                return "没有权限写入游戏目录，请用管理员身份运行助手。\r\n" + ex.Message;
            }
            catch (Exception ex)
            {
                return "切换失败: " + ex.Message +
                       "\r\n\r\n原文件已备份在 " + StoreDirName + " 目录，可手动还原。";
            }
        }

        // 下载版本包到源目录
        public static string Download(string url, string sourceDir, string versionName,
                                      Progress prog, out string savedPath)
        {
            savedPath = null;
            try
            {
                Directory.CreateDirectory(sourceDir);
                string name = string.IsNullOrEmpty(versionName)
                    ? Path.GetFileName(new Uri(url).LocalPath)
                    : "版本" + versionName + ".zip";
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    name = "版本" + (versionName ?? "download") + ".zip";
                string tmp = Path.Combine(sourceDir, name + ".part");
                string dst = Path.Combine(sourceDir, name);

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "WshHelper";
                req.Timeout = 30000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    long total = resp.ContentLength;
                    long got = 0;
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        got += n;
                        if (prog != null)
                        {
                            int pct = total > 0 ? (int)(got * 100 / total) : 0;
                            prog(string.Format("下载中 {0:n1}MB", got / 1048576.0), pct);
                        }
                    }
                }

                // 校验是不是有效zip
                try { using (ZipArchive a = ZipFile.OpenRead(tmp)) { if (a.Entries.Count == 0) throw new Exception("空压缩包"); } }
                catch (Exception ex) { File.Delete(tmp); return "下载的文件不是有效的版本包: " + ex.Message; }

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                savedPath = dst;
                if (prog != null) prog("下载完成", 100);
                return null;
            }
            catch (Exception ex)
            {
                return "下载失败: " + ex.Message;
            }
        }
    }
}
