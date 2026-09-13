using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace War3Helper
{
    // The embedded Miles module chooses a verified patch site for each exact game build.
    // Ownership belongs to the game directory, so the switch survives version changes.
    public static class HighFps
    {
        public const string ProjectUrl = "https://github.com/wangyendt/war3-helper/blob/main/docs/high-fps.md";
        internal const string Release = "native-v1";
        internal const string StateFile = ".war3helper-high-fps";
        internal const string IniFile = "War3HelperFps.ini";
        internal const string LoaderFile = "War3HelperFps.mix";
        internal static readonly object OperationGate = new object();
        static readonly string[] CurrentFiles = { IniFile, StateFile, LoaderFile };
        static readonly string[] LegacyFiles = { "WarcraftHelperLoader.mix", "WarcraftHelper.dll", "d3d9.dll", "WarcraftHelper.ini" };
        static readonly string[] LegacyHashes = {
            "B321FD2C314B8F633FDE972B85D615F87A6566DBDB9AAE8E78ECA8788442E226",
            "5B60823AC7643FD269D0D37B080F03E02511CF4D1B073C73CFC535AD53CCBF43",
            "6AA16F1921183DE41C39B0FB532B8CDBF3C9F10D748BD991239ED4807C5911ED"
        };
        internal const string LegacyRelease = "v1.18-20260124-fix";

        // Same exact versions/RVAs as native/HighFps.cpp. Check the instruction prefix
        // here on disk and again in memory before touching executable code.
        static readonly string[] Versions = { "1.20.4.6074", "1.21.0.6263", "1.22.0.6328", "1.23.0.6352",
            "1.24.1.6374", "1.24.4.6387", "1.25.1.6397", "1.26.0.6401", "1.27.0.52240", "1.27.1.7085" };
        static readonly int[] Rvas = { 0x445FC, 0x445FC, 0x61D879, 0x61F4F9, 0x62DEE9,
            0x62DF99, 0x62D5C9, 0x62D7F9, 0x5FCF9, 0x7B7A9 };
        static readonly byte[][] Prefixes = {
            new byte[] { 0x83,0xE0,0xFB,0x53,0xBA,0x11,0,0,0,0x8B,0xCE,0x89,0x46,0x44,0xE8 },
            new byte[] { 0x83,0xE0,0xFB,0x6A,0,0xBA,0x11,0,0,0,0x8B,0xCE,0x89,0x46,0x44,0xE8 },
            new byte[] { 0x83,0xE0,0xFB,0x8D,0x53,0x11,0x53,0x8B,0xCF,0x89,0x47,0x44,0xE8 }
        };

        public static bool IsEnabled(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, StateFile));
        }

        internal static bool SupportsVersion(Version v) { return v != null && Array.IndexOf(Versions, v.ToString()) >= 0; }

        static Version FileVersion(string file)
        {
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(file);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }

        internal static void RequireSupportedGame(string directory)
        {
            string exe = Path.Combine(directory, "War3.exe"), dll = Path.Combine(directory, "Game.dll");
            if (!File.Exists(exe) || !File.Exists(dll))
                throw new IOException("请先选择包含 War3.exe 和 Game.dll 的魔兽目录。");
            Version v = FileVersion(exe);
            int profile = Array.IndexOf(Versions, v.ToString());
            if (profile < 0 || !v.Equals(FileVersion(dll)))
                throw new IOException("高帧率支持内置的 1.20e 至 1.27b 共 10 个版本。当前构建 " + v +
                    " 未适配或 War3.exe 与 Game.dll 不一致，请关闭高帧率后启动。");
            if (!HasPatchSite(profile < 2 ? exe : dll, Rvas[profile], Prefixes[profile < 2 ? 0 : profile < 8 ? 1 : 2]))
                throw new IOException("游戏限帧代码与原版不符，可能已被其他补丁修改。未安装高帧率模块。");
        }

        internal static bool HasPatchSite(string file, int rva, byte[] expected)
        {
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(file)))
                {
                    Stream s = reader.BaseStream;
                    if (reader.ReadUInt16() != 0x5A4D) return false;
                    s.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    if (pe < 64 || pe > s.Length - 24) return false;
                    s.Position = pe;
                    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x14C) return false;
                    int count = reader.ReadUInt16();
                    s.Position = pe + 20;
                    int optionalSize = reader.ReadUInt16();
                    for (int i = 0; i < count; i++)
                    {
                        long section = pe + 24L + optionalSize + i * 40L;
                        if (section + 40 > s.Length) return false;
                        s.Position = section + 12;
                        uint address = reader.ReadUInt32(), length = reader.ReadUInt32(), offset = reader.ReadUInt32();
                        long delta = rva - (long)address;
                        if (delta < 0 || delta + expected.Length > length) continue;
                        if (offset + delta + expected.Length > s.Length) return false;
                        s.Position = offset + delta;
                        foreach (byte b in expected) if (reader.ReadByte() != b) return false;
                        return true;
                    }
                }
            }
            catch (EndOfStreamException) { }
            return false;
        }

        static void RequireGameClosed()
        {
            if (War3Version.War3Running()) throw new IOException("魔兽正在运行，请完全退出游戏后再开关高帧率或启动。");
        }

        internal static string BuildIni(int fps)
        {
            if (fps < 2 || fps > 2000) throw new IOException("无法读取有效的系统刷新率，未修改高帧率设置。");
            return "; Managed by War3Helper.\r\n[Options]\r\nTargetFps=" + fps.ToString(CultureInfo.InvariantCulture) + "\r\n";
        }

        // Exact previous writer, only for migration/removal of installations we owned.
        internal static string LegacyIni(int fps)
        {
            return "; Managed by War3Helper. Source: https://github.com/LoveBeforT/WarcraftHelper\r\n" +
                "[Options]\r\nUnlockFPS = true\r\nFpsLimit = true\r\nTargetFps = " + fps + "\r\n" +
                "ShowFPS = false\r\nWideScreen = false\r\nAutoFullScreen = false\r\n" +
                "UnlockMapSize = false\r\nShowHPBar = false\r\nAutoSaveReplay = false\r\n";
        }

        internal static byte[] ModuleBytes()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("War3Helper.HighFpsModule"))
            {
                if (stream == null) throw new IOException("助手缺少内置高帧率模块，请重新编译或下载完整程序。");
                using (MemoryStream buffer = new MemoryStream()) { stream.CopyTo(buffer); return buffer.ToArray(); }
            }
        }

        static byte[] IniBytes(int fps) { return Encoding.ASCII.GetBytes(BuildIni(fps)); }
        static byte[] StateBytes(int fps, string hash) { return Encoding.ASCII.GetBytes(Release + "\n" + fps + "\n" + hash + "\n"); }
        internal static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        static void RequireOrdinaryFile(string file)
        {
            if (Directory.Exists(file) || (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("模块路径被目录或链接占用，未修改: " + file);
        }

        class State { public int Fps; public bool Legacy; public string ModuleHash; }
        static State ReadState(string directory)
        {
            string path = Path.Combine(directory, StateFile);
            RequireOrdinaryFile(path);
            State state = new State();
            if (!File.Exists(path)) return state;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length >= 2 && int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out state.Fps) &&
                state.Fps >= 2 && state.Fps <= 2000)
            {
                if (lines.Length == 2 && lines[0] == LegacyRelease) { state.Legacy = true; return state; }
                if (lines.Length == 3 && lines[0] == Release && System.Text.RegularExpressions.Regex.IsMatch(lines[2], "\\A[0-9A-F]{64}\\z"))
                { state.ModuleHash = lines[2]; return state; }
            }
            throw new IOException("高帧率管理记录损坏或版本不兼容，已保留现有文件: " + path);
        }

        static State ValidateOwnedFiles(string directory, bool requireComplete)
        {
            State state = ReadState(directory);
            foreach (string name in CurrentFiles)
            {
                if (name == StateFile) continue;
                string expected = state.Fps == 0 || state.Legacy ? null : name == LoaderFile ? state.ModuleHash : Hash(IniBytes(state.Fps));
                ValidateFile(directory, name, expected, requireComplete && !state.Legacy);
            }
            for (int i = 0; i < LegacyFiles.Length; i++)
            {
                string expected = !state.Legacy ? null : i < 3 ? LegacyHashes[i] : Hash(Encoding.ASCII.GetBytes(LegacyIni(state.Fps)));
                ValidateFile(directory, LegacyFiles[i], expected, requireComplete && state.Legacy);
            }
            return state;
        }

        static void ValidateFile(string directory, string name, string expected, bool required)
        {
            string file = Path.Combine(directory, name);
            RequireOrdinaryFile(file);
            if (!File.Exists(file)) { if (required) throw new IOException("高帧率模块缺失，请关闭再开启以修复: " + name); return; }
            if (expected == null) throw new IOException("游戏目录已有 " + name + "，为保留现有插件，未安装或修改高帧率模块。");
            if (Hash(File.ReadAllBytes(file)) != expected) throw new IOException("高帧率文件已被修改，未覆盖或删除: " + file);
        }

        static void CheckOtherUnlocker(string directory)
        {
            foreach (string subdir in new string[] { "", Path.Combine("Redist", "Miles") })
                foreach (string name in new string[] { "FPSUnlocker.mix", "FPSUnlocker.m3d", "FPSUnlocker.dll", LoaderFile, "WarcraftHelperLoader.mix" })
                {
                    if (subdir.Length == 0 && (name == LoaderFile || name == "WarcraftHelperLoader.mix")) continue;
                    if (File.Exists(Path.Combine(Path.Combine(directory, subdir), name)))
                        throw new IOException("发现其他帧率解锁模块 " + name + "，请先移除旧模块以免重复加载。");
                }
        }

        public static string SetEnabled(string directory, bool enabled)
        {
            if (!Monitor.TryEnter(OperationGate)) return "正在处理启动、版本切换或高帧率设置，请稍候。";
            try
            {
                RequireGameClosed();
                if (!enabled) { RemoveFiles(directory); return null; }
                RequireSupportedGame(directory);
                int fps = War3Ctl.GetSystemRefreshRate();
                InstallFiles(directory, ModuleBytes(), fps, null);
                Diag.Note("已开启高帧率，目标 " + fps + " FPS: " + directory);
                return null;
            }
            catch (Exception ex) { return "高帧率设置失败: " + ex.Message; }
            finally { Monitor.Exit(OperationGate); }
        }

        internal static string PrepareForLaunch(string directory, int fps)
        {
            if (!IsEnabled(directory)) return null;
            try
            {
                RequireSupportedGame(directory);
                UpdateTarget(directory, fps);
                Diag.Note("高帧率模块已就绪，目标 " + fps + " FPS");
                return null;
            }
            catch (Exception ex) { return "高帧率模块未就绪: " + ex.Message; }
        }

        internal static void PrepareForSwitch(string directory)
        {
            if (!IsEnabled(directory)) return;
            State state = ValidateOwnedFiles(directory, true);
            // Replace the previous 1.27-only module before changing game files. The new
            // module chooses a build on each launch and refuses unknown builds.
            InstallFiles(directory, ModuleBytes(), state.Fps, null);
        }

        internal static void InstallFiles(string directory, byte[] module, int fps, Action<string> beforeWrite)
        {
            if (Hash(module) != Hash(ModuleBytes())) throw new IOException("内置高帧率模块校验失败，未安装。");
            byte[] ini = IniBytes(fps);
            State state = ValidateOwnedFiles(directory, false);
            CheckOtherUnlocker(directory);
            Dictionary<string, byte[]> desired = new Dictionary<string, byte[]>();
            if (state.Legacy) foreach (string name in LegacyFiles) desired.Add(name, null);
            desired.Add(IniFile, ini);
            desired.Add(StateFile, StateBytes(fps, Hash(module)));
            desired.Add(LoaderFile, module);
            ApplyFiles(directory, desired, beforeWrite);
        }

        internal static void UpdateTarget(string directory, int fps)
        {
            State previous = ValidateOwnedFiles(directory, true);
            CheckOtherUnlocker(directory);
            byte[] module = ModuleBytes();
            if (previous.Legacy || previous.ModuleHash != Hash(module)) { InstallFiles(directory, module, fps, null); return; }
            if (previous.Fps == fps) return;
            Dictionary<string, byte[]> desired = new Dictionary<string, byte[]>();
            desired.Add(IniFile, IniBytes(fps));
            desired.Add(StateFile, StateBytes(fps, previous.ModuleHash));
            ApplyFiles(directory, desired, null);
        }

        internal static void RemoveFiles(string directory)
        {
            if (!IsEnabled(directory)) return;
            State state = ValidateOwnedFiles(directory, false);
            Dictionary<string, byte[]> desired = new Dictionary<string, byte[]>();
            foreach (string name in state.Legacy ? LegacyFiles : CurrentFiles) desired.Add(name, null);
            desired[StateFile] = null;
            ApplyFiles(directory, desired, null);
        }

        static void ApplyFiles(string directory, Dictionary<string, byte[]> desired, Action<string> beforeWrite)
        {
            Dictionary<string, byte[]> original = new Dictionary<string, byte[]>();
            foreach (string name in desired.Keys)
            {
                string file = Path.Combine(directory, name);
                original.Add(name, File.Exists(file) ? File.ReadAllBytes(file) : null);
            }
            List<string> changed = new List<string>();
            List<string> order = new List<string>(LegacyFiles);
            order.AddRange(CurrentFiles);
            if (desired.ContainsKey(LoaderFile) && desired[LoaderFile] == null)
            { order.Remove(LoaderFile); order.Insert(0, LoaderFile); }
            try
            {
                foreach (string name in order)
                {
                    if (!desired.ContainsKey(name)) continue;
                    if (beforeWrite != null) beforeWrite(name);
                    string file = Path.Combine(directory, name);
                    changed.Add(name);
                    if (desired[name] == null) File.Delete(file); else WriteAtomic(file, desired[name]);
                }
            }
            catch (Exception failure)
            {
                List<string> restoreErrors = new List<string>();
                changed.Reverse();
                foreach (string name in changed)
                    try
                    {
                        string file = Path.Combine(directory, name);
                        if (original[name] == null) File.Delete(file); else WriteAtomic(file, original[name]);
                    }
                    catch (Exception ex) { restoreErrors.Add(name + ": " + ex.Message); }
                if (restoreErrors.Count > 0)
                    throw new IOException(failure.Message + "；部分文件恢复失败，请暂勿启动游戏: " + string.Join("; ", restoreErrors.ToArray()), failure);
                throw;
            }
        }

        static void WriteAtomic(string path, byte[] data)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
