/*
 * kbypass.sys — Kernel-mode SetWindowDisplayAffinity bypass
 *
 * System thread runs every 50ms:
 *   1. ZwQuerySystemInformation to list all processes
 *   2. Find a GUI process in the interactive session (session > 0)
 *   3. KeStackAttachProcess into that process
 *   4. Resolve NtUser exports from win32kfull.sys (once)
 *   5. NtUserBuildHwndList → enumerate all windows in session
 *   6. NtUserGetWindowDisplayAffinity + NtUserSetWindowDisplayAffinity(WDA_NONE)
 *   7. KeUnstackDetachProcess
 *
 * IOCTL_KBYPASS_ACTIVATE   — start thread
 * IOCTL_KBYPASS_DEACTIVATE — stop thread
 * IOCTL_KBYPASS_STATUS     — query statistics
 * IOCTL_KBYPASS_CLEAR_HWND — single-hwnd clear from user mode (legacy helper)
 *
 * Build: WDK + MSVC, x64 only
 * Requires: test signing (bcdedit /set testsigning on) or production EV cert
 */

#include <ntifs.h>
#include <ntimage.h>
#include "kbypass.h"

// ── Pool tag ──────────────────────────────────────────────────────────────────
#define POOL_TAG 'pByK'
#define WDA_NONE               0
#define WDA_MONITOR            1
#define WDA_EXCLUDEFROMCAPTURE 0x11

// ── SystemProcessInformation ──────────────────────────────────────────────────
#define SystemProcessInformation 5
#define SystemModuleInformation  11

typedef struct _SYSTEM_PROCESS_INFO {
    ULONG NextEntryOffset;
    ULONG NumberOfThreads;
    UCHAR Reserved1[48];
    UNICODE_STRING ImageName;
    LONG  BasePriority;
    HANDLE UniqueProcessId;
    PVOID  Reserved2;
    ULONG  HandleCount;
    ULONG  SessionId;
    PVOID  Reserved3;
    SIZE_T PeakVirtualSize;
    SIZE_T VirtualSize;
    ULONG  Reserved4;
    SIZE_T PeakWorkingSetSize;
    SIZE_T WorkingSetSize;
    PVOID  Reserved5;
    SIZE_T QuotaPagedPoolUsage;
    PVOID  Reserved6;
    SIZE_T QuotaNonPagedPoolUsage;
    SIZE_T PagefileUsage;
    SIZE_T PeakPagefileUsage;
    SIZE_T PrivatePageCount;
    LARGE_INTEGER Reserved7[6];
} SYSTEM_PROCESS_INFO, *PSYSTEM_PROCESS_INFO;

typedef struct {
    HANDLE  Section; PVOID MappedBase; PVOID ImageBase; ULONG ImageSize;
    ULONG   Flags; USHORT LoadOrderIndex; USHORT InitOrderIndex;
    USHORT  LoadCount; USHORT OffsetToFileName; UCHAR FullPathName[256];
} KB_MODULE_INFO;
typedef struct { ULONG Count; KB_MODULE_INFO Modules[1]; } KB_MODULE_LIST;

// ── NtUser function types ─────────────────────────────────────────────────────
typedef ULONG      (NTAPI *PFN_NtUserSetWDA)(PVOID hwnd, ULONG aff);
typedef ULONG      (NTAPI *PFN_NtUserGetWDA)(PVOID hwnd, PULONG aff);
typedef NTSTATUS   (NTAPI *PFN_NtUserBuildHwndList)(
    PVOID hDesktop, PVOID hwndNext, BOOLEAN fChildren, BOOLEAN fThread,
    ULONG idThread, ULONG cHwnd, PVOID* phwnds, PULONG pcNeeded);
// NtUserQueryWindow(hwnd, 0) returns the PID of the window's owner process.
// Used to skip clearing WDA on windows that belong to a protected PID.
typedef ULONG_PTR  (NTAPI *PFN_NtUserQueryWindow)(PVOID hwnd, ULONG eType);

// ── Kernel APIs not in wdm.h ──────────────────────────────────────────────────
NTSYSAPI NTSTATUS NTAPI ZwQuerySystemInformation(ULONG, PVOID, ULONG, PULONG);
NTSYSAPI NTSTATUS NTAPI PsLookupProcessByProcessId(HANDLE, PEPROCESS*);
NTSYSAPI ULONG    NTAPI PsGetProcessSessionId(PEPROCESS);

// PsProcessType is exported by ntoskrnl but not always declared in older WDK headers
extern POBJECT_TYPE *PsProcessType;

