#pragma once

// ── Device names ──────────────────────────────────────────────────────────────
#define KBYPASS_DEVICE_NAME   L"\\Device\\KernelBypass"
#define KBYPASS_SYMLINK       L"\\DosDevices\\KernelBypass"
#define KBYPASS_WIN32_NAME    "\\\\.\\KernelBypass"

// ── IOCTL codes ────────────────────────────────────────────────────────────────
#ifndef CTL_CODE
#define CTL_CODE(d,f,m,a) (((d)<<16)|((a)<<14)|((f)<<2)|(m))
#endif
#define FILE_DEVICE_UNKNOWN 0x00000022
#define METHOD_BUFFERED     0
#define FILE_ANY_ACCESS     0

// Start the kernel bypass thread — clears WDA_EXCLUDEFROMCAPTURE system-wide
#define IOCTL_KBYPASS_ACTIVATE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Stop the kernel bypass thread
#define IOCTL_KBYPASS_DEACTIVATE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Query driver status
#define IOCTL_KBYPASS_STATUS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x902, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Clear WDA on a single HWND passed from user mode
// Input: KBYPASS_CLEAR_HWND.  Output: none.
#define IOCTL_KBYPASS_CLEAR_HWND \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x903, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Register ObRegisterCallbacks to strip dangerous handle rights for the given PID.
// Input: KBYPASS_PROTECT_PID.  Output: none.
// Requires test signing or production EV cert.
#define IOCTL_KBYPASS_PROTECT_PID \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x904, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Clear the protected PID (stop stripping handles).
// No input/output buffers required.
#define IOCTL_KBYPASS_UNPROTECT_PID \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x905, METHOD_BUFFERED, FILE_ANY_ACCESS)

#pragma pack(push, 1)

typedef struct _KBYPASS_STATUS {
    ULONG  Active;          // 1 = thread running
    ULONG  ClearedTotal;    // total windows cleared since activate
    ULONG  LastPassCount;   // windows cleared in last pass
    ULONG  PassCount;       // total passes executed
} KBYPASS_STATUS, *PKBYPASS_STATUS;

typedef struct _KBYPASS_CLEAR_HWND {
    ULONG64 hwnd64;         // HWND as 64-bit (safe from 32/64 callers)
    ULONG64 pid64;          // owner PID (for KeStackAttachProcess context)
} KBYPASS_CLEAR_HWND, *PKBYPASS_CLEAR_HWND;

typedef struct _KBYPASS_PROTECT_PID {
    ULONG64 pid64;          // PID to protect; 0 = clear protection
} KBYPASS_PROTECT_PID, *PKBYPASS_PROTECT_PID;

#pragma pack(pop)
