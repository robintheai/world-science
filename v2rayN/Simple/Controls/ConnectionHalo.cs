using Avalonia.Media;
namespace WorldScience.Controls;

/// <summary>Native progress/focus feedback. The timer runs only during connection recovery.</summary>
public sealed class ConnectionHalo : Control
{
    public static readonly StyledProperty<bool> ConnectedProperty = AvaloniaProperty.Register<ConnectionHalo, bool>(nameof(Connected));
    public static readonly StyledProperty<bool> PendingProperty = AvaloniaProperty.Register<ConnectionHalo, bool>(nameof(Pending));
    public static readonly StyledProperty<bool> FocusedProperty = AvaloniaProperty.Register<ConnectionHalo, bool>(nameof(Focused));
    public bool Focused { get => GetValue(FocusedProperty); set => SetValue(FocusedProperty, value); }
    public bool Connected { get => GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }
    public bool Pending { get => GetValue(PendingProperty); set => SetValue(PendingProperty, value); }
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _attached;
    private double _connectedAt = double.NegativeInfinity;
    private bool Celebrating => Connected && _clock.Elapsed.TotalSeconds - _connectedAt < 1.2;
    static ConnectionHalo() => AffectsRender<ConnectionHalo>(ConnectedProperty, PendingProperty, FocusedProperty);
    public ConnectionHalo()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => { if (IsEffectivelyVisible && (Pending || Celebrating)) InvalidateVisual(); else UpdateTimer(); };
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateTimer(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; _timer.Stop(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ConnectedProperty && Connected) _connectedAt = _clock.Elapsed.TotalSeconds;
        if (change.Property == PendingProperty || change.Property == ConnectedProperty) UpdateTimer();
    }
    private void UpdateTimer()
    {
        if (_timer is null) return;
        if (_attached && !MacGlass.ReduceMotion && (Pending || Celebrating)) _timer.Start(); else _timer.Stop();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 9;
        if (Focused) context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#8B8D93"), .6), 1), center, radius + 5, radius + 5);
        var color = Connected ? Color.Parse("#34C759") : Color.Parse("#8B8D93");
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White, .11), 1), center, radius, radius);
        // A steady green ring makes "protected" readable at a glance, without a running animation.
        if (Connected && !Pending)
        {
            context.DrawEllipse(null, new Pen(new SolidColorBrush(color, .16), 7), center, radius, radius);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(color, .9), 2), center, radius, radius);
        }
        if (Celebrating)
        {
            var t = Math.Clamp((_clock.Elapsed.TotalSeconds - _connectedAt) / 1.2, 0, 1);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(color, .65 * (1-t)), 2), center, radius + 7*t, radius + 7*t);
        }
        if (!Pending) return;
        var start = Pending ? _clock.Elapsed.TotalSeconds * 2.5 : -Math.PI / 2;
        var sweep = Pending ? Math.PI * 1.3 : Math.PI * .30;
        var end = start + sweep;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            path.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.7, lineCap: PenLineCap.Round), geometry);
        context.DrawEllipse(new SolidColorBrush(color), new Pen(new SolidColorBrush(Colors.White, .6), 1), new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), 3, 3);
    }
}
