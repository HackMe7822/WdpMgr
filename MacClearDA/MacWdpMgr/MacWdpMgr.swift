// MacWdpMgr.swift — Mac Display Policy Manager
// Compile: swiftc MacWdpMgr.swift -framework AppKit -framework Security -o MacWdpMgr
// Mirrors WdpMgr.cs: same license format, same server check-in API, same states.

import AppKit
import Security
import Foundation

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – License
// ─────────────────────────────────────────────────────────────────────────────

struct LicenseData {
    var id           = ""
    var type         = ""   // "lifetime" | "temp" | "days" | "hr"
    var expiry       = ""
    var issued       = ""
    var durationDays = ""
    var appId        = ""
    var server       = ""
    var pubKey       = ""   // base64 DER SubjectPublicKeyInfo  (or XML for compat)
    var sig          = ""   // base64 RSA-SHA256 over canonical fields
}

// Paste the RSA public key from Admin Panel → Settings → WdpMgr Public Key.
// Accepts EITHER format:
//   • Windows XML:  <RSAKeyValue><Modulus>…</Modulus><Exponent>…</Exponent></RSAKeyValue>
//   • PEM/DER b64:  base64-encoded SubjectPublicKeyInfo
let RSA_PUBLIC_KEY = "REPLACE_WITH_SERVER_PUBLIC_KEY"

let LOG_PATH  = "/tmp/MacClearDA.log"
let LIC_BEGIN = "MACDPOLICY_LIC_BEGIN\n"
let LIC_END   = "MACDPOLICY_LIC_END"

func log(_ msg: String) {
    let line = "[MacWdpMgr] \(msg)\n"
    if let data = line.data(using: .utf8) {
        let url = URL(fileURLWithPath: LOG_PATH)
        if let fh = try? FileHandle(forWritingTo: url) {
            fh.seekToEndOfFile(); fh.write(data); fh.closeFile()
        } else { try? data.write(to: url, options: .atomic) }
    }
}

// ── Read license from wdp.lic next to binary, or from EXE tail ──────────────
func readLicense() -> LicenseData? {
    let exeDir = URL(fileURLWithPath: CommandLine.arguments[0])
        .deletingLastPathComponent().path
    let licPath = "\(exeDir)/wdp.lic"

    if FileManager.default.fileExists(atPath: licPath),
       let text = try? String(contentsOfFile: licPath, encoding: .utf8) {
        return parseLicenseText(text)
    }
    return readEmbeddedLicense()
}

func parseLicenseText(_ text: String) -> LicenseData? {
    let lines = text.components(separatedBy: "\n")
    guard lines.first?.trimmingCharacters(in: .whitespaces) == "MACDPOLICY_LICENSE_V1" else {
        return nil
    }
    var lic = LicenseData()
    for line in lines {
        let parts = line.split(separator: "=", maxSplits: 1)
        guard parts.count == 2 else { continue }
        let k = String(parts[0]).trimmingCharacters(in: .whitespaces)
        let v = String(parts[1]).trimmingCharacters(in: .whitespaces)
        switch k {
        case "id":           lic.id           = v
        case "type":         lic.type         = v
        case "expiry":       lic.expiry       = v
        case "issued":       lic.issued       = v
        case "durationDays": lic.durationDays = v
        case "appId":        lic.appId        = v
        case "server":       lic.server       = v
        case "pubkey":       lic.pubKey       = v
        case "sig":          lic.sig          = v
        default: break
        }
    }
    return (lic.id.isEmpty || lic.sig.isEmpty) ? nil : lic
}

func readEmbeddedLicense() -> LicenseData? {
    guard let exePath = CommandLine.arguments.first,
          let data = FileManager.default.contents(atPath: exePath),
          let text = String(data: data, encoding: .utf8) else { return nil }
    guard let beginRange = text.range(of: LIC_BEGIN),
          let endRange   = text.range(of: LIC_END,
              range: beginRange.upperBound..<text.endIndex) else { return nil }
    let body = String(text[beginRange.upperBound..<endRange.lowerBound])
    return parseLicenseText("MACDPOLICY_LICENSE_V1\n" + body)
}

