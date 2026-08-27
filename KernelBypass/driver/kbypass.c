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

#include <wdm.h>
#include <ntimage.h>
#include "kbypass.h"

// ── Pool tag ──────────────────────────────────────────────────────────────────
#define POOL_TAG 'pByK'
#define WDA_NONE 0
#define WDA_EXCLUDEFROMCAPTURE 0x11

// ── SystemProcessInformation ──────────────────────────────────────────────────
#define SystemProcessInformation 5
#define SystemModuleInformation  11

typedef struct _SYSTEM_PROCESS_INFO {
    ULONG NextEntryOffset;
    ULONG NumberOfThreads;
    BYTE  Reserved1[48];
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
typedef ULONG    (NTAPI *PFN_NtUserSetWDA)(PVOID hwnd, ULONG aff);
typedef ULONG    (NTAPI *PFN_NtUserGetWDA)(PVOID hwnd, PULONG aff);
typedef NTSTATUS (NTAPI *PFN_NtUserBuildHwndList)(
    PVOID hDesktop, PVOID hwndNext, BOOLEAN fChildren, BOOLEAN fThread,
    ULONG idThread, ULONG cHwnd, PVOID* phwnds, PULONG pcNeeded);

// ── Kernel APIs not in wdm.h ──────────────────────────────────────────────────
NTSYSAPI NTSTATUS NTAPI ZwQuerySystemInformation(ULONG, PVOID, ULONG, PULONG);
NTSYSAPI NTSTATUS NTAPI PsLookupProcessByProcessId(HANDLE, PEPROCESS*);
NTSYSAPI ULONG    NTAPI PsGetProcessSessionId(PEPROCESS);

// ── Globals ───────────────────────────────────────────────────────────────────
static PDEVICE_OBJECT   g_Device        = NULL;
static KEVENT           g_StopEvent     = {0};
static PKTHREAD         g_WorkerThread  = NULL;
static volatile LONG    g_Active        = 0;
static volatile ULONG   g_ClearedTotal  = 0;
static volatile ULONG   g_LastPass      = 0;
static volatile ULONG   g_PassCount     = 0;

static PFN_NtUserSetWDA        pfnSetWDA   = NULL;
static PFN_NtUserGetWDA        pfnGetWDA   = NULL;
static PFN_NtUserBuildHwndList pfnBuildHwnd = NULL;
static BOOLEAN                 g_Resolved  = FALSE;
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
    pfnSetWDA    = (PFN_NtUserSetWDA)       ResolveExport(base, "NtUserSetWindowDisplayAffinity");
    pfnGetWDA    = (PFN_NtUserGetWDA)        ResolveExport(base, "NtUserGetWindowDisplayAffinity");
    pfnBuildHwnd = (PFN_NtUserBuildHwndList) ResolveExport(base, "NtUserBuildHwndList");
    g_Resolved = (pfnSetWDA != NULL && pfnGetWDA != NULL && pfnBuildHwnd != NULL);
    DbgPrint("kbypass: SetWDA=%p GetWDA=%p BuildHwnd=%p resolved=%d\n",
             pfnSetWDA, pfnGetWDA, pfnBuildHwnd, g_Resolved);
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
    // Stop worker thread
    InterlockedExchange(&g_Active, 0);
    KeSetEvent(&g_StopEvent, 0, FALSE);
    if (g_WorkerThread) {
        KeWaitForSingleObject(g_WorkerThread, Executive, KernelMode, FALSE, NULL);
        ObDereferenceObject(g_WorkerThread);
        g_WorkerThread = NULL;
    }
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

    // Initialize stop event and start worker thread
    KeInitializeEvent(&g_StopEvent, SynchronizationEvent, FALSE);
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

    DbgPrint("kbypass: loaded, device=%p thread=%p\n", g_Device, g_WorkerThread);
    return STATUS_SUCCESS;
}
