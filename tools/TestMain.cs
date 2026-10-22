using System;

// 测试总入口：build-test.ps1 用 /main:TestMain 指定
static class TestMain
{
    [STAThread]
    static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--input") >= 0) return ShiftNumpadTests.Run() == 0 ? 0 : 1;
        int failures = 0;
        failures += VersionSwitchTests.Run();
        failures += EngineTests.Run();
        failures += DisplayTests.Run();
        failures += BackgroundRunTests.Run();
        int fpsPackageArg = Array.IndexOf(args, "--fps-package");
        failures += HighFpsTests.Run(fpsPackageArg >= 0 && fpsPackageArg + 1 < args.Length ? args[fpsPackageArg + 1] : null);
        int buildsArg = Array.IndexOf(args, "--fps-versions");
        if (buildsArg >= 0 && buildsArg + 1 < args.Length) failures += HighFpsTests.VerifyGameBuilds(args[buildsArg + 1]);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "==================  ALL SUITES PASSED  =================="
            : "==================  " + failures + " FAILURE(S)  ==================");
        return failures == 0 ? 0 : 1;
    }
}