// ── Key format helpers ────────────────────────────────────────────────────────

// Accept Windows RSA XML (<RSAKeyValue><Modulus>…</Modulus><Exponent>…</Exponent></RSAKeyValue>)
// OR plain base64 DER SubjectPublicKeyInfo.  Returns DER bytes.
func derFromKey(_ raw: String) -> Data? {
    let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)

    // ── Windows XML format ────────────────────────────────────────────────────
    if trimmed.hasPrefix("<RSAKeyValue>") {
        func extract(_ tag: String) -> Data? {
            guard let s = trimmed.range(of: "<\(tag)>"),
                  let e = trimmed.range(of: "</\(tag)>"),
                  s.upperBound <= e.lowerBound else { return nil }
            return Data(base64Encoded: String(trimmed[s.upperBound..<e.lowerBound]))
        }
        guard var mod = extract("Modulus"),
              var exp = extract("Exponent") else { return nil }

        // RSAPublicKey ::= SEQUENCE { modulus INTEGER, publicExponent INTEGER }
        func asn1Int(_ d: Data) -> Data {
            var b = d
            // Strip leading zeros, add 0x00 if high bit set (positive integer marker)
            while b.count > 1 && b[0] == 0 { b = b.dropFirst() }
            if b[0] & 0x80 != 0 { b = Data([0x00]) + b }
            return encodeASN1(tag: 0x02, content: b)
        }
        func encodeASN1(tag: UInt8, content: Data) -> Data {
            var out = Data([tag])
            let len = content.count
            if len < 0x80 {
                out.append(UInt8(len))
            } else if len < 0x100 {
                out.append(contentsOf: [0x81, UInt8(len)])
            } else {
                out.append(contentsOf: [0x82, UInt8(len >> 8), UInt8(len & 0xFF)])
            }
            out.append(content)
            return out
        }
        let rsaSeq = encodeASN1(tag: 0x30, content: asn1Int(mod) + asn1Int(exp))
        // SubjectPublicKeyInfo = SEQUENCE { AlgorithmIdentifier, BIT STRING { rsaSeq } }
        let oid: [UInt8] = [0x30,0x0D,0x06,0x09,0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01,0x05,0x00]
        let bitStr = encodeASN1(tag: 0x03, content: Data([0x00]) + rsaSeq)
        return encodeASN1(tag: 0x30, content: Data(oid) + bitStr)
    }

    // ── Plain base64 DER / PEM body ───────────────────────────────────────────
    let stripped = trimmed
        .replacingOccurrences(of: "-----BEGIN PUBLIC KEY-----", with: "")
        .replacingOccurrences(of: "-----END PUBLIC KEY-----", with: "")
        .replacingOccurrences(of: "\n", with: "")
        .replacingOccurrences(of: "\r", with: "")
        .trimmingCharacters(in: .whitespaces)
    return Data(base64Encoded: stripped)
}

// ── Verify RSA-SHA256 signature ──────────────────────────────────────────────
func verifyLicense(_ lic: LicenseData) -> Bool {
    // Canonical message: same field order as Windows WdpMgr
    let msg = "\(lic.id)|\(lic.type)|\(lic.expiry)|\(lic.issued)|\(lic.appId)"
    guard let msgData = msg.data(using: .utf8),
          let sigData = Data(base64Encoded: lic.sig) else { return false }

    // Use pubkey from license file if present, else fall back to embedded constant
    let rawKey = lic.pubKey.isEmpty ? RSA_PUBLIC_KEY : lic.pubKey
    guard rawKey != "REPLACE_WITH_SERVER_PUBLIC_KEY" else {
        log("WARN: public key not configured"); return false
    }
    // Accept both Windows RSA XML and plain base64 DER
    guard let keyDER = derFromKey(rawKey) else {
        log("WARN: could not parse public key"); return false
    }

    let attrs: [String: Any] = [
        kSecAttrKeyType as String:  kSecAttrKeyTypeRSA,
        kSecAttrKeyClass as String: kSecAttrKeyClassPublic,
    ]
    var err: Unmanaged<CFError>?
    guard let secKey = SecKeyCreateWithData(keyDER as CFData, attrs as CFDictionary, &err)
    else {
        log("RSA key import failed: \(err!.takeRetainedValue())")
        return false
    }
    let ok = SecKeyVerifySignature(
        secKey,
        .rsaSignatureMessagePKCS1v15SHA256,
        msgData as CFData,
        sigData as CFData,
        &err
    )
    if !ok { log("Signature invalid: \(err?.takeRetainedValue().localizedDescription ?? "")") }
    return ok
}

