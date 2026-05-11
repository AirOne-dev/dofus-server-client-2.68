import Cocoa
import Foundation
import Network
import CoreImage

enum Palette {
    static let bg          = NSColor(srgbHex: 0x1a140b)
    static let bg2         = NSColor(srgbHex: 0x110c06)
    static let border      = NSColor(srgbHex: 0x3a2c19)
    static let borderStrong = NSColor(srgbHex: 0x5a4424)
    static let gold        = NSColor(srgbHex: 0xe5b455)
    static let goldHover   = NSColor(srgbHex: 0xefc36a)
    static let goldPress   = NSColor(srgbHex: 0xc89a3f)
    static let text        = NSColor(srgbHex: 0xefe6d0)
    static let textDim     = NSColor(srgbHex: 0xa89878)
    static let textSoft    = NSColor(srgbHex: 0x6e6244)
    static let green       = NSColor(srgbHex: 0x8fbf4f)
    static let red         = NSColor(srgbHex: 0xd97757)
    static let amber       = NSColor(srgbHex: 0xf4b942)
    static let darkOnGold  = NSColor(srgbHex: 0x1a1208)
    static let inputBg     = NSColor(srgbRed: 15/255, green: 10/255, blue: 5/255, alpha: 0.55)
    static let inputBorder = NSColor(srgbRed: 90/255, green: 68/255, blue: 36/255, alpha: 0.7)
}

extension NSColor {
    convenience init(srgbHex hex: UInt32) {
        let r = CGFloat((hex >> 16) & 0xFF) / 255
        let g = CGFloat((hex >>  8) & 0xFF) / 255
        let b = CGFloat( hex        & 0xFF) / 255
        self.init(srgbRed: r, green: g, blue: b, alpha: 1)
    }
}

enum Typo {
    static func ui(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        if let f = NSFont(name: "Avenir Next", size: size) {
            if weight == .semibold || weight == .bold {
                return NSFontManager.shared.font(withFamily: "Avenir Next",
                    traits: .boldFontMask, weight: 7, size: size) ?? f
            }
            return f
        }
        return NSFont.systemFont(ofSize: size, weight: weight)
    }
    static func mono(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        NSFont.monospacedSystemFont(ofSize: size, weight: weight)
    }
}

enum Prefs {
    static let host = "host", port = "port"
    static let saveLogin = "saveLogin"
    static let accounts = "accounts"
    static let lastAccount = "lastAccount"
}

/// Clé d'index dans `<connection.host>` (`AuthentificationFrame._allHostsInfos`).
let kServerHostKey = "OneAir"

enum ConfigPatcher {
    /// Le SWF Giny patché tourne en BUILD_TYPE=DEBUG, donc `Signature.verify()`
    /// est shortcircuité — on peut laisser la signature à vide.
    static func patch(configPath: String, name: String, host: String, port: Int) throws {
        let url = URL(fileURLWithPath: configPath)
        let data = try Data(contentsOf: url)
        guard var text = String(data: data, encoding: .utf8) else { return }
        let bak = url.deletingPathExtension().appendingPathExtension("xml.orig")
        if !FileManager.default.fileExists(atPath: bak.path) { try data.write(to: bak) }
        text = text.replacingMatches(
            of: #"<entry key="connection\.host">[^<]*</entry>"#,
            with: "<entry key=\"connection.host\">\(name):\(host):\(port)</entry>")
        text = text.replacingMatches(
            of: #"<entry key="connection\.host\.signature">[^<]*</entry>"#,
            with: "<entry key=\"connection.host.signature\"></entry>")
        try text.write(to: url, atomically: true, encoding: .utf8)
    }
}

extension String {
    func replacingMatches(of pattern: String, with template: String) -> String {
        guard let r = try? NSRegularExpression(pattern: pattern) else { return self }
        return r.stringByReplacingMatches(in: self, range: NSRange(startIndex..., in: self),
                                          withTemplate: template)
    }
}

enum TCPProbe {
    static func test(host: String, port: Int, timeout: TimeInterval = 3,
                     completion: @escaping (Result<Int, Error>) -> Void) {
        guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else {
            completion(.failure(NSError(domain: "OneAir", code: 2,
                userInfo: [NSLocalizedDescriptionKey: "Port invalide"]))); return
        }
        let conn = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        let start = Date()
        var done = false
        let finalize: (Result<Int, Error>) -> Void = { r in
            guard !done else { return }; done = true
            DispatchQueue.main.async { completion(r) }
            conn.cancel()
        }
        conn.stateUpdateHandler = { state in
            switch state {
            case .ready:
                finalize(.success(Int(Date().timeIntervalSince(start) * 1000)))
            case .failed(let e): finalize(.failure(e))
            case .cancelled where !done:
                finalize(.failure(NSError(domain: "OneAir", code: 3,
                    userInfo: [NSLocalizedDescriptionKey: "Connexion annulée"])))
            default: break
            }
        }
        conn.start(queue: .global(qos: .userInitiated))
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
            if conn.state != .ready {
                conn.cancel()
                finalize(.failure(NSError(domain: "OneAir", code: 4,
                    userInfo: [NSLocalizedDescriptionKey: "Timeout"])))
            }
        }
    }
}

enum DofusLaunch {
    static let zaapPort = 4242, zaapHttpPort = 4243

    static func realDofusBinary() -> URL {
        Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/dofus-real")
    }
    static func zaapServerBinary() -> URL {
        Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/zaap-server")
    }
    static func configXML() -> URL {
        Bundle.main.resourceURL!.appendingPathComponent("config.xml")
    }

