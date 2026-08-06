using System;
using War3Helper;

// 诊断工具：用程序自己的加载逻辑读一遍配置，打印它实际用了哪个文件、读到了什么。
// 用 build-dump.ps1 编译运行。
static class DumpConfig
{
    static int Main(string[] args)
    {
        Console.WriteLine("exe 目录      : " + AppDomain.CurrentDomain.BaseDirectory);
        Console.WriteLine("APPDATA       : " +
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Console.WriteLine("解析到的配置路径: " + AppConfig.ConfigPath);
        Console.WriteLine("该文件存在     : " + System.IO.File.Exists(AppConfig.ConfigPath));
        Console.WriteLine("绿色版模式     : " + AppConfig.IsPortableConfig);
        Console.WriteLine();

        AppConfig c = AppConfig.Load();
        if (AppConfig.LoadWarning != null)
            Console.WriteLine("!! 加载告警: " + AppConfig.LoadWarning);

        Console.WriteLine("ConfigVersion : " + c.ConfigVersion);
        Console.WriteLine("方案数        : " + c.Schemes.Count + "   当前=" + c.CurrentScheme);
        foreach (Scheme s in c.Schemes)
        {
            Console.WriteLine("  [" + s.Name + "]");
            Console.WriteLine("     物品键 : " + string.Join(",", Array.ConvertAll(s.ItemKeys, delegate(int v) { return v.ToString(); })));
            Console.WriteLine("     目标键 : " + string.Join(",", Array.ConvertAll(s.ItemSlotDst, delegate(int v) { return v.ToString(); })));
            Console.WriteLine("     自定义 : " + s.Maps.Count + " 条");
            foreach (KeyMapEntry e in s.Maps)
                Console.WriteLine("        " + KeyNames.Name(e.Src) + " -> " + KeyNames.Name(e.Dst));
        }
        Console.WriteLine("喊话条目      : " + c.Chats.Count);
        Console.WriteLine("内置 S->H     : " + c.BuiltinStopAsHold);
        Console.WriteLine("商店模式      : " + c.ShopModeEnabled);
        return 0;
    }
}