// ── Globals ───────────────────────────────────────────────────────────────────
static PDEVICE_OBJECT   g_Device        = NULL;
static KEVENT           g_StopEvent     = {0};   // NotificationEvent — wakes all threads
static PKTHREAD         g_WorkerThread  = NULL;
static PKTHREAD         g_GuardThread   = NULL;  // WDA guardian (re-apply loop)
static volatile LONG    g_Active        = 0;
static volatile ULONG   g_ClearedTotal  = 0;
static volatile ULONG   g_LastPass      = 0;
static volatile ULONG   g_PassCount     = 0;

// ── Process-protection globals (ObRegisterCallbacks) ──────────────────────────
static volatile LONG64  g_ProtectedPid  = 0;   // PID protected by handle stripping
static PVOID            g_ObHandle      = NULL; // ObRegisterCallbacks registration handle

static PFN_NtUserSetWDA        pfnSetWDA    = NULL;
static PFN_NtUserGetWDA        pfnGetWDA    = NULL;
static PFN_NtUserBuildHwndList pfnBuildHwnd = NULL;
static PFN_NtUserQueryWindow   pfnQueryWindow = NULL; // optional; skip protected PID's windows
static BOOLEAN                 g_Resolved   = FALSE;
static ERESOURCE               g_ResolveLock = {0};

// ── Inline helpers ────────────────────────────────────────────────────────────
static BOOLEAN StrEqIA(const char* a, const char* b) {
    while (*a && *b) {
        char ca = (*a>='A'&&*a<='Z')?(char)(*a+32):*a;
        char cb = (*b>='A'&&*b<='Z')?(char)(*b+32):*b;
        if (ca!=cb) return FALSE; a++; b++;
    }
    return (*a==0 && *b==0);
}

