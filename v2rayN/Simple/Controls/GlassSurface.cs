using Avalonia.Media;
using Avalonia.VisualTree;

namespace WorldScience.Controls;

/// <summary>System Liquid Glass on macOS 26+, with a readable themed fallback elsewhere.
/// The native material is below Avalonia's drawing surface so input and accessibility stay managed.</summary>
public sealed class GlassSurface : Border
{
    public static readonly StyledProperty<bool> ContrastTintProperty = AvaloniaProperty.Register<GlassSurface, bool>(nameof(ContrastTint));
    public bool ContrastTint { get => GetValue(ContrastTintProperty); set => SetValue(ContrastTintProperty, value); }
    public static readonly StyledProperty<bool> KeepContentFillProperty = AvaloniaProperty.Register<GlassSurface, bool>(nameof(KeepContentFill));
    public bool KeepContentFill { get => GetValue(KeepContentFillProperty); set => SetValue(KeepContentFillProperty, value); }
    private IntPtr _glass;
    private TopLevel? _root;
    private IBrush? _fallbackFill, _fallbackEdge;
    private bool _updating;
    private (Rect Bounds, double Radius, bool Visible, double Opacity)? _lastFrame;
    private readonly DispatcherTimer _entrance = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    internal bool NativeActive => _glass != IntPtr.Zero;
    public GlassSurface()
    {
        AttachedToVisualTree += (_, _) =>
        {
            LayoutUpdated += UpdateGlass;
            if (MacGlass.Available && !MacGlass.ReduceMotion) { _clock.Restart(); _entrance.Start(); }
        };
        _entrance.Tick += (_, _) => { UpdateGlass(null, EventArgs.Empty); if (_clock.ElapsedMilliseconds >= 240) _entrance.Stop(); };
        DetachedFromVisualTree += (_, _) =>
        {
            LayoutUpdated -= UpdateGlass; _entrance.Stop();
            var wasNative = NativeActive;
            MacGlass.Remove(ref _glass); _root = null; _lastFrame = null;
            if (wasNative)
            {
                SetCurrentValue(BackgroundProperty, _fallbackFill);
                SetCurrentValue(BorderBrushProperty, _fallbackEdge);
            }
        };
    }
    private void UpdateGlass(object? sender, EventArgs e)
    {
        _root ??= TopLevel.GetTopLevel(this);
        if (_root is null || !MacGlass.Available) return;
        var transform = this.TransformToVisual(_root);
        if (transform is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var rect = new Rect(Bounds.Size).TransformToAABB(transform.Value);
        if (_glass == IntPtr.Zero)
        {
            _glass = MacGlass.Add(_root, ContrastTint);
            if (_glass != IntPtr.Zero)
            {
                _fallbackFill = Background; _fallbackEdge = BorderBrush; ClearNativeFill();
            }
        }
        if (_glass != IntPtr.Zero)
        {
            var opacity = Opacity;
            foreach (var parent in this.GetVisualAncestors()) opacity *= parent.Opacity;
            var radius = CornerRadius.TopLeft * Math.Min(rect.Width / Bounds.Width, rect.Height / Bounds.Height);
            // Native coordinates include the root height, so resizing also invalidates the cache.
            var nativeBounds = new Rect(rect.X, _root.ClientSize.Height - rect.Y - rect.Height, rect.Width, rect.Height);
            var frame = (nativeBounds, radius, IsEffectivelyVisible, opacity);
            if (_lastFrame != frame)
            {
                MacGlass.Update(_glass, rect.TopLeft, rect.Size, _root.ClientSize.Height, radius, IsEffectivelyVisible, opacity);
                _lastFrame = frame;
            }
        }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_updating || !NativeActive) return;
        if (change.Property == BackgroundProperty) { _fallbackFill = Background; ClearNativeFill(); }
        if (change.Property == BorderBrushProperty) { _fallbackEdge = BorderBrush; ClearNativeFill(); }
    }
    private void ClearNativeFill()
    {
        _updating = true;
        // Native glass sits below the Avalonia canvas. An in-window popup needs
        // its own content fill to occlude that canvas's underlying labels.
        SetCurrentValue(BackgroundProperty, KeepContentFill ? _fallbackFill : Brushes.Transparent);
        SetCurrentValue(BorderBrushProperty, Brushes.Transparent);
        _updating = false;
    }

}

