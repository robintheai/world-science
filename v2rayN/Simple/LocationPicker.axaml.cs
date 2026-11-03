using Avalonia.Media;
namespace WorldScience;

public sealed record LocationSelection(string? NodeId);

public sealed class LocationOption(NodeRow? node, bool selected, string? title = null)
{
    public NodeRow? Node { get; } = node;
    public bool Selected { get; } = selected;
    public string Flag => Node is null ? "◎" : CountryLocation.Flag(Node.Country);
    public string Title => title ?? Node?.LocationName ?? Strings.Automatic;
    public string Speed { get; } = node is null ? Strings.AutomaticDetail : node.RankedSpeedLabel;
    /// <summary>The provider's original node name, for users who recognise their nodes by it.</summary>
    public string Detail => Node?.Name ?? Strings.AutomaticDetail;
    private readonly int _signal = node?.Signal ?? -1;
    public bool HasSignal => _signal >= 0;
    public bool Bar1 => _signal >= 1;
    public bool Bar2 => _signal >= 2;
    public bool Bar3 => _signal >= 3;
}

public partial class LocationPicker : UserControl
{
    private readonly ClientController _client;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private readonly Flyout _flyout = new() { Placement = PlacementMode.Bottom };
    private readonly TaskCompletionSource<LocationSelection?> _result = new();
    public ClientController Client => _client;
    public ObservableCollection<LocationOption> Items { get; } = [];
    internal Task Measurement => Task.CompletedTask;

    public LocationPicker(ClientController client)
    {
        _client = client;
        RebuildItems();
        InitializeComponent(); DataContext = this;
        _flyout.Content = this;
        _flyout.FlyoutPresenterTheme = new Avalonia.Styling.ControlTheme(typeof(Avalonia.Controls.FlyoutPresenter))
        {
            BasedOn = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource(typeof(Avalonia.Controls.FlyoutPresenter))!,
            Setters = { new Avalonia.Styling.Setter(BackgroundProperty, Brushes.Transparent),
                new Avalonia.Styling.Setter(PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(BorderThicknessProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(CornerRadiusProperty, new CornerRadius(22)) }
        };
        _flyout.Opened += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } root && root is not Window)
                root.TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur];
            _client.StatusChanged += RefreshItems;
            _ = AnimateOpen();
        };
        _flyout.Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; _closing = true;
            await StopMeasurements(); Dispatcher.UIThread.Post(_flyout.Hide);
        };
        _flyout.Closed += (_, _) =>
        {
            _client.StatusChanged -= RefreshItems;
            _lifetime.Dispose(); _result.TrySetResult(null);
        };
    }

    private void RefreshItems()
    {
        if (_closing) return;
        var next = BuildItems().ToList();
        if (Items.Select(i => (i.Node?.Id, i.Title, i.Speed, i.Selected)).SequenceEqual(next.Select(i => (i.Node?.Id, i.Title, i.Speed, i.Selected)))) return;
        Items.Clear(); foreach (var item in next) Items.Add(item);
    }
    private IEnumerable<LocationOption> BuildItems()
    {
        yield return new LocationOption(null, _client.AutomaticLocation);
        foreach (var node in _client.SpeedRankedLocations)
            yield return new LocationOption(node, !_client.AutomaticLocation && node.Id == _client.ChosenLocationId, _client.LocationTitle(node));
    }
    private void RebuildItems() { foreach (var item in BuildItems()) Items.Add(item); }

    public Task<LocationSelection?> ShowAt(Control target) { _flyout.ShowAt(target); return _result.Task; }
    internal void Dismiss() => _flyout.Hide();

    private async Task AnimateOpen()
    {
        if (Controls.MacGlass.ReduceMotion) return;
        // A small, one-shot expansion from the anchor; no bounce or continuous motion.
        RenderTransformOrigin = new RelativePoint(.5, 0, RelativeUnit.Relative);
        RenderTransform = new ScaleTransform(1, 1);
        var animation = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters =
                    { new Avalonia.Styling.Setter(OpacityProperty, 0d), new Avalonia.Styling.Setter(ScaleTransform.ScaleXProperty, .96d), new Avalonia.Styling.Setter(ScaleTransform.ScaleYProperty, .92d) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters =
                    { new Avalonia.Styling.Setter(OpacityProperty, 1d), new Avalonia.Styling.Setter(ScaleTransform.ScaleXProperty, 1d), new Avalonia.Styling.Setter(ScaleTransform.ScaleYProperty, 1d) } }
            }
        };
        try { await animation.RunAsync(this, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private Task StopMeasurements() { _lifetime.Cancel(); return Task.CompletedTask; }

    private async void TestSpeedClick(object? sender, RoutedEventArgs e)
    {
        if (_client.IsTesting) _client.CancelTest();
        else await _client.MeasureLocations();
    }

    private async void ChooseClick(object? sender, RoutedEventArgs e)
    {
        if (_closing || sender is not Button { DataContext: LocationOption option }) return;
        _closing = true;
        await StopMeasurements();
        _result.TrySetResult(new LocationSelection(option.Node?.Id));
        _flyout.Hide();
    }
}
