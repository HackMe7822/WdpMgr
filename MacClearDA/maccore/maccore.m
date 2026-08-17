#define TARGET_OS_MAC 1
#import <Cocoa/Cocoa.h>
#import <objc/runtime.h>
#import <pthread.h>

/* Mirror of wdpcore.c:
 *  - Hooks -[NSWindow setSharingType:] instead of patching SetWindowDisplayAffinity
 *  - Background thread retries for 3s/60 cycles (same cadence as Windows)
 *  - Logs to /tmp/MacClearDA.log  (Windows uses C:\ProgramData\WdpCore.log)
 */

static void DbgLog(const char *msg) {
    FILE *f = fopen("/tmp/MacClearDA.log", "a");
    if (!f) return;
    fprintf(f, "[maccore pid=%d] %s\n", (int)getpid(), msg);
    fclose(f);
}

/* Original IMP saved before swizzle */
static void (*g_origSetSharingType)(id, SEL, NSWindowSharingType);
static BOOL g_hooked = NO;

/* Hook: silently block any attempt to hide a window from screen capture */
static void hook_setSharingType(id self, SEL _cmd, NSWindowSharingType sharingType) {
    if (sharingType == NSWindowSharingNone) {
        char buf[256];
        snprintf(buf, sizeof(buf),
            "  Blocked setSharingType:None on %p -> ReadOnly",
            (void *)(__bridge void *)self);
        DbgLog(buf);
        sharingType = NSWindowSharingReadOnly;
    }
    g_origSetSharingType(self, _cmd, sharingType);
}

static void InstallHook(void) {
    if (g_hooked) return;
    Method m = class_getInstanceMethod(objc_getClass("NSWindow"),
                                       @selector(setSharingType:));
    if (!m) { DbgLog("WARN: -[NSWindow setSharingType:] not found"); return; }
    g_origSetSharingType =
        (void (*)(id, SEL, NSWindowSharingType))method_getImplementation(m);
    method_setImplementation(m, (IMP)hook_setSharingType);
    g_hooked = YES;
    DbgLog("Hook installed: -[NSWindow setSharingType:]");
}

static void RemoveHook(void) {
    if (!g_hooked) return;
    Method m = class_getInstanceMethod(objc_getClass("NSWindow"),
                                       @selector(setSharingType:));
    if (m && g_origSetSharingType)
        method_setImplementation(m, (IMP)g_origSetSharingType);
    g_hooked = NO;
}

/* Enumerate windows of this process and clear any that have sharing disabled.
   Temporarily removes the hook so our own setSharingType call goes through. */
static void ClearWindows(int cycle) {
    @autoreleasepool {
        @try {
            RemoveHook();
            NSArray<NSWindow *> *wins = [NSApp windows];
            for (NSWindow *win in wins) {
                if ([win sharingType] == NSWindowSharingNone) {
                    char buf[160];
                    snprintf(buf, sizeof(buf),
                        "  Cleared window %p (cycle %d)",
                        (void *)(__bridge void *)win, cycle);
                    DbgLog(buf);
                    [win setSharingType:NSWindowSharingReadOnly];
                }
            }
        } @catch (id) {}
        InstallHook();
    }
}

/* Background thread — 60 cycles × 50 ms = 3 s of retries, same as wdpcore.c */
static void *ClearThread(void *arg) {
    (void)arg;
    for (int i = 0; i < 60; i++) {
        ClearWindows(i);
        struct timespec ts = { 0, 50 * 1000 * 1000 }; /* 50 ms */
        nanosleep(&ts, NULL);
    }
    char buf[80];
    snprintf(buf, sizeof(buf), "Initial clear done (60 cycles). Hook remains active.");
    DbgLog(buf);
    /* Hook stays installed for the lifetime of this process */
    return NULL;
}

__attribute__((constructor))
static void Initialize(void) {
    char buf[128];
    snprintf(buf, sizeof(buf), "maccore loaded pid=%d", (int)getpid());
    DbgLog(buf);

    InstallHook();

    pthread_t thr;
    pthread_create(&thr, NULL, ClearThread, NULL);
    pthread_detach(thr);

    DbgLog("maccore init done");
}

__attribute__((destructor))
static void Cleanup(void) {
    RemoveHook();
    DbgLog("maccore unloaded");
}
