using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using War3Helper;

static class HighFpsTests
{
    static int failures;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetPrivateProfileString(string section, string key, string fallback,
        StringBuilder output, uint size, string path);

    static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + name);
        if (!condition) failures++;
    }

    static void Reject(Action action, string name)
    {
        try { action(); Check(false, name); }
        catch (IOException) { Check(true, name); }
    }

    static string ReadIni(string directory, string key)
    {
        StringBuilder text = new StringBuilder(512);
        GetPrivateProfileString("Options", key, "MISSING", text, 512, Path.Combine(directory, HighFps.IniFile));
        return text.ToString();
    }

    static Dictionary<string, string> Snapshot(string root)
    {
        Dictionary<string, string> result = new Dictionary<string, string>();
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            result.Add(file.Substring(root.Length), HighFps.Hash(File.ReadAllBytes(file)));
        return result;
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (KeyValuePair<string, string> entry in a)
            if (!b.ContainsKey(entry.Key) || b[entry.Key] != entry.Value) return false;
        return true;
    }

    public static int Run(string packagePath)
    {
        failures = 0;
        Console.WriteLine("\n===== HIGH FPS TESTS =====");
        foreach (string version in new string[] { "1.20.4.6074", "1.21.0.6263", "1.22.0.6328", "1.23.0.6352",
            "1.24.1.6374", "1.24.4.6387", "1.25.1.6397", "1.26.0.6401", "1.27.0.52240", "1.27.1.7085" })
            Check(HighFps.SupportsVersion(new Version(version)), "supported exact build: " + version);
        Check(!HighFps.SupportsVersion(new Version(1, 28, 0, 7205)) &&
              !HighFps.SupportsVersion(new Version(1, 27, 0, 1)), "unknown builds refused");
        Reject(delegate { HighFps.BuildIni(0); }, "unknown refresh rate is rejected");
        Reject(delegate { HighFps.BuildIni(1); }, "driver default rate is rejected");

        string root = Path.Combine(Path.GetTempPath(), "war3helper-fps-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Game.dll"), "original game bytes");
            File.WriteAllText(Path.Combine(root, "War3.exe"), "original executable bytes");
            Directory.CreateDirectory(Path.Combine(root, "Maps"));
            File.WriteAllText(Path.Combine(root, "Maps", "player.w3x"), "player map");
            Dictionary<string, string> baseline = Snapshot(root);
            Check(!HighFps.IsEnabled(root), "existing installations default to disabled without config migration");
            Check(HighFps.PrepareForLaunch(root, 0) == null, "disabled feature does not block ordinary launch");
            Reject(delegate { HighFps.InstallFiles(root, new byte[] { 1, 2, 3 }, 240, null); }, "invalid package rejected");
            Check(Same(baseline, Snapshot(root)), "rejected package leaves installation untouched");

            // Independent Win32 INI parser, as used by the native module.
            File.WriteAllText(Path.Combine(root, HighFps.IniFile), HighFps.BuildIni(240), Encoding.ASCII);
            Check(ReadIni(root, "TargetFps") == "240", "INI is readable by Windows and selects 240 FPS");
            HighFps.RemoveFiles(root);
            Check(File.Exists(Path.Combine(root, HighFps.IniFile)), "unowned INI is not removed");
            File.Delete(Path.Combine(root, HighFps.IniFile));

            byte[] package = HighFps.ModuleBytes();
            Check(package.Length > 4096 && package[0] == 'M' && package[1] == 'Z', "native module embedded in executable");
            foreach (string conflicting in new string[] { "d3d9.dll", "WarcraftHelper.dll", HighFps.LoaderFile, HighFps.IniFile, "FPSUnlocker.mix" })
            {
                string path = Path.Combine(root, conflicting);
                File.WriteAllText(path, "existing third party plugin");
                Dictionary<string, string> before = Snapshot(root);
                Reject(delegate { HighFps.InstallFiles(root, package, 240, null); }, "conflict rejected: " + conflicting);
                Check(Same(before, Snapshot(root)), "conflict preserves all files: " + conflicting);
                File.Delete(path);
            }
            Reject(delegate
            {
                HighFps.InstallFiles(root, package, 240, delegate(string name)
                {
                    if (name == HighFps.LoaderFile) throw new IOException("simulated final write failure");
                });
            }, "installation failure is surfaced");
            Check(Same(baseline, Snapshot(root)), "partial install rolls back all generated files and state");

            HighFps.InstallFiles(root, package, 240, null);
            Check(HighFps.IsEnabled(root) && File.Exists(Path.Combine(root, HighFps.LoaderFile)) &&
                  ReadIni(root, "TargetFps") == "240", "install produces loader, state and 240 FPS config");
            Dictionary<string, string> installed = Snapshot(root);
            HighFps.InstallFiles(root, package, 240, null);
            Check(Same(installed, Snapshot(root)), "repeated installation is idempotent");
            Reject(delegate
            {
                HighFps.InstallFiles(root, package, 144, delegate(string name)
                {
                    if (name == HighFps.LoaderFile) throw new IOException("simulated repair failure");
                });
            }, "failed repair is surfaced");
            Check(Same(installed, Snapshot(root)), "failed repair restores previous 240 FPS installation");
            HighFps.UpdateTarget(root, 144);
            Check(ReadIni(root, "TargetFps") == "144", "system change is reflected in FPS target");
            Dictionary<string, string> at144 = Snapshot(root);
            Reject(delegate { HighFps.UpdateTarget(root, 0); }, "unknown display cannot replace working target");
            Check(Same(at144, Snapshot(root)), "invalid target preserves files and state");

            string ini = Path.Combine(root, HighFps.IniFile);
            byte[] originalIni = File.ReadAllBytes(ini);
            File.AppendAllText(ini, "; user change");
            Dictionary<string, string> modified = Snapshot(root);
            Reject(delegate { HighFps.RemoveFiles(root); }, "changed INI blocks deletion of all managed files");
            Reject(delegate { HighFps.UpdateTarget(root, 240); }, "changed INI is not overwritten on launch");
            Check(Same(modified, Snapshot(root)), "user edits preserved after refused operations");
            File.WriteAllBytes(ini, originalIni);

            File.Delete(Path.Combine(root, HighFps.LoaderFile));
            Reject(delegate { HighFps.UpdateTarget(root, 240); }, "missing loader prevents a false ready status");
            HighFps.InstallFiles(root, package, 240, null);
            Check(File.Exists(Path.Combine(root, HighFps.LoaderFile)), "re-enable repairs a missing owned loader");
            HighFps.RemoveFiles(root);
            Check(!HighFps.IsEnabled(root) && Same(baseline, Snapshot(root)), "disable restores original game, maps and file set exactly");
            HighFps.RemoveFiles(root);
            Check(Same(baseline, Snapshot(root)), "repeated disable is harmless");

            HighFps.InstallFiles(root, package, 240, null);
            string zipPath = Path.Combine(root, "target-version.zip");
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (string name in new string[] { "War3.exe", "Game.dll", "War3Patch.mpq" })
                    using (StreamWriter writer = new StreamWriter(archive.CreateEntry(name).Open()))
                        writer.Write("target version " + name);
            VersionPackage target = new VersionPackage();
            target.Name = "test-version"; target.ZipPath = zipPath;
            target.Entries.AddRange(new string[] { "War3.exe", "Game.dll", "War3Patch.mpq" });
            string switchError = War3Version.ApplySwitch(root, target, "old-version", new List<VersionPackage> { target }, null);
            Check(switchError == null && HighFps.IsEnabled(root) && File.Exists(Path.Combine(root, HighFps.LoaderFile)) && ReadIni(root, "TargetFps") == "240",
                  "version switch preserves enabled module and target");
            Check(File.ReadAllText(Path.Combine(root, "Maps", "player.w3x")) == "player map", "version switch preserves player map");
            Check(HighFps.PrepareForLaunch(root, 240) != null, "unsupported or malformed game cannot report FPS ready");
            HighFps.RemoveFiles(root);
            if (!string.IsNullOrEmpty(packagePath)) TestMigration(root, packagePath, package);
        }
        catch (Exception ex) { Check(false, "unexpected error: " + ex); }
        finally { Directory.Delete(root, true); }
        return failures;
    }

    static void TestMigration(string root, string zipPath, byte[] module)
    {
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            foreach (string name in new string[] { "d3d9.dll", "WarcraftHelper.dll", "WarcraftHelperLoader.mix" })
                archive.GetEntry(name).ExtractToFile(Path.Combine(root, name), true);
        File.WriteAllText(Path.Combine(root, "WarcraftHelper.ini"), HighFps.LegacyIni(240), Encoding.ASCII);
        File.WriteAllText(Path.Combine(root, HighFps.StateFile), HighFps.LegacyRelease + "\n240\n", Encoding.ASCII);
        Dictionary<string, string> before = Snapshot(root);
        string legacyIni = Path.Combine(root, "WarcraftHelper.ini");
        File.AppendAllText(legacyIni, "; user change");
        Dictionary<string, string> edited = Snapshot(root);
        Reject(delegate { HighFps.PrepareForSwitch(root); }, "edited legacy module blocks migration before switching");
        Check(Same(edited, Snapshot(root)), "migration preserves edited legacy files");
        File.WriteAllText(legacyIni, HighFps.LegacyIni(240), Encoding.ASCII);
        Reject(delegate { HighFps.InstallFiles(root, module, 240, delegate(string name) {
            if (name == HighFps.LoaderFile) throw new IOException("migration failure");
        }); }, "migration write failure is surfaced");
        Check(Same(before, Snapshot(root)), "failed migration restores the complete previous module");
        HighFps.PrepareForSwitch(root);
        Check(HighFps.IsEnabled(root) && File.Exists(Path.Combine(root, HighFps.LoaderFile)) &&
            !File.Exists(Path.Combine(root, "WarcraftHelperLoader.mix")) && !File.Exists(Path.Combine(root, "d3d9.dll")),
            "switch upgrades owned 1.27 module and retains enabled state");
        HighFps.RemoveFiles(root);
    }

    public static int VerifyGameBuilds(string root)
    {
        failures = 0;
        Console.WriteLine("\n===== REAL GAME BUILD PREFLIGHT =====");
        foreach (string directory in Directory.GetDirectories(root))
        {
            if (!File.Exists(Path.Combine(directory, "War3.exe"))) continue;
            try { HighFps.RequireSupportedGame(directory); Check(true, Path.GetFileName(directory) + " exact version and PE instruction prefix"); }
            catch (Exception ex) { Check(false, Path.GetFileName(directory) + ": " + ex.Message); }
        }
        return failures;
    }
}
