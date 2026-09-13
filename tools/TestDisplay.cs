using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using War3Helper;

// 使用独立临时键验证持久化，不触碰真实魔兽配置。
static class DisplayTests
{
    static int failures;

    static void Check(bool condition, string message)
    {
        Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + message);
        if (!condition) failures++;
    }

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("\n===== DISPLAY TESTS =====");
        Check(Marshal.SizeOf(typeof(Native.DEVMODE)) == 220, "DEVMODEW size = 220");
        Check(Marshal.OffsetOf(typeof(Native.DEVMODE), "dmSize").ToInt32() == 68,
              "DEVMODEW.dmSize offset = 68");
        Check(Marshal.OffsetOf(typeof(Native.DEVMODE), "dmDisplayFrequency").ToInt32() == 184,
              "DEVMODEW.dmDisplayFrequency offset = 184");

        string path = @"Software\War3Helper\DisplayTest-" + Guid.NewGuid().ToString("N");
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("refreshrate", 75, RegistryValueKind.DWord);
                key.SetValue("maxfps", 200, RegistryValueKind.DWord);
                key.SetValue("reswidth", 3840, RegistryValueKind.DWord);
                War3Ctl.WriteRefreshRateSettings(key, 240);
            }
            // 重新打开，检查真正保存的值和类型。
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                Check((int)key.GetValue("refreshrate") == 240 && (int)key.GetValue("maxfps") == 240,
                      "75 Hz / 200 FPS are updated to 240 Hz / 240 FPS");
                Check(key.GetValueKind("refreshrate") == RegistryValueKind.DWord
                      && key.GetValueKind("maxfps") == RegistryValueKind.DWord,
                      "both settings persist as DWORD values");
                foreach (int unknownRate in new int[] { 0, 1, -1 })
                    War3Ctl.WriteRefreshRateSettings(key, unknownRate);
                Check((int)key.GetValue("refreshrate") == 240 && (int)key.GetValue("maxfps") == 240,
                      "unknown refresh rates preserve previous settings");
                War3Ctl.WriteRefreshRateSettings(key, 144);
                Check((int)key.GetValue("refreshrate") == 144 && (int)key.GetValue("maxfps") == 144,
                      "a later system change replaces the earlier rate");
                Check((int)key.GetValue("reswidth") == 3840, "unrelated video settings are preserved");
            }
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, false); }
        int currentRate = War3Ctl.GetSystemRefreshRate();
        Console.WriteLine("  Current system refresh rate: " + currentRate + " Hz (0 = unavailable)");
        return failures;
    }
}
