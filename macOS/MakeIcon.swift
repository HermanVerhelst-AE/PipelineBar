// Tekent het app-icoon (1024×1024 PNG) met een SF Symbol. Gebruik: swift MakeIcon.swift uit.png
import AppKit

let px = 1024
let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.png")

guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px,
                                 bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { exit(1) }
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// macOS-icoonraster: 824 pt inhoud binnen 1024, afgeronde vierkant.
let tile = NSRect(x: 100, y: 100, width: 824, height: 824)
let shape = NSBezierPath(roundedRect: tile, xRadius: 186, yRadius: 186)
NSGradient(starting: NSColor(srgbRed: 0.10, green: 0.56, blue: 0.98, alpha: 1),
           ending: NSColor(srgbRed: 0.02, green: 0.31, blue: 0.72, alpha: 1))?.draw(in: shape, angle: -90)

let config = NSImage.SymbolConfiguration(pointSize: 430, weight: .semibold)
if let symbol = NSImage(systemSymbolName: "arrow.triangle.branch", accessibilityDescription: nil)?
    .withSymbolConfiguration(config) {
    let size = symbol.size
    let white = NSImage(size: size, flipped: false) { rect in
        symbol.draw(in: rect)
        NSColor.white.set()
        rect.fill(using: .sourceAtop)
        return true
    }
    white.draw(in: NSRect(x: (CGFloat(px) - size.width) / 2, y: (CGFloat(px) - size.height) / 2,
                          width: size.width, height: size.height))
}

NSGraphicsContext.restoreGraphicsState()
try rep.representation(using: .png, properties: [:])!.write(to: out)
