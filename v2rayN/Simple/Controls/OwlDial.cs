using Avalonia.Media;
namespace WorldScience.Controls;

/// <summary>Owl avatar filling the connect button. Disconnected: daylight white, eyes shut. Connecting: dusk colours,
/// eyes half open and glowing softly. Connected: charcoal night, the moon behind the owl, amber eyes that blink now and then.
/// Scenes cross-fade; the frame timer runs only while a fade, the connecting pulse or a blink is in progress.</summary>
public sealed class OwlDial : Control
{
    public static readonly StyledProperty<bool> ConnectedProperty = AvaloniaProperty.Register<OwlDial, bool>(nameof(Connected));
    public static readonly StyledProperty<bool> PendingProperty = AvaloniaProperty.Register<OwlDial, bool>(nameof(Pending));
    public bool Connected { get => GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }
    public bool Pending { get => GetValue(PendingProperty); set => SetValue(PendingProperty, value); }
    // Owl drawn in 512-unit coordinates, the same outline as the app icon.
    private static readonly Geometry Body = Geometry.Parse("M184 150C212 170 236 178 256 178C276 178 300 170 328 150C342 186 350 218 348 252C372 296 376 360 344 398C320 426 290 434 256 434C222 434 192 426 168 398C136 360 140 296 164 252C162 218 170 186 184 150Z");
    private static readonly Geometry Beak = Geometry.Parse("M244 304H268L256 324Z");
    private static readonly Geometry ShutEyes = Geometry.Parse("M192 260Q214 280 236 260M276 260Q298 280 320 260");
    private static readonly Color Amber = Color.Parse("#FFB52E"), Ink = Color.Parse("#0E1838");
    private static readonly IBrush DayFill = new RadialGradientBrush { GradientStops = { new(Color.Parse("#FFFFFF"), 0), new(Color.Parse("#EFEFEA"), 1) } };
    private static readonly IBrush DuskFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(.5, 1, RelativeUnit.Relative),
        GradientStops = { new(Color.Parse("#343A7C"), 0), new(Color.Parse("#8A5EA0"), .55), new(Color.Parse("#F2A877"), 1) },
    };
    private static readonly IBrush NightFill = new RadialGradientBrush { GradientStops = { new(Color.Parse("#2C2E35"), 0), new(Color.Parse("#0D0E11"), 1) } };
    private static readonly IBrush MoonFill = new RadialGradientBrush
    {
        Center = new RelativePoint(.42, .38, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.42, .38, RelativeUnit.Relative),
        GradientStops = { new(Color.Parse("#FFF4D2"), 0), new(Color.Parse("#F1D88E"), 1) },
    };
    private static readonly IBrush Glow = new RadialGradientBrush { GradientStops = { new(Color.Parse("#F2FFC94D"), 0), new(Color.Parse("#00FFC94D"), 1) } };
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _blinkDue = new() { Interval = TimeSpan.FromSeconds(6.5) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _night, _dusk, _blinkStart = double.NegativeInfinity;
    private bool _attached, _initialized;
    static OwlDial() => AffectsRender<OwlDial>(ConnectedProperty, PendingProperty);
    public OwlDial()
    {
        _frame.Tick += (_, _) => Step();
        _blinkDue.Tick += (_, _) => { _blinkStart = Now; _frame.Start(); };
        AttachedToVisualTree += (_, _) => { _attached = true; Settle(MacGlass.ReduceMotion || !_initialized); _initialized = true; };
        DetachedFromVisualTree += (_, _) => { _attached = false; _frame.Stop(); _blinkDue.Stop(); };
    }
    private double Now => _clock.Elapsed.TotalSeconds;
    private double NightTarget => Connected && !Pending ? 1 : 0;
    private double DuskTarget => Pending ? 1 : 0;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ConnectedProperty || change.Property == PendingProperty) Settle(MacGlass.ReduceMotion);
    }
    private void Settle(bool instantly)
    {
        // Off screen there is nothing to animate: snap, so a detached dial also renders its final scene.
        if (instantly || !_attached) { _night = NightTarget; _dusk = DuskTarget; }
        if (!_attached) return;
        if (NightTarget == 1 && !MacGlass.ReduceMotion) _blinkDue.Start(); else _blinkDue.Stop();
        if (!instantly || Pending) _frame.Start();
        InvalidateVisual();
    }
    private void Step()
    {
        _night += (NightTarget - _night) * .14;
        _dusk += (DuskTarget - _dusk) * .14;
        var fading = Math.Abs(NightTarget - _night) > .004 || Math.Abs(DuskTarget - _dusk) > .004;
        if (!fading) { _night = NightTarget; _dusk = DuskTarget; }
        var blinking = Now - _blinkStart < .24;
        if (!fading && !blinking && (!Pending || MacGlass.ReduceMotion)) _frame.Stop();
        InvalidateVisual();
    }
    private static Color Mix(Color day, Color dusk, Color night, double d, double n)
    {
        var a = 1 - d - n;
        byte C(byte x, byte y, byte z) => (byte)Math.Clamp(Math.Round(x * a + y * d + z * n), 0, 255);
        return Color.FromRgb(C(day.R, dusk.R, night.R), C(day.G, dusk.G, night.G), C(day.B, dusk.B, night.B));
    }
    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = s / 2;
        double n = Math.Clamp(_night, 0, 1), d = Math.Clamp(_dusk, 0, 1 - n), day = 1 - n - d;
        context.DrawEllipse(DayFill, null, c, r, r);
        using (context.PushOpacity(d)) context.DrawEllipse(DuskFill, null, c, r, r);
        using (context.PushOpacity(n)) context.DrawEllipse(NightFill, null, c, r, r);
        // The moon rises behind the owl as night falls.
        var moon = n * .95 + d * .8;
        if (moon > .01)
            using (context.PushOpacity(moon))
                context.DrawEllipse(MoonFill, null, new Point(c.X, c.Y - s * (.11 - .07 * (1 - n))), s * .25, s * .25);
        var k = s * .40 / 296;
        var owl = Matrix.CreateTranslation(-256, -144) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(c.X, s * .15);
        using (context.PushTransform(owl))
        {
            var body = Mix(Color.Parse("#55658F"), Color.Parse("#2A2D5E"), Color.Parse("#0B0D16"), d, n);
            context.DrawGeometry(new SolidColorBrush(body), new Pen(new SolidColorBrush(body), 12, lineJoin: PenLineJoin.Round), Body);
            var pulse = Pending && !MacGlass.ReduceMotion ? .5 + .5 * Math.Sin(Now * 4) : .5;
            var glow = n * .6 + d * (.15 + .25 * pulse);
            if (glow > .01) using (context.PushOpacity(glow)) context.DrawEllipse(Glow, null, new Point(256, 262), 140, 76);
            var blink = Now - _blinkStart < .24 ? Math.Sin(Math.PI * (Now - _blinkStart) / .24) : 0;
            var open = (n + d * .45) * (1 - .92 * blink);
            if (open > .02)
                foreach (var (eye, pupil, shine) in new[] { (214.0, 218.0, 208.0), (298.0, 294.0, 288.0) })
                {
                    context.DrawEllipse(new SolidColorBrush(Amber), null, new Point(eye, 262), 36, 36 * open);
                    context.DrawEllipse(new SolidColorBrush(Ink), null, new Point(pupil, 265), 15, 15 * open);
                    if (open > .6) context.DrawEllipse(new SolidColorBrush(Color.Parse("#FFF3C4"), (open - .6) / .4), null, new Point(shine, 254), 6, 6);
                }
            if (day > .01) context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#D5DCF2"), day), 9, lineCap: PenLineCap.Round), ShutEyes);
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#E59A2F")), new Pen(new SolidColorBrush(Color.Parse("#E59A2F")), 8, lineJoin: PenLineJoin.Round), Beak);
        }
    }
}
