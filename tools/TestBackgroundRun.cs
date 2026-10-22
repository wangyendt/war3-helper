using System;
using System.IO;
using System.Web.Script.Serialization;
using War3Helper;

static class BackgroundRunTests
{
    static int failures;
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
    public static int Run()
    {
        failures = 0;
        Console.WriteLine("\n===== BACKGROUND RUN TESTS =====");
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Check(!serializer.Deserialize<AppConfig>("{\"UseOpenGL\":true}").RunInBackground,
            "old configurations default to disabled");
        AppConfig cfg = new AppConfig();
        cfg.SetDefaults();
        Check(!cfg.RunInBackground, "new configurations default to disabled");
        cfg.RunInBackground = true;
        Check(serializer.Deserialize<AppConfig>(serializer.Serialize(cfg)).RunInBackground,
            "checkbox setting round-trips through JSON");
        cfg.SetDefaults();
        Check(!cfg.RunInBackground, "reset defaults clears background mode");
        Check(BackgroundRun.ValidateMode(true, LaunchMode.ExclusiveFullscreen) != null,
            "exclusive fullscreen is refused when enabled");
        Check(BackgroundRun.ValidateMode(false, LaunchMode.ExclusiveFullscreen) == null &&
            BackgroundRun.ValidateMode(true, LaunchMode.Windowed) == null &&
            BackgroundRun.ValidateMode(true, LaunchMode.BorderlessFullscreen) == null,
            "disabled and windowed launch modes allowed");

        string root = Path.Combine(Path.GetTempPath(), "war3helper-background-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string module = Path.Combine(root, BackgroundRun.LoaderFile), state = Path.Combine(root, BackgroundRun.StateFile);
        string fps = Path.Combine(root, HighFps.LoaderFile);
        try
        {
            File.WriteAllText(fps, "existing FPS module");
            Check(BackgroundRun.PrepareForLaunch(root, false, LaunchMode.ExclusiveFullscreen) == null,
                "disabled feature needs no game validation");
            Check(BackgroundRun.PrepareForLaunch(root, true, LaunchMode.Windowed) != null && !File.Exists(module),
                "missing game rejected before installation");
            File.WriteAllText(module, "third party");
            BackgroundRun.ApplyFiles(root, false, null);
            Check(File.ReadAllText(module) == "third party", "disabled feature leaves unowned modules untouched");
            Reject(delegate { BackgroundRun.ApplyFiles(root, true, null); }, "same-name plugin not overwritten");
            File.Delete(module);
            BackgroundRun.ApplyFiles(root, true, null);
            byte[] embedded = BackgroundRun.ModuleBytes();
            Check(embedded.Length > 4096 && embedded[0] == 'M' && embedded[1] == 'Z', "x86 module embedded");
            string moduleHash = HighFps.Hash(File.ReadAllBytes(module));
            string savedState = File.ReadAllText(state);
            Check(moduleHash == HighFps.Hash(embedded) && savedState.Contains(moduleHash), "installation and ownership hashes agree");
            BackgroundRun.ApplyFiles(root, true, null);
            Check(HighFps.Hash(File.ReadAllBytes(module)) == moduleHash, "repeated enable is safe");
            // Failed disable restores the removed executable before returning an error.
            Reject(delegate { BackgroundRun.ApplyFiles(root, false, delegate(string name) {
                if (name == BackgroundRun.StateFile) throw new IOException("simulated write failure");
            }); }, "failed disable reported");
            Check(HighFps.Hash(File.ReadAllBytes(module)) == moduleHash && File.ReadAllText(state) == savedState,
                "failed disable rolls back the complete installation");
            File.WriteAllText(module, "modified");
            Reject(delegate { BackgroundRun.ApplyFiles(root, false, null); }, "modified module not deleted");
            Reject(delegate { BackgroundRun.ApplyFiles(root, true, null); }, "modified module not overwritten");
            File.WriteAllBytes(module, embedded);
            File.WriteAllText(state, "corrupt");
            Reject(delegate { BackgroundRun.ApplyFiles(root, false, null); }, "corrupt ownership record refused");
            File.WriteAllText(state, savedState);
            File.Delete(module);
            BackgroundRun.ApplyFiles(root, true, null);
            Check(File.Exists(module), "missing owned module repaired on launch");
            BackgroundRun.ApplyFiles(root, false, null);
            Check(!File.Exists(module) && !File.Exists(state), "disable removes only owned files");
            Reject(delegate { BackgroundRun.ApplyFiles(root, true, delegate(string name) {
                if (name == BackgroundRun.LoaderFile) throw new IOException("simulated write failure");
            }); }, "failed installation reported");
            Check(!File.Exists(module) && !File.Exists(state), "failed installation restores empty state");
            Directory.CreateDirectory(module);
            Reject(delegate { BackgroundRun.ApplyFiles(root, true, null); }, "directory collision refused");
            Directory.Delete(module);
            Check(File.ReadAllText(fps) == "existing FPS module", "high FPS installation never changed");
            Check(Directory.GetFiles(root).Length == 1, "no transaction temporary files remain");
        }
        finally { Directory.Delete(root, true); }
        return failures;
    }
}