    static func launch(host: String, port: Int, serverName: String,
                       login: String?, password: String?) throws -> Never {
        let cfg = configXML().path
        if FileManager.default.fileExists(atPath: cfg) {
            try? ConfigPatcher.patch(configPath: cfg, name: serverName,
                                     host: host, port: port)
        }

        let logDir = NSHomeDirectory() + "/Library/Logs/OneAir"
        try? FileManager.default.createDirectory(atPath: logDir, withIntermediateDirectories: true)
        let logURL = URL(fileURLWithPath: logDir).appendingPathComponent("launcher.log")
        let date = ISO8601DateFormatter().string(from: Date())

        var logLines: [String] = ["[\(date)] launch host=\(host) port=\(port)"]

        // Écriture sous plusieurs chemins : `applicationStorageDirectory` côté
        // AIR varie selon comment l'app a été lancée.
        if let login = login, !login.isEmpty,
           let password = password, !password.isEmpty {
            let creds = "\(login)\n\(password)"
            let support = FileManager.default.urls(for: .applicationSupportDirectory,
                in: .userDomainMask).first!
            let candidates: [URL] = [
                support.appendingPathComponent("Dofus/Local Store/oneair-creds"),
                support.appendingPathComponent("Dofus/oneair-creds"),
                URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".oneair-creds"),
                URL(fileURLWithPath: "/tmp/oneair-creds"),
            ]
            for url in candidates {
                do {
                    try FileManager.default.createDirectory(
                        at: url.deletingLastPathComponent(),
                        withIntermediateDirectories: true)
                    try creds.data(using: .utf8)!.write(to: url)
                    logLines.append("  WROTE \(url.path)")
                } catch {
                    logLines.append("  FAIL  \(url.path) — \(error.localizedDescription)")
                }
            }
        } else {
            logLines.append("  SKIP credentials (login=\(login ?? "nil") pass=\(password == nil ? "nil" : "***"))")
        }

        if let h = try? FileHandle(forWritingTo: logURL) {
            try? h.seekToEnd()
            h.write((logLines.joined(separator: "\n") + "\n").data(using: .utf8)!)
            try? h.close()
        } else {
            try? logLines.joined(separator: "\n")
                .data(using: .utf8)?.write(to: logURL)
        }

        // credentials.json est lu par le SWF Giny en BUILD_TYPE=DEBUG via
        // `File.applicationDirectory.resolvePath("credentials.json")` (=
        // Contents/Resources sur macOS). Adobe AIR ignore les args CLI quand
        // l'app n'est pas lancée via deep-link.
        let instanceId = Int.random(in: 1...1_000_000)
        let hash = UUID().uuidString.lowercased()
        let credsURL = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Resources/credentials.json")
        let credsObj: [String: Any] = [
            "port": zaapPort, "name": "dofus", "release": "main",
            "instanceId": instanceId, "hash": hash,
        ]
        if let blob = try? JSONSerialization.data(withJSONObject: credsObj, options: []) {
            try? blob.write(to: credsURL)
        }

        try? startZaap(port: zaapPort, httpPort: zaapHttpPort, hash: hash,
                       instanceId: instanceId, authAddr: "\(host):\(port)",
                       login: login ?? "", password: password ?? "")
        Thread.sleep(forTimeInterval: 0.5)

        let cwd = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS").path
        FileManager.default.changeCurrentDirectoryPath(cwd)

        let argv: [String] = ["Dofus"]
        let cArgs = argv.map { strdup($0) } + [UnsafeMutablePointer<CChar>?.none]
        let buf = UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>
            .allocate(capacity: cArgs.count)
        for (i, a) in cArgs.enumerated() { buf[i] = a }
        execv(realDofusBinary().path, buf)
        fatalError("execv : \(String(cString: strerror(errno)))")
    }

    static func startZaap(port: Int, httpPort: Int, hash: String,
                          instanceId: Int, authAddr: String,
                          login: String, password: String) throws {
        let zaap = zaapServerBinary()
        guard FileManager.default.isExecutableFile(atPath: zaap.path) else { return }

        // pkill ciblé sur le chemin du bundle (et pas juste "zaap-server")
        // pour ne pas tuer un zaap-server lancé par un autre OneAir.app.
        let pkill = Process()
        pkill.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
        pkill.arguments = ["-f", "Contents/MacOS/zaap-server"]
        try? pkill.run()
        pkill.waitUntilExit()
        Thread.sleep(forTimeInterval: 0.3)

        let logDir = NSHomeDirectory() + "/Library/Logs/OneAir"
        try? FileManager.default.createDirectory(atPath: logDir, withIntermediateDirectories: true)
        let logURL = URL(fileURLWithPath: logDir).appendingPathComponent("zaap-server.log")
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        let h = try FileHandle(forWritingTo: logURL); try h.seekToEnd()
        let p = Process()
        p.executableURL = zaap
        // game-token == password : Giny renvoie le password en clair comme
        // ticket, le SWF le ré-envoie en IdentificationMessage.
        p.arguments = [
            "--port=\(port)", "--http-port=\(httpPort)", "--hash=\(hash)",
            "--instance-id=\(instanceId)",
            "--login=\(login)", "--game-token=\(password)",
            "--auth-addr=\(authAddr)",
        ]
        p.standardOutput = h; p.standardError = h
        try p.run()
    }
}

final class PortalBackgroundView: NSView {
    private let imageLayer = CALayer()
    private let dimLayer = CALayer()

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = Palette.bg.cgColor

        if let url = Bundle.main.url(forResource: "portal-bg", withExtension: "jpg"),
           let img = NSImage(contentsOf: url),
           let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            let ci = CIImage(cgImage: cg)
            let blurred = ci.applyingFilter("CIGaussianBlur",
                                            parameters: [kCIInputRadiusKey: 18])
                          .cropped(to: ci.extent)
            let ctx = CIContext()
            if let outCG = ctx.createCGImage(blurred, from: blurred.extent) {
                imageLayer.contents = outCG
            } else { imageLayer.contents = cg }
            imageLayer.contentsGravity = .resizeAspectFill
            imageLayer.opacity = 0.55
        }
        layer?.addSublayer(imageLayer)

        dimLayer.backgroundColor = NSColor(srgbRed: 10/255, green: 7/255,
                                            blue: 3/255, alpha: 0.55).cgColor
        layer?.addSublayer(dimLayer)
    }
    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        CATransaction.begin(); CATransaction.setDisableActions(true)
        imageLayer.frame = bounds.insetBy(dx: -16, dy: -16)
        dimLayer.frame   = bounds
        CATransaction.commit()
    }
}

/// Deux NSTextField (sécurisé + plain) superposés. Le toggle synchronise le
/// contenu et inverse la visibilité.
final class IconTextField: NSView, NSTextFieldDelegate {

    private let secureField = NSSecureTextField()
    private let plainField = NSTextField()
    private let iconView = NSImageView()
    private var toggleButton: NSButton?
    private var passwordVisible = false
    let isSecureMode: Bool

    var stringValue: String {
        get {
            isSecureMode
                ? (passwordVisible ? plainField.stringValue : secureField.stringValue)
                : plainField.stringValue
        }
        set {
            secureField.stringValue = newValue
            plainField.stringValue = newValue
        }
    }

