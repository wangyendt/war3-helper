using System;
using System.IO;

// 构建期工具：调用 IconGen 生成 app.ico 供 csc /win32icon 使用
static class MakeIcon
{
    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        try
        {
            File.WriteAllBytes(outPath, WshHelper.IconGen.BuildIco());
            Console.WriteLine("icon written: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("icon generation failed: " + ex.Message);
            return 1;
        }
    }
}
