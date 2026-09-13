// War3Helper's render-idle limiter, compiled as a 32-bit Miles module.
// Patch sites are verified against the complete instruction prefix for each supported build.
// No game files are changed on disk. See docs/high-fps.md for the compatibility matrix.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>

struct Profile { DWORD versionMS, versionLS, rva; bool inExe; int kind; };
static const Profile profiles[] = {
    {0x00010014,0x000417BA,0x445FC,true,0}, // 1.20.4.6074
    {0x00010015,0x00001877,0x445FC,true,0}, // 1.21.0.6263
    {0x00010016,0x000018B8,0x61D879,false,1},
    {0x00010017,0x000018D0,0x61F4F9,false,1},
    {0x00010018,0x000118E6,0x62DEE9,false,1},
    {0x00010018,0x000418F3,0x62DF99,false,1},
    {0x00010019,0x000118FD,0x62D5C9,false,1},
    {0x0001001A,0x00001901,0x62D7F9,false,1},
    {0x0001001B,0x0000CC10,0x5FCF9,false,2},
    {0x0001001B,0x00011BAD,0x7B7A9,false,2}
};
static const BYTE prefix0[] = {0x83,0xE0,0xFB,0x53,0xBA,0x11,0,0,0,0x8B,0xCE,0x89,0x46,0x44,0xE8};
static const BYTE prefix1[] = {0x83,0xE0,0xFB,0x6A,0,0xBA,0x11,0,0,0,0x8B,0xCE,0x89,0x46,0x44,0xE8};
static const BYTE prefix2[] = {0x83,0xE0,0xFB,0x8D,0x53,0x11,0x53,0x8B,0xCF,0x89,0x47,0x44,0xE8};
static LARGE_INTEGER frequency;
static LONGLONG nextFrame = 0, period = 0;
static HANDLE timer = NULL;
static DWORD renderThread = 0;

static void __cdecl LimitFrame()
{
    DWORD thread = GetCurrentThreadId();
    if (!renderThread) renderThread = thread;
    if (renderThread != thread) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (!nextFrame || now.QuadPart - nextFrame > period)
        nextFrame = now.QuadPart;
    while (now.QuadPart < nextFrame) {
        LONGLONG remaining = nextFrame - now.QuadPart;
        // High-resolution relative timer handles most of the wait; only the last 0.3 ms spins.
        if (timer && remaining > frequency.QuadPart / 1000) {
            LARGE_INTEGER due;
            due.QuadPart = -(remaining * 10000000 / frequency.QuadPart - 3000);
            if (SetWaitableTimer(timer, &due, 0, NULL, NULL, FALSE)) WaitForSingleObject(timer, 100);
        } else YieldProcessor();
        QueryPerformanceCounter(&now);
    }
    nextFrame += period;
}

// The hook is inside a function, not at an ABI boundary. Preserve the game's
// x87/SSE state as well as the integer registers saved by the trampoline.
__declspec(naked) static void PreserveFloatAndLimit()
{
    __asm {
        push ebp
        mov ebp, esp
        sub esp, 528
        and esp, -16
        fxsave [esp]
        call LimitFrame
        fxrstor [esp]
        mov esp, ebp
        pop ebp
        ret
    }
}

