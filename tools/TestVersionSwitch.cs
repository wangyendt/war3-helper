using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using WshHelper;

// 沙箱测试：在临时目录里模拟一个魔兽安装 + 两个版本包，验证切换/切回不会丢文件。
// 用 build-test.ps1 编译运行，不会碰真实游戏目录。
// 测的是 ApplySwitch(纯文件逻辑)，不走 SwitchTo，这样结果不受"此刻有没有开着魔兽"影响。
static class VersionSwitchTests
{
    static int failures = 0;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + what);
        if (!cond) failures++;
    }

    static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    static string Read(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    static void MakeZip(string zipPath, Dictionary<string, string> files)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (ZipArchive a = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (KeyValuePair<string, string> kv in files)
            {
                ZipArchiveEntry e = a.CreateEntry(kv.Key);
                using (StreamWriter w = new StreamWriter(e.Open()))
                    w.Write(kv.Value);
            }
        }
    }

    public static int Run()
    {
        Console.WriteLine("===== VERSION SWITCH TESTS =====");
        string root = Path.Combine(Path.GetTempPath(), "wsh_vertest");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string war3 = Path.Combine(root, "war3");
        string ver = Path.Combine(root, "ver");
        Directory.CreateDirectory(war3);
        Directory.CreateDirectory(ver);

        Console.WriteLine("sandbox: " + root);

        // --- 模拟 1.24e 安装 (注意: 1.24 的补丁包叫 War3Patch.mpq，且不在版本包里) ---
        Write(Path.Combine(war3, "War3.exe"), "EXE-124e");
        Write(Path.Combine(war3, "Game.dll"), "GAME-124e");
        Write(Path.Combine(war3, "Storm.dll"), "STORM-124e");
        Write(Path.Combine(war3, "War3Patch.mpq"), "PATCH-124e");
        Write(Path.Combine(war3, "war3.mpq"), "SHARED-DATA");   // 非受管文件，任何时候都不该动

        // --- 两个版本包 ---
        Dictionary<string, string> p124 = new Dictionary<string, string>();
        p124["War3.exe"] = "EXE-124e";
        p124["Game.dll"] = "GAME-124e";
        p124["Storm.dll"] = "STORM-124e";
        p124["War3Patch/UI/TriggerData.txt"] = "TRIG-124e";
        MakeZip(Path.Combine(ver, "版本1.24e.zip"), p124);

        Dictionary<string, string> p127 = new Dictionary<string, string>();
        p127["War3.exe"] = "EXE-127b";
        p127["Game.dll"] = "GAME-127b";
        p127["Storm.dll"] = "STORM-127b";
        p127["war3patch.mpq"] = "PATCH-127b";
        MakeZip(Path.Combine(ver, "版本1.27b.zip"), p127);

        War3Version.Progress prog = delegate(string m, int pct) { };

        // --- 1) 扫描应能认出当前装的是 1.24e ---
        Console.WriteLine("\n[1] scan");
        List<VersionPackage> pkgs = War3Version.Scan(ver, war3);
        Check(pkgs.Count == 2, "found 2 packages (got " + pkgs.Count + ")");
        VersionPackage v124 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.24e"; });
        VersionPackage v127 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.27b"; });
        Check(v124 != null && v124.Installed, "1.24e identified as installed");
        Check(v127 != null && !v127.Installed, "1.27b not marked installed");
        string label124 = War3Version.CurrentLabel(war3, pkgs);
        Check(label124 == "1.24e", "current label = 1.24e (got " + label124 + ")");

        // --- 2) 切到 1.27b ---
        Console.WriteLine("\n[2] switch 1.24e -> 1.27b");
        string err = War3Version.ApplySwitch(war3, v127, label124, pkgs, prog);
        Check(err == null, "switch returned no error" + (err != null ? " (" + err + ")" : ""));
        Check(Read(Path.Combine(war3, "War3.exe")) == "EXE-127b", "War3.exe is 1.27b");
        Check(Read(Path.Combine(war3, "Game.dll")) == "GAME-127b", "Game.dll is 1.27b");
        Check(Read(Path.Combine(war3, "war3patch.mpq")) == "PATCH-127b", "war3patch.mpq is 1.27b");
        Check(Read(Path.Combine(war3, "war3.mpq")) == "SHARED-DATA", "unmanaged war3.mpq untouched");
        Check(File.Exists(Path.Combine(war3, "VersionStore\\1.24e\\War3Patch.mpq")),
              "1.24e War3Patch.mpq snapshotted");

        // --- 3) 切回 1.24e：关键是 War3Patch.mpq 必须从快照还原回来 ---
        Console.WriteLine("\n[3] switch back 1.27b -> 1.24e");
        pkgs = War3Version.Scan(ver, war3);
        v124 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.24e"; });
        v127 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.27b"; });
        Check(v127 != null && v127.Installed, "1.27b now identified as installed");
        string label127 = War3Version.CurrentLabel(war3, pkgs);
        err = War3Version.ApplySwitch(war3, v124, label127, pkgs, prog);
        Check(err == null, "switch back returned no error" + (err != null ? " (" + err + ")" : ""));
        Check(Read(Path.Combine(war3, "War3.exe")) == "EXE-124e", "War3.exe restored to 1.24e");
        Check(Read(Path.Combine(war3, "Game.dll")) == "GAME-124e", "Game.dll restored to 1.24e");
        Check(Read(Path.Combine(war3, "War3Patch.mpq")) == "PATCH-124e",
              "War3Patch.mpq restored from snapshot (the file the zip does NOT contain)");
        Check(!File.Exists(Path.Combine(war3, "war3patch.mpq")) ||
               Read(Path.Combine(war3, "war3patch.mpq")) == "PATCH-124e",
              "1.27b war3patch.mpq no longer active");
        Check(Read(Path.Combine(war3, "war3.mpq")) == "SHARED-DATA", "unmanaged war3.mpq still untouched");
        Check(Read(Path.Combine(war3, "War3Patch\\UI\\TriggerData.txt")) == "TRIG-124e",
              "nested War3Patch/UI file extracted");

        // --- 4) 再切一次 1.27b，确认可以反复来回 ---
        Console.WriteLine("\n[4] switch again 1.24e -> 1.27b (round trip x2)");
        pkgs = War3Version.Scan(ver, war3);
        v127 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.27b"; });
        err = War3Version.ApplySwitch(war3, v127, War3Version.CurrentLabel(war3, pkgs), pkgs, prog);
        Check(err == null, "second switch returned no error" + (err != null ? " (" + err + ")" : ""));
        Check(Read(Path.Combine(war3, "War3.exe")) == "EXE-127b", "War3.exe is 1.27b again");
        Check(Read(Path.Combine(war3, "war3patch.mpq")) == "PATCH-127b", "war3patch.mpq is 1.27b again");

        // --- 5) 安全守卫：魔兽运行时 SwitchTo 必须拒绝(只在确实开着游戏时才能验证) ---
        Console.WriteLine("\n[5] guard");
        if (War3Version.War3Running())
        {
            pkgs = War3Version.Scan(ver, war3);
            v124 = pkgs.Find(delegate(VersionPackage x) { return x.Name == "1.24e"; });
            string guard = War3Version.SwitchTo(war3, v124, War3Version.CurrentLabel(war3, pkgs), pkgs, prog);
            Check(guard != null && guard.Contains("正在运行"), "SwitchTo refuses while war3 is running");
            Check(Read(Path.Combine(war3, "War3.exe")) == "EXE-127b", "files untouched after refusal");
        }
        else
        {
            Console.WriteLine("  SKIP  war3 not running, cannot exercise the guard");
        }

        Console.WriteLine("\n" + (failures == 0 ? "VERSION TESTS PASSED" : failures + " VERSION TEST(S) FAILED"));
        try { Directory.Delete(root, true); }
        catch { }
        return failures;
    }
}