    init(icon: String, secure: Bool = false, placeholder: String) {
        self.isSecureMode = secure
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = Palette.inputBg.cgColor
        layer?.borderColor = Palette.inputBorder.cgColor
        layer?.borderWidth = 1
        layer?.cornerRadius = 4

        let iconCfg = NSImage.SymbolConfiguration(pointSize: 12, weight: .regular)
        if let img = NSImage(systemSymbolName: icon, accessibilityDescription: nil) {
            iconView.image = img
            iconView.symbolConfiguration = iconCfg
            iconView.contentTintColor = Palette.textSoft
        }
        iconView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(iconView)

        configureField(secureField, placeholder: placeholder)
        configureField(plainField,  placeholder: placeholder)
        secureField.delegate = self
        plainField.delegate  = self

        addSubview(secureField)
        addSubview(plainField)
        secureField.isHidden = !secure
        plainField.isHidden  = secure

        var rightAnchor: NSLayoutXAxisAnchor = trailingAnchor
        var rightConstant: CGFloat = -12

        if secure {
            let btn = NSButton()
            btn.attributedTitle = NSAttributedString(string: "VOIR", attributes: [
                .font: Typo.mono(9, weight: .medium),
                .foregroundColor: Palette.textSoft, .kern: 0.7,
            ])
            btn.bezelStyle = .inline
            btn.isBordered = false
            btn.target = self
            btn.action = #selector(togglePw)
            btn.translatesAutoresizingMaskIntoConstraints = false
            addSubview(btn)
            toggleButton = btn
            NSLayoutConstraint.activate([
                btn.centerYAnchor.constraint(equalTo: centerYAnchor),
                btn.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
                btn.widthAnchor.constraint(greaterThanOrEqualToConstant: 42),
            ])
            rightAnchor = btn.leadingAnchor
            rightConstant = -4
        }

        for field in [secureField, plainField] {
            NSLayoutConstraint.activate([
                field.leadingAnchor.constraint(equalTo: iconView.trailingAnchor, constant: 10),
                field.trailingAnchor.constraint(equalTo: rightAnchor, constant: rightConstant),
                field.centerYAnchor.constraint(equalTo: centerYAnchor),
                field.heightAnchor.constraint(equalToConstant: 18),
            ])
        }

        NSLayoutConstraint.activate([
            iconView.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 11),
            iconView.widthAnchor.constraint(equalToConstant: 14),
            iconView.heightAnchor.constraint(equalToConstant: 14),
            heightAnchor.constraint(equalToConstant: 38),
        ])
    }
    required init?(coder: NSCoder) { fatalError() }

    private func configureField(_ f: NSTextField, placeholder: String) {
        f.font = Typo.ui(13)
        f.textColor = Palette.text
        f.backgroundColor = .clear
        f.drawsBackground = false
        f.isBordered = false
        f.focusRingType = .none
        f.placeholderAttributedString = NSAttributedString(
            string: placeholder,
            attributes: [.foregroundColor: Palette.textSoft, .font: Typo.ui(13)])
        f.translatesAutoresizingMaskIntoConstraints = false
    }

    @objc private func togglePw() {
        if passwordVisible {
            secureField.stringValue = plainField.stringValue
        } else {
            plainField.stringValue = secureField.stringValue
        }
        passwordVisible.toggle()
        secureField.isHidden = passwordVisible
        plainField.isHidden  = !passwordVisible

        let label = passwordVisible ? "CACHER" : "VOIR"
        toggleButton?.attributedTitle = NSAttributedString(string: label, attributes: [
            .font: Typo.mono(9, weight: .medium),
            .foregroundColor: Palette.textSoft, .kern: 0.7,
        ])
        let focused = passwordVisible ? plainField : secureField
        window?.makeFirstResponder(focused)
    }

    func controlTextDidChange(_ obj: Notification) {
        guard let src = obj.object as? NSTextField else { return }
        if src === secureField { plainField.stringValue = src.stringValue }
        else if src === plainField { secureField.stringValue = src.stringValue }
    }
}

final class PlayButton: NSButton {
    private var hovering = false { didSet { needsDisplay = true } }
    var titleText: String = "JOUER" { didSet { needsDisplay = true } }
    var arrowVisible: Bool = true { didSet { needsDisplay = true } }
    private var pressing = false { didSet { needsDisplay = true } }

    override init(frame: NSRect) {
        super.init(frame: frame)
        bezelStyle = .rounded
        wantsLayer = true
        title = ""
        isBordered = false
        addTrackingArea(NSTrackingArea(rect: .zero,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self, userInfo: nil))
    }
    required init?(coder: NSCoder) { fatalError() }
    override func mouseEntered(with e: NSEvent) { hovering = true }
    override func mouseExited(with e: NSEvent)  { hovering = false; pressing = false }
    override func mouseDown(with event: NSEvent) { pressing = true; super.mouseDown(with: event); pressing = false }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds
        let path = NSBezierPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5),
                                xRadius: 4, yRadius: 4)
        let fill: NSColor =
            !isEnabled  ? Palette.gold.withAlphaComponent(0.55)
            : pressing  ? Palette.goldPress
            : hovering  ? Palette.goldHover
            :             Palette.gold
        fill.setFill(); path.fill()
        Palette.goldPress.setStroke(); path.lineWidth = 1; path.stroke()

        let para = NSMutableParagraphStyle(); para.alignment = .center
        let attrs: [NSAttributedString.Key: Any] = [
            .font: Typo.ui(13, weight: .semibold),
            .foregroundColor: Palette.darkOnGold.withAlphaComponent(isEnabled ? 1 : 0.55),
            .kern: 3.6,
            .paragraphStyle: para,
        ]
        let label = (title.isEmpty ? titleText : title) as NSString
        let textSize = label.size(withAttributes: attrs)
        let totalWidth = textSize.width + (arrowVisible ? 18 : 0)
        let originX = (r.width - totalWidth) / 2
        let y = (r.height - textSize.height) / 2
        label.draw(at: NSPoint(x: originX, y: y), withAttributes: attrs)

        if arrowVisible, isEnabled {
            let ax = originX + textSize.width + 8
            let ay = r.midY
            let arrow = NSBezierPath()
            arrow.move(to: NSPoint(x: ax, y: ay))
            arrow.line(to: NSPoint(x: ax + 9, y: ay))
            arrow.move(to: NSPoint(x: ax + 5, y: ay - 4))
            arrow.line(to: NSPoint(x: ax + 9, y: ay))
            arrow.line(to: NSPoint(x: ax + 5, y: ay + 4))
            Palette.darkOnGold.setStroke()
            arrow.lineWidth = 1.8
            arrow.lineCapStyle = .round
            arrow.lineJoinStyle = .round
            arrow.stroke()
        }
    }
}