// ── Server check-in (same HTTP API as Windows) ───────────────────────────────
// Returns: "ok" | "expired" | "revoked" | "wrong_machine" | "invalid" | "offline"
@discardableResult
func checkIn(_ lic: LicenseData) -> String {
    guard !lic.server.isEmpty else { return "offline" }

    var machineId = ""
    if let output = shell("system_profiler SPHardwareDataType | awk '/Serial Number/{print $4}'") {
        machineId = output.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var comps = URLComponents(string: "\(lic.server)/api/checkin")!
    comps.queryItems = [
        URLQueryItem(name: "id",        value: lic.id),
        URLQueryItem(name: "appId",     value: lic.appId),
        URLQueryItem(name: "machine",   value: machineId),
        URLQueryItem(name: "platform",  value: "mac"),
    ]
    guard let url = comps.url else { return "offline" }

    let sem = DispatchSemaphore(value: 0)
    var result = "offline"
    let task = URLSession.shared.dataTask(with: url) { data, _, _ in
        if let data, let body = String(data: data, encoding: .utf8) {
            let s = body.trimmingCharacters(in: .whitespacesAndNewlines)
            if ["ok","expired","revoked","wrong_machine","invalid"].contains(s) {
                result = s
            }
        }
        sem.signal()
    }
    task.resume()
    _ = sem.wait(timeout: .now() + 10)
    return result
}

// Start periodic check-in loop (every 30 min), same as Windows StartLicenseLoop
func startLicenseLoop(_ lic: LicenseData) {
    Thread.detachNewThread {
        while true {
            Thread.sleep(forTimeInterval: 1800)
            let status = checkIn(lic)
            log("Periodic check-in: \(status)")
            if status == "revoked" || status == "expired" {
                log("License \(status) by server — self-removing")
                selfDestruct()
                exit(0)
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – Injection (equivalent to InjectAll / InjectProcess in WdpMgr.cs)
// ─────────────────────────────────────────────────────────────────────────────

let DYLIB_NAME = "maccore.dylib"

// Processes to skip (mirrors s_block in WdpMgr.cs)
let BLOCKED: Set<String> = [
    "Finder","Dock","SystemUIServer","WindowServer","loginwindow",
    "launchd","MacWdpMgr","kernel_task","notifyd","cfprefsd"
]

var injectedPIDs: Set<pid_t> = []

func dylibPath() -> String {
    let dir = URL(fileURLWithPath: CommandLine.arguments[0])
        .deletingLastPathComponent().path
    return "\(dir)/\(DYLIB_NAME)"
}

// Inject into a running process using lldb (requires SIP disabled or root).
// Equivalent to CreateRemoteThread + LoadLibraryA in WdpMgr.cs.
func injectPID(_ pid: pid_t) -> Bool {
    let dylib = dylibPath()
    guard FileManager.default.fileExists(atPath: dylib) else {
        log("DYLIB not found: \(dylib)"); return false
    }
    // Use lldb to call dlopen inside the target process
    let script = """
    expr (void*)dlopen("\(dylib)", 2)
    detach
    quit
    """
    let result = shell(
        "echo '\(script)' | lldb --attach-pid \(pid) --source /dev/stdin 2>/dev/null"
    )
    let ok = (result != nil)
    log("inject pid=\(pid): \(ok ? "ok" : "FAILED")")
    return ok
}

// Enumerate all running GUI processes and inject into new ones
func injectAll() -> Int {
    var count = 0
    let apps = NSWorkspace.shared.runningApplications
    for app in apps {
        guard let name = app.localizedName,
              !BLOCKED.contains(name),
              app.processIdentifier > 0 else { continue }
        let pid = app.processIdentifier
        if injectedPIDs.contains(pid) { continue }
        if injectPID(pid) {
            injectedPIDs.insert(pid)
            count += 1
        }
    }
    return count
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – LaunchAgent (equivalent to Windows service install/uninstall)
// ─────────────────────────────────────────────────────────────────────────────

let PLIST_LABEL = "com.creationsit.macdpolicy"
let PLIST_PATH  = "\(NSHomeDirectory())/Library/LaunchAgents/\(PLIST_LABEL).plist"

func installService() -> Bool {
    let exePath = (CommandLine.arguments[0] as NSString).resolvingSymlinksInPath
    let plist: [String: Any] = [
        "Label":             PLIST_LABEL,
        "ProgramArguments": [exePath, "--service"],
        "RunAtLoad":         true,
        "KeepAlive":         true,
        "StandardOutPath":   LOG_PATH,
        "StandardErrorPath": LOG_PATH,
    ]
    do {
        let data = try PropertyListSerialization.data(
            fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: URL(fileURLWithPath: PLIST_PATH))
    } catch { log("plist write: \(error)"); return false }

    let r = shell("launchctl load '\(PLIST_PATH)'")
    log("launchctl load: \(r ?? "nil")")
    return true
}

func uninstallService() {
    _ = shell("launchctl unload '\(PLIST_PATH)' 2>/dev/null")
    try? FileManager.default.removeItem(atPath: PLIST_PATH)
    log("service uninstalled")
}

func serviceStatus() -> String {
    guard FileManager.default.fileExists(atPath: PLIST_PATH) else {
        return "Not installed"
    }
    if let out = shell("launchctl list '\(PLIST_LABEL)' 2>/dev/null"),
       !out.isEmpty { return "Running" }
    return "Stopped"
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – Self-destruct (same behaviour as WdpMgr SelfDestruct on revocation)
// ─────────────────────────────────────────────────────────────────────────────

func selfDestruct() {
    uninstallService()
    let path = (CommandLine.arguments[0] as NSString).resolvingSymlinksInPath
    _ = shell("rm -f '\(path)'")
    log("self-destruct: binary deleted")
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – Helpers
// ─────────────────────────────────────────────────────────────────────────────

@discardableResult
func shell(_ cmd: String) -> String? {
    let proc = Process()
    proc.launchPath = "/bin/bash"
    proc.arguments  = ["-c", cmd]
    let pipe = Pipe()
    proc.standardOutput = pipe
    proc.standardError  = Pipe()
    do { try proc.run() } catch { return nil }
    proc.waitUntilExit()
    return String(data: pipe.fileHandleForReading.readDataToEndOfFile(),
                  encoding: .utf8)
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – Service mode (runs when launchd starts with --service)
// ─────────────────────────────────────────────────────────────────────────────

func runServiceMode() {
    log("=== Service worker started PID=\(ProcessInfo.processInfo.processIdentifier) ===")

    guard let lic = readLicense() else {
        log("No license — stopping"); exit(1)
    }
    guard verifyLicense(lic) else {
        log("Invalid signature — stopping"); exit(1)
    }
    let status = checkIn(lic)
    log("Check-in on start: \(status)")
    if ["expired","revoked","invalid"].contains(status) {
        log("License \(status) — self-removing")
        selfDestruct(); exit(0)
    }
    startLicenseLoop(lic)

    while true {
        let n = injectAll()
        if n > 0 { log("Injected \(n) new process(es)") }
        Thread.sleep(forTimeInterval: 2.0) // 2s poll, same cadence as Windows 20×100ms
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – GUI (mirrors MainForm in WdpMgr.cs)
// ─────────────────────────────────────────────────────────────────────────────

let BG_COLOR     = NSColor(calibratedRed: 16/255, green: 51/255, blue: 100/255, alpha: 1)
let BG2_COLOR    = NSColor(calibratedRed: 11/255, green: 37/255, blue:  76/255, alpha: 1)
let YELLOW_COLOR = NSColor(calibratedRed: 1, green: 220/255, blue: 100/255, alpha: 1)

class AppDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow!
    var statusLabel: NSTextField!
    var btnInstall:   NSButton!
    var btnUninstall: NSButton!
    var btnOnce:      NSButton!

    func applicationDidFinishLaunching(_ n: Notification) {
        let w = NSWindow(
            contentRect: NSMakeRect(0, 0, 540, 242),
            styleMask:   [.titled, .closable, .miniaturizable],
            backing:     .buffered, defer: false)
        w.title = "Mac Display Policy Manager"
        w.center()
        w.backgroundColor = BG_COLOR
        window = w

        guard let cv = w.contentView else { return }
        cv.wantsLayer = true
        cv.layer?.backgroundColor = BG_COLOR.cgColor

        // Description
        let desc = makeLabel(
            "Click the buttons below to install or uninstall the Mac Display Policy Manager service.\n\n" +
            "When installed, this software runs as a LaunchAgent and manages display policy settings for all active desktop sessions.",
            frame: NSMakeRect(12, 142, 296, 80), color: .white)
        desc.maximumNumberOfLines = 0
        cv.addSubview(desc)

        // Shield logo area
        let logo = LogoView(frame: NSMakeRect(318, 110, 170, 125))
        cv.addSubview(logo)

        // Status label
        statusLabel = makeLabel("", frame: NSMakeRect(12, 100, 510, 20), color: YELLOW_COLOR)
        cv.addSubview(statusLabel)

        // Separator
        let sep = NSView(frame: NSMakeRect(0, 62, 540, 1))
        sep.wantsLayer = true
        sep.layer?.backgroundColor = NSColor(calibratedRed: 38/255, green: 78/255,
                                              blue: 130/255, alpha: 1).cgColor
        cv.addSubview(sep)

        // Bottom panel
        let bottom = NSView(frame: NSMakeRect(0, 0, 540, 62))
        bottom.wantsLayer = true
        bottom.layer?.backgroundColor = BG2_COLOR.cgColor
        cv.addSubview(bottom)

        btnInstall   = makeBtn("Install",   x: 10,  in: bottom, action: #selector(doInstall))
        btnUninstall = makeBtn("Uninstall", x: 115, in: bottom, action: #selector(doUninstall))
        btnOnce      = makeBtn("Run Once",  x: 220, in: bottom, action: #selector(doRunOnce))
        let btnLog   = makeBtn("View Log",  x: 325, in: bottom, action: #selector(doViewLog))
        let btnClose = makeBtn("Close",     x: 435, in: bottom, action: #selector(doClose))
        _ = btnLog; _ = btnClose

        refreshStatus()
        w.makeKeyAndOrderFront(nil)
    }

    @objc func doInstall() {
        guard let lic = readLicense() else {
            alert("No license found in this EXE.\n\nDownload from the admin panel (Licenses → ⬇ EXE).")
            return
        }
        guard verifyLicense(lic) else {
            alert("License signature verification failed.\n\nRe-download the EXE from the admin panel.")
            return
        }
        statusLabel.stringValue = "Checking license…"
        DispatchQueue.global().async {
            let status = checkIn(lic)
            log("Pre-flight check-in: \(status)")
            DispatchQueue.main.async {
                switch status {
                case "revoked":      self.alert("This license has been revoked. Contact your admin."); self.refreshStatus(); return
                case "expired":      self.alert("License expired. Ask admin to extend, then re-download."); self.refreshStatus(); return
                case "wrong_machine": self.alert("Max seats reached for this license."); self.refreshStatus(); return
                case "invalid":      self.alert("Server rejected this license as invalid."); self.refreshStatus(); return
                default: break  // "ok" or "offline" — proceed
                }
                let ok = installService()
                self.refreshStatus()
                if ok {
                    startLicenseLoop(lic)
                    self.alert("Service installed. Display affinity bypass is now active.")
                } else {
                    self.alert("Install failed. Check \(LOG_PATH) for details.")
                }
            }
        }
    }

    @objc func doUninstall() {
        let r = NSAlert()
        r.messageText     = "Confirm"
        r.informativeText = "Stop and remove the Mac Display Policy Manager service?\n\nThis will also delete the application."
        r.addButton(withTitle: "Yes")
        r.addButton(withTitle: "No")
        guard r.runModal() == .alertFirstButtonReturn else { return }
        uninstallService()
        alert("Service removed. The application will now delete itself.")
        selfDestruct()
        NSApp.terminate(nil)
    }

    @objc func doRunOnce() {
        guard let lic = readLicense(), verifyLicense(lic) else {
            alert("No valid license found."); return
        }
        let n = injectAll()
        alert(n > 0
            ? "Hooked \(n) new process(es). Display affinity cleared."
            : "No new processes to hook. Check \(LOG_PATH).")
    }

    @objc func doViewLog() {
        if !FileManager.default.fileExists(atPath: LOG_PATH) {
            alert("No log yet. Install the service or click Run Once first."); return
        }
        NSWorkspace.shared.open(URL(fileURLWithPath: LOG_PATH))
    }

    @objc func doClose() { NSApp.terminate(nil) }

    func refreshStatus() {
        var licInfo = "License: NOT FOUND"
        if let lic = readLicense() {
            if verifyLicense(lic) {
                switch lic.type {
                case "temp":     licInfo = "License: Temp — expires \(lic.expiry)"
                case "days":     licInfo = "License: Days — \(lic.durationDays)h from activation"
                case "hr":       licInfo = "License: HR/Per-seat\(lic.expiry.isEmpty ? "" : " — until \(lic.expiry)")"
                default:         licInfo = "License: Lifetime"
                }
            } else {
                licInfo = "License: INVALID SIGNATURE"
            }
        }
        let st = serviceStatus()
        statusLabel.stringValue = "Status: \(st)    |    \(licInfo)"
        btnInstall.isEnabled   = (st != "Running")
        btnUninstall.isEnabled = (st != "Not installed")
    }

    func alert(_ msg: String) {
        let a = NSAlert()
        a.messageText = "Mac Display Policy Manager"
        a.informativeText = msg
        a.runModal()
    }

    func makeLabel(_ text: String, frame: NSRect, color: NSColor) -> NSTextField {
        let f = NSTextField(frame: frame)
        f.stringValue = text; f.textColor = color
        f.isBezeled = false; f.isEditable = false
        f.backgroundColor = .clear; f.drawsBackground = false
        return f
    }

    func makeBtn(_ title: String, x: CGFloat, in parent: NSView, action: Selector) -> NSButton {
        let b = NSButton(frame: NSMakeRect(x, 16, 92, 30))
        b.title = title
        b.target = self; b.action = action
        parent.addSubview(b)
        return b
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

// Shield logo drawn in code (mirrors DrawLogo in WdpMgr.cs)
class LogoView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.white.setFill()
        NSBezierPath.fill(bounds)
        let pts: [NSPoint] = [
            NSPoint(x: 85, y: 8),  NSPoint(x: 140, y: 30), NSPoint(x: 140, y: 68),
            NSPoint(x: 85, y: 98), NSPoint(x: 30,  y: 68), NSPoint(x: 30,  y: 30)
        ]
        let shield = NSBezierPath()
        shield.move(to: pts[0])
        for p in pts.dropFirst() { shield.line(to: p) }
        shield.close()
        BG_COLOR.setFill(); shield.fill()
        NSColor(calibratedRed: 38/255, green: 78/255, blue: 130/255, alpha: 1).setStroke()
        shield.lineWidth = 2; shield.stroke()
        // Tick
        let tick = NSBezierPath()
        tick.move(to: NSPoint(x: 58, y: 58))
        tick.line(to: NSPoint(x: 76, y: 76))
        tick.line(to: NSPoint(x: 114, y: 40))
        NSColor.white.setStroke(); tick.lineWidth = 4; tick.stroke()
        // Label
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.boldSystemFont(ofSize: 7.5),
            .foregroundColor: BG_COLOR
        ]
        let s = NSAttributedString(string: "Mac Display Policy", attributes: attrs)
        s.draw(at: NSPoint(x: 20, y: 4))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: – Entry point
// ─────────────────────────────────────────────────────────────────────────────

if CommandLine.arguments.contains("--service") {
    runServiceMode()
} else {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.regular)
    app.run()
}
