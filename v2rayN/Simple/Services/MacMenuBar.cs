using System.Text;
namespace WorldScience.Services;
/// <summary>Native NSStatusItem supports a system-adaptive template icon and live text; Avalonia's tray tooltip alone cannot do this.
/// Disconnected the owl sleeps; connected it opens its eyes and, while traffic flows, beats its wings faster as traffic grows.</summary>
public sealed class MacMenuBar : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect GetRect(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void GetRectIntel(out Rect result, IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRect(IntPtr obj, IntPtr sel, Rect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void DrawRect(IntPtr obj, IntPtr sel, Rect rect, IntPtr bitmap);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr EncodeBitmap(IntPtr obj, IntPtr sel, long type, IntPtr properties);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern bool WriteData(IntPtr obj, IntPtr sel, IntPtr path, bool atomic);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, UIntPtr extra);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendObj(IntPtr obj, IntPtr sel, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDouble(IntPtr obj, IntPtr sel, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendTwoDouble(IntPtr obj, IntPtr sel, double size, double weight);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendTwoObj(IntPtr obj, IntPtr sel, IntPtr first, IntPtr second);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double GetDouble(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern bool GetBool(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLong(IntPtr obj, IntPtr sel, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSize(IntPtr obj, IntPtr sel, Size value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr NewString(IntPtr obj, IntPtr sel, string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr NewItem(IntPtr obj, IntPtr sel, IntPtr title, IntPtr action, IntPtr key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeAction(IntPtr self, IntPtr cmd, IntPtr sender);
    private static NativeAction? _action;
    private static Action<string>? _clicked;
    private readonly IntPtr _item;
    private readonly IntPtr _target;
    private readonly IntPtr _button;
    private readonly IntPtr _image;
    private readonly IntPtr _still;
    private readonly IntPtr[] _run;
    internal int FrameCount => _run.Length;
    private readonly IntPtr _textAttributes;
    private readonly DispatcherTimer _animation = new();
    private IntPtr _renderedImage;
    private string _title = "";
    private string _tooltip = "";
    private bool _connected;
    private int _frame;
    private bool _previewRunning;
    // Pose 22x16 pt, a 2 pt gap, then a text column wide enough for "999.9M/s↓" at 9 pt.
    private const double PoseWidth = 22, TextLeft = 24, TextWidth = 50, IconOnlyLength = 26;
    internal const double SpeedLength = TextLeft + TextWidth + 4;
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr InitSize(IntPtr obj, IntPtr sel, Size size);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void DrawImage(IntPtr obj, IntPtr sel, Rect dest, Rect source, ulong operation, double fraction);
    private bool _disposed;
    internal double Width => GetDouble(_item, Sel("length"));
    internal string DisplayedTitle => _title;
    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr Str(string text) => NewString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), text);
    public MacMenuBar(string icon, Action<string> clicked)
    {
        _clicked = clicked;
        var cls = objc_getClass("WorldScienceMenuTarget");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(objc_getClass("NSObject"), "WorldScienceMenuTarget", UIntPtr.Zero);
            _action = (self, cmd, sender) => Dispatcher.UIThread.Post(() => _clicked?.Invoke(cmd == Sel("simpleQuit:") ? "quit" : cmd == Sel("simpleToggle:") ? "toggle" : "open"));
            foreach (var action in new[] { "simpleOpen:", "simpleToggle:", "simpleQuit:" })
                class_addMethod(cls, Sel(action), Marshal.GetFunctionPointerForDelegate(_action), "v@:@");
            objc_registerClassPair(cls);
        }
        _target = Send(Send(cls, Sel("alloc")), Sel("init"));
        var bar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
        _item = SendDouble(bar, Sel("statusItemWithLength:"), 84); Send(_item, Sel("retain"));
        _button = Send(_item, Sel("button"));
        _image = SendObj(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), Str(icon));
        SendSize(_image, Sel("setSize:"), new Size(PoseWidth, 16)); SendLong(_image, Sel("setTemplate:"), 1);
        var assets = Path.GetDirectoryName(icon)!;
        _still = Pose(Path.Combine(assets, "status-still.png"));
        _run = Enumerable.Range(0, 64).Select(i => Path.Combine(assets, $"status-run-{i}.png")).TakeWhile(File.Exists).Select(Pose).ToArray();
        if (_run.Length == 0) _run = [_still];
        _animation.Tick += (_, _) => { _frame = (_frame + 1) % _run.Length; Render(); };
        SendObj(_button, Sel("setImage:"), _image); SendLong(_button, Sel("setImagePosition:"), 2);
        var font = SendTwoDouble(objc_getClass("NSFont"), Sel("monospacedDigitSystemFontOfSize:weight:"), 9, 0);
        SendObj(_button, Sel("setFont:"), font);
        var cell = Send(_button, Sel("cell"));
        SendLong(cell, Sel("setUsesSingleLineMode:"), 0); SendLong(cell, Sel("setWraps:"), 1);
        _textAttributes = Send(Send(objc_getClass("NSMutableDictionary"), Sel("alloc")), Sel("init"));
        SendTwoObj(_textAttributes, Sel("setObject:forKey:"), font, Str("NSFont"));
        SendTwoObj(_textAttributes, Sel("setObject:forKey:"), Send(objc_getClass("NSColor"), Sel("blackColor")), Str("NSColor"));
        // Draw icon and rates into one template image so macOS centers the whole block.
        // A multiline NSStatusBarButton title uses a single-line baseline and clips or floats.
        var paragraph = Send(Send(objc_getClass("NSMutableParagraphStyle"), Sel("alloc")), Sel("init"));
        SendLong(paragraph, Sel("setAlignment:"), 0); // left: rates sit right next to the owl
        SendDouble(paragraph, Sel("setMinimumLineHeight:"), 9.5); SendDouble(paragraph, Sel("setMaximumLineHeight:"), 9.5);
        SendTwoObj(_textAttributes, Sel("setObject:forKey:"), paragraph, Str("NSParagraphStyle"));
        Send(paragraph, Sel("release"));
        var menu = Send(Send(objc_getClass("NSMenu"), Sel("alloc")), Sel("init"));
        foreach (var (title, action) in new[] { (Ui.T("Open Owlpath", "打开 Owlpath"), "simpleOpen:"), (Ui.T("Connect / Disconnect", "连接 / 断开"), "simpleToggle:"), (Ui.T("Quit", "退出"), "simpleQuit:") })
        {
            var item = NewItem(Send(objc_getClass("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"), Str(title), Sel(action), Str(""));
            SendObj(item, Sel("setTarget:"), _target); SendObj(menu, Sel("addItem:"), item); Send(item, Sel("release"));
        }
        SendObj(_item, Sel("setMenu:"), menu); Send(menu, Sel("release"));
    }
    private static IntPtr Pose(string path)
    {
        var image = SendObj(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), Str(path));
        SendSize(image, Sel("setSize:"), new Size(PoseWidth, 16)); SendLong(image, Sel("setTemplate:"), 1);
        return image;
    }
    /// <summary>Time per running frame, or null to stand still. About 4 frames/s at 1 KB/s up to 22 frames/s at 100 MB/s.</summary>
    internal static TimeSpan? FrameInterval(long bytesPerSecond, bool reduceMotion)
    {
        if (reduceMotion || bytesPerSecond < 1000) return null;
        var ms = 220 - 35 * Math.Log10(bytesPerSecond / 1000d);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 45, 220));
    }
    private static bool ReduceMotion() =>
        GetBool(Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace")), Sel("accessibilityDisplayShouldReduceMotion"));
    public void Update(ClientController controller)
    {
        if (_disposed) return;
        _connected = controller.Connected;
        _title = _connected && controller.Settings.ShowMenuSpeed ? controller.MenuTrafficText : "";
        var interval = _connected ? FrameInterval(controller.MenuRate, ReduceMotion()) : null;
        if (interval is { } every) { if (_animation.Interval != every) _animation.Interval = every; if (!_animation.IsEnabled) _animation.Start(); }
        else { _animation.Stop(); _frame = 0; }
        Render();
        // Setting a tooltip makes macOS pop it up at the pointer, wherever the pointer is. Live rates changed it every
        // second, so it followed every click; set it only when status or node changes and keep rates out of it.
        var tip = $"Owlpath · {controller.ConnectionStatus}\n{controller.CurrentNode}";
        if (tip != _tooltip) { _tooltip = tip; SendObj(_button, Sel("setToolTip:"), Str(tip)); }
    }
    private void Render()
    {
        if (_disposed) return;
        var previous = _renderedImage;
        _renderedImage = IntPtr.Zero;
        if (!_connected)
        {
            SendObj(_button, Sel("setImage:"), _image);
            SendDouble(_item, Sel("setLength:"), IconOnlyLength);
        }
        else
        {
            var width = _title.Length == 0 ? PoseWidth : TextLeft + TextWidth;
            _renderedImage = InitSize(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithSize:"), new Size(width, 20));
            Send(_renderedImage, Sel("lockFocus"));
            var pose = _animation.IsEnabled || _previewRunning ? _run[_frame] : _still;
            DrawImage(pose, Sel("drawInRect:fromRect:operation:fraction:"), new Rect(0, 2, PoseWidth, 16), new Rect(0, 0, 0, 0), 2, 1);
            var rows = _title.Length == 0 ? [] : _title.Split('\n');
            for (var i = 0; i < Math.Min(2, rows.Length); i++)
            {
                var row = SendTwoObj(Send(objc_getClass("NSAttributedString"), Sel("alloc")), Sel("initWithString:attributes:"), Str(rows[i]), _textAttributes);
                SendRect(row, Sel("drawInRect:"), new Rect(TextLeft, i == 0 ? 10 : 0, TextWidth, 10));
                Send(row, Sel("release"));
            }
            Send(_renderedImage, Sel("unlockFocus"));
            SendLong(_renderedImage, Sel("setTemplate:"), 1);
            SendObj(_button, Sel("setImage:"), _renderedImage);
            SendDouble(_item, Sel("setLength:"), _title.Length == 0 ? IconOnlyLength : SpeedLength);
        }
        if (previous != IntPtr.Zero) Send(previous, Sel("release"));
    }
    // Render this control alone for isolated visual QA; no desktop or user content is captured.
    internal bool SavePreview(string path, string? previewTitle = null, int? runFrame = null)
    {
        var (saved, savedConnected, savedFrame) = (_title, _connected, _frame);
        if (runFrame is { } f) { _previewRunning = true; _frame = f % _run.Length; }
        if (previewTitle is not null) { _title = previewTitle; _connected = true; Render(); }
        Rect bounds;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) GetRectIntel(out bounds, _button, Sel("bounds"));
        else bounds = GetRect(_button, Sel("bounds"));
        var bitmap = SendRect(_button, Sel("bitmapImageRepForCachingDisplayInRect:"), bounds);
        DrawRect(_button, Sel("cacheDisplayInRect:toBitmapImageRep:"), bounds, bitmap);
        var data = EncodeBitmap(bitmap, Sel("representationUsingType:properties:"), 4,
            Send(objc_getClass("NSDictionary"), Sel("dictionary")));
        var written = WriteData(data, Sel("writeToFile:atomically:"), Str(path), true);
        _previewRunning = false; _frame = savedFrame;
        if (previewTitle is not null) { _title = saved; _connected = savedConnected; Render(); }
        return written;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _clicked = null; _animation.Stop();
        var bar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
        SendObj(bar, Sel("removeStatusItem:"), _item);
        if (_renderedImage != IntPtr.Zero) Send(_renderedImage, Sel("release"));
        Send(_item, Sel("release")); Send(_target, Sel("release")); Send(_image, Sel("release")); Send(_textAttributes, Sel("release"));
        Send(_still, Sel("release")); foreach (var frame in _run) Send(frame, Sel("release"));
    }
}
