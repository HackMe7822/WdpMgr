import Cocoa
import WebKit
import CommonCrypto
import IOKit
import Security

// ── License data ──────────────────────────────────────────────────────────────
struct LicenseData {
    var id           = ""
    var type         = ""
    var expiry       = ""
    var issued       = ""
    var server       = ""
    var sig          = ""
    var pubKeyXml    = ""
    var durationDays = 0
    var appId        = ""
}

// ── Entry point ───────────────────────────────────────────────────────────────
class AppDelegate: NSObject, NSApplicationDelegate {
    var overlayWindow: OverlayWindow?

    func applicationDidFinishLaunching(_ aNotification: Notification) {
        guard let lic = loadLicense() else {
            alert("No valid license found.\n\nPlace a .lic file in the same directory as MacOverlay or use a licensed build.",
                  title: "MacOverlay — License Missing")
            NSApp.terminate(nil); return
        }
        if let err = validateLicense(lic) {
            alert("License error: \(err)"); NSApp.terminate(nil); return
        }
        let status = checkIn(lic)
        if status == "revoked" { alert("License has been revoked."); NSApp.terminate(nil); return }
        if status == "expired" { alert("License has expired."); NSApp.terminate(nil); return }
        if status == "invalid" { alert("License rejected by server."); NSApp.terminate(nil); return }

        overlayWindow = OverlayWindow(lic: lic)
        overlayWindow?.makeKeyAndOrderFront(nil)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

// ── License loading ───────────────────────────────────────────────────────────
func loadLicense() -> LicenseData? {
    // Try embedded in binary tail
    if let lic = readEmbedded() { return lic }
    // Try .lic file next to executable
    let exeDir = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
    let licFiles = (try? FileManager.default.contentsOfDirectory(at: exeDir,
        includingPropertiesForKeys: nil))?.filter { $0.pathExtension == "lic" } ?? []
    for f in licFiles {
        if let txt = try? String(contentsOf: f), let ld = parseLicBlock(txt), !ld.id.isEmpty { return ld }
    }
    // Try ~/Library/Application Support/MacOverlay/*.lic
    if let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
        let dir = appSupport.appendingPathComponent("MacOverlay")
        let files = (try? FileManager.default.contentsOfDirectory(at: dir,
            includingPropertiesForKeys: nil))?.filter { $0.pathExtension == "lic" } ?? []
        for f in files {
            if let txt = try? String(contentsOf: f), let ld = parseLicBlock(txt), !ld.id.isEmpty { return ld }
        }
    }
    return nil
}

func readEmbedded() -> LicenseData? {
    guard let exePath = Bundle.main.executableURL?.path,
          let data = FileManager.default.contents(atPath: exePath) else { return nil }
    let tail = data.suffix(8192)
    guard let tailStr = String(data: tail, encoding: .utf8) else { return nil }
    guard let begin = tailStr.range(of: "WDPMGR_LIC_BEGIN"),
          let end   = tailStr.range(of: "WDPMGR_LIC_END"),
          begin.upperBound < end.lowerBound else { return nil }
    let block = String(tailStr[begin.upperBound..<end.lowerBound])
    return parseLicBlock(block)
}

func parseLicBlock(_ block: String) -> LicenseData? {
    var ld = LicenseData()
    for line in block.components(separatedBy: "\n") {
        guard let ci = line.firstIndex(of: "=") else { continue }
        let k = String(line[line.startIndex..<ci]).trimmingCharacters(in: .whitespaces)
        let v = String(line[line.index(after: ci)...]).trimmingCharacters(in: .whitespacesAndNewlines)
        switch k {
        case "id":           ld.id           = v
        case "type":         ld.type         = v
        case "expiry":       ld.expiry       = v
        case "issued":       ld.issued       = v
        case "server":       ld.server       = v
        case "sig":          ld.sig          = v
        case "durationDays": ld.durationDays = Int(v) ?? 0
        case "appId":        ld.appId        = v
        case "pubkey":
            if let d = Data(base64Encoded: v), let s = String(data: d, encoding: .utf8) { ld.pubKeyXml = s }
            else { ld.pubKeyXml = v }
        default: break
        }
    }
    return ld.id.isEmpty ? nil : ld
}

// ── License validation (RSA-2048 SHA256 PKCS1) ───────────────────────────────
func validateLicense(_ lic: LicenseData) -> String? {
    guard !lic.pubKeyXml.isEmpty else { return "missing public key" }
    guard let pubKey = importRSAPublicKeyFromXml(lic.pubKeyXml) else { return "public key import failed" }
    guard let sigData = Data(base64Encoded: lic.sig),
          let payload = lic.id.data(using: .utf8) else { return "bad signature data" }
    var error: Unmanaged<CFError>?
    let ok = SecKeyVerifySignature(pubKey,
        .rsaSignatureMessagePKCS1v15SHA256,
        payload as CFData, sigData as CFData, &error)
    if !ok { return "signature invalid" }
    if lic.type == "temp", !lic.expiry.isEmpty {
        let fmt = ISO8601DateFormatter()
        if let ed = fmt.date(from: lic.expiry) ?? parseSimpleDate(lic.expiry), ed < Date() { return "expired" }
    }
    return nil
}

func importRSAPublicKeyFromXml(_ xml: String) -> SecKey? {
    // Parse Modulus and Exponent from RSAKeyValue XML
    guard let mod = xmlValue(xml, tag: "Modulus"),
          let exp = xmlValue(xml, tag: "Exponent"),
          let modData = Data(base64Encoded: mod),
          let expData = Data(base64Encoded: exp) else { return nil }
    // Build DER-encoded RSA public key
    var der = Data()
    der.append(contentsOf: [0x30, 0x00]) // SEQ placeholder
    der.append(contentsOf: [0x02]) // INTEGER (modulus)
    appendDERLength(&der, modData.count + (modData.first! >= 0x80 ? 1 : 0))
    if modData.first! >= 0x80 { der.append(0x00) }
    der.append(modData)
    der.append(contentsOf: [0x02]) // INTEGER (exponent)
    appendDERLength(&der, expData.count)
    der.append(expData)
    // Fix outer SEQ length
    let inner = der.dropFirst(2)
    var final = Data()
    final.append(0x30); appendDERLength(&final, inner.count); final.append(inner)
    let attrs: [String: Any] = [
        kSecAttrKeyType as String:       kSecAttrKeyTypeRSA,
        kSecAttrKeyClass as String:      kSecAttrKeyClassPublic,
        kSecAttrKeySizeInBits as String: 2048
    ]
    var err: Unmanaged<CFError>?
    return SecKeyCreateWithData(final as CFData, attrs as CFDictionary, &err)
}

func appendDERLength(_ data: inout Data, _ length: Int) {
    if length < 0x80 { data.append(UInt8(length)) }
    else if length < 0x100 { data.append(0x81); data.append(UInt8(length)) }
    else { data.append(0x82); data.append(UInt8((length >> 8) & 0xff)); data.append(UInt8(length & 0xff)) }
}

func xmlValue(_ xml: String, tag: String) -> String? {
    guard let r = xml.range(of: "<\(tag)>"),
          let e = xml.range(of: "</\(tag)>"), r.upperBound < e.lowerBound else { return nil }
    return String(xml[r.upperBound..<e.lowerBound])
}

func parseSimpleDate(_ s: String) -> Date? {
    let fmt = DateFormatter()
    for f in ["yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm","yyyy-MM-dd"] {
        fmt.dateFormat = f; fmt.timeZone = TimeZone(abbreviation: "UTC")
        if let d = fmt.date(from: s) { return d }
    }
    return nil
}

// ── Server check-in ───────────────────────────────────────────────────────────
func checkIn(_ lic: LicenseData) -> String {
    guard !lic.server.isEmpty, !lic.server.hasPrefix("REPLACE") else { return "ok" }
    let fp   = getFingerprint()
    let host = escJson(Host.current().localizedName ?? ProcessInfo.processInfo.hostName)
    let user = escJson(NSUserName())
    let json = "{\"licenseId\":\"\(escJson(lic.id))\",\"fingerprint\":\"\(fp)\",\"hostname\":\"\(host)\",\"windowsUser\":\"\(user)\",\"appId\":\"macoverlay\"}"
    guard let url = URL(string: lic.server.trimmingCharacters(in: .init(charactersIn: "/")) + "/api/checkin"),
          let body = json.data(using: .utf8) else { return "ok" }
    var req = URLRequest(url: url)
    req.httpMethod = "POST"
    req.setValue("application/json", forHTTPHeaderField: "Content-Type")
    req.httpBody = body
    var result = "ok"
    let sem = DispatchSemaphore(value: 0)
    URLSession.shared.dataTask(with: req) { data, _, _ in
        if let d = data, let s = String(data: d, encoding: .utf8) {
            if let r = s.range(of: "\"status\":\"") {
                let after = s[r.upperBound...]
                if let q = after.firstIndex(of: "\"") { result = String(after[after.startIndex..<q]) }
            }
        }
        sem.signal()
    }.resume()
    _ = sem.wait(timeout: .now() + 8)
    return result
}

func getFingerprint() -> String {
    // Use IOPlatformUUID (hardware UUID) as fingerprint
    let service = IOServiceGetMatchingService(kIOMainPortDefault,
        IOServiceMatching("IOPlatformExpertDevice") as CFDictionary)
    defer { IOObjectRelease(service) }
    let uuid = IORegistryEntryCreateCFProperty(service,
        "IOPlatformUUID" as CFString, kCFAllocatorDefault, 0)?
        .takeRetainedValue() as? String ?? "unknown"
    if let d = (uuid + "|macoverlay").data(using: .utf8) {
        var hash = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
        d.withUnsafeBytes { ptr in _ = CC_SHA256(ptr.baseAddress, CC_LONG(d.count), &hash) }
        return hash.prefix(16).map { String(format:"%02x",$0) }.joined()
    }
    return "unknown"
}

func escJson(_ s: String) -> String {
    s.replacingOccurrences(of: "\\", with: "\\\\")
     .replacingOccurrences(of: "\"", with: "\\\"")
     .replacingOccurrences(of: "\n", with: "\\n")
     .replacingOccurrences(of: "\r", with: "\\r")
}

func alert(_ msg: String, title: String = "MacOverlay") {
    let a = NSAlert(); a.messageText = title; a.informativeText = msg
    a.alertStyle = .critical; a.addButton(withTitle: "OK"); a.runModal()
}

// ── Overlay Window ────────────────────────────────────────────────────────────
class OverlayWindow: NSPanel {
    private var webView: WKWebView!
    private var lic: LicenseData
    private var checkinTimer: Timer?
    private var urlField: NSTextField!
    private var opacitySlider: NSSlider!

