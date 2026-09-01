/*
 * mode6helper.exe — Mode 6 native subprocess for FakeLDB
 *
 * Protections set at startup (before any threads):
 *   1. ACG (ProcessDynamicCodePolicy) — VirtualAllocEx(RWX) → ERROR_ACCESS_DENIED
 *      Kills all shellcode-based injection (thread hijack, APC shellcode, etc.)
 *   2. MicrosoftSignedOnly — unsigned LoadLibraryW blocked
 *   3. ExtensionPointDisable — WH_GETMESSAGE injection blocked
 *   4. WDA_MONITOR every 10ms (faster than WdpHook's 150ms external clear)
 *
 * Command line: mode6helper.exe <parent-pid>
 *   parent-pid: decimal PID of the FakeLDB process; helper exits when parent exits.
 */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

#define WDA_MONITOR 1

/* ── Simple decimal string → DWORD (no CRT) ─────────────────────────────── */
static DWORD StrToDword(const char *s)
{
    while (*s == ' ' || *s == '\t') s++;
    DWORD v = 0;
    while (*s >= '0' && *s <= '9') { v = v * 10 + (*s++ - '0'); }
    return v;
}

/* ── Apply process mitigations ───────────────────────────────────────────── */
static void SetupMitigations(void)
{
    HMODULE k = GetModuleHandleW(L"kernel32.dll");
    typedef BOOL (WINAPI *PFN)(PROCESS_MITIGATION_POLICY, PVOID, SIZE_T);
    PFN fn = (PFN)GetProcAddress(k, "SetProcessMitigationPolicy");
    if (!fn) return;

    /* 1. Block WH_GETMESSAGE hook injection */
    {
        PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY v = {0};
        v.DisableExtensionPoints = 1;
        fn(ProcessExtensionPointDisablePolicy, &v, sizeof(v));
    }
    /* 2. Block unsigned DLL load */
    {
        PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY v = {0};
        v.MicrosoftSignedOnly = 1;
        fn(ProcessSignaturePolicy, &v, sizeof(v));
    }
    /* 3. ACG: block new executable mappings — kills VirtualAllocEx(RWX) shellcode */
    {
        PROCESS_MITIGATION_DYNAMIC_CODE_POLICY v = {0};
        v.ProhibitDynamicCode = 1;
        fn(ProcessDynamicCodePolicy, &v, sizeof(v));
    }
}

/* ── Globals ─────────────────────────────────────────────────────────────── */
static HWND          g_hwnd   = NULL;
static HANDLE        g_parent = NULL;
static volatile BOOL g_stop   = FALSE;

/* ── WDA re-apply thread (10 ms) ─────────────────────────────────────────── */
static DWORD WINAPI WdaThread(LPVOID p)
{
    (void)p;
    while (!g_stop) {
        if (g_hwnd) SetWindowDisplayAffinity(g_hwnd, WDA_MONITOR);
        Sleep(10);
    }
    return 0;
}

/* ── Parent-watch thread: exit helper when FakeLDB exits ─────────────────── */
static DWORD WINAPI ParentWatch(LPVOID p)
{
    (void)p;
    WaitForSingleObject(g_parent, INFINITE);
    g_stop = TRUE;
    if (g_hwnd) PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
    return 0;
}