final class StatusPill: NSView {
    let dot = NSView()
    let label = NSTextField(labelWithString: "En ligne")

    enum Kind { case ok, warn, err }
    var kind: Kind = .ok {
        didSet {
            let c: NSColor
            switch kind {
            case .ok: c = Palette.green
            case .warn: c = Palette.amber
            case .err: c = Palette.red
            }
            dot.layer?.backgroundColor = c.cgColor
        }
    }

    override init(frame: NSRect) {
        super.init(frame: frame)
        dot.wantsLayer = true
        dot.layer?.backgroundColor = Palette.green.cgColor
        dot.layer?.cornerRadius = 3
        dot.translatesAutoresizingMaskIntoConstraints = false
        addSubview(dot)
        label.font = Typo.mono(10)
        label.textColor = Palette.textSoft
        label.translatesAutoresizingMaskIntoConstraints = false
        addSubview(label)
        NSLayoutConstraint.activate([
            dot.centerYAnchor.constraint(equalTo: centerYAnchor),
            dot.leadingAnchor.constraint(equalTo: leadingAnchor),
            dot.widthAnchor.constraint(equalToConstant: 6),
            dot.heightAnchor.constraint(equalToConstant: 6),
            label.leadingAnchor.constraint(equalTo: dot.trailingAnchor, constant: 6),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
            label.trailingAnchor.constraint(equalTo: trailingAnchor),
            heightAnchor.constraint(equalToConstant: 18),
        ])
    }
    required init?(coder: NSCoder) { fatalError() }
}

final class AdvancedVC: NSViewController {

    var onSave: ((String, Int) -> Void)?
    var onCancel: (() -> Void)?

    private let ipField = NSTextField()
    private let portField = NSTextField()
    private let testButton = NSButton(title: "TESTER LA CONNEXION",
        target: nil, action: nil)
    private let resultLabel = NSTextField(labelWithString: "— aucun test effectué")

    override func loadView() {
        let v = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 230))
        v.wantsLayer = true
        v.layer?.backgroundColor = Palette.bg2.cgColor
        view = v
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        let title = NSTextField(labelWithString: "OPTIONS AVANCÉES")
        title.attributedStringValue = NSAttributedString(
            string: "OPTIONS AVANCÉES", attributes: [
                .font: Typo.ui(10, weight: .semibold),
                .foregroundColor: Palette.textDim, .kern: 1.8,
            ])

        ipField.stringValue =
            UserDefaults.standard.string(forKey: Prefs.host) ?? "127.0.0.1"
        portField.stringValue =
            UserDefaults.standard.string(forKey: Prefs.port) ?? "5555"

        let ipBox = labeledField("IP / DNS DU SERVEUR", field: ipField, width: 180)
        let portBox = labeledField("PORT", field: portField, width: 70)
        let row = NSStackView(views: [ipBox, portBox])
        row.orientation = .horizontal
        row.spacing = 8

        testButton.bezelStyle = .rounded
        testButton.isBordered = false
        testButton.attributedTitle = NSAttributedString(
            string: "TESTER LA CONNEXION", attributes: [
                .font: Typo.ui(10, weight: .medium),
                .foregroundColor: Palette.gold, .kern: 1.4,
            ])
        testButton.wantsLayer = true
        testButton.layer?.borderColor = Palette.borderStrong.cgColor
        testButton.layer?.borderWidth = 1
        testButton.layer?.cornerRadius = 4
        testButton.target = self
        testButton.action = #selector(testTapped)
        testButton.translatesAutoresizingMaskIntoConstraints = false
        testButton.heightAnchor.constraint(equalToConstant: 30).isActive = true

        resultLabel.font = Typo.mono(10)
        resultLabel.textColor = Palette.textSoft

        let cancelBtn = ghostButton("ANNULER", #selector(cancelTapped))
        let saveBtn = goldButton("ENREGISTRER", #selector(saveTapped))
        let actions = NSStackView(views: [cancelBtn, saveBtn])
        actions.orientation = .horizontal
        actions.distribution = .fillEqually
        actions.spacing = 8

        let separator = NSBox(); separator.boxType = .separator
        separator.translatesAutoresizingMaskIntoConstraints = false
        separator.heightAnchor.constraint(equalToConstant: 1).isActive = true

        let stack = NSStackView(views: [
            title, row, testButton, resultLabel, separator, actions,
        ])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.alignment = .leading
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            stack.topAnchor.constraint(equalTo: view.topAnchor, constant: 16),
            stack.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -16),
            row.widthAnchor.constraint(equalTo: stack.widthAnchor),
            testButton.widthAnchor.constraint(equalTo: stack.widthAnchor),
            actions.widthAnchor.constraint(equalTo: stack.widthAnchor),
            separator.widthAnchor.constraint(equalTo: stack.widthAnchor),
        ])
    }

    private func labeledField(_ text: String, field: NSTextField,
                              width: CGFloat) -> NSView {
        let lbl = NSTextField(labelWithString: text)
        lbl.attributedStringValue = NSAttributedString(string: text, attributes: [
            .font: Typo.ui(9, weight: .medium),
            .foregroundColor: Palette.textDim, .kern: 1.0,
        ])
        field.font = Typo.ui(12)
        field.textColor = Palette.text
        field.isBordered = true
        field.bezelStyle = .roundedBezel
        field.focusRingType = .none
        field.translatesAutoresizingMaskIntoConstraints = false
        field.heightAnchor.constraint(equalToConstant: 26).isActive = true
        field.widthAnchor.constraint(equalToConstant: width).isActive = true
        let s = NSStackView(views: [lbl, field])
        s.orientation = .vertical
        s.alignment = .leading
        s.spacing = 4
        return s
    }

    private func ghostButton(_ text: String, _ action: Selector) -> NSButton {
        let b = NSButton(title: text, target: self, action: action)
        b.bezelStyle = .rounded
        b.isBordered = false
        b.wantsLayer = true
        b.layer?.borderColor = Palette.borderStrong.cgColor
        b.layer?.borderWidth = 1
        b.layer?.cornerRadius = 4
        b.attributedTitle = NSAttributedString(string: text, attributes: [
            .font: Typo.ui(10, weight: .medium),
            .foregroundColor: Palette.textDim, .kern: 1.2,
        ])
        b.translatesAutoresizingMaskIntoConstraints = false
        b.heightAnchor.constraint(equalToConstant: 28).isActive = true
        return b
    }

    private func goldButton(_ text: String, _ action: Selector) -> NSButton {
        let b = NSButton(title: text, target: self, action: action)
        b.bezelStyle = .rounded
        b.isBordered = false
        b.wantsLayer = true
        b.layer?.backgroundColor = Palette.gold.cgColor
        b.layer?.cornerRadius = 4
        b.attributedTitle = NSAttributedString(string: text, attributes: [
            .font: Typo.ui(10, weight: .semibold),
            .foregroundColor: Palette.darkOnGold, .kern: 1.2,
        ])
        b.translatesAutoresizingMaskIntoConstraints = false
        b.heightAnchor.constraint(equalToConstant: 28).isActive = true
        return b
    }

    @objc private func testTapped() {
        let host = ipField.stringValue.trimmingCharacters(in: .whitespaces)
        let port = Int(portField.stringValue.trimmingCharacters(in: .whitespaces)) ?? 0
        guard !host.isEmpty, port > 0 else {
            resultLabel.stringValue = "✗ IP/port invalide"
            resultLabel.textColor = Palette.red
            return
        }
        resultLabel.stringValue = "Test en cours sur \(host):\(port)…"
        resultLabel.textColor = Palette.textSoft
        TCPProbe.test(host: host, port: port) { [weak self] r in
            guard let self else { return }
            switch r {
            case .success(let ms):
                self.resultLabel.stringValue = "✓ Connexion OK · \(ms) ms"
                self.resultLabel.textColor = Palette.green
            case .failure(let e):
                self.resultLabel.stringValue = "✗ \(e.localizedDescription)"
                self.resultLabel.textColor = Palette.red
            }
        }
    }

    @objc private func cancelTapped() { onCancel?() }
    @objc private func saveTapped() {
        let host = ipField.stringValue.trimmingCharacters(in: .whitespaces)
        let port = Int(portField.stringValue.trimmingCharacters(in: .whitespaces)) ?? 5555
        UserDefaults.standard.set(host, forKey: Prefs.host)
        UserDefaults.standard.set(String(port), forKey: Prefs.port)
        onSave?(host, port)
    }
}

