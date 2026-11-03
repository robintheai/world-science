import AppKit
// No longer used: the menu bar is now the owl (assets/brand/owl/menubar). Running this would bring back the old satellite.
// Mac menu bar template frames: a satellite orbiting the Earth (22 x 16 pt at 2x, black on transparent; macOS tints them).
// status.png is the disconnected globe, status-still.png the parked satellite, status-run-N.png one orbit.
// Usage: swift scripts/make-status-frames.swift v2rayN/Simple/Assets
let directory = CommandLine.arguments[1]
let satelliteAngle: CGFloat = 318
func bitmap(_ w: Int, _ h: Int) -> NSBitmapImageRep {
    NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
}
func render(_ w: Int, _ h: Int, _ draw: () -> Void) -> NSBitmapImageRep {
    let rep = bitmap(w, h)
    NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSColor.clear.setFill(); NSRect(x: 0, y: 0, width: w, height: h).fill()
    draw()
    NSGraphicsContext.restoreGraphicsState()
    return rep
}
func png(_ rep: NSBitmapImageRep) -> Data { rep.representation(using: .png, properties: [:])! }
/// The orbit is an ellipse around the globe centre, tilted so it rises to the right. Angle 0 is the right end;
/// angles in (180°, 360°) are the near half that passes in front of the globe.
struct Orbit {
    var center: NSPoint, rx: CGFloat, ry: CGFloat, tilt: CGFloat = 20
    var transform: AffineTransform { var t = AffineTransform(); t.translate(x: center.x, y: center.y); t.rotate(byDegrees: tilt); return t }
    func point(_ degrees: CGFloat) -> NSPoint {
        let a = degrees * .pi / 180
        return transform.transform(NSPoint(x: rx * cos(a), y: ry * sin(a)))
    }
    func isNear(_ degrees: CGFloat) -> Bool { sin(degrees * .pi / 180) < 0 }
    func path(near: Bool?) -> NSBezierPath {
        let p = NSBezierPath()
        let (from, to): (CGFloat, CGFloat) = near == nil ? (0, 360) : near! ? (180, 360) : (0, 180)
        p.appendArc(withCenter: .zero, radius: 1, startAngle: from, endAngle: to)
        var scale = AffineTransform(); scale.scale(x: rx, y: ry); p.transform(using: scale); p.transform(using: transform)
        return p
    }
    /// Direction of travel, used to align the solar panels.
    func heading(_ degrees: CGFloat) -> CGFloat {
        let a = point(degrees), b = point(degrees + 1)
        return atan2(b.y - a.y, b.x - a.x) * 180 / .pi
    }
}

/// Satellite: a body with two solar wings across the direction of travel.
func satellite(at p: NSPoint, heading: CGFloat, size s: CGFloat, body: NSColor, wing: NSColor, ribs: NSColor?, span: CGFloat = 1.4) {
    NSGraphicsContext.saveGraphicsState()
    let t = NSAffineTransform(); t.translateX(by: p.x, yBy: p.y); t.rotate(byDegrees: heading + 90); t.concat()
    for side: CGFloat in [-1, 1] {
        let panel = NSRect(x: side < 0 ? -s * (0.75 + span) : s * 0.75, y: -s * 0.38, width: s * span, height: s * 0.76)
        wing.setFill(); NSBezierPath(roundedRect: panel, xRadius: s * 0.08, yRadius: s * 0.08).fill()
        if let ribs {
            ribs.setStroke()
            for i in 1...2 {
                let x = panel.minX + panel.width * CGFloat(i) / 3
                let line = NSBezierPath(); line.move(to: NSPoint(x: x, y: panel.minY)); line.line(to: NSPoint(x: x, y: panel.maxY)); line.lineWidth = s * 0.06; line.stroke()
            }
        }
        let strut = NSBezierPath(); strut.move(to: NSPoint(x: side * s * 0.45, y: 0)); strut.line(to: NSPoint(x: side * s * 0.8, y: 0)); strut.lineWidth = s * 0.16
        body.setStroke(); strut.stroke()
    }
    body.setFill(); NSBezierPath(roundedRect: NSRect(x: -s * 0.5, y: -s * 0.5, width: s, height: s), xRadius: s * 0.18, yRadius: s * 0.18).fill()
    NSGraphicsContext.restoreGraphicsState()
}

