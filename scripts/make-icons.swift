import AppKit
let directory = CommandLine.arguments[1]
try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
let size = 1024
func bitmap(_ side:Int) -> NSBitmapImageRep {
 NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:side,pixelsHigh:side,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:0,bitsPerPixel:0)!
}
let bmp=bitmap(size)
NSGraphicsContext.saveGraphicsState();NSGraphicsContext.current=NSGraphicsContext(bitmapImageRep:bmp)
NSColor.clear.setFill(); NSRect(x:0,y:0,width:size,height:size).fill()
let rect = NSRect(x:32,y:32,width:960,height:960)
let shape = NSBezierPath(roundedRect:rect,xRadius:240,yRadius:240)
let gradient = NSGradient(starting:NSColor(calibratedWhite:0.98,alpha:1), ending:NSColor(calibratedWhite:0.82,alpha:1))!
gradient.draw(in:shape,angle:-90)
// The same rounded Z geometry as the connection control, rendered from vector source.
let path = NSBezierPath()
path.move(to:NSPoint(x:18,y:10));path.line(to:NSPoint(x:72,y:10))
path.curve(to:NSPoint(x:78,y:24),controlPoint1:NSPoint(x:80,y:10),controlPoint2:NSPoint(x:84,y:18))
path.line(to:NSPoint(x:34,y:58));path.line(to:NSPoint(x:74,y:58))
path.curve(to:NSPoint(x:74,y:76),controlPoint1:NSPoint(x:86,y:58),controlPoint2:NSPoint(x:86,y:76))
path.line(to:NSPoint(x:16,y:76))
path.curve(to:NSPoint(x:12,y:60),controlPoint1:NSPoint(x:8,y:76),controlPoint2:NSPoint(x:4,y:67))
path.line(to:NSPoint(x:56,y:28));path.line(to:NSPoint(x:18,y:28))
path.curve(to:NSPoint(x:18,y:10),controlPoint1:NSPoint(x:6,y:28),controlPoint2:NSPoint(x:6,y:10));path.close()
var transform = AffineTransform();transform.translate(x:80,y:930);transform.scale(x:10,y:-10);path.transform(using:transform)
NSColor(calibratedWhite:0.20,alpha:1).setFill();path.fill()
NSGraphicsContext.restoreGraphicsState()
let image=NSImage(size:NSSize(width:size,height:size));image.addRepresentation(bmp)
try bmp.representation(using:.png,properties:[:])!.write(to:URL(fileURLWithPath:directory+"/simple.png"))
let set=directory+"/simple.iconset"
try FileManager.default.createDirectory(atPath:set,withIntermediateDirectories:true)
for n in [16,32,128,256,512] {
 for scale in [1,2] {
  let side=n*scale
  let rep=bitmap(side);NSGraphicsContext.saveGraphicsState();NSGraphicsContext.current=NSGraphicsContext(bitmapImageRep:rep)
  image.draw(in:NSRect(x:0,y:0,width:side,height:side));NSGraphicsContext.restoreGraphicsState()
  try rep.representation(using:.png,properties:[:])!.write(to:URL(fileURLWithPath:set+"/icon_\(n)x\(n)"+(scale==2 ? "@2x":"")+".png"))
 }
}
// Menu bar frames come from make-status-frames.swift.