struct OneAirAccount: Codable, Equatable {
    var login: String
    var password: String
}

enum AccountsStore {
    static func load() -> [OneAirAccount] {
        if let data = UserDefaults.standard.data(forKey: Prefs.accounts),
           let arr = try? JSONDecoder().decode([OneAirAccount].self, from: data) {
            return arr
        }
        return []
    }
    static func save(_ accounts: [OneAirAccount]) {
        if let data = try? JSONEncoder().encode(accounts) {
            UserDefaults.standard.set(data, forKey: Prefs.accounts)
        }
    }
    static func upsert(login: String, password: String) {
        guard !login.isEmpty else { return }
        var arr = load()
        if let i = arr.firstIndex(where: { $0.login == login }) {
            arr[i].password = password
        } else {
            arr.append(OneAirAccount(login: login, password: password))
        }
        save(arr)
        UserDefaults.standard.set(login, forKey: Prefs.lastAccount)
    }
    static func remove(login: String) {
        var arr = load()
        arr.removeAll { $0.login == login }
        save(arr)
        if UserDefaults.standard.string(forKey: Prefs.lastAccount) == login {
            UserDefaults.standard.set(arr.first?.login ?? "", forKey: Prefs.lastAccount)
        }
    }
}

final class AccountSelectorButton: NSView {
    private let iconView = NSImageView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let chevron = NSImageView()
    private var trackingArea: NSTrackingArea?
    private var hovered = false { didSet { updateColors() } }
    var isOpen = false { didSet { updateColors(); rotateChevron() } }
    var onClick: () -> Void = {}

    var displayedLogin: String = "" {
        didSet {
            let placeholder = "+ Nouveau compte"
            let isPlaceholder = displayedLogin.isEmpty
            titleLabel.attributedStringValue = NSAttributedString(
                string: isPlaceholder ? placeholder : displayedLogin,
                attributes: [
                    .font: Typo.ui(13),
                    .foregroundColor: isPlaceholder ? Palette.textSoft : Palette.text,
                ])
            iconView.image = NSImage(systemSymbolName:
                isPlaceholder ? "plus.circle.fill" : "person.crop.circle.fill",
                accessibilityDescription: nil)
        }
    }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = Palette.inputBg.cgColor
        layer?.borderColor = Palette.inputBorder.cgColor
        layer?.borderWidth = 1
        layer?.cornerRadius = 4

        let iconCfg = NSImage.SymbolConfiguration(pointSize: 13, weight: .regular)
        iconView.symbolConfiguration = iconCfg
        iconView.contentTintColor = Palette.textSoft
        iconView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(iconView)

        titleLabel.font = Typo.ui(13)
        titleLabel.textColor = Palette.text
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(titleLabel)

        chevron.image = NSImage(systemSymbolName: "chevron.down", accessibilityDescription: nil)
        chevron.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 10, weight: .semibold)
        chevron.contentTintColor = Palette.textSoft
        chevron.translatesAutoresizingMaskIntoConstraints = false
        chevron.wantsLayer = true
        addSubview(chevron)

        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 38),
            iconView.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 11),
            iconView.widthAnchor.constraint(equalToConstant: 16),
            iconView.heightAnchor.constraint(equalToConstant: 16),
            titleLabel.leadingAnchor.constraint(equalTo: iconView.trailingAnchor, constant: 10),
            titleLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            titleLabel.trailingAnchor.constraint(equalTo: chevron.leadingAnchor, constant: -8),
            chevron.centerYAnchor.constraint(equalTo: centerYAnchor),
            chevron.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            chevron.widthAnchor.constraint(equalToConstant: 14),
            chevron.heightAnchor.constraint(equalToConstant: 12),
        ])

        displayedLogin = ""
    }
    required init?(coder: NSCoder) { fatalError() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea { removeTrackingArea(ta) }
        let ta = NSTrackingArea(rect: bounds,
            options: [.mouseEnteredAndExited, .activeInActiveApp, .inVisibleRect],
            owner: self)
        addTrackingArea(ta)
        trackingArea = ta
    }
    override func mouseEntered(with event: NSEvent) { hovered = true }
    override func mouseExited(with event: NSEvent)  { hovered = false }
    override func mouseDown(with event: NSEvent)    { onClick() }

    private func updateColors() {
        layer?.borderColor = (isOpen || hovered) ? Palette.gold.cgColor : Palette.inputBorder.cgColor
    }
    private func rotateChevron() {
        let layer = chevron.layer
        layer?.transform = CATransform3DMakeRotation(isOpen ? .pi : 0, 0, 0, 1)
    }
}