internal static class MacGlass
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendObj(IntPtr obj, IntPtr sel, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr At(IntPtr obj, IntPtr sel, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetLong(IntPtr obj, IntPtr sel, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr WithAlpha(IntPtr obj, IntPtr sel, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetDouble(IntPtr obj, IntPtr sel, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetBool(IntPtr obj, IntPtr sel, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void AddBelow(IntPtr obj, IntPtr sel, IntPtr view, nint position, IntPtr relative);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetFrame(IntPtr obj, IntPtr sel, NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativeRect(double x, double y, double width, double height)
    { public readonly double X = x, Y = y, Width = width, Height = height; }
    private static IntPtr S(string name) => sel_registerName(name);
    internal static bool Available => OperatingSystem.IsMacOSVersionAtLeast(26) && objc_getClass("NSGlassEffectView") != IntPtr.Zero;
    internal static bool ReduceMotion => OperatingSystem.IsMacOS() &&
        Send(Send(objc_getClass("NSWorkspace"), S("sharedWorkspace")), S("accessibilityDisplayShouldReduceMotion")) != IntPtr.Zero;
    internal static IntPtr Add(TopLevel root, bool contrastTint)
    {
        // Use only public AppKit APIs; do not replace the window's content view or first responder.
        var handle = root.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "NSWindow" || handle.Handle == IntPtr.Zero) return IntPtr.Zero;
        var container = Send(handle.Handle, S("contentView"));
        var children = Send(container, S("subviews"));
        var count = (int)Send(children, S("count"));
        var avnClass = objc_getClass("AvnView");
        IntPtr drawingView = IntPtr.Zero;
        for (var i = 0; i < count; i++)
        {
            var child = At(children, S("objectAtIndex:"), (nuint)i);
            if (SendObj(child, S("isKindOfClass:"), avnClass) != IntPtr.Zero) drawingView = child;
            // A standard window material beneath the functional glass layer.
            if (root is Window && SendObj(child, S("isKindOfClass:"), objc_getClass("NSVisualEffectView")) != IntPtr.Zero)
                SetLong(child, S("setMaterial:"), 12); // NSVisualEffectMaterialWindowBackground
        }
        // A changed host hierarchy must fall back, never cover controls or steal focus.
        if (drawingView == IntPtr.Zero) return IntPtr.Zero;
        var glass = Send(Send(objc_getClass("NSGlassEffectView"), S("alloc")), S("init"));
        if (contrastTint)
        {
            var background = Send(objc_getClass("NSColor"), S("windowBackgroundColor"));
            SendObj(glass, S("setTintColor:"), WithAlpha(background, S("colorWithAlphaComponent:"), .9));
        }
        var content = Send(Send(objc_getClass("NSView"), S("alloc")), S("init"));
        SendObj(glass, S("setContentView:"), content); Send(content, S("release"));
        AddBelow(container, S("addSubview:positioned:relativeTo:"), glass, -1, drawingView);
        return glass; // our alloc is balanced on detach; superview retains independently
    }
    internal static void Update(IntPtr glass, Point origin, Size size, double rootHeight, double radius, bool visible, double opacity)
    {
        SetFrame(glass, S("setFrame:"), new NativeRect(origin.X, rootHeight - origin.Y - size.Height, size.Width, size.Height));
        SetDouble(glass, S("setCornerRadius:"), radius);
        SetDouble(glass, S("setAlphaValue:"), opacity);
        SetBool(glass, S("setHidden:"), visible ? (byte)0 : (byte)1);
    }
    internal static void Remove(ref IntPtr glass)
    {
        if (glass == IntPtr.Zero) return;
        Send(glass, S("removeFromSuperview")); Send(glass, S("release")); glass = IntPtr.Zero;
    }
}
