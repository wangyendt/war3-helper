// Hidden test windows only: no input injection and no running game is touched.
#define BACKGROUND_RUN_TEST
#include "../native/BackgroundRun.cpp"
#include <stdio.h>

static int failures, inactive, active, appInactive, appActive, lostFocus, keyUp, manualPause;
static void Check(bool condition, const char* label)
{
    printf("  %s  %s\n", condition ? "PASS" : "FAIL", label);
    if (!condition) ++failures;
}
static LRESULT CALLBACK TestProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == WM_ACTIVATE) { if (LOWORD(wparam) == WA_INACTIVE) ++inactive; else ++active; return 123; }
    if (message == WM_ACTIVATEAPP) { if (wparam) ++appActive; else ++appInactive; return 123; }
    if (message == WM_KILLFOCUS) { ++lostFocus; return 123; }
    if (message == WM_KEYUP) { ++keyUp; return 123; }
    if (message == WM_COMMAND) { ++manualPause; return 123; }
    return DefWindowProcW(window, message, wparam, lparam);
}
static bool WaitForAttach()
{
    DWORD start = GetTickCount();
    while (!attached && GetTickCount() - start < 5000) {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(10);
    }
    return attached != 0;
}
int main()
{
    printf("\n===== BACKGROUND NATIVE TESTS =====\n");
    Check(!SupportedProcess(), "non-game process is refused");
    HWND foreground = GetForegroundWindow();
    module = GetModuleHandleW(NULL);
    WNDCLASSW cls = {};
    cls.lpfnWndProc = TestProc;
    cls.hInstance = module;
    cls.lpszClassName = L"Warcraft III";
    if (!RegisterClassW(&cls)) return 1;
    HWND window = CreateWindowW(cls.lpszClassName, L"Background test (hidden)", 0, 0, 0, 100, 100, NULL, NULL, module, NULL);
    Check(window != NULL, "hidden game-class test window created");
    HANDLE worker = CreateThread(NULL, 0, AttachWorker, NULL, 0, NULL);
    Check(worker != NULL && WaitForAttach(), "worker installs subclass on the window thread");
    if (worker) CloseHandle(worker);
    inactive = active = appInactive = appActive = lostFocus = keyUp = manualPause = 0;
    Check(SendMessageW(window, WM_ACTIVATE, WA_INACTIVE, 0) == 0 && inactive == 0, "inactive window event suppressed");
    Check(SendMessageW(window, WM_ACTIVATEAPP, FALSE, 0) == 0 && appInactive == 0, "inactive application event suppressed");
    Check(SendMessageW(window, WM_ACTIVATE, WA_ACTIVE, 0) == 123 && active == 1, "activation and original result forwarded");
    Check(SendMessageW(window, WM_ACTIVATEAPP, TRUE, 0) == 123 && appActive == 1, "application activation forwarded");
    SendMessageW(window, WM_KILLFOCUS, 0, 0);
    SendMessageW(window, WM_KEYUP, VK_MENU, 0);
    SendMessageW(window, WM_COMMAND, 1, 0);
    Check(lostFocus == 1 && keyUp == 1, "input focus loss and Alt key release preserved");
    Check(manualPause == 1, "manual menu commands not intercepted");
    DestroyWindow(window);
    Check(!attached, "window destruction removes subclass state");
    window = CreateWindowW(cls.lpszClassName, L"Recreated hidden window", 0, 0, 0, 100, 100, NULL, NULL, module, NULL);
    Check(WaitForAttach(), "recreated window reattached");
    Check(GetForegroundWindow() == foreground, "foreground ownership unchanged");
    DestroyWindow(window);
    printf("Background native tests: %d failures\n", failures);
    return failures ? 1 : 0;
}
