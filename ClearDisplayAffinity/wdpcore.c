#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

static HMODULE g_hMod;

/* ── SetWindowDisplayAffinity hook ─────────────────────────────────────── */
static void*   g_pSetWDA;
static BYTE    g_orig[14];
static BYTE    g_patch[14];
static BOOL    g_hooked = FALSE;

static void Cpy14(BYTE* d, const BYTE* s) {
    int i; for (i = 0; i < 14; i++) d[i] = s[i];
}

/* ── Shared logger ─────────────────────────────────────────────────────── */
static void DbgLog(const char* msg) {
    HANDLE h = CreateFileA("C:\\ProgramData\\WdpCore.log",
        GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, NULL,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);

    /* Timestamp: use GetSystemTime for readability */
    SYSTEMTIME st;
    GetLocalTime(&st);
    char ts[32];
    wsprintfA(ts, "[%02u:%02u:%02u.%03u] ",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    DWORD w;
    WriteFile(h, ts, lstrlenA(ts), &w, NULL);
    WriteFile(h, msg, lstrlenA(msg), &w, NULL);
    WriteFile(h, "\r\n", 2, &w, NULL);
    CloseHandle(h);
}

/* ── LL hook globals ───────────────────────────────────────────────────── */
static HHOOK g_hKbdHook   = NULL;
static HHOOK g_hMouseHook = NULL;
static DWORD g_llThreadId = 0;
static HANDLE g_llThread  = NULL;
/* Flag: set to 1 when GetMsgProc has installed LL hooks on the target's UI thread */
static volatile LONG g_llFromMsgProc = 0;

#define LLKHF_INJECTED           0x00000010
#define LLKHF_LOWER_IL_INJECTED  0x00000002
#define LLMHF_INJECTED           0x00000001
#define LLMHF_LOWER_IL_INJECTED  0x00000002

/* WH_KEYBOARD_LL callback: strip injected flags so lockdown browser
   hooks see the event as coming from hardware */
static LRESULT CALLBACK MyKbdLL(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && lParam) {
        KBDLLHOOKSTRUCT* ks = (KBDLLHOOKSTRUCT*)lParam;
        if (ks->flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) {
            char buf[128];
            wsprintfA(buf, "KbdLL: vk=0x%02X flags=0x%X -> stripping INJECTED", ks->vkCode, ks->flags);
            DbgLog(buf);
            ks->flags &= ~(DWORD)(LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED);
        }
    }
    return CallNextHookEx(g_hKbdHook, nCode, wParam, lParam);
}

/* WH_MOUSE_LL callback: strip injected flags */
static LRESULT CALLBACK MyMouseLL(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && lParam) {
        MSLLHOOKSTRUCT* ms = (MSLLHOOKSTRUCT*)lParam;
        if (ms->flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) {
            char buf[128];
            wsprintfA(buf, "MouseLL: msg=0x%04X flags=0x%X -> stripping INJECTED", (UINT)wParam, ms->flags);
            DbgLog(buf);
            ms->flags &= ~(DWORD)(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED);
        }
    }
    return CallNextHookEx(g_hMouseHook, nCode, wParam, lParam);
}

/* Dedicated thread: installs LL hooks then pumps messages so they fire.
   WH_KEYBOARD_LL / WH_MOUSE_LL callbacks are dispatched to the installing
   thread's message queue, so a pump is required. */
static DWORD WINAPI LLHookThread(LPVOID lp) {
    char buf[128];
    wsprintfA(buf, "LLHookThread started pid=%u tid=%u", GetCurrentProcessId(), GetCurrentThreadId());
    DbgLog(buf);

    g_hKbdHook = SetWindowsHookExW(WH_KEYBOARD_LL, MyKbdLL, g_hMod, 0);
    if (g_hKbdHook)
        DbgLog("KbdLL hook installed OK");
    else {
        wsprintfA(buf, "KbdLL hook FAILED err=%u", GetLastError());
        DbgLog(buf);
    }

    g_hMouseHook = SetWindowsHookExW(WH_MOUSE_LL, MyMouseLL, g_hMod, 0);
    if (g_hMouseHook)
        DbgLog("MouseLL hook installed OK");
    else {
        wsprintfA(buf, "MouseLL hook FAILED err=%u", GetLastError());
        DbgLog(buf);
    }

    /* Message pump — required for LL hooks to receive events */
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_hKbdHook)   { UnhookWindowsHookEx(g_hKbdHook);   g_hKbdHook = NULL; }
    if (g_hMouseHook) { UnhookWindowsHookEx(g_hMouseHook); g_hMouseHook = NULL; }
    DbgLog("LLHookThread exiting");
    return 0;
}

/* ── SetWindowDisplayAffinity hook ─────────────────────────────────────── */
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

/* SetupThread: LLHookThread + 3-second re-clear loop for Modes 1-4.
   In Mode 5, wdpcore is manually mapped — CreateThread calls are killed by the
   RtlUserThreadStart hook (start addr in unmapped code). This thread may never
   run in Mode 5, but DllMain already cleared WDA synchronously so that's OK. */