final class AccountListRow: NSView {
    let login: String
    let isAddNew: Bool
    var onSelect: () -> Void = {}
    var onDelete: () -> Void = {}

    private let avatarView = NSView()
    private let avatarLabel = NSTextField(labelWithString: "")
    private let titleLabel = NSTextField(labelWithString: "")
    private let trashButton = NSButton()
    private var trackingArea: NSTrackingArea?

    private var hovered = false {
        didSet {
            layer?.backgroundColor = hovered
                ? Palette.gold.withAlphaComponent(0.12).cgColor
                : NSColor.clear.cgColor
            trashButton.isHidden = isAddNew || !hovered
        }
    }

    init(login: String, isAddNew: Bool = false) {
        self.login = login
        self.isAddNew = isAddNew
        super.init(frame: .zero)
        wantsLayer = true

        avatarView.wantsLayer = true
        avatarView.layer?.cornerRadius = 14
        avatarView.layer?.backgroundColor = isAddNew
            ? Palette.gold.withAlphaComponent(0.18).cgColor
            : NSColor(deviceRed: 0.20, green: 0.27, blue: 0.36, alpha: 1).cgColor
        avatarView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(avatarView)

        avatarLabel.alignment = .center
        avatarLabel.attributedStringValue = NSAttributedString(
            string: isAddNew ? "+" : String(login.prefix(1).uppercased()),
            attributes: [
                .font: Typo.ui(13, weight: .semibold),
                .foregroundColor: isAddNew ? Palette.gold : Palette.text,
            ])
        avatarLabel.translatesAutoresizingMaskIntoConstraints = false
        avatarView.addSubview(avatarLabel)

        titleLabel.attributedStringValue = NSAttributedString(
            string: isAddNew ? "Nouveau compte" : login,
            attributes: [
                .font: Typo.ui(13, weight: isAddNew ? .medium : .regular),
                .foregroundColor: isAddNew ? Palette.gold : Palette.text,
            ])
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.lineBreakMode = .byTruncatingTail
        addSubview(titleLabel)

        trashButton.attributedTitle = NSAttributedString(string: "🗑", attributes: [
            .font: Typo.ui(13),
        ])
        trashButton.bezelStyle = .inline
        trashButton.isBordered = false
        trashButton.target = self
        trashButton.action = #selector(onTrashTapped)
        trashButton.translatesAutoresizingMaskIntoConstraints = false
        trashButton.isHidden = true
        addSubview(trashButton)

        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 38),
            avatarView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            avatarView.centerYAnchor.constraint(equalTo: centerYAnchor),
            avatarView.widthAnchor.constraint(equalToConstant: 28),
            avatarView.heightAnchor.constraint(equalToConstant: 28),
            avatarLabel.centerXAnchor.constraint(equalTo: avatarView.centerXAnchor),
            avatarLabel.centerYAnchor.constraint(equalTo: avatarView.centerYAnchor),
            titleLabel.leadingAnchor.constraint(equalTo: avatarView.trailingAnchor, constant: 10),
            titleLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            titleLabel.trailingAnchor.constraint(equalTo: trashButton.leadingAnchor, constant: -4),
            trashButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            trashButton.centerYAnchor.constraint(equalTo: centerYAnchor),
            trashButton.widthAnchor.constraint(equalToConstant: 28),
            trashButton.heightAnchor.constraint(equalToConstant: 24),
        ])
    }
    required init?(coder: NSCoder) { fatalError() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea { removeTrackingArea(ta) }
        let ta = NSTrackingArea(rect: bounds,
            options: [.mouseEnteredAndExited, .activeInActiveApp, .inVisibleRect],
            owner: self)
        addTrackingArea(ta)
        trackingArea = ta
    }
    override func mouseEntered(with event: NSEvent) { hovered = true }
    override func mouseExited(with event: NSEvent)  { hovered = false }
    override func mouseDown(with event: NSEvent)    { onSelect() }
    @objc private func onTrashTapped() { onDelete() }
}

// MARK: - Main view controller

final class LauncherVC: NSViewController, NSPopoverDelegate {


    private let bg = PortalBackgroundView(frame: .zero)
    private let logoView = NSImageView()
    private let tagLabel = NSTextField(labelWithString: "")
    private let userField = IconTextField(icon: "person.fill",
        placeholder: "Votre nom de compte")
    private let passField = IconTextField(icon: "lock.fill", secure: true,
        placeholder: "••••••••••")
    private let rememberCheckbox = NSButton(checkboxWithTitle: "",
        target: nil, action: nil)
    private let playButton = PlayButton(frame: .zero)
    private let accountSelector = AccountSelectorButton(frame: .zero)
    private var accountsPopover: NSPopover?

    /// nil = mode "+ Nouveau compte" (inputs visibles), sinon login du compte sélectionné (inputs cachés)
    private var selectedAccountLogin: String? = nil

    private var inputsContainer: NSStackView!

    private let advancedButton = NSButton(title: "", target: nil, action: nil)
    private let statusPill = StatusPill(frame: .zero)
    private var statusReset: DispatchWorkItem?

    private var advPopover: NSPopover?

