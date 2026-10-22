using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace War3Helper
{
    // Independent from HighFps: opting in must not unlock FPS or replace other plugins.
    internal static class BackgroundRun
    {
        internal const string LoaderFile = "War3HelperBackground.mix";
        internal const string StateFile = ".war3helper-background";
        const string Release = "background-v1";

        internal static byte[] ModuleBytes()
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("War3Helper.BackgroundModule"))
            {
                if (source == null) throw new IOException("助手缺少后台运行模块，请重新编译或下载完整程序。");
                using (MemoryStream buffer = new MemoryStream()) { source.CopyTo(buffer); return buffer.ToArray(); }
            }
        }

        internal static string ValidateMode(bool enabled, LaunchMode mode)
        {
            return enabled && mode == LaunchMode.ExclusiveFullscreen
                ? "切换应用不暂停仅支持窗口化或无边框全屏，请更改启动模式或取消勾选。" : null;
        }

        internal static void RequireSupportedGame(string directory)
        {
            Version previous = null;
            foreach (string name in new string[] { "War3.exe", "Game.dll" })
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) throw new IOException("找不到 " + name + "，请检查魔兽路径。");
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                Version version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                if (!HighFps.SupportsVersion(version) || (previous != null && !previous.Equals(version)))
                    throw new IOException("后台运行模块仅支持内置的 1.20e 至 1.27b 共 10 个构建，且 War3.exe 与 Game.dll 必须一致。请取消勾选后启动此版本。");
                previous = version;
            }
        }

        // Caller holds HighFps.OperationGate and has checked that the game is closed.
        internal static string PrepareForLaunch(string directory, bool enabled, LaunchMode mode)
        {
            string modeError = ValidateMode(enabled, mode);
            if (modeError != null) return modeError;
            try
            {
                if (enabled) RequireSupportedGame(directory);
                ApplyFiles(directory, enabled, null);
                return null;
            }
            catch (Exception ex) { return "切换应用不暂停设置失败: " + ex.Message; }
        }

        static void RequireOrdinaryFile(string path)
        {
            if (Directory.Exists(path) || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("模块路径被目录或链接占用，未修改: " + path);
        }

        internal static void ApplyFiles(string directory, bool enabled, Action<string> beforeWrite)
        {
            string statePath = Path.Combine(directory, StateFile), modulePath = Path.Combine(directory, LoaderFile);
            RequireOrdinaryFile(statePath);
            RequireOrdinaryFile(modulePath);
            bool owned = File.Exists(statePath);
            if (!owned && !enabled) return; // Never remove a plugin without our ownership record.
            string expectedHash = null;
            if (owned)
            {
                string[] lines = File.ReadAllLines(statePath);
                if (lines.Length != 2 || lines[0] != Release ||
                    !System.Text.RegularExpressions.Regex.IsMatch(lines[1], "\\A[0-9A-F]{64}\\z"))
                    throw new IOException("后台运行管理记录损坏，已保留现有文件: " + statePath);
                expectedHash = lines[1];
            }
            if (File.Exists(modulePath) && (!owned || HighFps.Hash(File.ReadAllBytes(modulePath)) != expectedHash))
                throw new IOException("已有同名模块或模块已被修改，未覆盖或删除: " + modulePath);

            byte[] module = enabled ? ModuleBytes() : null;
            byte[] state = enabled ? Encoding.ASCII.GetBytes(Release + "\n" + HighFps.Hash(module) + "\n") : null;
            // Install ownership before executable; remove executable before ownership.
            string[] paths = enabled ? new string[] { statePath, modulePath } : new string[] { modulePath, statePath };
            byte[][] data = enabled ? new byte[][] { state, module } : new byte[][] { null, null };
            byte[][] old = new byte[2][];
            for (int i = 0; i < paths.Length; i++) old[i] = File.Exists(paths[i]) ? File.ReadAllBytes(paths[i]) : null;
            int changed = -1;
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    if (beforeWrite != null) beforeWrite(Path.GetFileName(paths[i]));
                    changed = i;
                    WriteAtomic(paths[i], data[i]);
                }
            }
            catch (Exception failure)
            {
                List<string> errors = new List<string>();
                for (int i = changed; i >= 0; i--)
                    try { WriteAtomic(paths[i], old[i]); }
                    catch (Exception ex) { errors.Add(paths[i] + ": " + ex.Message); }
                if (errors.Count > 0) throw new IOException(failure.Message + "；回滚失败，请暂勿启动游戏: " + string.Join("; ", errors.ToArray()), failure);
                throw;
            }
        }

        static void WriteAtomic(string path, byte[] bytes)
        {
            if (bytes == null) { File.Delete(path); return; }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
