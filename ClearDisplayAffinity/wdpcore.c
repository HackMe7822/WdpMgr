#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

static HMODULE g_hMod;
static void*   g_pSetWDA;
static BYTE    g_orig[14];
static BYTE    g_patch[14];
static BOOL    g_hooked = FALSE;

static void Cpy14(BYTE* d, const BYTE* s) {
    int i; for (i = 0; i < 14; i++) d[i] = s[i];
}

/* Append a line to C:\ProgramData\ClearDA_dll.log */
static void DbgLog(const char* msg) {
    HANDLE h = CreateFileA("C:\\ProgramData\\WdpCore.log",
        GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, NULL,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    DWORD w;
    WriteFile(h, msg, lstrlenA(msg), &w, NULL);
    WriteFile(h, "\r\n", 2, &w, NULL);
    CloseHandle(h);
}

/* Hook: silently block any attempt to (re-)enable display affinity */
static BOOL WINAPI MySetWDA(HWND hwnd, DWORD affinity) {
    (void)hwnd; (void)affinity;
    return TRUE;
}

static void InstallHook(void) {
    DWORD old;
    VirtualProtect(g_pSetWDA, 14, PAGE_EXECUTE_READWRITE, &old);
    Cpy14((BYTE*)g_pSetWDA, g_patch);
    VirtualProtect(g_pSetWDA, 14, old, &old);
    g_hooked = TRUE;
}

static void RemoveHook(void) {
    DWORD old;
    VirtualProtect(g_pSetWDA, 14, PAGE_EXECUTE_READWRITE, &old);
    Cpy14((BYTE*)g_pSetWDA, g_orig);
    VirtualProtect(g_pSetWDA, 14, old, &old);
    g_hooked = FALSE;
}

static BOOL CALLBACK ClearCb(HWND hwnd, LPARAM lp) {
    DWORD wpid = 0;
    GetWindowThreadProcessId(hwnd, &wpid);
    if (wpid != (DWORD)lp) return TRUE;
    DWORD aff = 0;
    if (GetWindowDisplayAffinity(hwnd, &aff) && aff != 0) {
        char buf[128];
        wsprintfA(buf, "  hwnd=%p aff=0x%X -> clearing", (void*)(ULONG_PTR)hwnd, aff);
        DbgLog(buf);
        BOOL ok = SetWindowDisplayAffinity(hwnd, 0);
        wsprintfA(buf, "  SetWDA(hwnd=%p, 0) = %d err=%u",
            (void*)(ULONG_PTR)hwnd, ok, GetLastError());
        DbgLog(buf);
    }
    return TRUE;
}

static DWORD WINAPI SetupThread(LPVOID lp) {
    char buf[128];
    wsprintfA(buf, "SetupThread pid=%u", GetCurrentProcessId());
    DbgLog(buf);

    HMODULE hU32 = GetModuleHandleW(L"user32.dll");
    if (!hU32) { DbgLog("ERROR: user32 not found"); return 1; }

    g_pSetWDA = (void*)GetProcAddress(hU32, "SetWindowDisplayAffinity");
    if (!g_pSetWDA) { DbgLog("ERROR: SetWindowDisplayAffinity not found"); return 1; }

    wsprintfA(buf, "SetWindowDisplayAffinity @ %p", g_pSetWDA);
    DbgLog(buf);

    Cpy14(g_orig, (BYTE*)g_pSetWDA);

    /* MOV RAX, imm64 / JMP RAX — 14-byte absolute indirect jump */
    g_patch[0]=0x48; g_patch[1]=0xB8;
    *(UINT64*)(g_patch+2) = (UINT64)(ULONG_PTR)MySetWDA;
    g_patch[10]=0xFF; g_patch[11]=0xE0;
    g_patch[12]=0x90; g_patch[13]=0x90;

    InstallHook();
    DbgLog("Hook installed");

    /* Retry clear for 3 seconds: handles apps that already had WDA set before
       injection, and brief race windows. 50ms between cycles = ~60 attempts. */
    DWORD endTime = GetTickCount() + 3000;
    int cycles = 0;
    while (GetTickCount() < endTime) {
        RemoveHook();
        EnumWindows(ClearCb, (LPARAM)(ULONG_PTR)GetCurrentProcessId());
        InstallHook();
        cycles++;
        Sleep(50);
    }

    wsprintfA(buf, "Initial clear done (%d cycles). Hook remains active.", cycles);
    DbgLog(buf);

    /* Stay resident — hook remains active as long as this process lives */
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hMod, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hMod);
        g_hMod = hMod;
        HANDLE h = CreateThread(NULL, 0, SetupThread, NULL, 0, NULL);
        if (h) CloseHandle(h);
    } else if (reason == DLL_PROCESS_DETACH && reserved == NULL) {
        if (g_hooked) RemoveHook();
    }
    return TRUE;
}