/* ── Window proc ─────────────────────────────────────────────────────────── */
static LRESULT CALLBACK WndProc(HWND hw, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_DESTROY) { g_stop = TRUE; PostQuitMessage(0); return 0; }
    if (msg == WM_PAINT) {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hw, &ps);
        RECT r; GetClientRect(hw, &r);
        FillRect(hdc, &r, (HBRUSH)GetStockObject(BLACK_BRUSH));
        SetBkMode(hdc, TRANSPARENT);

        /* Title */
        SetTextColor(hdc, RGB(0, 240, 90));
        HFONT hf = CreateFontW(26, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT prev = (HFONT)SelectObject(hdc, hf);
        RECT rh = {16, 14, r.right - 16, 52};
        DrawTextW(hdc, L"MODE 6  —  SECRET CONTENT", -1, &rh, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, prev); DeleteObject(hf);

        /* Body */
        SetTextColor(hdc, RGB(155, 155, 155));
        HFONT hf2 = CreateFontW(15, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        prev = (HFONT)SelectObject(hdc, hf2);
        RECT rd = {16, 60, r.right - 16, r.bottom - 12};
        DrawTextW(hdc,
            L"Protections (all set before first thread):\n"
            L"  ●  ACG (Arbitrary Code Guard) — VirtualAllocEx(PAGE_EXECUTE_READWRITE) → ERROR_ACCESS_DENIED\n"
            L"      kills thread-hijack shellcode, APC shellcode, and all code-injection techniques\n"
            L"  ●  MicrosoftSignedOnly — unsigned LoadLibraryW blocked\n"
            L"  ●  ExtensionPointDisablePolicy — WH_GETMESSAGE injection blocked\n"
            L"  ●  WDA_MONITOR re-applied every 10 ms\n\n"
            L"Bypass from user-mode: only possible via kernel driver\n"
            L"  (NtUserSetWindowDisplayAffinity SSDT hook, ObRegisterCallbacks, or PPL).",
            -1, &rd, DT_LEFT | DT_WORDBREAK);
        SelectObject(hdc, prev); DeleteObject(hf2);
        EndPaint(hw, &ps);
        return 0;
    }
    if (msg == WM_SIZE && wp != SIZE_MINIMIZED) {
        InvalidateRect(hw, NULL, FALSE); return 0;
    }
    return DefWindowProcW(hw, msg, wp, lp);
}

/* ── Entry point ─────────────────────────────────────────────────────────── */
int APIENTRY WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR cmdLine, int nShow)
{
    (void)hPrev; (void)nShow;

    /* Open parent process for watch-for-exit (before mitigations) */
    DWORD ppid = cmdLine ? StrToDword(cmdLine) : 0;
    if (ppid) g_parent = OpenProcess(SYNCHRONIZE, FALSE, ppid);

    /* ── 1. Set ALL mitigations before any thread or DLL load ── */
    SetupMitigations();

    /* ── 2. Register window class ── */
    WNDCLASSEXW wc = {0};
    wc.cbSize        = sizeof(wc);
    wc.hInstance     = hInst;
    wc.lpfnWndProc   = WndProc;
    wc.lpszClassName = L"M6Cls";
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    wc.hCursor       = LoadCursorW(NULL, IDC_ARROW);
    RegisterClassExW(&wc);

    /* ── 3. Create window ── */
    g_hwnd = CreateWindowExW(0, L"M6Cls", L"Mode 6 — Protected",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 660, 310,
        NULL, NULL, hInst, NULL);
    if (!g_hwnd) return 1;

    /* ── 4. Apply WDA immediately ── */
    SetWindowDisplayAffinity(g_hwnd, WDA_MONITOR);

    /* ── 5. WDA re-apply thread (10 ms) ── */
    /* ACG is set, but WdaThread's start address is in THIS module (.text section
       mapped RX at load time — not a new dynamic allocation), so it works fine. */
    DWORD tid;
    HANDLE hT = CreateThread(NULL, 0, WdaThread, NULL, 0, &tid);
    if (hT) CloseHandle(hT);

    /* ── 6. Parent-watch thread ── */
    if (g_parent) {
        HANDLE hW = CreateThread(NULL, 0, ParentWatch, NULL, 0, &tid);
        if (hW) CloseHandle(hW);
    }

    /* ── 7. Write PID to stdout so FakeLDB knows we are ready ── */
    {
        char buf[16]; DWORD wr;
        wsprintfA(buf, "%lu\n", GetCurrentProcessId());
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), buf, lstrlenA(buf), &wr, NULL);
    }

    /* ── 8. Message loop ── */
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_parent) CloseHandle(g_parent);
    return 0;
}