    override func loadView() {
        let v = NSView(frame: NSRect(x: 0, y: 0, width: 540, height: 620))
        v.wantsLayer = true
        v.layer?.backgroundColor = Palette.bg.cgColor
        view = v
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.addSubview(bg)
        bg.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            bg.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            bg.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            bg.topAnchor.constraint(equalTo: view.topAnchor),
            bg.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])

        // Logo
        if let url = Bundle.main.url(forResource: "logo", withExtension: "png"),
           let img = NSImage(contentsOf: url) {
            logoView.image = img
        }
        logoView.imageScaling = .scaleProportionallyUpOrDown
        logoView.translatesAutoresizingMaskIntoConstraints = false

        tagLabel.attributedStringValue = NSAttributedString(
            string: "ÉDITION DU VOYAGEUR", attributes: [
                .font: Typo.ui(9, weight: .medium),
                .foregroundColor: Palette.textDim, .kern: 3.4,
            ])
        tagLabel.alignment = .center

        let accountsLabel = makeFieldLabel("COMPTE")
        let userLabel = makeFieldLabel("IDENTIFIANT")
        let passLabel = makeFieldLabel("MOT DE PASSE")
        userField.translatesAutoresizingMaskIntoConstraints = false
        passField.translatesAutoresizingMaskIntoConstraints = false

        accountSelector.translatesAutoresizingMaskIntoConstraints = false
        accountSelector.onClick = { [weak self] in self?.toggleAccountsPopover() }

        rememberCheckbox.attributedTitle = NSAttributedString(
            string: "Se souvenir de moi", attributes: [
                .font: Typo.ui(12),
                .foregroundColor: Palette.textDim,
            ])
        rememberCheckbox.contentTintColor = Palette.gold
        rememberCheckbox.state = .on

        playButton.target = self
        playButton.action = #selector(playTapped)
        playButton.keyEquivalent = "\r"
        playButton.translatesAutoresizingMaskIntoConstraints = false
        playButton.heightAnchor.constraint(equalToConstant: 44).isActive = true

        advancedButton.bezelStyle = .rounded
        advancedButton.isBordered = false
        advancedButton.attributedTitle = NSAttributedString(
            string: "⚙   OPTIONS AVANCÉES", attributes: [
                .font: Typo.ui(11, weight: .medium),
                .foregroundColor: Palette.textDim, .kern: 1.4,
            ])
        advancedButton.target = self
        advancedButton.action = #selector(toggleAdvanced)

        let footer = NSStackView(views: [advancedButton, statusPill])
        footer.orientation = .horizontal
        footer.distribution = .equalSpacing
        footer.alignment = .centerY
        footer.translatesAutoresizingMaskIntoConstraints = false
        footer.heightAnchor.constraint(equalToConstant: 28).isActive = true

        let separator = NSBox()
        separator.boxType = .separator
        separator.translatesAutoresizingMaskIntoConstraints = false
        separator.heightAnchor.constraint(equalToConstant: 1).isActive = true

        let cardWidth: CGFloat = 360
        // Container masquable en bloc quand un compte existant est sélectionné.
        inputsContainer = NSStackView(views: [
            userLabel, userField,
            spacer(2),
            passLabel, passField,
            rememberCheckbox,
        ])
        inputsContainer.orientation = .vertical
        inputsContainer.spacing = 8
        inputsContainer.alignment = .leading
        inputsContainer.translatesAutoresizingMaskIntoConstraints = false

        let stack = NSStackView(views: [
            logoView, tagLabel,
            spacer(16),
            accountsLabel, accountSelector,
            spacer(8),
            inputsContainer,
            spacer(4),
            playButton,
            spacer(10),
            separator,
            spacer(2),
            footer,
        ])
        stack.orientation = .vertical
        stack.spacing = 8
        stack.alignment = .centerX
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            stack.widthAnchor.constraint(equalToConstant: cardWidth),
            logoView.widthAnchor.constraint(equalToConstant: 200),
            logoView.heightAnchor.constraint(equalToConstant: 100),
            accountSelector.widthAnchor.constraint(equalToConstant: cardWidth),
            inputsContainer.widthAnchor.constraint(equalToConstant: cardWidth),
            userField.widthAnchor.constraint(equalToConstant: cardWidth),
            passField.widthAnchor.constraint(equalToConstant: cardWidth),
            playButton.widthAnchor.constraint(equalToConstant: cardWidth),
            separator.widthAnchor.constraint(equalToConstant: cardWidth),
            footer.widthAnchor.constraint(equalToConstant: cardWidth),
            accountsLabel.leadingAnchor.constraint(equalTo: stack.leadingAnchor),
            userLabel.leadingAnchor.constraint(equalTo: inputsContainer.leadingAnchor),
            passLabel.leadingAnchor.constraint(equalTo: inputsContainer.leadingAnchor),
            rememberCheckbox.leadingAnchor.constraint(equalTo: inputsContainer.leadingAnchor),
        ])

        loadPrefs()
    }

    private func makeFieldLabel(_ text: String) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.attributedStringValue = NSAttributedString(string: text, attributes: [
            .font: Typo.ui(10, weight: .medium),
            .foregroundColor: Palette.textDim, .kern: 1.0,
        ])
        return l
    }
    private func spacer(_ h: CGFloat) -> NSView {
        let v = NSView()
        v.translatesAutoresizingMaskIntoConstraints = false
        v.heightAnchor.constraint(equalToConstant: h).isActive = true
        return v
    }

    private func loadPrefs() {
        let d = UserDefaults.standard
        rememberCheckbox.state = d.bool(forKey: Prefs.saveLogin) ? .on : .off
        let last = d.string(forKey: Prefs.lastAccount) ?? ""
        if let acc = AccountsStore.load().first(where: { $0.login == last }) ??
                     AccountsStore.load().first {
            selectAccount(acc.login)
        } else {
            selectNewAccountMode()
        }
    }

    private func selectAccount(_ login: String) {
        guard let acc = AccountsStore.load().first(where: { $0.login == login }) else {
            selectNewAccountMode(); return
        }
        selectedAccountLogin = acc.login
        accountSelector.displayedLogin = acc.login
        userField.stringValue = acc.login
        passField.stringValue = acc.password
        UserDefaults.standard.set(acc.login, forKey: Prefs.lastAccount)
        inputsContainer.isHidden = true
    }

    private func selectNewAccountMode() {
        selectedAccountLogin = nil
        accountSelector.displayedLogin = ""
        userField.stringValue = ""
        passField.stringValue = ""
        inputsContainer.isHidden = false
        view.window?.makeFirstResponder(userField)
    }

    private func toggleAccountsPopover() {
        if let p = accountsPopover, p.isShown {
            p.performClose(nil); return
        }
        let p = NSPopover()
        p.behavior = .transient
        p.contentViewController = makeAccountsPopoverVC()
        accountsPopover = p
        accountSelector.isOpen = true
        p.show(relativeTo: accountSelector.bounds,
               of: accountSelector,
               preferredEdge: .maxY)
        DispatchQueue.main.async {
            NotificationCenter.default.addObserver(forName: NSPopover.didCloseNotification,
                object: p, queue: .main) { [weak self] _ in
                self?.accountSelector.isOpen = false
                self?.accountsPopover = nil
            }
        }
    }

    private func makeAccountsPopoverVC() -> NSViewController {
        let cardWidth: CGFloat = 320
        let accounts = AccountsStore.load()
        let rows = NSStackView()
        rows.orientation = .vertical
        rows.spacing = 0
        rows.alignment = .leading
        rows.edgeInsets = NSEdgeInsets(top: 6, left: 0, bottom: 6, right: 0)

        for acc in accounts {
            let row = AccountListRow(login: acc.login)
            row.translatesAutoresizingMaskIntoConstraints = false
            row.widthAnchor.constraint(equalToConstant: cardWidth).isActive = true
            row.onSelect = { [weak self] in
                self?.accountsPopover?.performClose(nil)
                self?.selectAccount(acc.login)
            }
            row.onDelete = { [weak self] in
                self?.accountsPopover?.performClose(nil)
                self?.confirmDelete(login: acc.login)
            }
            rows.addArrangedSubview(row)
        }

        if !accounts.isEmpty {
            let sep = NSBox()
            sep.boxType = .separator
            sep.translatesAutoresizingMaskIntoConstraints = false
            sep.heightAnchor.constraint(equalToConstant: 1).isActive = true
            sep.widthAnchor.constraint(equalToConstant: cardWidth).isActive = true
            rows.addArrangedSubview(sep)
        }

        let addRow = AccountListRow(login: "", isAddNew: true)
        addRow.translatesAutoresizingMaskIntoConstraints = false
        addRow.widthAnchor.constraint(equalToConstant: cardWidth).isActive = true
        addRow.onSelect = { [weak self] in
            self?.accountsPopover?.performClose(nil)
            self?.selectNewAccountMode()
        }
        rows.addArrangedSubview(addRow)

        let container = NSView()
        container.wantsLayer = true
        container.layer?.backgroundColor = Palette.inputBg.cgColor
        container.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(rows)
        rows.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            rows.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            rows.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            rows.topAnchor.constraint(equalTo: container.topAnchor),
            rows.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            container.widthAnchor.constraint(equalToConstant: cardWidth),
        ])

        let vc = NSViewController()
        vc.view = container
        return vc
    }

    private func confirmDelete(login: String) {
        let alert = NSAlert()
        alert.messageText = "Supprimer le compte « \(login) » ?"
        alert.informativeText = "Le mot de passe stocké sera effacé. Cette action est irréversible."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Supprimer")
        alert.addButton(withTitle: "Annuler")
        if alert.runModal() == .alertFirstButtonReturn {
            AccountsStore.remove(login: login)
            if let first = AccountsStore.load().first {
                selectAccount(first.login)
            } else {
                selectNewAccountMode()
            }
        }
    }

    private func savePrefs() {
        let d = UserDefaults.standard
        d.set(rememberCheckbox.state == .on, forKey: Prefs.saveLogin)
        if selectedAccountLogin == nil, rememberCheckbox.state == .on {
            AccountsStore.upsert(login: userField.stringValue,
                                 password: passField.stringValue)
            selectedAccountLogin = userField.stringValue
        }
    }

    private func setStatus(_ txt: String, _ kind: StatusPill.Kind) {
        statusReset?.cancel()
        statusPill.label.stringValue = txt
        statusPill.kind = kind
        if kind != .ok {
            let work = DispatchWorkItem { [weak self] in
                self?.statusPill.label.stringValue = "En ligne"
                self?.statusPill.kind = .ok
            }
            statusReset = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 3.5, execute: work)
        }
    }

    @objc private func playTapped() {
        let user = userField.stringValue.trimmingCharacters(in: .whitespaces)
        let pass = passField.stringValue
        if user.isEmpty || pass.isEmpty {
            setStatus("Identifiants requis", .err); return
        }
        savePrefs()
        let host = UserDefaults.standard.string(forKey: Prefs.host) ?? "127.0.0.1"
        let port = Int(UserDefaults.standard.string(forKey: Prefs.port) ?? "5555") ?? 5555
        let serverName = kServerHostKey

        playButton.titleText = "Connexion…"
        playButton.title = "Connexion…"
        playButton.arrowVisible = false
        playButton.isEnabled = false
        setStatus("Authentification…", .warn)

        TCPProbe.test(host: host, port: port) { [weak self] r in
            guard let self else { return }
            switch r {
            case .failure(let e):
                self.playButton.title = ""; self.playButton.titleText = "JOUER"
                self.playButton.arrowVisible = true; self.playButton.isEnabled = true
                self.setStatus("Injoignable : \(e.localizedDescription)", .err)
            case .success(let ms):
                self.setStatus("OK · \(ms)ms", .ok)
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                    do {
                        try DofusLaunch.launch(host: host, port: port,
                            serverName: serverName,
                            login: user.isEmpty ? nil : user,
                            password: pass.isEmpty ? nil : pass)
                    } catch {
                        self.setStatus(error.localizedDescription, .err)
                        self.playButton.title = ""; self.playButton.titleText = "JOUER"
                        self.playButton.arrowVisible = true; self.playButton.isEnabled = true
                    }
                }
            }
        }
    }

    @objc private func toggleAdvanced() {
        if let p = advPopover, p.isShown { p.close(); return }
        let pop = NSPopover()
        pop.behavior = .semitransient
        pop.delegate = self
        pop.appearance = NSAppearance(named: .darkAqua)
        let vc = AdvancedVC()
        vc.onSave = { [weak self] _, _ in
            self?.setStatus("Paramètres enregistrés", .ok)
            pop.close()
        }
        vc.onCancel = { pop.close() }
        pop.contentViewController = vc
        pop.show(relativeTo: advancedButton.bounds, of: advancedButton, preferredEdge: .maxY)
        advPopover = pop
    }

    func popoverDidClose(_ note: Notification) { advPopover = nil }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow!

    func applicationDidFinishLaunching(_ note: Notification) {
        let vc = LauncherVC()
        let style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable,
                                         .fullSizeContentView]
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 620),
                          styleMask: style, backing: .buffered, defer: false)
        window.title = "One Air"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.backgroundColor = Palette.bg
        window.contentViewController = vc
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ s: NSApplication) -> Bool { true }
}

let app = NSApplication.shared
let d = AppDelegate()
app.delegate = d
app.run()
