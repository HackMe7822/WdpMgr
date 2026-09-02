/*
 * mode7helper.exe — Mode 7 native subprocess for FakeLDB
 *
 * Protections set at startup (before any threads):
 *   1. ACG (ProcessDynamicCodePolicy) — VirtualAllocEx(RWX) → ERROR_ACCESS_DENIED
 *   2. MicrosoftSignedOnly — unsigned LoadLibraryW blocked
 *   3. ExtensionPointDisable — WH_GETMESSAGE injection blocked
 *   4. WDA_MONITOR every 10ms
 *   5. IOCTL_KBYPASS_PROTECT_PID — ObRegisterCallbacks strips PROCESS_VM_OPERATION /
 *      PROCESS_VM_WRITE / PROCESS_VM_READ / PROCESS_CREATE_THREAD from any OpenProcess
 *      targeting this PID; requires kbypass.sys to be loaded.
 *
 * Command line: mode7helper.exe <parent-pid>
 */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

// IOCTL codes for kbypass.sys (user-mode values, no ntddk.h needed)
// CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, fn, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
#define IOCTL_KBYPASS_PROTECT_PID   0x00222410UL  // fn=0x904
#define IOCTL_KBYPASS_UNPROTECT_PID 0x00222414UL  // fn=0x905

typedef struct { DWORD64 pid64; } KBYPASS_PROTECT_PID;

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
    /* 3. ACG: block new executable mappings */
    {
        PROCESS_MITIGATION_DYNAMIC_CODE_POLICY v = {0};
        v.ProhibitDynamicCode = 1;
        fn(ProcessDynamicCodePolicy, &v, sizeof(v));
    }
}

/* ── Register this process as kbypass-protected ──────────────────────────── */
static HANDLE g_kbypass = INVALID_HANDLE_VALUE;

static void KbypassProtect(DWORD pid)
{
    g_kbypass = CreateFileA("\\\\.\\KernelBypass",
        GENERIC_READ | GENERIC_WRITE, 0, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_kbypass == INVALID_HANDLE_VALUE) {
        /* kbypass.sys not loaded — mode 7 runs as mode 6 (still useful for testing) */
        return;
    }
    KBYPASS_PROTECT_PID req = {0};
    req.pid64 = (DWORD64)pid;
    DWORD dummy = 0;
    DeviceIoControl(g_kbypass, IOCTL_KBYPASS_PROTECT_PID,
        &req, sizeof(req), NULL, 0, &dummy, NULL);
}