/// Latitude and longitude lines of a sphere seen slightly from above, clipped to the disc.
func graticule(center c: NSPoint, radius r: CGFloat, width: CGFloat, color: NSColor, simple: Bool = false) {
    NSGraphicsContext.saveGraphicsState()
    NSBezierPath(ovalIn: NSRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r)).addClip()
    color.setStroke()
    for k in [0.42] as [CGFloat] {
        let m = NSBezierPath(ovalIn: NSRect(x: c.x - r * k, y: c.y - r, width: 2 * r * k, height: 2 * r)); m.lineWidth = width; m.stroke()
    }
    for lat in (simple ? [] : [-0.45, 0.45]) as [CGFloat] {
        let y = c.y + r * lat, half = r * sqrt(1 - lat * lat)
        let l = NSBezierPath(); l.move(to: NSPoint(x: c.x - half, y: y)); l.line(to: NSPoint(x: c.x + half, y: y)); l.lineWidth = width; l.stroke()
    }
    let e = NSBezierPath(); e.move(to: NSPoint(x: c.x - r, y: c.y)); e.line(to: NSPoint(x: c.x + r, y: c.y)); e.lineWidth = width; e.stroke()
    NSGraphicsContext.restoreGraphicsState()
}

let w = 44, h = 32
let small = Orbit(center: NSPoint(x: 22, y: 16), rx: 18, ry: 5.4, tilt: 16)
let smallRadius: CGFloat = 8.4
func hidden(_ angle: CGFloat) -> Bool {
    let q = small.point(angle)
    return !small.isNear(angle) && hypot(q.x - small.center.x, q.y - small.center.y) < smallRadius + 1
}
func mark(satelliteAt angle: CGFloat?, trail: Bool) -> NSBitmapImageRep {
    render(w, h) {
        let ink = NSColor.black
        let disc = NSBezierPath(ovalIn: NSRect(x: small.center.x - smallRadius, y: small.center.y - smallRadius, width: 2 * smallRadius, height: 2 * smallRadius))
        let farGap = NSBezierPath(ovalIn: NSRect(x: small.center.x - smallRadius - 2, y: small.center.y - smallRadius - 2, width: 2 * smallRadius + 4, height: 2 * smallRadius + 4))
        // Far half of the orbit, hidden where the globe covers it.
        NSGraphicsContext.saveGraphicsState()
        let outside = NSBezierPath(rect: NSRect(x: 0, y: 0, width: w, height: h)); outside.append(farGap); outside.windingRule = .evenOdd; outside.addClip()
        let far = small.path(near: false); far.lineWidth = 2.2; ink.setStroke(); far.stroke()
        NSGraphicsContext.restoreGraphicsState()
        disc.lineWidth = 2.4; ink.setStroke(); disc.stroke()
        graticule(center: small.center, radius: smallRadius, width: 1.3, color: ink.withAlphaComponent(0.75), simple: true)
        // Near half crosses in front of the globe with a small knockout so the lines stay separate.
        let near = small.path(near: true)
        NSGraphicsContext.current?.compositingOperation = .clear
        near.lineWidth = 5.6; near.stroke()
        NSGraphicsContext.current?.compositingOperation = .sourceOver
        near.lineWidth = 2.2; ink.setStroke(); near.stroke()
        guard let angle else { return }
        if trail {
            for i in 1...3 {
                let back = angle - CGFloat(i) * 13
                if hidden(back) { continue }
                let q = small.point(back), r = 2.2 - CGFloat(i) * 0.4
                ink.withAlphaComponent(0.55 - CGFloat(i) * 0.15).setFill()
                NSBezierPath(ovalIn: NSRect(x: q.x - r, y: q.y - r, width: 2 * r, height: 2 * r)).fill()
            }
        }
        if hidden(angle) { return }
        let p = small.point(angle)
        NSGraphicsContext.current?.compositingOperation = .clear
        NSBezierPath(ovalIn: NSRect(x: p.x - 4, y: p.y - 4, width: 8, height: 8)).fill()
        NSGraphicsContext.current?.compositingOperation = .sourceOver
        satellite(at: p, heading: small.heading(angle), size: 3.6, body: ink, wing: ink, ribs: nil, span: 0.85)
    }
}
try png(mark(satelliteAt: nil, trail: false)).write(to: URL(fileURLWithPath: directory + "/status.png"))
try png(mark(satelliteAt: satelliteAngle, trail: false)).write(to: URL(fileURLWithPath: directory + "/status-still.png"))
let frames = 12
for old in (try? FileManager.default.contentsOfDirectory(atPath: directory)) ?? [] where old.hasPrefix("status-run-") {
    try FileManager.default.removeItem(atPath: directory + "/" + old)
}
for i in 0..<frames {
    // Counter-clockwise from the parked position, so frame 0 continues the still pose.
    let angle = satelliteAngle + 360 * CGFloat(i) / CGFloat(frames)
    try png(mark(satelliteAt: angle, trail: true)).write(to: URL(fileURLWithPath: directory + "/status-run-\(i).png"))
}

