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

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "==================  ALL SUITES PASSED  =================="
            : "==================  " + failures + " FAILURE(S)  ==================");
        return failures == 0 ? 0 : 1;
    }
}