    init(lic: LicenseData) {
        self.lic = lic
        let screen = NSScreen.main?.frame ?? CGRect(x:0,y:0,width:1280,height:800)
        let rect = CGRect(x: screen.midX - 640, y: screen.midY - 400, width: 1280, height: 800)
        super.init(contentRect: rect,
                   styleMask: [.titled, .closable, .resizable, .miniaturizable, .nonactivatingPanel, .utilityWindow],
                   backing: .buffered, defer: false)
        title = "WinOverlay"
        level = .floating
        // Invisible to screen sharing / screen capture
        sharingType = .none
        isOpaque    = false
        backgroundColor = NSColor(white: 0, alpha: 0.05)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        setupUI()
        startCheckin()
    }

    private func setupUI() {
        let root = NSView(frame: contentView!.bounds)
        root.autoresizingMask = [.width, .height]
        contentView!.addSubview(root)

        // Toolbar
        let bar = NSView(frame: CGRect(x:0, y:root.bounds.height-36, width:root.bounds.width, height:36))
        bar.autoresizingMask = [.width, .minYMargin]
        bar.wantsLayer = true
        bar.layer?.backgroundColor = NSColor(white:0.12, alpha:0.95).cgColor
        root.addSubview(bar)

        urlField = NSTextField(frame: CGRect(x:8, y:6, width:bar.bounds.width-200, height:22))
        urlField.autoresizingMask = [.width]
        urlField.placeholderString = "Enter URL and press Return"
        urlField.bezelStyle = .roundedBezel
        urlField.target = self; urlField.action = #selector(navigateFromField)
        bar.addSubview(urlField)

        let btnGo = NSButton(frame: CGRect(x:bar.bounds.width-190, y:4, width:50, height:28))
        btnGo.autoresizingMask = [.minXMargin]
        btnGo.title = "Go"; btnGo.target = self; btnGo.action = #selector(navigateFromField)
        bar.addSubview(btnGo)

        opacitySlider = NSSlider(frame: CGRect(x:bar.bounds.width-135, y:8, width:80, height:20))
        opacitySlider.autoresizingMask = [.minXMargin]
        opacitySlider.minValue = 0.15; opacitySlider.maxValue = 1.0
        opacitySlider.doubleValue = 0.92
        opacitySlider.target = self; opacitySlider.action = #selector(opacityChanged)
        bar.addSubview(opacitySlider)

        let lblAlpha = NSTextField(labelWithString: "Opacity")
        lblAlpha.frame = CGRect(x:bar.bounds.width-52, y:8, width:48, height:20)
        lblAlpha.autoresizingMask = [.minXMargin]
        lblAlpha.font = .systemFont(ofSize: 10); lblAlpha.textColor = .lightGray
        bar.addSubview(lblAlpha)

        // WebView — screen-capture invisible via sharingType on NSPanel
        let config = WKWebViewConfiguration()
        config.preferences.javaScriptCanOpenWindowsAutomatically = true
        webView = WKWebView(frame: CGRect(x:0, y:0, width:root.bounds.width, height:root.bounds.height-36), configuration: config)
        webView.autoresizingMask = [.width, .height]
        webView.wantsLayer = true
        root.addSubview(webView)

        alphaValue = 0.92
    }

    @objc private func navigateFromField() {
        var url = urlField.stringValue.trimmingCharacters(in: .whitespaces)
        if !url.hasPrefix("http://") && !url.hasPrefix("https://") && !url.hasPrefix("about:") { url = "https://" + url }
        if let u = URL(string: url) { webView.load(URLRequest(url: u)) }
    }

    @objc private func opacityChanged() {
        alphaValue = opacitySlider.doubleValue
    }

    private func startCheckin() {
        checkinTimer = Timer.scheduledTimer(withTimeInterval: 5*60, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            let st = checkIn(self.lic)
            if st == "revoked" || st == "expired" || st == "invalid" {
                DispatchQueue.main.async {
                    alert("License \(st). Closing."); NSApp.terminate(nil)
                }
            }
        }
    }
}

// ── Run ───────────────────────────────────────────────────────────────────────
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
