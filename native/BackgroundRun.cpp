// Optional, windowed single-player background mode. No game code/data is patched.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <wchar.h>

static HMODULE module;
static HWND target;
static UINT attachMessage;
static volatile LONG attached;

// Do not spoof Windows foreground ownership or swallow key/focus messages.
// In particular WM_KILLFOCUS must still release the game's input state.
static bool IsDeactivation(UINT message, WPARAM wparam)
{
    return (message == WM_ACTIVATE && LOWORD(wparam) == WA_INACTIVE) ||
        (message == WM_ACTIVATEAPP && !wparam);
}

static LRESULT CALLBACK BackgroundProc(HWND window, UINT message, WPARAM wparam,
    LPARAM lparam, UINT_PTR id, DWORD_PTR)
{
    if (IsDeactivation(message, wparam)) return 0;
    if (message == WM_NCDESTROY) {
        RemoveWindowSubclass(window, BackgroundProc, id);
        LRESULT result = DefSubclassProc(window, message, wparam, lparam);
        InterlockedExchange(&attached, 0);
        return result;
    }
    return DefSubclassProc(window, message, wparam, lparam);
}

static LRESULT CALLBACK AttachHook(int code, WPARAM wparam, LPARAM lparam)
{
    if (code >= 0) {
        const CWPSTRUCT* message = reinterpret_cast<const CWPSTRUCT*>(lparam);
        if (message->hwnd == target && message->message == attachMessage && !attached) {
            // SetWindowSubclass must run on the window's owning thread.
            if (SetWindowSubclass(target, BackgroundProc, 1, 0)) {
                InterlockedExchange(&attached, 1);
                OutputDebugStringW(L"War3HelperBackground: window attached\n");
            }
        }
    }
    return CallNextHookEx(NULL, code, wparam, lparam);
}

static BOOL CALLBACK FindGameWindow(HWND window, LPARAM parameter)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    WCHAR name[64];
    if (pid == GetCurrentProcessId() && GetClassNameW(window, name, 64) &&
        wcscmp(name, L"Warcraft III") == 0) {
        *reinterpret_cast<HWND*>(parameter) = window;
        return FALSE;
    }
    return TRUE;
}

static DWORD WINAPI AttachWorker(void*)
{
    attachMessage = RegisterWindowMessageW(L"War3Helper.BackgroundRun.Attach.v1");
    if (!attachMessage) return 0;
    // Also handle a recreated game window. The pinned module lives until process exit.
    for (;;) {
        if (!attached) {
            HWND window = NULL;
            EnumWindows(FindGameWindow, reinterpret_cast<LPARAM>(&window));
            if (window) {
                target = window;
                DWORD thread = GetWindowThreadProcessId(window, NULL);
                HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, AttachHook, module, thread);
                if (hook) {
                    DWORD_PTR result = 0;
                    SendMessageTimeoutW(window, attachMessage, 0, 0,
                        SMTO_ABORTIFHUNG | SMTO_BLOCK, 2000, &result);
                    UnhookWindowsHookEx(hook);
                }
            }
        }
        Sleep(1000);
    }
}

static bool SupportedProcess()
{
    // Never interfere with exclusive-fullscreen device loss handling.
    int count = 0;
    LPWSTR* args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args) return false;
    bool windowed = false;
    for (int i = 1; i < count; ++i)
        if (_wcsicmp(args[i], L"-window") == 0) windowed = true;
    LocalFree(args);
    if (!windowed) return false;
    WCHAR path[MAX_PATH];
    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (!game || !GetModuleFileNameW(game, path, MAX_PATH)) return false;
    DWORD ignored = 0, size = GetFileVersionInfoSizeW(path, &ignored);
    if (!size || size > 1024 * 1024) return false;
    void* data = HeapAlloc(GetProcessHeap(), 0, size);
    if (!data) return false;
    VS_FIXEDFILEINFO* v = NULL;
    UINT length = 0;
    bool supported = false;
    const DWORD versions[][2] = {
        {0x10014,0x417BA},{0x10015,0x1877},{0x10016,0x18B8},{0x10017,0x18D0},
        {0x10018,0x118E6},{0x10018,0x418F3},{0x10019,0x118FD},{0x1001A,0x1901},
        {0x1001B,0xCC10},{0x1001B,0x11BAD}
    };
    if (GetFileVersionInfoW(path, 0, size, data) &&
        VerQueryValueW(data, L"\\", reinterpret_cast<void**>(&v), &length) && length >= sizeof(*v))
        for (const auto& version : versions)
            if (v->dwFileVersionMS == version[0] && v->dwFileVersionLS == version[1]) supported = true;
    HeapFree(GetProcessHeap(), 0, data);
    return supported;
}

#ifndef BACKGROUND_RUN_TEST
BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(self);
        if (!SupportedProcess()) return TRUE;
        // Miles may unload plugins with no exported providers; pin before starting a worker.
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(&AttachWorker), &module)) return TRUE;
        HANDLE worker = CreateThread(NULL, 0, AttachWorker, NULL, 0, NULL);
        if (worker) CloseHandle(worker);
        else OutputDebugStringW(L"War3HelperBackground: unable to start window hook\n");
    }
    return TRUE;
}
#endif
