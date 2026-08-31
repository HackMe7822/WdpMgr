/*
 * wdphook.exe — No-injection display/input bypass
 *
 * Does two things without injecting any DLL:
 *
 *   1. WDA clear: polls EnumWindows every 150 ms and calls
 *      SetWindowDisplayAffinity(hwnd, 0) on any window that has
 *      WDA_MONITOR set.  Fixes the black screen in MeshCentral/RDP.
 *
 *   2. Input fix: installs global WH_KEYBOARD_LL + WH_MOUSE_LL hooks
 *      that strip LLKHF_INJECTED / LLMHF_INJECTED from every event
 *      before passing it down the chain.  Makes MeshCentral SendInput()
 *      appear as hardware to lockdown-browser LL hooks.
 *      Re-registers whenever a new process is detected so we stay at
 *      the TOP of the hook chain (LIFO = first called).
 *
 * Build (MSVC, no CRT, x64):
 *   cl /nologo /O1 /Os /GS- /W3 wdphook.c /link /SUBSYSTEM:WINDOWS
 *      /NODEFAULTLIB /ENTRY:WinMainEntry /OPT:REF
 *      user32.lib kernel32.lib
 */

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <TlHelp32.h>

/* ── tunables ───────────────────────────────────────────────────────────── */
#define POLL_MS          150    /* WDA poll + process-change check interval  */
#define REHOOK_DELAY_MS  600    /* wait after new process before re-hooking  */
#define LOG_PATH         "C:\\ProgramData\\WdpHook.log"

/* ── globals ────────────────────────────────────────────────────────────── */
static HHOOK  g_hKbd   = NULL;
static HHOOK  g_hMouse = NULL;
static DWORD  g_mainTid = 0;    /* message-loop thread — hooks must be here */
static HINSTANCE g_hInst = NULL;

#define WM_REHOOK  (WM_USER + 1)

/* ── logging ────────────────────────────────────────────────────────────── */
static void Log(const char* msg)
{
    HANDLE h = CreateFileA(LOG_PATH, GENERIC_WRITE,
        FILE_SHARE_READ|FILE_SHARE_WRITE, NULL,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    SYSTEMTIME st; GetLocalTime(&st);
    char ts[40];
    wsprintfA(ts, "[%02u:%02u:%02u.%03u] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    DWORD w;
    WriteFile(h, ts,  lstrlenA(ts),  &w, NULL);
    WriteFile(h, msg, lstrlenA(msg), &w, NULL);
    WriteFile(h, "\r\n", 2,          &w, NULL);
    CloseHandle(h);
}

static void LogFmt(const char* fmt, DWORD v)
{
    char buf[128];
    wsprintfA(buf, fmt, v);
    Log(buf);
}

/* ── LL keyboard hook ───────────────────────────────────────────────────── */
static LRESULT CALLBACK KbdHook(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode == HC_ACTION && lParam) {
        KBDLLHOOKSTRUCT* ks = (KBDLLHOOKSTRUCT*)lParam;
        if (ks->flags & 0x12) {   /* LLKHF_INJECTED(0x10)|LLKHF_LOWER_IL(0x02) */
            ks->flags &= ~(DWORD)0x12;
        }
    }
    return CallNextHookEx(g_hKbd, nCode, wParam, lParam);
}

/* ── LL mouse hook ──────────────────────────────────────────────────────── */
static LRESULT CALLBACK MouseHook(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode == HC_ACTION && lParam) {
        MSLLHOOKSTRUCT* ms = (MSLLHOOKSTRUCT*)lParam;
        if (ms->flags & 0x03) {   /* LLMHF_INJECTED(0x01)|LLMHF_LOWER_IL(0x02) */
            ms->flags &= ~(DWORD)0x03;
        }
    }
    return CallNextHookEx(g_hMouse, nCode, wParam, lParam);
}

/* ── re-register hooks (must be called on the message-loop thread) ──────── */
static void DoRehook(void)
{
    if (g_hKbd)   { UnhookWindowsHookEx(g_hKbd);   g_hKbd   = NULL; }
    if (g_hMouse) { UnhookWindowsHookEx(g_hMouse); g_hMouse = NULL; }

    g_hKbd   = SetWindowsHookExW(WH_KEYBOARD_LL, KbdHook,   g_hInst, 0);
    g_hMouse = SetWindowsHookExW(WH_MOUSE_LL,    MouseHook, g_hInst, 0);

    {
        char buf[128];
        wsprintfA(buf, "Hooks registered: kbd=%p mouse=%p err=%u",
            (void*)g_hKbd, (void*)g_hMouse, GetLastError());
        Log(buf);
    }
}

/* ── WDA clear callback ─────────────────────────────────────────────────── */
static BOOL CALLBACK WdaClearCb(HWND hwnd, LPARAM lp)
{
    (void)lp;
    DWORD aff = 0;
    if (GetWindowDisplayAffinity(hwnd, &aff) && aff != 0) {
        char buf[96];
        wsprintfA(buf, "WDA clear hwnd=%p aff=0x%X", (void*)(ULONG_PTR)hwnd, aff);
        Log(buf);
        SetWindowDisplayAffinity(hwnd, 0);
    }
    return TRUE;
}

/* ── background worker thread ───────────────────────────────────────────── */
static DWORD WINAPI Worker(LPVOID lp)
{
    (void)lp;
    DWORD lastProcCount = 0;
    DWORD wdaCycles = 0;

    for (;;) {
        Sleep(POLL_MS);

        /* --- 1. Clear WDA on all windows --------------------------------- */
        EnumWindows(WdaClearCb, 0);
        wdaCycles++;

        /* --- 2. Detect new processes → signal hook re-registration ------- */
        DWORD count = 0;
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap != INVALID_HANDLE_VALUE) {
            PROCESSENTRY32W pe;
            pe.dwSize = sizeof(pe);
            if (Process32FirstW(snap, &pe)) {
                do { count++; } while (Process32NextW(snap, &pe));
            }
            CloseHandle(snap);
        }

        if (count != lastProcCount) {
            char buf[96];
            wsprintfA(buf, "Process count changed %u -> %u, scheduling rehook", lastProcCount, count);
            Log(buf);
            lastProcCount = count;
            /* Wait for new process to finish registering its own hooks,
               then re-register ours so we end up FIRST in the LIFO chain. */
            Sleep(REHOOK_DELAY_MS);
            PostThreadMessageW(g_mainTid, WM_REHOOK, 0, 0);
        }

        /* Heartbeat every ~30 s */
        if (wdaCycles % 200 == 0) {
            char buf[96];
            wsprintfA(buf, "heartbeat cycles=%u procs=%u kbd=%p mouse=%p",
                wdaCycles, count, (void*)g_hKbd, (void*)g_hMouse);
            Log(buf);
        }
    }
}

/* ── entry point ────────────────────────────────────────────────────────── */
void WINAPI WinMainEntry(void)
{
    g_hInst  = GetModuleHandleW(NULL);
    g_mainTid = GetCurrentThreadId();

    Log("=== wdphook started ===");
    LogFmt("pid=%u", GetCurrentProcessId());

    /* Initial hook registration */
    DoRehook();

    /* Start worker */
    HANDLE hW = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    if (hW) CloseHandle(hW);

    /* Message loop — LL hooks REQUIRE this on the installing thread */
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        if (msg.message == WM_REHOOK) {
            DoRehook();
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_hKbd)   UnhookWindowsHookEx(g_hKbd);
    if (g_hMouse) UnhookWindowsHookEx(g_hMouse);
    Log("=== wdphook exiting ===");
    ExitProcess(0);
}