static DWORD WINAPI SetupThread(LPVOID lp) {
    char buf[128];
    wsprintfA(buf, "SetupThread pid=%u", GetCurrentProcessId());
    DbgLog(buf);

    /* g_pSetWDA, g_orig, g_patch, and the inline hook were set up synchronously
       in DllMain — skip redundant init here. */

    /* Start LL input hook thread so KbdLL/MouseLL hooks are in the LIFO chain
       before the target process installs its own hooks. */
    g_llThread = CreateThread(NULL, 0, LLHookThread, NULL, 0, &g_llThreadId);
    if (g_llThread) {
        DbgLog("LLHookThread launched");
        CloseHandle(g_llThread);
    } else {
        wsprintfA(buf, "LLHookThread CreateThread FAILED err=%u", GetLastError());
        DbgLog(buf);
    }

    /* Re-clear WDA for 3 seconds: handles apps that re-set WDA after injection
       (e.g. FakeLDB's 200ms re-apply timer) and late-created windows. */
    if (!g_pSetWDA) { DbgLog("SetupThread: g_pSetWDA NULL — skipping loop"); return 1; }
    DWORD endTime = GetTickCount() + 3000;
    int cycles = 0;
    while (GetTickCount() < endTime) {
        RemoveHook();
        EnumWindows(ClearCb, (LPARAM)(ULONG_PTR)GetCurrentProcessId());
        InstallHook();
        cycles++;
        Sleep(50);
    }

    wsprintfA(buf, "Re-clear done (%d cycles). WDA hook remains active.", cycles);
    DbgLog(buf);

    return 0;
}

/* ── WH_GETMESSAGE callback ────────────────────────────────────────────────
 * Runs on the TARGET PROCESS'S UI THREAD (the thread that called GetMessage).
 * We install KbdLL / MouseLL hooks here on FIRST CALL so they fire on the
 * same thread as the target's own LL hooks.  Same-thread CallNextHookEx is
 * synchronous and shares lParam, so our flag-stripping propagates to the
 * target's callback — unlike cross-thread delivery where each hook gets its
 * own independent copy of KBDLLHOOKSTRUCT.
 * ─────────────────────────────────────────────────────────────────────────── */
__declspec(dllexport)
LRESULT CALLBACK GetMsgProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (InterlockedExchange(&g_llFromMsgProc, 1) == 0) {
        /* First call: install LL hooks from the target's UI thread. */
        char buf[128];
        wsprintfA(buf, "GetMsgProc: first call pid=%u tid=%u — installing LL hooks on UI thread",
            GetCurrentProcessId(), GetCurrentThreadId());
        DbgLog(buf);
        HHOOK hK = SetWindowsHookExW(WH_KEYBOARD_LL, MyKbdLL,   g_hMod, 0);
        HHOOK hM = SetWindowsHookExW(WH_MOUSE_LL,    MyMouseLL, g_hMod, 0);
        wsprintfA(buf, "GetMsgProc: KbdLL=%p MouseLL=%p err=%u", hK, hM, GetLastError());
        DbgLog(buf);
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hMod, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hMod);
        g_hMod = hMod;

        char buf[128];
        wsprintfA(buf, "DllMain ATTACH pid=%u hMod=%p", GetCurrentProcessId(), (void*)(ULONG_PTR)hMod);
        DbgLog(buf);

        /*
         * WDA clear + inline hook done synchronously here so Mode 5 works.
         * In Mode 5, wdpcore is manually mapped — CreateThread calls are killed
         * by an RtlUserThreadStart hook (start addrs in unmapped code fail
         * GetModuleHandleEx).  DllMain runs on the calling loader thread which
         * is not a new thread, so it is safe to do real work here.
         *
         * Sequence:
         *   1. Save original SetWindowDisplayAffinity bytes into g_orig.
         *   2. EnumWindows → ClearCb: call real SetWDA(hwnd, 0) on all WDA
         *      windows while the function is still unpatched.
         *   3. InstallHook: redirect SetWDA to MySetWDA (no-op stub).  Any
         *      subsequent SetWDA call (e.g. FakeLDB's 200ms re-apply timer) hits
         *      the stub and returns TRUE without actually setting WDA.
         */
        HMODULE hU32 = GetModuleHandleW(L"user32.dll");
        if (hU32) {
            g_pSetWDA = (void*)GetProcAddress(hU32, "SetWindowDisplayAffinity");
            if (g_pSetWDA) {
                wsprintfA(buf, "SetWindowDisplayAffinity @ %p", g_pSetWDA);
                DbgLog(buf);

                Cpy14(g_orig, (BYTE*)g_pSetWDA);

                /* MOV RAX, imm64 / JMP RAX — 14-byte absolute patch */
                g_patch[0]=0x48; g_patch[1]=0xB8;
                *(UINT64*)(g_patch+2) = (UINT64)(ULONG_PTR)MySetWDA;
                g_patch[10]=0xFF; g_patch[11]=0xE0;
                g_patch[12]=0x90; g_patch[13]=0x90;

                EnumWindows(ClearCb, (LPARAM)(ULONG_PTR)GetCurrentProcessId());
                InstallHook();
                DbgLog("SetWDA hook installed (DllMain sync)");
            } else {
                DbgLog("ERROR: SetWindowDisplayAffinity not found");
            }
        } else {
            DbgLog("ERROR: user32.dll not found");
        }

        /* SetupThread handles LLHookThread + 3-second re-clear loop (Modes 1-4).
           May be killed in Mode 5 — WDA already handled above. */
        HANDLE h = CreateThread(NULL, 0, SetupThread, NULL, 0, NULL);
        if (h) CloseHandle(h);
        else {
            wsprintfA(buf, "SetupThread CreateThread FAILED err=%u (Mode 5? — OK)", GetLastError());
            DbgLog(buf);
        }
    } else if (reason == DLL_PROCESS_DETACH && reserved == NULL) {
        if (g_hooked) RemoveHook();
        if (g_llThreadId) PostThreadMessageW(g_llThreadId, WM_QUIT, 0, 0);
    }
    return TRUE;
}