// ── PE export resolver ────────────────────────────────────────────────────────
static PVOID ResolveExport(PVOID base, const char* name) {
    if (!base || !name) return NULL;
    __try {
        PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)base;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return NULL;
        PIMAGE_NT_HEADERS64 nt = (PIMAGE_NT_HEADERS64)((PUCHAR)base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return NULL;
        ULONG edRva  = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
        ULONG edSize = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].Size;
        if (!edRva) return NULL;
        PIMAGE_EXPORT_DIRECTORY ed = (PIMAGE_EXPORT_DIRECTORY)((PUCHAR)base + edRva);
        PULONG  nameRvas = (PULONG) ((PUCHAR)base + ed->AddressOfNames);
        PUSHORT ords     = (PUSHORT)((PUCHAR)base + ed->AddressOfNameOrdinals);
        PULONG  funcs    = (PULONG) ((PUCHAR)base + ed->AddressOfFunctions);
        for (ULONG i = 0; i < ed->NumberOfNames; i++) {
            const char* n = (const char*)((PUCHAR)base + nameRvas[i]);
            if (StrEqIA(n, name)) {
                ULONG rva = funcs[ords[i]];
                if (rva >= edRva && rva < edRva + edSize) return NULL; // forwarded
                return (PVOID)((PUCHAR)base + rva);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return NULL;
}

// ── Find win32kfull.sys base via module list ──────────────────────────────────
static PVOID FindWin32kBase(void) {
    ULONG sz = 0;
    ZwQuerySystemInformation(SystemModuleInformation, NULL, 0, &sz);
    if (!sz) return NULL;
    sz += 4096;
    KB_MODULE_LIST* mods = (KB_MODULE_LIST*)ExAllocatePoolWithTag(
        NonPagedPoolNx, sz, POOL_TAG);
    if (!mods) return NULL;
    NTSTATUS st = ZwQuerySystemInformation(SystemModuleInformation, mods, sz, &sz);
    PVOID base = NULL;
    if (NT_SUCCESS(st)) {
        for (ULONG i = 0; i < mods->Count; i++) {
            const char* n = (const char*)mods->Modules[i].FullPathName
                            + mods->Modules[i].OffsetToFileName;
            if (StrEqIA(n, "win32kfull.sys")) {
                base = mods->Modules[i].ImageBase;
                break;
            }
        }
    }
    ExFreePoolWithTag(mods, POOL_TAG);
    return base;
}

// ── Resolve NtUser exports (must be called in GUI process context) ─────────────
static BOOLEAN EnsureResolved(void) {
    if (g_Resolved) return TRUE;
    PVOID base = FindWin32kBase();
    if (!base) return FALSE;
    pfnSetWDA      = (PFN_NtUserSetWDA)        ResolveExport(base, "NtUserSetWindowDisplayAffinity");
    pfnGetWDA      = (PFN_NtUserGetWDA)         ResolveExport(base, "NtUserGetWindowDisplayAffinity");
    pfnBuildHwnd   = (PFN_NtUserBuildHwndList)  ResolveExport(base, "NtUserBuildHwndList");
    pfnQueryWindow = (PFN_NtUserQueryWindow)     ResolveExport(base, "NtUserQueryWindow"); // optional
    g_Resolved = (pfnSetWDA != NULL && pfnGetWDA != NULL && pfnBuildHwnd != NULL);
    DbgPrint("kbypass: SetWDA=%p GetWDA=%p BuildHwnd=%p QueryWindow=%p resolved=%d\n",
             pfnSetWDA, pfnGetWDA, pfnBuildHwnd, pfnQueryWindow, g_Resolved);
    return g_Resolved;
}

// ── Find a GUI process in the interactive session ─────────────────────────────
// Returns PEPROCESS with reference held (caller must ObDereferenceObject)
static PEPROCESS FindGuiProcess(void) {
    ULONG sz = 0;
    ZwQuerySystemInformation(SystemProcessInformation, NULL, 0, &sz);
    if (!sz) return NULL;
    sz += 65536;
    PSYSTEM_PROCESS_INFO info = (PSYSTEM_PROCESS_INFO)ExAllocatePoolWithTag(
        NonPagedPoolNx, sz, POOL_TAG);
    if (!info) return NULL;
    NTSTATUS st = ZwQuerySystemInformation(SystemProcessInformation, info, sz, &sz);
    PEPROCESS found = NULL;
    if (NT_SUCCESS(st)) {
        PSYSTEM_PROCESS_INFO cur = info;
        for (;;) {
            // Skip idle (PID 0) and session 0 processes
            if ((ULONG_PTR)cur->UniqueProcessId > 4 && cur->SessionId > 0) {
                PEPROCESS proc = NULL;
                if (NT_SUCCESS(PsLookupProcessByProcessId(cur->UniqueProcessId, &proc))) {
                    found = proc;
                    break; // first session-1 process works
                }
            }
            if (!cur->NextEntryOffset) break;
            cur = (PSYSTEM_PROCESS_INFO)((PUCHAR)cur + cur->NextEntryOffset);
        }
    }
    ExFreePoolWithTag(info, POOL_TAG);
    return found;
}

// ── One bypass pass: attach to GUI process, enumerate + clear WDA windows ─────
static ULONG DoBypassPass(void) {
    PEPROCESS proc = FindGuiProcess();
    if (!proc) return 0;

    KAPC_STATE apc = {0};
    KeStackAttachProcess(proc, &apc);

    if (!EnsureResolved()) {
        KeUnstackDetachProcess(&apc);
        ObDereferenceObject(proc);
        return 0;
    }

    ULONG cleared = 0;

    // Build list of all top-level windows in this session (NULL desktop = all)
    ULONG needed = 0;
    NTSTATUS st = pfnBuildHwnd(NULL, NULL, FALSE, FALSE, 0, 0, NULL, &needed);
    if (needed > 0 && needed < 8192) {
        ULONG allocCount = needed + 64;
        PVOID* hwnds = (PVOID*)ExAllocatePoolWithTag(NonPagedPoolNx,
                            allocCount * sizeof(PVOID), POOL_TAG);
        if (hwnds) {
            RtlZeroMemory(hwnds, allocCount * sizeof(PVOID));
            st = pfnBuildHwnd(NULL, NULL, FALSE, FALSE, 0, allocCount, hwnds, &needed);
            if (NT_SUCCESS(st)) {
                for (ULONG i = 0; i < needed && i < allocCount; i++) {
                    if (!hwnds[i]) continue;
                    __try {
                        // Skip windows belonging to the ObCallbacks-protected PID so the
                        // kernel WDA-clearing thread doesn't fight mode7helper's WDA re-apply.
                        LONG64 protPid = InterlockedCompareExchange64(&g_ProtectedPid, 0, 0);
                        if (protPid != 0 && pfnQueryWindow != NULL) {
                            ULONG_PTR ownerPid = pfnQueryWindow(hwnds[i], 0);
                            if ((LONG64)ownerPid == protPid) continue;
                        }
                        ULONG aff = 0;
                        if (pfnGetWDA(hwnds[i], &aff) && aff == WDA_EXCLUDEFROMCAPTURE) {
                            pfnSetWDA(hwnds[i], WDA_NONE);
                            cleared++;
                        }
                    } __except (EXCEPTION_EXECUTE_HANDLER) {}
                }
            }
            ExFreePoolWithTag(hwnds, POOL_TAG);
        }
    }

    KeUnstackDetachProcess(&apc);
    ObDereferenceObject(proc);
    return cleared;
}

// ── Inline hook on NtUserSetWindowDisplayAffinity ────────────────────────────
//
// When g_ProtectedPid is set, we install a 5-byte relative JMP at the start of
// NtUserSetWindowDisplayAffinity.  Our hook blocks any call that tries to clear
// WDA (affinity==0) on a window owned by g_ProtectedPid from an external process.
// This is what stops wdphook.exe's EnumWindows+SetWDA loop from winning.
//
// win32kfull.sys is NOT a PatchGuard-protected code region (PG targets ntoskrnl,
// hal, and SSDT — not the Win32 subsystem driver), so this is safe on test VMs.
//
#define HOOK_PATCH 5  // E9 rel32 — 5-byte relative JMP
#define TRAMP_SIZE (HOOK_PATCH + 14)  // 5 orig bytes + FF25 abs-JMP back

static UCHAR    g_HookOrig[HOOK_PATCH] = {0};
static PUCHAR   g_Tramp = NULL;          // NonPagedPool (executable) trampoline
static BOOLEAN  g_HookActive = FALSE;
static PFN_NtUserSetWDA g_TrampFn = NULL;

// Our replacement: called instead of the real NtUserSetWindowDisplayAffinity.
// RCX = hwnd, RDX = affinity (x64 MSVC calling convention)
static ULONG NTAPI HookSetWDA(PVOID hwnd, ULONG affinity)
{
    if (affinity == WDA_NONE) {
        LONG64 protPid = InterlockedCompareExchange64(&g_ProtectedPid, 0, 0);
        if (protPid != 0 && pfnQueryWindow != NULL) {
            __try {
                ULONG_PTR ownerPid = pfnQueryWindow(hwnd, 0);
                if ((LONG64)ownerPid == protPid) {
                    // Caller trying to clear WDA on mode7helper's window.
                    // Block it unless the protected process itself is asking.
                    if ((LONG64)(ULONG_PTR)PsGetCurrentProcessId() != protPid)
                        return 1; // silently succeed without clearing
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
    }
    return g_TrampFn ? g_TrampFn(hwnd, affinity) : 0;
}

static NTSTATUS InstallSetWDAHook(void)
{
    if (g_HookActive) return STATUS_SUCCESS;
    if (!pfnSetWDA || !pfnQueryWindow) return STATUS_NOT_FOUND;

    // Allocate executable trampoline: [5 orig bytes][14-byte abs JMP to pfnSetWDA+5]
    g_Tramp = (PUCHAR)ExAllocatePoolWithTag(NonPagedPool, TRAMP_SIZE, POOL_TAG);
    if (!g_Tramp) return STATUS_INSUFFICIENT_RESOURCES;

    // Compute relative offset: hook function must be within ±2 GB of pfnSetWDA
    LONGLONG rel = (LONGLONG)(ULONG_PTR)HookSetWDA
                 - ((LONGLONG)(ULONG_PTR)pfnSetWDA + HOOK_PATCH);
    if (rel < -0x7FFFFFFFLL || rel > 0x7FFFFFFFLL) {
        DbgPrint("kbypass: SetWDA hook offset too large (%lld), skipping hook\n", rel);
        ExFreePoolWithTag(g_Tramp, POOL_TAG); g_Tramp = NULL;
        return STATUS_NOT_SUPPORTED; // guardian thread still protects
    }

    // Build trampoline
    RtlCopyMemory(g_Tramp, pfnSetWDA, HOOK_PATCH);         // 5 original bytes
    g_Tramp[5]  = 0xFF; g_Tramp[6]  = 0x25;                // FF 25 00 00 00 00
    g_Tramp[7]  = 0;    g_Tramp[8]  = 0;
    g_Tramp[9]  = 0;    g_Tramp[10] = 0;
    *(PVOID*)(g_Tramp + 11) = (PUCHAR)pfnSetWDA + HOOK_PATCH; // JMP back to orig+5
    g_TrampFn = (PFN_NtUserSetWDA)g_Tramp;

    // Save original 5 bytes
    RtlCopyMemory(g_HookOrig, pfnSetWDA, HOOK_PATCH);

    // Map the physical page containing pfnSetWDA as writable and patch it
    PHYSICAL_ADDRESS pa = MmGetPhysicalAddress(pfnSetWDA);
    PUCHAR rw = (PUCHAR)MmMapIoSpace(pa, HOOK_PATCH, MmNonCached);
    if (!rw) {
        ExFreePoolWithTag(g_Tramp, POOL_TAG); g_Tramp = NULL;
        return STATUS_UNSUCCESSFUL;
    }
    rw[0] = 0xE9;
    *(INT32*)(rw + 1) = (INT32)rel;
    MmUnmapIoSpace(rw, HOOK_PATCH);

    g_HookActive = TRUE;
    DbgPrint("kbypass: SetWDA hook installed (rel=%lld tramp=%p)\n", rel, g_Tramp);
    return STATUS_SUCCESS;
}

static void RemoveSetWDAHook(void)
{
    if (!g_HookActive || !pfnSetWDA) return;

    PHYSICAL_ADDRESS pa = MmGetPhysicalAddress(pfnSetWDA);
    PUCHAR rw = (PUCHAR)MmMapIoSpace(pa, HOOK_PATCH, MmNonCached);
    if (rw) {
        RtlCopyMemory(rw, g_HookOrig, HOOK_PATCH);
        MmUnmapIoSpace(rw, HOOK_PATCH);
    }
    if (g_Tramp) { ExFreePoolWithTag(g_Tramp, POOL_TAG); g_Tramp = NULL; }
    g_TrampFn   = NULL;
    g_HookActive = FALSE;
    DbgPrint("kbypass: SetWDA hook removed\n");
}

// ── WDA guardian thread ───────────────────────────────────────────────────────
// Secondary protection: every 15ms, re-applies WDA_MONITOR on any window owned
// by g_ProtectedPid that has had its WDA cleared (e.g. if the hook wasn't
// installed because the offset was too far, or on Windows builds where it fails).
static VOID WdaGuardThread(PVOID ctx) {
    UNREFERENCED_PARAMETER(ctx);
    DbgPrint("kbypass: WDA guard thread started\n");
    LARGE_INTEGER interval;
    interval.QuadPart = -150000LL; // 15ms in 100ns units

    while (TRUE) {
        NTSTATUS st = KeWaitForSingleObject(&g_StopEvent, Executive, KernelMode,
                                            FALSE, &interval);
        if (st == STATUS_SUCCESS) break; // stop signaled (NotificationEvent)

        LONG64 protPid = InterlockedCompareExchange64(&g_ProtectedPid, 0, 0);
        if (!protPid || !pfnQueryWindow) continue;

        PEPROCESS proc = FindGuiProcess();
        if (!proc) continue;

        KAPC_STATE apc = {0};
        KeStackAttachProcess(proc, &apc);

        if (EnsureResolved()) {
            // Try installing the SetWDA hook now that we have resolved exports.
            // PROTECT_PID may have arrived before EnsureResolved ran, so retry here.
            if (!g_HookActive && protPid) InstallSetWDAHook();

            ULONG needed = 0;
            pfnBuildHwnd(NULL, NULL, FALSE, FALSE, 0, 0, NULL, &needed);
            if (needed > 0 && needed < 8192) {
                ULONG allocCount = needed + 64;
                PVOID* hwnds = (PVOID*)ExAllocatePoolWithTag(NonPagedPoolNx,
                                    allocCount * sizeof(PVOID), POOL_TAG);
                if (hwnds) {
                    RtlZeroMemory(hwnds, allocCount * sizeof(PVOID));
                    pfnBuildHwnd(NULL, NULL, FALSE, FALSE, 0, allocCount, hwnds, &needed);
                    for (ULONG i = 0; i < needed && i < allocCount; i++) {
                        if (!hwnds[i]) continue;
                        __try {
                            ULONG_PTR ownerPid = pfnQueryWindow(hwnds[i], 0);
                            if ((LONG64)ownerPid == protPid) {
                                ULONG aff = 0;
                                if (pfnGetWDA(hwnds[i], &aff) && aff != WDA_MONITOR) {
                                    pfnSetWDA(hwnds[i], WDA_MONITOR);
                                    DbgPrint("kbypass: guardian restored WDA on %p\n",
                                             hwnds[i]);
                                }
                            }
                        } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    }
                    ExFreePoolWithTag(hwnds, POOL_TAG);
                }
            }
        }

        KeUnstackDetachProcess(&apc);
        ObDereferenceObject(proc);
    }

    DbgPrint("kbypass: WDA guard thread exiting\n");
    PsTerminateSystemThread(STATUS_SUCCESS);
}

// ── Worker thread ─────────────────────────────────────────────────────────────
static VOID WorkerThread(PVOID ctx) {
    UNREFERENCED_PARAMETER(ctx);
    DbgPrint("kbypass: worker thread started\n");
    LARGE_INTEGER interval;
    interval.QuadPart = -500000LL; // 50ms in 100ns units

    while (TRUE) {
        NTSTATUS st = KeWaitForSingleObject(&g_StopEvent, Executive, KernelMode,
                                            FALSE, &interval);
        if (st == STATUS_SUCCESS) break; // stop signaled

        if (!InterlockedOr(&g_Active, 0)) continue;

        __try {
            ULONG n = DoBypassPass();
            InterlockedAdd((LONG*)&g_ClearedTotal, (LONG)n);
            g_LastPass = n;
            InterlockedIncrement((LONG*)&g_PassCount);
            if (n > 0) DbgPrint("kbypass: cleared %u window(s)\n", n);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DbgPrint("kbypass: pass exception\n");
        }
    }
    DbgPrint("kbypass: worker thread exiting\n");
    PsTerminateSystemThread(STATUS_SUCCESS);
}

// ── Process handle protection via ObRegisterCallbacks ────────────────────────
//
// Strip dangerous access from any OpenProcess/DuplicateHandle targeting the
// protected PID.  This simulates PPL handle restrictions without needing to
// modify EPROCESS.Protection (which requires OS-version-specific offsets).
//
// Rights stripped — injector cannot allocate, write, or create threads:
//   PROCESS_TERMINATE            0x0001
//   PROCESS_CREATE_THREAD        0x0002
//   PROCESS_SET_SESSIONID        0x0004
//   PROCESS_VM_OPERATION         0x0008
//   PROCESS_VM_READ              0x0010
//   PROCESS_VM_WRITE             0x0020
//   PROCESS_DUP_HANDLE           0x0040
//   PROCESS_CREATE_PROCESS       0x0080
//   PROCESS_SET_QUOTA            0x0100
//   PROCESS_SET_INFORMATION      0x0200
//   PROCESS_SUSPEND_RESUME       0x0800
//   PROCESS_SET_LIMITED_INFO     0x2000
//   DELETE / WRITE_DAC / WRITE_OWNER (standard rights)
//
// Rights kept (IsSafeToInject still works, injection attempt then fails):
//   PROCESS_QUERY_INFORMATION    0x0400
//   PROCESS_QUERY_LIMITED_INFO   0x1000
//   READ_CONTROL                 0x00020000
//   SYNCHRONIZE                  0x00100000

#define PROC_RIGHTS_STRIP \
    (0x0001UL | 0x0002UL | 0x0004UL | 0x0008UL | 0x0010UL | 0x0020UL | \
     0x0040UL | 0x0080UL | 0x0100UL | 0x0200UL | 0x0800UL | 0x2000UL | \
     0x00010000UL | 0x00040000UL | 0x00080000UL)

static OB_PREOP_CALLBACK_STATUS ProcPreOp(PVOID ctx, POB_PRE_OPERATION_INFORMATION info)
{
    UNREFERENCED_PARAMETER(ctx);
    if (info->ObjectType != *PsProcessType) return OB_PREOP_SUCCESS;

    LONG64 pid = InterlockedCompareExchange64(&g_ProtectedPid, 0, 0);
    if (!pid) return OB_PREOP_SUCCESS;

    PEPROCESS target = (PEPROCESS)info->Object;
    if ((LONG64)(ULONG_PTR)PsGetProcessId(target) != pid) return OB_PREOP_SUCCESS;

    if (info->Operation == OB_OPERATION_HANDLE_CREATE)
        info->Parameters->CreateHandleInformation.DesiredAccess &= ~PROC_RIGHTS_STRIP;
    else if (info->Operation == OB_OPERATION_HANDLE_DUPLICATE)
        info->Parameters->DuplicateHandleInformation.DesiredAccess &= ~PROC_RIGHTS_STRIP;

    return OB_PREOP_SUCCESS;
}

static NTSTATUS RegisterProtectCallbacks(void)
{
    if (g_ObHandle) return STATUS_SUCCESS; // already registered

    UNICODE_STRING altitude;
    RtlInitUnicodeString(&altitude, L"321123");

    OB_OPERATION_REGISTRATION opReg = {0};
    opReg.ObjectType   = PsProcessType;
    opReg.Operations   = OB_OPERATION_HANDLE_CREATE | OB_OPERATION_HANDLE_DUPLICATE;
    opReg.PreOperation = ProcPreOp;

    OB_CALLBACK_REGISTRATION reg = {0};
    reg.Version                        = OB_FLT_REGISTRATION_VERSION;
    reg.OperationRegistrationCount     = 1;
    reg.Altitude                       = altitude;
    reg.OperationRegistration          = &opReg;

    NTSTATUS st = ObRegisterCallbacks(&reg, &g_ObHandle);
    DbgPrint("kbypass: ObRegisterCallbacks status=%08X handle=%p\n", st, g_ObHandle);
    return st;
}

// ── IOCTL dispatch ────────────────────────────────────────────────────────────
static NTSTATUS DispatchDevCtl(PDEVICE_OBJECT dev, PIRP irp) {
    UNREFERENCED_PARAMETER(dev);
    PIO_STACK_LOCATION sl = IoGetCurrentIrpStackLocation(irp);
    ULONG code   = sl->Parameters.DeviceIoControl.IoControlCode;
    ULONG outLen = sl->Parameters.DeviceIoControl.OutputBufferLength;
    NTSTATUS st  = STATUS_SUCCESS;
    ULONG_PTR info = 0;

    switch (code) {
    case IOCTL_KBYPASS_ACTIVATE:
        InterlockedExchange(&g_Active, 1);
        DbgPrint("kbypass: ACTIVATED\n");
        break;

    case IOCTL_KBYPASS_DEACTIVATE:
        InterlockedExchange(&g_Active, 0);
        DbgPrint("kbypass: DEACTIVATED\n");
        break;

    case IOCTL_KBYPASS_STATUS:
        if (outLen >= sizeof(KBYPASS_STATUS)) {
            PKBYPASS_STATUS s = (PKBYPASS_STATUS)irp->AssociatedIrp.SystemBuffer;
            s->Active       = (ULONG)InterlockedOr(&g_Active, 0);
            s->ClearedTotal = g_ClearedTotal;
            s->LastPassCount= g_LastPass;
            s->PassCount    = g_PassCount;
            info = sizeof(KBYPASS_STATUS);
        } else {
            st = STATUS_BUFFER_TOO_SMALL;
        }
        break;

    case IOCTL_KBYPASS_CLEAR_HWND: {
        // Legacy: user mode passes a single HWND + PID; driver clears it
        PKBYPASS_CLEAR_HWND req =
            (PKBYPASS_CLEAR_HWND)irp->AssociatedIrp.SystemBuffer;
        ULONG inLen = sl->Parameters.DeviceIoControl.InputBufferLength;
        if (inLen < sizeof(KBYPASS_CLEAR_HWND) || !req) {
            st = STATUS_INVALID_PARAMETER; break;
        }
        PVOID  hwnd = (PVOID)(ULONG_PTR)req->hwnd64;
        HANDLE pid  = (HANDLE)(ULONG_PTR)req->pid64;
        PEPROCESS proc = NULL;
        if (!NT_SUCCESS(PsLookupProcessByProcessId(pid, &proc))) {
            st = STATUS_NOT_FOUND; break;
        }
        KAPC_STATE apc = {0};
        KeStackAttachProcess(proc, &apc);
        if (EnsureResolved()) {
            __try { pfnSetWDA(hwnd, WDA_NONE); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        KeUnstackDetachProcess(&apc);
        ObDereferenceObject(proc);
        break;
    }

    case IOCTL_KBYPASS_PROTECT_PID: {
        ULONG inLen = sl->Parameters.DeviceIoControl.InputBufferLength;
        if (inLen < sizeof(KBYPASS_PROTECT_PID)) { st = STATUS_BUFFER_TOO_SMALL; break; }
        PKBYPASS_PROTECT_PID req = (PKBYPASS_PROTECT_PID)irp->AssociatedIrp.SystemBuffer;
        st = RegisterProtectCallbacks();
        if (!NT_SUCCESS(st)) break;
        InterlockedExchange64(&g_ProtectedPid, (LONG64)req->pid64);
        // Install SetWDA hook so external processes can't clear mode7helper's WDA.
        // EnsureResolved() must run in a GUI process context — attempt from guard thread
        // on next tick; InstallSetWDAHook is safe to call here if already resolved.
        if (g_Resolved) InstallSetWDAHook();
        DbgPrint("kbypass: PROTECT_PID pid=%llu hook=%d\n", req->pid64, g_HookActive);
        break;
    }

    case IOCTL_KBYPASS_UNPROTECT_PID:
        RemoveSetWDAHook();
        InterlockedExchange64(&g_ProtectedPid, 0);
        DbgPrint("kbypass: UNPROTECT_PID\n");
        break;

    default:
        st = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    irp->IoStatus.Status = st;
    irp->IoStatus.Information = info;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return st;
}

static NTSTATUS DispatchCreateClose(PDEVICE_OBJECT dev, PIRP irp) {
    UNREFERENCED_PARAMETER(dev);
    irp->IoStatus.Status = STATUS_SUCCESS;
    irp->IoStatus.Information = 0;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

// ── Unload ────────────────────────────────────────────────────────────────────
static VOID DriverUnload(PDRIVER_OBJECT drv) {
    UNREFERENCED_PARAMETER(drv);
    // Remove SetWDA hook before anything else
    RemoveSetWDAHook();
    InterlockedExchange64(&g_ProtectedPid, 0);

    // Signal both threads to stop (NotificationEvent wakes all waiters)
    InterlockedExchange(&g_Active, 0);
    KeSetEvent(&g_StopEvent, 0, FALSE);
    if (g_WorkerThread) {
        KeWaitForSingleObject(g_WorkerThread, Executive, KernelMode, FALSE, NULL);
        ObDereferenceObject(g_WorkerThread);
        g_WorkerThread = NULL;
    }
    if (g_GuardThread) {
        KeWaitForSingleObject(g_GuardThread, Executive, KernelMode, FALSE, NULL);
        ObDereferenceObject(g_GuardThread);
        g_GuardThread = NULL;
    }
    if (g_ObHandle) { ObUnRegisterCallbacks(g_ObHandle); g_ObHandle = NULL; }

    // Remove device
    UNICODE_STRING symlink = RTL_CONSTANT_STRING(KBYPASS_SYMLINK);
    IoDeleteSymbolicLink(&symlink);
    if (g_Device) { IoDeleteDevice(g_Device); g_Device = NULL; }
    DbgPrint("kbypass: unloaded\n");
}

// ── DriverEntry ───────────────────────────────────────────────────────────────
NTSTATUS DriverEntry(PDRIVER_OBJECT drv, PUNICODE_STRING reg) {
    UNREFERENCED_PARAMETER(reg);
    DbgPrint("kbypass: loading\n");

    drv->DriverUnload                         = DriverUnload;
    drv->MajorFunction[IRP_MJ_CREATE]         = DispatchCreateClose;
    drv->MajorFunction[IRP_MJ_CLOSE]          = DispatchCreateClose;
    drv->MajorFunction[IRP_MJ_DEVICE_CONTROL] = DispatchDevCtl;

    UNICODE_STRING devName  = RTL_CONSTANT_STRING(KBYPASS_DEVICE_NAME);
    UNICODE_STRING symlink  = RTL_CONSTANT_STRING(KBYPASS_SYMLINK);

    NTSTATUS st = IoCreateDevice(drv, 0, &devName, FILE_DEVICE_UNKNOWN,
                                 FILE_DEVICE_SECURE_OPEN, FALSE, &g_Device);
    if (!NT_SUCCESS(st)) {
        DbgPrint("kbypass: IoCreateDevice failed %08X\n", st);
        return st;
    }
    g_Device->Flags |= DO_BUFFERED_IO;
    g_Device->Flags &= ~DO_DEVICE_INITIALIZING;

    st = IoCreateSymbolicLink(&symlink, &devName);
    if (!NT_SUCCESS(st)) {
        IoDeleteDevice(g_Device); g_Device = NULL;
        DbgPrint("kbypass: IoCreateSymbolicLink failed %08X\n", st);
        return st;
    }

    // NotificationEvent so KeSetEvent wakes BOTH worker and guard threads at once
    KeInitializeEvent(&g_StopEvent, NotificationEvent, FALSE);

    HANDLE hThread = NULL;
    st = PsCreateSystemThread(&hThread, THREAD_ALL_ACCESS, NULL, NULL, NULL,
                              WorkerThread, NULL);
    if (!NT_SUCCESS(st)) {
        IoDeleteSymbolicLink(&symlink);
        IoDeleteDevice(g_Device); g_Device = NULL;
        DbgPrint("kbypass: PsCreateSystemThread failed %08X\n", st);
        return st;
    }
    ObReferenceObjectByHandle(hThread, THREAD_ALL_ACCESS, NULL, KernelMode,
                              (PVOID*)&g_WorkerThread, NULL);
    ZwClose(hThread);

    // Start WDA guardian thread (keeps mode7helper's WDA locked when PROTECT_PID is set)
    HANDLE hGuard = NULL;
    if (NT_SUCCESS(PsCreateSystemThread(&hGuard, THREAD_ALL_ACCESS, NULL, NULL, NULL,
                                        WdaGuardThread, NULL))) {
        ObReferenceObjectByHandle(hGuard, THREAD_ALL_ACCESS, NULL, KernelMode,
                                  (PVOID*)&g_GuardThread, NULL);
        ZwClose(hGuard);
    }

    DbgPrint("kbypass: loaded, device=%p worker=%p guard=%p\n",
             g_Device, g_WorkerThread, g_GuardThread);
    return STATUS_SUCCESS;
}