static bool Initialize(HMODULE self)
{
    WCHAR gamePath[MAX_PATH], configPath[MAX_PATH];
    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (!game || !GetModuleFileNameW(game, gamePath, MAX_PATH)) return false;
    DWORD ignored = 0, size = GetFileVersionInfoSizeW(gamePath, &ignored);
    if (!size || size > 1024 * 1024) return false;
    void* data = HeapAlloc(GetProcessHeap(), 0, size);
    if (!data) return false;
    VS_FIXEDFILEINFO* version = NULL;
    UINT versionSize = 0;
    const Profile* profile = NULL;
    if (GetFileVersionInfoW(gamePath, 0, size, data) &&
        VerQueryValueW(data, L"\\", reinterpret_cast<void**>(&version), &versionSize) &&
        versionSize >= sizeof(VS_FIXEDFILEINFO)) {
        for (const auto& candidate : profiles)
            if (version->dwFileVersionMS == candidate.versionMS && version->dwFileVersionLS == candidate.versionLS)
                profile = &candidate;
    }
    HeapFree(GetProcessHeap(), 0, data);
    if (!profile) return false;
    BYTE* base = reinterpret_cast<BYTE*>(profile->inExe ? GetModuleHandleW(NULL) : game);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 || profile->rva + 32 >= nt->OptionalHeader.SizeOfImage) return false;
    BYTE* site = base + profile->rva;
    const BYTE* prefix = profile->kind == 0 ? prefix0 : profile->kind == 1 ? prefix1 : prefix2;
    size_t prefixSize = profile->kind == 0 ? sizeof(prefix0) : profile->kind == 1 ? sizeof(prefix1) : sizeof(prefix2);
    if (memcmp(site, prefix, prefixSize) != 0) return false;
    if (!GetModuleFileNameW(self, configPath, MAX_PATH)) return false;
    WCHAR* slash = wcsrchr(configPath, L'\\');
    if (!slash || slash - configPath > MAX_PATH - 32) return false;
    wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - configPath), L"War3HelperFps.ini");
    UINT fps = GetPrivateProfileIntW(L"Options", L"TargetFps", 0, configPath);
    if (fps < 2 || fps > 2000 || !QueryPerformanceFrequency(&frequency)) return false;
    period = frequency.QuadPart / fps;
    // Miles can unload plugins without exported providers. Pin before installing
    // the jump so its destination remains valid for the entire game process.
    HMODULE pinned;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&LimitFrame), &pinned)) return false;
    timer = CreateWaitableTimerExW(NULL, NULL, 0x00000002 /* high resolution */, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(NULL, FALSE, NULL);

    // Prefixes contain no relative instructions. Preserve flags/registers around the limiter,
    // replay the displaced instructions with the 64 FPS flag retained, then return to the game.
    int stolen = profile->kind == 0 ? 9 : profile->kind == 1 ? 5 : 6;
    BYTE* thunk = static_cast<BYTE*>(VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!thunk) return false;
    int p = 0;
    thunk[p++] = 0x9C; // pushfd
    thunk[p++] = 0x60; // pushad
    thunk[p++] = 0xE8;
    *reinterpret_cast<int32_t*>(thunk + p) = static_cast<int32_t>(reinterpret_cast<intptr_t>(&PreserveFloatAndLimit) - reinterpret_cast<intptr_t>(thunk + p + 4));
    p += 4;
    thunk[p++] = 0x61; // popad
    thunk[p++] = 0x9D; // popfd
    memcpy(thunk + p, site, stolen);
    thunk[p + 2] = 0xFF; // and eax, -1: keep the render-idle flag set
    p += stolen;
    thunk[p++] = 0xE9;
    *reinterpret_cast<int32_t*>(thunk + p) = static_cast<int32_t>((site + stolen) - (thunk + p + 4));
    DWORD oldProtect;
    if (!VirtualProtect(thunk, 64, PAGE_EXECUTE_READ, &oldProtect)) { VirtualFree(thunk, 0, MEM_RELEASE); return false; }
    if (!VirtualProtect(site, stolen, PAGE_EXECUTE_READWRITE, &oldProtect)) { VirtualFree(thunk, 0, MEM_RELEASE); return false; }
    BYTE patch[9];
    memset(patch, 0x90, sizeof(patch));
    patch[0] = 0xE9;
    *reinterpret_cast<int32_t*>(patch + 1) = static_cast<int32_t>(thunk - (site + 5));
    memcpy(site, patch, stolen);
    DWORD unused;
    VirtualProtect(site, stolen, oldProtect, &unused);
    FlushInstructionCache(GetCurrentProcess(), site, stolen);
    FlushInstructionCache(GetCurrentProcess(), thunk, 64);
    return true;
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(self);
        bool ready = Initialize(self);
        if (!ready && timer) { CloseHandle(timer); timer = NULL; }
        OutputDebugStringW(ready ? L"War3HelperFps: ready\n" : L"War3HelperFps: unsupported build, modified code or invalid configuration; no patch applied\n");
    }
    return TRUE;
}