static void KbypassUnprotect(void)
{
    if (g_kbypass == INVALID_HANDLE_VALUE) return;
    DWORD dummy = 0;
    DeviceIoControl(g_kbypass, IOCTL_KBYPASS_UNPROTECT_PID,
        NULL, 0, NULL, 0, &dummy, NULL);
    CloseHandle(g_kbypass);
    g_kbypass = INVALID_HANDLE_VALUE;
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
    if (msg == WM_DESTROY) {
        g_stop = TRUE;
        KbypassUnprotect();
        PostQuitMessage(0);
        return 0;
    }
    if (msg == WM_PAINT) {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hw, &ps);
        RECT r; GetClientRect(hw, &r);
        FillRect(hdc, &r, (HBRUSH)GetStockObject(BLACK_BRUSH));
        SetBkMode(hdc, TRANSPARENT);

        SetTextColor(hdc, RGB(255, 160, 0));
        HFONT hf = CreateFontW(26, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT prev = (HFONT)SelectObject(hdc, hf);
        RECT rh = {16, 14, r.right - 16, 52};
        DrawTextW(hdc, L"MODE 7  —  SECRET CONTENT", -1, &rh, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, prev); DeleteObject(hf);

        SetTextColor(hdc, RGB(155, 155, 155));
        HFONT hf2 = CreateFontW(15, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        prev = (HFONT)SelectObject(hdc, hf2);
        RECT rd = {16, 60, r.right - 16, r.bottom - 12};

        BOOL kbLoaded = (g_kbypass != INVALID_HANDLE_VALUE);
        DrawTextW(hdc,
            kbLoaded
            ? L"Protections:\n"
              L"  ●  ACG — VirtualAllocEx(PAGE_EXECUTE_READWRITE) → ERROR_ACCESS_DENIED\n"
              L"  ●  MicrosoftSignedOnly — unsigned LoadLibraryW blocked\n"
              L"  ●  ExtensionPointDisablePolicy — WH_GETMESSAGE injection blocked\n"
              L"  ●  WDA_MONITOR re-applied every 10 ms\n"
              L"  ●  kbypass.sys ObRegisterCallbacks — LOADED\n"
              L"      OpenProcess(PROCESS_ALL_ACCESS) returns stripped handle:\n"
              L"      PROCESS_VM_OPERATION / VM_WRITE / VM_READ / CREATE_THREAD removed\n"
              L"      VirtualAllocEx + CreateRemoteThread → ERROR_ACCESS_DENIED\n\n"
              L"Bypass requires a kernel exploit or a second more-privileged driver."
            : L"Protections:\n"
              L"  ●  ACG, MicrosoftSignedOnly, ExtensionPointDisable, WDA(10ms)\n"
              L"  ●  kbypass.sys ObRegisterCallbacks — NOT LOADED (running as Mode 6)\n\n"
              L"Load kbypass.sys first:\n"
              L"  sc start KernelBypass\n"
              L"  (or run activate_kbypass.bat as Administrator)",
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

    /* Open parent for watch-for-exit BEFORE mitigations */
    DWORD ppid = cmdLine ? StrToDword(cmdLine) : 0;
    if (ppid) g_parent = OpenProcess(SYNCHRONIZE, FALSE, ppid);

    /* 1. Mitigations before any thread or DLL load */
    SetupMitigations();

    /* 2. Register window class */
    WNDCLASSEXW wc = {0};
    wc.cbSize        = sizeof(wc);
    wc.hInstance     = hInst;
    wc.lpfnWndProc   = WndProc;
    wc.lpszClassName = L"M7Cls";
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    wc.hCursor       = LoadCursorW(NULL, IDC_ARROW);
    RegisterClassExW(&wc);

    /* 3. Create window */
    g_hwnd = CreateWindowExW(0, L"M7Cls", L"Mode 7 — Protected",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 700, 350,
        NULL, NULL, hInst, NULL);
    if (!g_hwnd) return 1;

    /* 4. Apply WDA immediately */
    SetWindowDisplayAffinity(g_hwnd, WDA_MONITOR);

    /* 5. WDA re-apply thread (10 ms) */
    DWORD tid;
    HANDLE hT = CreateThread(NULL, 0, WdaThread, NULL, 0, &tid);
    if (hT) CloseHandle(hT);

    /* 6. Parent-watch thread */
    if (g_parent) {
        HANDLE hW = CreateThread(NULL, 0, ParentWatch, NULL, 0, &tid);
        if (hW) CloseHandle(hW);
    }

    /* 7. Register process protection with kbypass.sys.
          Done AFTER window creation — device open + DeviceIoControl need user32.dll
          loaded which happens during CreateWindowExW; also ACG is already set so
          we can't allocate RWX but DeviceIoControl only needs RW kernel buffers. */
    KbypassProtect(GetCurrentProcessId());

    /* 8. Force repaint now that kbypass status is known */
    InvalidateRect(g_hwnd, NULL, FALSE);

    /* 9. Write PID to stdout so FakeLDB knows we are ready */
    {
        char buf[16]; DWORD wr;
        wsprintfA(buf, "%lu\n", GetCurrentProcessId());
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), buf, lstrlenA(buf), &wr, NULL);
    }

    /* 10. Message loop */
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_parent) CloseHandle(g_parent);
    return 0;
}
