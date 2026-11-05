using System.Net.Sockets;
using System.Text.Json;
namespace WorldScience.Services;

public sealed class NodeRow : INotifyPropertyChanged
{
    public ProfileItem Profile { get; }
    public string Id => Profile.IndexId;
    public string Name => string.IsNullOrWhiteSpace(Profile.Remarks) ? Profile.ConfigType.ToString() : Profile.Remarks;
    public string Protocol => Profile.ConfigType.ToString();
    public string Flag
    {
        get
        {
            var runes = Name.EnumerateRunes().ToArray();
            return runes.Length >= 2 && runes[0].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[1].Value is >= 0x1F1E6 and <= 0x1F1FF ? string.Concat(runes.Take(2).Select(r => r.ToString())) : "◉";
        }
    }
    public string Country => CountryLocation.Name(Name);
    public string CountryLabel => Ui.Country(Country);
    public string ShortName => Flag == "◉" ? Name : Name[Flag.Length..].Trim();
    public string LocationName
    {
        get
        {
            var text = ShortName;
            if (!Ui.Chinese)
                foreach (var (source, target) in new[] { ("美国", "United States"), ("日本", "Japan"), ("德国", "Germany"), ("荷兰", "Netherlands"), ("香港", "Hong Kong"), ("台湾", "Taiwan"), ("新加坡", "Singapore"), ("英国", "United Kingdom"), ("加拿大", "Canada"), ("法国", "France"), ("韩国", "South Korea"), ("澳大利亚", "Australia") }) text = text.Replace(source, target);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(?:VLESS|VMESS|REALITY|XTLS|TLS|VISION|HYSTERIA\s*2?|HY2|ANYTLS|TROJAN|TUIC\s*V?5?|SHADOWSOCKS|SOCKS|UDP|TCP|IPV[46]|BBR)\b", "");
            var label = System.Text.RegularExpressions.Regex.Replace(text, @"[\s·|/—-]+", " ").Trim();
            return string.IsNullOrWhiteSpace(label) ? ShortName : label;
        }
    }
    public bool Testing { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool HasFreshSpeed => Failure is null && Speed is > 0 && double.IsFinite(Speed.Value)
        && LastTestAt is { } at && at <= DateTimeOffset.Now && DateTimeOffset.Now - at < TimeSpan.FromHours(2);
    public bool HasFreshMeasurement => HasFreshSpeed && Delay > 0;
    /// <summary>Latency from the lightweight background check, which uses almost no subscription data.</summary>
    public DateTimeOffset? LastDelayAt { get; set; }
    public bool HasFreshDelay => Failure is null && Delay is > 0 && double.IsFinite(Delay.Value)
        && LastDelayAt is { } at && at <= DateTimeOffset.Now && DateTimeOffset.Now - at < TimeSpan.FromHours(2);
    public string RankedSpeedLabel => Testing ? Ui.T("Testing…", "测速中…") : Failure is not null ? (Failure == "Timeout" ? Ui.T("Timed out", "超时") : Ui.T("Unavailable", "不可用"))
        : HasFreshSpeed ? (Speed!.Value * 8 < .1 ? "<0.1 Mbps" : $"{Speed!.Value * 8:0.0} Mbps") : HasFreshDelay ? $"{Delay:0} ms" : "—";
    /// <summary>0–3 signal bars for the location list: speed when measured, otherwise latency.</summary>
    public int Signal => Failure is not null ? 0 : HasFreshSpeed ? (Speed * 8) switch { >= 50 => 3, >= 15 => 2, > 0 => 1, _ => 0 }
        : HasFreshDelay ? Delay switch { <= 150 => 3, <= 300 => 2, _ => 1 } : -1;
    public string DelayMethod { get; set; } = "CONNECT";
    public string LatencyLabel => Delay.HasValue ? (Delay > 0 ? $"{Delay:0} ms" : Ui.T("Timed out", "超时")) : "";
    public string DownloadLabel => Failure ?? (Speed.HasValue && Speed > 0 ? $"{Speed:0.00} MB/s" : "");
    public bool Current { get; set; }
    public string DisplayName => (Current ? "●  " : "     ") + Name;
    public int Sort { get; set; }
    public double? Delay { get; set; }
    public double? Speed { get; set; }
    public string? Failure { get; set; }
    public string DelayText => Delay.HasValue ? (Delay > 0 ? $"{Delay:0}" : Ui.T("Timed out", "超时")) : "—";
    public string SpeedText => Failure ?? (Speed.HasValue ? $"{Speed:0.00}" : "—");
    public event PropertyChangedEventHandler? PropertyChanged;
    public NodeRow(ProfileItem profile) => Profile = profile;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class ClientController(bool integrationTest = false) : INotifyPropertyChanged
{
    public Config Config => AppManager.Instance.Config;
    public SimpleSettings Settings { get; } = SimpleSettings.Load();
    public List<NodeRow> Nodes { get; } = [];
    public ObservableCollection<NodeRow> VisibleNodes { get; } = [];
    public ObservableCollection<NodeGroup> Groups { get; } = [];
    private Dictionary<string,string> _groupNames = new();
    private HashSet<string> _disabledSubIds = new();
    public NodeRow? SelectedNode { get; set; }
    public bool Connected { get; private set; }
    public bool Busy { get; private set; }
    public string ConnectionStatus => Connected ? (_offline ? Ui.T("Waiting for network…", "等待网络恢复…") : _health.Degraded ? Ui.T("Checking connection…", "正在检查连接…") : Ui.T("Connected", "已连接"))
        : _wantConnected ? (_offline ? Ui.T("Waiting for network…", "等待网络恢复…") : Busy || _powerCts is not null ? Ui.T("Connecting…", "正在连接…") : Ui.T("Reconnecting…", "正在重连…"))
        : Ui.T("Not connected", "未连接");
    public string ConnectionHint => Connected ? CurrentNode : _wantConnected && AutomaticLocation ? Ui.T("Finding the best location", "正在寻找最佳线路") : LocationChoice;
    /// <summary>Time on the current session, across automatic reconnects; empty when not connected.</summary>
    public DateTimeOffset? ConnectedSince { get; private set; }
    public string ConnectionDuration => Connected && ConnectedSince is { } since ? Ui.Duration(DateTimeOffset.UtcNow - since) : "";
    public string CurrentLatency => Connected && Nodes.FirstOrDefault(n => n.Id == Config.IndexId) is { HasFreshDelay: true } node ? $"{node.Delay:0} ms" : "";
    public string? ChosenCountry => Settings.ManualCountry ?? ManualNode()?.Country;
    public bool AutomaticLocation => string.IsNullOrEmpty(Settings.ManualCountry) && string.IsNullOrEmpty(Settings.ManualNodeKey);
    public bool CanChooseLocation => true;
    private bool _lastTestCancelled;
    public bool IsTesting => _testCts is not null;
    private bool IsActiveSubscription(string id) => !_disabledSubIds.Contains(id);
    public IEnumerable<NodeRow> AvailableLocations => Nodes.Where(IsUsableNode);
    public string? ChosenLocationId => ManualNode()?.Id;
    public string LocationChoice => AutomaticLocation ? Ui.T("Automatic · fastest available", "自动选择 · 最快线路") : ManualNode() is { } node ? LocationTitle(node) : ChosenCountry is { } country ? Ui.Country(country) : Ui.T("Selected location unavailable", "所选线路已失效");
    private CancellationTokenSource? _refreshNodesCts;
    private CancellationTokenSource? _subscriptionCts, _powerCts;
    private bool _automating;
    public bool UpdatingNodes { get; private set; }
    public bool CanCancelNodeUpdate => _refreshNodesCts is not null;
    public bool CanUpdateNodes => CanCancelNodeUpdate || (!Busy && !IsTesting && !UpdatingNodes && _powerCts is null);
    public void CancelNodeUpdate() => _refreshNodesCts?.Cancel();
    private string _measurementStatus = "";
    public string SpeedTestStatus => Busy ? (ConnectionPending ? Ui.T("Finish connecting before testing speed.", "连接完成后再测速。") : Ui.T("Another task is running.", "另一项操作正在进行。")) : UpdatingNodes ? Ui.T("Updating subscriptions…", "正在更新订阅…") : _measurementStatus;
    public string SpeedTestLabel => IsTesting ? Ui.T("Stop test", "停止测速") : Ui.T("Test speed", "测速");
    public bool CanTestSpeed => !Busy && !UpdatingNodes && _powerCts is null;
    public string UpdateNodesLabel => UpdatingNodes ? Ui.T("Cancel update", "取消更新") : _refreshNodesCts is not null ? Ui.T("Stop speed test", "停止测速") : Ui.T("Update nodes", "更新节点");
    public string NodeUpdateStatus { get; private set; } = "";
    public IEnumerable<NodeRow> SpeedRankedLocations => AvailableLocations
        .OrderByDescending(n => n.HasFreshSpeed).ThenByDescending(n => n.HasFreshSpeed ? n.Speed : null)
        .ThenByDescending(n => n.HasFreshDelay).ThenBy(n => n.HasFreshDelay ? n.Delay : null)
        .ThenBy(n => n.Sort).ThenBy(n => n.Id, StringComparer.Ordinal);
    public string LocationTitle(NodeRow node)
    {
        var siblings = AvailableLocations.Where(n => n.Country == node.Country).OrderBy(n => n.Sort).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        return siblings.Count > 1 ? $"{node.CountryLabel} {siblings.FindIndex(n => n.Id == node.Id) + 1}" : node.CountryLabel;
    }
    internal static string MeasurementKey(NodeRow node)
    {
        var profile = JsonUtils.DeepCopy(node.Profile);
        profile.IndexId = ""; // Subscription import can regenerate a local database ID.
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));
    }
    private NodeRow? ManualNode() => AvailableLocations.FirstOrDefault(n => NodeKey(n) == Settings.ManualNodeKey && n.Id == Settings.ManualNodeId)
        ?? AvailableLocations.FirstOrDefault(n => NodeKey(n) == Settings.ManualNodeKey);
    private static string NodeKey(NodeRow node) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { node.Profile.Subid, node.Profile.Address, node.Profile.Port, node.Profile.ConfigType, node.Profile.Remarks }))));
    public bool ConnectionPending => _wantConnected && !Connected;
    public string PowerShortLabel => Connected ? Ui.T("Disconnect", "断开") : _wantConnected ? Ui.T("Cancel", "取消") : Ui.T("Connect", "连接");
    public string PowerLabel => _wantConnected || Connected ? Ui.T("Disconnect", "断开") : Ui.T("Connect", "连接");
    public string UploadRate => TrafficText.Split("    ")[0].Replace("↑", "").Trim();
    public string DownloadRate => TrafficText.Split("    ").Last().Replace("↓", "").Trim();
    public string SmartDescription => Connected ? "线路已就绪，轻点圆环即可断开" : _wantConnected ? "正在寻找可用线路，请稍候" : "轻点连接，自动为你选择线路";
    public string TunStatus => Settings.Tun ? (Connected ? "TUN 已开启" : "TUN 待连接") : "TUN 已关闭";
    private bool _wantConnected;
    private bool _expectsPreCore;
    private bool _choosingLocation;
    private readonly MacAuthorizationPromptGate _macPromptGate = new();
    private bool UseMacBridge => !integrationTest && OperatingSystem.IsMacOS() && Settings.Tun;
    // macOS and Windows keep one TUN bridge across node switches, so recovery never falls back to the
    // physical network. Only an explicit Disconnect or Quit removes it.
    private bool UseTunBridge => !integrationTest && Settings.Tun && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());
    // True once this Connect has carried traffic: later failures are recovery, which must stay inside the tunnel.
    private bool _recovering;
    private bool KeepsTunnel => _recovering && UseTunBridge && CoreManager.Instance.HasRetainedMacTunBridge && !CoreAdminManager.Instance.MacTunSuspended;
    private CancellationTokenSource? _connectCts;
    private readonly HealthPolicy _health = new();
    private bool _offline;
    private DateTimeOffset _lastHealth, _lastTick;
    private DateTimeOffset _nextRetry;
    private DateTimeOffset _lastRecoveryRefresh;
    private int _recoveryAttempts;
    private readonly SystemTrafficMonitor _traffic = new();
    internal bool WantsConnection => _wantConnected;
    private readonly Dictionary<string,DateTimeOffset> _cooldowns = new();
    public string CurrentFlag
    {
        get
        {
            if (!Connected) return "◉";
            var name = Nodes.FirstOrDefault(n => n.Id == Config.IndexId)?.Name ?? "";
            foreach (var pair in new[] { ("美国", "🇺🇸"), ("日本", "🇯🇵"), ("德国", "🇩🇪"), ("荷兰", "🇳🇱"), ("香港", "🇭🇰"), ("台湾", "🇹🇼"), ("新加坡", "🇸🇬"), ("英国", "🇬🇧"), ("加拿大", "🇨🇦") })
                if (name.Contains(pair.Item1)) return pair.Item2;
            return Nodes.FirstOrDefault(n => n.Id == Config.IndexId)?.Flag ?? "◉";
        }
    }
    public string CurrentNode => Connected
        ? Nodes.FirstOrDefault(n => n.Id == Config.IndexId) is { } current ? LocationTitle(current) : Ui.T("Automatic", "自动")
        : Ui.T("Automatic", "自动");
    public string LiveDownloadRate => Connected ? DownloadRate : "—";
    public string LiveUploadRate => Connected ? UploadRate : "—";
    public string TrafficText { get; private set; } = "↑ —    ↓ —";
    public string ModeDescription => Settings.Tun ? "TUN 接管系统流量；连接时由系统申请管理员权限。" : "系统代理接管支持代理设置的应用。";
    /// <summary>Message for the user. Routine states use an empty notice; the status line already shows them.</summary>
    public string Notice { get; set; } = "";
    public string UserNotice => Notice;
    public bool HasNotice => Notice != NodeUpdateStatus && !string.IsNullOrWhiteSpace(Notice);
    public string TestSummary { get; private set; } = "";
    public AvailabilityReport? Availability { get; private set; }
    public bool CheckingAvailability { get; private set; }
    private string? _availabilityNode;
    public SubscriptionUsage? Usage => AppSubscriptionIds.Select(id => Settings.SubscriptionUsage.GetValueOrDefault(id)).FirstOrDefault(u => u is not null);
    public string UsageSummary => Usage?.Summary(DateTimeOffset.UtcNow) ?? "";
    public string? UsageWarning => Usage?.Warning(DateTimeOffset.UtcNow);
    private List<string> AppSubscriptionIds { get; set; } = [];
    public bool HasUsage => Usage is { } usage && (usage.Total > 0 || usage.Expire is not null);
    public double UsagePercent => (Usage?.UsedFraction ?? 0) * 100;
    public bool UsageNeedsAttention => Usage?.NeedsAttention(DateTimeOffset.UtcNow) == true;
    public bool ShowAvailability => Connected && (Availability is not null || CheckingAvailability);
    public IReadOnlyList<ServiceStatus> ServiceChips => CheckingAvailability && Availability is null
        ? ServiceAvailability.Names.Select(n => new ServiceStatus(n, WorldScience.Services.Availability.Checking, Ui.T("Checking…", "检测中…"))).ToList()
        : Availability?.Services ?? [];
    public string AvailabilityTitle => Availability?.Country is { } country
        ? Ui.T($"Exit {ServiceAvailability.Flag(country)} {country}", $"出口 {ServiceAvailability.Flag(country)} {country}") : Strings.ServiceCheck;
    /// <summary>Latency and session time under the location name, e.g. "86 ms · 12:31".</summary>
    public string SessionDetail => string.Join("  ·  ", new[] { CurrentLatency, ConnectionDuration }.Where(t => t.Length > 0));
    private CancellationTokenSource? _testCts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProxySnapshot? _proxySnapshot;
    private string _filter = "";
    private int _sort;
    private long _lastTraffic, _menuUp, _menuDown;
    /// <summary>Upload plus download bytes per second for the menu bar animation; 0 when idle or stale.</summary>
    public long MenuRate => Connected && _lastTraffic != 0 && Stopwatch.GetElapsedTime(_lastTraffic).TotalSeconds <= 3 ? _menuUp + _menuDown : 0;
    public string MenuTrafficText => CompactMenuTraffic.Title(Connected && _lastTraffic != 0 && Stopwatch.GetElapsedTime(_lastTraffic).TotalSeconds <= 3, _menuUp, _menuDown);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StatusChanged;
    public void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        StatusChanged?.Invoke();
    }
    public async Task Initialize()
    {
        if (!integrationTest) { Settings.Tun = true; Settings.Mode = "全局代理"; Settings.Save(); }
        if (Settings.Language != "auto") Ui.Use(Settings.Language);
        if (!integrationTest && OperatingSystem.IsMacOS()) CoreAdminManager.Instance.MacPromptGate = _macPromptGate;
        Config.UiItem.CurrentLanguage = "en";
        Thread.CurrentThread.CurrentUICulture = new("en-US");
        // Never take over the user's network merely by opening the app.
        Config.SystemProxyItem.SysProxyType = ESysProxyType.Unchanged;
        Config.TunModeItem.EnableTun = false;
        Config.GuiItem.EnableStatistics = Config.GuiItem.DisplayRealTimeSpeed = true;
        Config.UiItem.HideColumnIpInfo = true;
        Config.SpeedTestItem.SpeedPingTestUrl = Settings.PingUrl;
        Config.SpeedTestItem.SpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
        await ConfigHandler.InitBuiltinRouting(Config);
        await ConfigHandler.InitBuiltinDNS(Config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(Config);
        await ProfileExManager.Instance.Init();
        await Task.Run(() => CoreManager.Instance.Init(Config, Log));
        await CertPemManager.Instance.Init(Config);
        // Keep upstream accounting intact; presentation uses byte-accurate local telemetry.
        await StatisticsManager.Instance.Init(Config, _ => Task.CompletedTask);
        _traffic.Start((up, down) =>
        {
            var captured = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                if (Stopwatch.GetElapsedTime(captured).TotalSeconds > 2) return;
                _menuUp = up; _menuDown = down;
                TrafficText = $"↑ {Rate(up)}    ↓ {Rate(down)}";
                _lastTraffic = captured; Notify();
            });
        }, () => Dispatcher.UIThread.Post(() => { TrafficText = "↑ —    ↓ —"; Notify(); }));
        await LinkSubscriptions.CleanPending(Config);
        await Refresh();
        await ConfigHandler.SaveConfig(Config);
        Notice = "";
        Notify();
    }
    public void ExpireTraffic()
    {
        if (_lastTraffic == 0 || Stopwatch.GetElapsedTime(_lastTraffic).TotalSeconds > 3)
        {
            TrafficText = "↑ —    ↓ —";
            Notify();
        }
    }
    public static string Rate(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.00} MB/s" : bytes >= 1000 ? $"{bytes / 1000d:0.0} KB/s" : $"{bytes} B/s";
    private Task Log(bool notify, string message)
    {
        // Raw engine diagnostics stay in the private local log, not published artifacts.
        Logging.SaveLog("Simple.Core: " + message);
        return Task.CompletedTask;
    }
    public async Task Refresh()
    {
        var profiles = await AppManager.Instance.ProfileItems("") ?? [];
        var extras = await ProfileExManager.Instance.GetProfileExs();
        var subscriptions = await AppManager.Instance.SubItems() ?? [];
        _groupNames = subscriptions.ToDictionary(s => s.Id, s => s.Remarks);
        _disabledSubIds = subscriptions.Where(s => !s.Enabled).Select(s => s.Id).ToHashSet();
        AppSubscriptionIds = subscriptions.Where(s => s.Enabled).Select(s => s.Id).ToList();
        var previous = Nodes.ToDictionary(n => n.Id);
        Nodes.Clear();
        foreach (var p in profiles)
        {
            var extra = extras.FirstOrDefault(e => e.IndexId == p.IndexId);
            var row = new NodeRow(p) { Current = p.IndexId == Config.IndexId, Sort = extra?.Sort ?? Nodes.Count };
            // Legacy persisted values have no timestamp or measurement method. Do not present them as current.
            if (previous.TryGetValue(row.Id, out var old) && JsonSerializer.Serialize(old.Profile) == JsonSerializer.Serialize(p))
            { row.Delay = old.Delay; row.Speed = old.Speed; row.LastTestAt = old.LastTestAt; row.Failure = old.Failure; row.DelayMethod = old.DelayMethod; }
            if (previous.TryGetValue(row.Id, out var kept)) row.LastDelayAt = kept.LastDelayAt;
            if (row.LastTestAt is null && row.LastDelayAt is null && Settings.NodeMeasurements.TryGetValue(MeasurementKey(row), out var measured))
            {
                row.Speed = measured.Speed > 0 ? measured.Speed : null; row.Delay = measured.Delay;
                row.LastTestAt = measured.Speed > 0 ? measured.At : null; row.LastDelayAt = measured.Delay > 0 ? measured.At : null;
            }
            Nodes.Add(row);
        }
        ApplyFilter(_filter, _sort);
        SelectedNode = AvailableLocations.FirstOrDefault(n => n.Current) ?? AvailableLocations.FirstOrDefault();
        Notify();
    }
    public void ApplyFilter(string filter, int sort)
    {
        _filter = filter; _sort = sort;
        IEnumerable<NodeRow> rows = AvailableLocations.Where(n => n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        rows = sort switch
        {
            1 => rows.OrderBy(n => n.Delay > 0 ? n.Delay : double.MaxValue),
            2 => rows.OrderByDescending(n => n.Speed ?? -1),
            _ => rows.OrderBy(n => n.Sort)
        };
        var keep = SelectedNode;
        VisibleNodes.Clear();
        foreach (var row in rows) VisibleNodes.Add(row);
        SelectedNode = VisibleNodes.Contains(keep!) ? keep : VisibleNodes.FirstOrDefault();
        RebuildGroups();
        Notify();
    }
    private void RebuildGroups()
    {
        var expanded = Groups.ToDictionary(g => g.Id, g => g.Expanded);
        Groups.Clear();
        foreach (var group in VisibleNodes.GroupBy(n => n.Profile.Subid ?? ""))
            Groups.Add(new NodeGroup(group.Key, _groupNames.GetValueOrDefault(group.Key, Ui.T("Local", "本地配置")), group.ToList(), expanded.GetValueOrDefault(group.Key, true)));
    }
    // Legacy/test selection changes the preferred automatic candidate only.
    public Task Select(NodeRow row) => SetLocation(row, manual: false);
    public async Task ChooseLocation(NodeRow? row)
    {
        if (_choosingLocation) return;
        _choosingLocation = true;
        _refreshNodesCts?.Cancel();
        try
        {
            if (IsTesting) { CancelTest(); while (IsTesting) await Task.Delay(50); }
            if (_connectCts is not null)
            {
                _connectCts.Cancel(); CoreAdminManager.Instance.CancelMacAuthorization();
            }
            // Complete the previous operation before changing the profile or starting another core.
            await _gate.WaitAsync(); _gate.Release();
            await SetLocation(row, manual: row is not null);
        }
        finally { _choosingLocation = false; }
    }
    private Task SetLocation(NodeRow? row, bool manual) => RunExclusive(async () =>
    {
        if (row is not null && !AvailableLocations.Any(n => n.Id == row.Id))
            throw new InvalidOperationException(Ui.T("This location is no longer available. Choose another location.", "这条线路已失效，请换一条。"));
        FallbackActive = false;
        Settings.ManualCountry = null; // New choices pin an endpoint; legacy country preferences remain readable until the next choice.
        Settings.ManualNodeId = manual ? row!.Id : null;
        Settings.ManualNodeKey = manual ? NodeKey(row!) : null;
        Settings.Save();
        // Selecting Automatic keeps a healthy connection; it enables failover from now on.
        if (row is null) { _nextRetry = DateTimeOffset.MinValue; return; }
        SelectedNode = row;
        if (!manual) _lastHealthyNode = NodeKey(row);
        var reconnect = _wantConnected;
        if (Connected && row.Id == Config.IndexId) return;
        if (reconnect) await DisconnectInternal(keepMacBridge: UseTunBridge);
        await ConfigHandler.SetDefaultServerIndex(Config, row.Id);
        foreach (var n in Nodes) { n.Current = n.Id == row.Id; n.Notify(); }
        _cooldowns.Remove(row.Id);
        await ConfigHandler.SaveConfig(Config);
        if (!reconnect) return;
        _connectCts = new();
        _macPromptGate.BeginUserConnect();
        try { await ConnectSmart(_connectCts.Token); }
        finally
        {
            _macPromptGate.EndUserConnect(); _connectCts.Dispose(); _connectCts = null;
            if (!Connected && _wantConnected) ScheduleRetry();
        }
    });
    public async Task StopForSignOut()
    {
        _powerCts?.Cancel(); _subscriptionCts?.Cancel(); _refreshNodesCts?.Cancel();
        _wantConnected = false; _connectCts?.Cancel(); CancelTest(); CoreAdminManager.Instance.CancelMacAuthorization();
        await _gate.WaitAsync();
        try
        {
            await DisconnectInternal(suspendMacBridge: UseMacBridge); ConnectedSince = null;
            Settings.ManualCountry = Settings.ManualNodeId = Settings.ManualNodeKey = null; Settings.Save();
        }
        finally { _gate.Release(); Notify(); }
    }
    public async Task Toggle()
    {
        // One owner completes the transition; another click only cancels it.
        if (_powerCts is not null)
        {
            _wantConnected = false; _powerCts.Cancel(); _connectCts?.Cancel();
            CoreAdminManager.Instance.CancelMacAuthorization(); Notify(); return;
        }
        // A first Wintun installation plus a slow first node can exceed one minute on Windows.
        using var transition = new CancellationTokenSource(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 120 : 60));
        _powerCts = transition;
        var disconnect = Connected || _wantConnected;
        _wantConnected = !disconnect;
        _recovering = false;
        if (!disconnect) { _offline = false; _health.Reset(); ConnectedSince = null; }
        Notice = "";
        NodeUpdateStatus = ""; Notify();
        try
        {
            // Publish the click before cancelling background network work.
            await Task.Yield();
            _refreshNodesCts?.Cancel(); _subscriptionCts?.Cancel(); CancelTest(); _connectCts?.Cancel();
            var wait = Stopwatch.StartNew();
            while (Busy || UpdatingNodes || IsTesting)
            {
                transition.Token.ThrowIfCancellationRequested();
                if (wait.Elapsed > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException(Ui.T("The previous task is still stopping. Please try again shortly.", "上一项操作还在收尾，请稍后再点连接。"));
                await Task.Delay(50, transition.Token);
            }
            transition.Token.ThrowIfCancellationRequested();
            await RunExclusive(async () =>
            {
                _connectCts = transition;
                try
                {
                    if (disconnect) { await DisconnectInternal(suspendMacBridge: UseMacBridge); ConnectedSince = null; FallbackActive = false; return; }
                    if (!integrationTest && await FindConflict(transition.Token) is { } conflict)
                        throw new InvalidOperationException(conflict);
                    transition.Token.ThrowIfCancellationRequested();
                    _macPromptGate.BeginUserConnect();
                    await ConnectSmart(transition.Token);
                }
                catch (Exception ex)
                {
                    var timedOut = ex is OperationCanceledException && _wantConnected;
                    _wantConnected = false;
                    await DisconnectInternal(suspendMacBridge: UseMacBridge);
                    if (timedOut) throw new InvalidOperationException(Ui.T("Connection timed out. Check your network or choose another location.", "连接超时。请检查网络，或换一条线路再试。"));
                    throw;
                }
                finally { _macPromptGate.EndUserConnect(); _connectCts = null; }
            }, connectionOperation: true);
        }
        catch (OperationCanceledException)
        { Notice = transition.IsCancellationRequested && _wantConnected ? Ui.T("Connection timed out. Please try again.", "连接超时，请重试。") : ""; }
        catch (Exception ex)
        { Logging.SaveLog("Simple.Connect", ex); Notice = ex is InvalidOperationException ? ex.Message : Ui.T("Could not connect. Please try again.", "连接失败，请重试。"); }
        finally
        {
            if (!Connected) { _wantConnected = false; ConnectedSince = null; }
            _powerCts = null; Notify();
        }
    }
    /// <summary>A user-facing reason Connect cannot work right now, or null.</summary>
    private async Task<string?> FindConflict(CancellationToken token)
    {
        if (Settings.Tun && await MacVpnConflict.Find() is { } systemVpn) return OtherVpnClients.Message(systemVpn);
        return await OtherVpnClients.Find(Utils.StartupPath(), token) is { } app ? OtherVpnClients.Message(app) : null;
    }
    /// <summary>Checks ChatGPT, Claude and streaming access through the running proxy, once per node.</summary>
    public async Task RefreshAvailability(bool force = false)
    {
        if (integrationTest || !Connected || CheckingAvailability) return;
        var node = Config.IndexId;
        if (!force && node == _availabilityNode && Availability is { } last && DateTimeOffset.UtcNow - last.At < TimeSpan.FromMinutes(30)) return;
        CheckingAvailability = true; Notify();
        try
        {
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}"), UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(6) };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await ServiceAvailability.Check(http, timeout.Token);
            if (Connected && Config.IndexId == node) { Availability = report; _availabilityNode = node; }
        }
        catch (Exception ex) { Logging.SaveLog("Simple.Availability", ex); }
        finally
        {
            CheckingAvailability = false; Notify();
            // The location changed while this check ran; check the new one instead of leaving the panel empty.
            if (Connected && Config.IndexId != node) _ = RefreshAvailability();
        }
    }
    private bool IsUsableNode(NodeRow node) => IsActiveSubscription(node.Profile.Subid) && !_disabledSubIds.Contains(node.Profile.Subid) && node.Profile.IsValid() &&
        (!IPAddress.TryParse(node.Profile.Address, out var literal) || literal.AddressFamily == AddressFamily.InterNetwork) &&
        !SubscriptionLocations.IsNotice(node.Name);
    private async Task ConnectSmart(CancellationToken token)
    {
        var candidates = AvailableLocations.Where(n => AutomaticLocation ||
            (Settings.ManualCountry is not null ? n.Country == ChosenCountry : n.Id == ChosenLocationId)).ToList();
        var fallback = !AutomaticLocation && Settings.Fallback;
        var missing = !AvailableLocations.Any() ? Ui.T("Add a working subscription first.", "请先添加有效的订阅链接。")
            : !AutomaticLocation && candidates.Count == 0 && !fallback ? Ui.T("Selected location is no longer available. Choose another location or Automatic.", "所选线路已失效，请换一条线路或选择「自动」。") : null;
        if (missing is not null)
        {
            // A subscription refresh can remove the endpoint while the tunnel is up. Never drop that tunnel
            // automatically: traffic stays blocked and recovery keeps retrying until the user acts.
            if (KeepsTunnel) { await DisconnectInternal(keepMacBridge: true); throw new InvalidOperationException(missing); }
            _wantConnected = false; await DisconnectInternal();
            throw new InvalidOperationException(missing);
        }
        bool Cooling(NodeRow n) => _cooldowns.TryGetValue(n.Id, out var until) && until > DateTimeOffset.UtcNow;
        var available = await RankCandidates(candidates.Where(n => !Cooling(n)).ToList(), token);
        // Like a fallback group: the chosen location first, then the fastest others, same country first.
        var others = fallback ? AvailableLocations.Except(candidates).ToList() : [];
        if (fallback) available.AddRange((await RankCandidates(others.Where(n => !Cooling(n)).ToList(), token)).OrderBy(n => n.Country == ChosenCountry ? 0 : 1));
        // Every candidate failed recently: try them all again rather than give up.
        if (available.Count == 0) available = await RankCandidates([.. candidates, .. others], token);
        Exception? lastFailure = null;
        foreach (var row in available)
        {
            token.ThrowIfCancellationRequested();
            Notice = ""; Notify();
            await ConfigHandler.SetDefaultServerIndex(Config, row.Id);
            try
            {
                // No system-proxy fallback: apps that ignore the proxy setting would use the physical network.
                await ConnectInternal(token);
                _recovering = true;
                _health.Reset(); _offline = false; _recoveryAttempts = 0; _lastHealth = DateTimeOffset.UtcNow;
                ConnectedSince ??= DateTimeOffset.UtcNow;
                FallbackActive = fallback && !candidates.Contains(row);
                if (FallbackActive)
                {
                    _nextReturnCheck = DateTimeOffset.UtcNow + ReturnCheckInterval;
                    Logging.SaveLog($"Simple.Fallback: chosen location unavailable; using {row.Name}");
                    Notice = Ui.T($"Your chosen location is unavailable, so {LocationTitle(row)} is used for now. It switches back when it recovers.", $"所选线路暂不可用，已临时切换到 {LocationTitle(row)}，恢复后会自动切回。");
                }
                foreach (var node in Nodes) { node.Current = node.Id == row.Id; node.Notify(); }
                SelectedNode = row; _lastHealthyNode = NodeKey(row); await ConfigHandler.SaveConfig(Config);
                _ = RefreshAvailability();
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (MacNetworkAuthorizationException)
            {
                _wantConnected = false; _macPromptGate.EndUserConnect();
                await DisconnectInternal();
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                Logging.SaveLog("Simple.SmartConnect", ex); _cooldowns[row.Id] = DateTimeOffset.UtcNow.AddMinutes(5);
                await DisconnectInternal(keepMacBridge: UseTunBridge);
                if (!_wantConnected) throw;
                // These failures are not caused by the node, so trying every other node only wastes time.
                if (ex is TunStartException)
                {
                    if (_recovering)
                    {
                        // Recovery keeps retrying with the retained Mac bridge; a Windows adapter that failed
                        // is released so the next attempt builds a new one.
                        if (!OperatingSystem.IsMacOS()) await DisconnectInternal();
                        throw;
                    }
                    _wantConnected = false;
                    if (!integrationTest && await FindConflict(token) is { } conflict) throw new InvalidOperationException(conflict, ex);
                    throw;
                }
                if (Settings.Tun && (ex.Message.Contains("授权") || ex.Message.Contains("管理员权限")))
                {
                    // The adapter itself failed; do not keep a broken bridge behind a "Not connected" state.
                    _wantConnected = false; await DisconnectInternal(suspendMacBridge: UseMacBridge); throw;
                }
            }
        }
        throw new InvalidOperationException(ConnectionFailure.Message(lastFailure, AutomaticLocation), lastFailure);
    }
    /// <summary>Connected to another location because the chosen one failed. The chosen one is retried in the background.</summary>
    public bool FallbackActive { get; private set; }
    private static readonly TimeSpan ReturnCheckInterval = TimeSpan.FromMinutes(3);
    private DateTimeOffset _nextReturnCheck;
    // The chosen location is tested through its own core, so the fallback connection keeps carrying traffic meanwhile.
    private Task ReturnToChosen(NodeRow chosen, DateTimeOffset now) => RunExclusive(async () =>
    {
        _nextReturnCheck = now + ReturnCheckInterval;
        _connectCts = new();
        try
        {
            if (!await NodeWorks(chosen, _connectCts.Token)) return;
            Logging.SaveLog("Simple.Fallback: chosen location works again; switching back");
            _cooldowns.Remove(chosen.Id);
            await DisconnectInternal(keepMacBridge: UseTunBridge);
            if (_wantConnected) await ConnectSmart(_connectCts.Token);
            if (Connected && !FallbackActive) Notice = "";
        }
        catch (OperationCanceledException) when (_connectCts.IsCancellationRequested) { }
        finally
        {
            _connectCts.Dispose(); _connectCts = null;
            if (!Connected && _wantConnected) ScheduleRetry();
        }
    }, allowMeasurements: true);
    private async Task<bool> NodeWorks(NodeRow row, CancellationToken token)
    {
        var physicalInterface = integrationTest ? null : await MacVpnConflict.FindPhysicalInterface();
        // Unbound, a Mac test would run through the current tunnel and pass because of the fallback location.
        if (!integrationTest && OperatingSystem.IsMacOS() && physicalInterface is null) return false;
        ProcessService? process = null;
        try
        {
            int port;
            (process, port) = await StartTestCore(row, physicalInterface, token);
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            await LatencyBenchmark.Measure(http, Config.SpeedTestItem.SpeedPingTestUrl, TimeSpan.FromSeconds(Settings.PingTimeoutSeconds), token);
            return true;
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { Logging.SaveLog("Simple.Fallback: chosen location still unavailable", ex); return false; }
        finally { if (process is not null) { await process.StopAsync(); process.Dispose(); } }
    }
    /// <summary>Starts a separate core for one location and waits until its local SOCKS port accepts connections.</summary>
    private async Task<(ProcessService Process, int Port)> StartTestCore(NodeRow row, string? physicalInterface, CancellationToken token)
    {
        var item = new ServerTestItem { IndexId = row.Id, Profile = row.Profile, ConfigType = row.Profile.ConfigType,
            CoreType = AppManager.Instance.GetCoreType(row.Profile, row.Profile.ConfigType), Port = row.Profile.Port };
        var process = await CoreManager.Instance.LoadCoreConfigSpeedtest(item, includeSubChain: false, bindInterface: physicalInterface)
            ?? throw new InvalidOperationException("speed-test core did not start");
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("speed-test core exited");
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, item.Port, startup.Token); break; }
                catch (SocketException) { await Task.Delay(100, startup.Token); }
            }
            return (process, item.Port);
        }
        catch { await process.StopAsync(); process.Dispose(); throw; }
    }
    private readonly Dictionary<string, (double Delay, DateTimeOffset At)> _reachability = new();
    private string? _lastHealthyNode;
    // Bounded, silent TCP reachability ranks candidates; actual tunnel traffic is still
    // verified by ConnectInternal before reporting Connected. No download benchmarks.
    private async Task<List<NodeRow>> RankCandidates(List<NodeRow> rows, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(900));
        using var limit = new SemaphoreSlim(8);
        var now = DateTimeOffset.UtcNow;
        var checks = rows.Where(n => !_reachability.TryGetValue(NodeKey(n), out var v) || now - v.At > TimeSpan.FromMinutes(5)).Take(32).Select(async node =>
        {
            var entered = false;
            try
            {
                await limit.WaitAsync(budget.Token); entered = true;
                using var socket = new TcpClient(); var started = Stopwatch.GetTimestamp();
                await socket.ConnectAsync(node.Profile.Address, node.Profile.Port, budget.Token);
                return (Key: NodeKey(node), Delay: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception) when (!token.IsCancellationRequested) { return (Key: NodeKey(node), Delay: double.MaxValue); }
            finally { if (entered) limit.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(checks);
        token.ThrowIfCancellationRequested();
        foreach (var result in results) _reachability[result.Key] = (result.Delay, now);
        return rows.OrderByDescending(n => n.HasFreshSpeed)
            .ThenByDescending(n => n.HasFreshSpeed ? n.Speed : null)
            .ThenBy(n => NodeKey(n) == _lastHealthyNode ? 0 : 1)
            .ThenBy(n => _reachability.GetValueOrDefault(NodeKey(n), (double.MaxValue, now)).Item1)
            .ThenBy(n => n.Sort).ToList();
    }
    private void ScheduleRetry()
    {
        _nextRetry = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(_recoveryAttempts++, 4))));
        Notice = _offline ? "" : AutomaticLocation ? Ui.T("Network unavailable. Recovering automatically…", "网络暂不可用，正在自动恢复…") : Ui.T("Selected location unavailable. Retrying automatically.", "所选线路暂不可用，正在自动重试。");
    }
    public async Task Automate()
    {
        if (integrationTest || _automating || _powerCts is not null) return;
        _automating = true;
        try { await AutomateCore(DateTimeOffset.UtcNow); }
        finally { _automating = false; }
        // A full latency sweep can take minutes; run it beside the health checks instead of blocking them.
        if (!_measuring) { _measuring = true; try { await CheckScheduledMeasurements(DateTimeOffset.UtcNow); } finally { _measuring = false; } }
    }
    private bool _measuring;
    internal Task VerifyRecoveryTick(DateTimeOffset now) => integrationTest
        ? AutomateCore(now) : throw new InvalidOperationException();
    private async Task AutomateCore(DateTimeOffset now)
    {
        if (_powerCts is not null || Busy || (IsTesting && !Connected)) return;
        // A healthy tunnel is retained while subscriptions are refreshed.
        if (!IsTesting && (Settings.LastSubscriptionRefresh is null || now - Settings.LastSubscriptionRefresh > TimeSpan.FromHours(6)))
        {
            // Rate-limit failed subscription fetches as well as successful ones.
            if (now - _lastRecoveryRefresh > TimeSpan.FromMinutes(10))
            {
                _lastRecoveryRefresh = now;
                var subs = await AppManager.Instance.SubItems();
                if (subs?.Any(s => s.Enabled && IsActiveSubscription(s.Id) && !string.IsNullOrWhiteSpace(s.Url)) == true)
                    await UpdateSubscriptions(true);
            }
        }
        if (_powerCts is not null || !_wantConnected) return;
        // A long timer gap means the computer slept. Check at once instead of waiting out the interval.
        var woke = _lastTick != default && now - _lastTick > HealthPolicy.WakeGap;
        _lastTick = now;
        if (woke) { Logging.SaveLog("Simple.Health: resumed after sleep"); _lastHealth = DateTimeOffset.MinValue; _nextRetry = DateTimeOffset.MinValue; }
        if (!Connected)
        {
            // Offline: wait for Wi-Fi/Ethernet to return instead of burning through every node.
            var online = integrationTest || NetworkState.PhysicalNetworkUp();
            if (_offline != !online) { _offline = !online; Notify(); }
            if (!online) return;
            if (now < _nextRetry) return;
            await RunExclusive(async () =>
            {
                _connectCts = new();
                try { await ConnectSmart(_connectCts.Token); }
                finally
                {
                    _connectCts.Dispose(); _connectCts = null;
                    if (!Connected && _wantConnected) ScheduleRetry();
                }
            });
            // Exhausted stale nodes: try refreshing once per ten minutes, never per node.
            if (!Connected && _wantConnected && now - _lastRecoveryRefresh > TimeSpan.FromMinutes(10))
            { _lastRecoveryRefresh = now; await UpdateSubscriptions(true); }
            return;
        }
        if (FallbackActive && now >= _nextReturnCheck && !IsTesting && ManualNode() is { } chosen) { await ReturnToChosen(chosen, now); return; }
        if (now - _lastHealth < _health.Interval) return;
        _lastHealth = now;
        await RunExclusive(async () =>
        {
            _connectCts = new();
            try
            {
                var (proxyOk, systemOk) = await ProbeHealth(_connectCts.Token);
                var networkUp = proxyOk && systemOk || integrationTest || NetworkState.PhysicalNetworkUp();
                var wasOffline = _offline;
                var action = _health.Record(proxyOk, systemOk, networkUp);
                _offline = action == HealthAction.Offline;
                if (action != HealthAction.Healthy)
                    Logging.SaveLog($"Simple.Health: action={action}; proxy={proxyOk}; system={systemOk}; network={networkUp}; tunnel={_health.TunnelFailures}; dns={_health.DnsFailures}; bridge={CoreManager.Instance.HasRetainedMacTunBridge}");
                if (action == HealthAction.Healthy && wasOffline) Notice = "";
                if (action != HealthAction.Reconnect) { Notify(); return; }
                CancelTest();
                while (IsTesting) await Task.Delay(50);
                _cooldowns[Config.IndexId] = now.AddMinutes(5);
                await DisconnectInternal(keepMacBridge: UseTunBridge);
                if (_wantConnected) await ConnectSmart(_connectCts.Token);
            }
            catch (OperationCanceledException) when (_connectCts.IsCancellationRequested) { }
            finally
            {
                _connectCts.Dispose(); _connectCts = null;
                if (!Connected && _wantConnected) ScheduleRetry();
            }
        }, allowMeasurements: true);
    }
    private async Task<(bool ProxyOk, bool SystemOk)> ProbeHealth(CancellationToken token)
    {
        var urls = integrationTest ? new[] { Config.SpeedTestItem.SpeedPingTestUrl } :
            new[] { "https://cp.cloudflare.com/generate_204", "https://www.google.com/generate_204" };
        async Task<bool> Ok(Task probe)
        {
            try { await probe; return true; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
        }
        var tunAlive = !Settings.Tun || await Ok(VerifyTunCore(token));
        var proxy = Ok(ProbePath(urls, true, token));
        var system = Settings.Tun && !integrationTest ? Ok(ProbePath(urls, false, token)) : Task.FromResult(true);
        return (tunAlive && await proxy, tunAlive && await system);
    }
    private async Task ProbeLive(CancellationToken token)
    {
        // Probe the tunnel, not arbitrary websites visited by the user. Confirm with
        // an independent endpoint so a single probe site's outage cannot churn nodes.
        if (Settings.Tun) await VerifyTunCore(token);
        var urls = integrationTest ? new[] { Config.SpeedTestItem.SpeedPingTestUrl } :
            new[] { "https://cp.cloudflare.com/generate_204", "https://www.google.com/generate_204" };
        // Both paths are required, but neither must wait for the other to start.
        await Task.WhenAll(ProbePath(urls, true, token),
            Settings.Tun && !integrationTest ? ProbePath(urls, false, token) : Task.CompletedTask);
    }
    private async Task ProbePath(IEnumerable<string> urls, bool throughSocks, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = throughSocks ? new WebProxy($"socks5://127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}") : null,
            UseProxy = throughSocks, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Connected ? 6 : 12)
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try { await ConnectivityProbe.Check(http, urls, TimeSpan.FromSeconds(Connected ? 8 : 12), token); }
        catch (HttpRequestException ex)
        { throw new HttpRequestException(throughSocks ? "代理出口检查未通过" : "系统网络或 DNS 检查未通过", ex); }
    }
    internal async Task VerifyTunCore(CancellationToken token)
    {
        // Upstream can leave the main SOCKS core running when privileged pre-core
        // startup fails. A reachable SOCKS port alone is not proof of working TUN.
        CoreManager.Instance.VerifyRunningProcesses(_expectsPreCore);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await http.GetAsync($"http://127.0.0.1:{AppManager.Instance.StatePort2}/version", token);
        response.EnsureSuccessStatusCode();
    }
    public Task ChangeMode(string mode, bool tun) => RunExclusive(async () =>
    {
        Settings.Mode = mode; Settings.Tun = tun; Settings.Save();
        if (Connected) { await DisconnectInternal(); await ConnectInternal(); }
    });
    private async Task ConnectInternal(CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(Config.IndexId) && SelectedNode is not null)
            await ConfigHandler.SetDefaultServerIndex(Config, SelectedNode.Id);
        var profile = await ConfigHandler.GetDefaultServer(Config) ?? throw new InvalidOperationException(Ui.T("Add a subscription and choose a location first.", "请先添加订阅并选择线路。"));
        if (Settings.Tun && OperatingSystem.IsWindows() && !ServiceLib.Common.Utils.IsAdministrator())
        {
            _wantConnected = false;
            throw new InvalidOperationException(Ui.T("Quit World Science, then right-click it and choose Run as administrator.", "请退出 World Science，再右键点击它，选择「以管理员身份运行」。"));
        }
        if (!integrationTest)
        {
            Settings.Tun = true; Settings.Mode = "全局代理";
            Config.TunModeItem.EnableIPv6Address = true;
            Config.TunModeItem.StrictRoute = true;
            Config.TunModeItem.AutoRoute = true;
            Config.TunModeItem.RouteExcludeAddress = [];
            // Simple validates the sing-box TUN API. Old settings must not switch
            // Windows to native Xray TUN, which has no such API.
            if (OperatingSystem.IsWindows()) Config.TunModeItem.EnableLegacyProtect = true;
        }
        Config.TunModeItem.EnableTun = Settings.Tun;
        var routings = await AppManager.Instance.RoutingItems();
        var routing = routings?.FirstOrDefault(r => r.Remarks.Contains(Settings.Mode == "规则分流" ? "Whitelist" : "Global"));
        if (routing is not null) await ConfigHandler.SetDefaultRouting(Config, routing);
        if (Settings.Mode == "直连")
        {
            // Use an explicit direct final rule, including in TUN mode.
            var direct = routings?.FirstOrDefault(r => r.Remarks == "Simple Direct");
            if (direct is null)
            {
                direct = new RoutingItem { Remarks = "Simple Direct" };
                await ConfigHandler.AddBatchRoutingRules(direct, "[{\"type\":\"field\",\"outboundTag\":\"direct\",\"port\":\"0-65535\"}]");
            }
            await ConfigHandler.SetDefaultRouting(Config, direct);
        }
        using (var check = new TcpClient())
        {
            try { await check.ConnectAsync(IPAddress.Loopback, AppManager.Instance.GetLocalPort(EInboundProtocol.socks)); throw new InvalidOperationException(Ui.T($"Local port {AppManager.Instance.GetLocalPort(EInboundProtocol.socks)} is in use. Quit other proxy apps and try again.", $"本地端口 {AppManager.Instance.GetLocalPort(EInboundProtocol.socks)} 被占用。请退出其他代理软件后重试。")); }
            catch (SocketException) { }
        }
        foreach (var inbound in Config.Inbound)
        {
            inbound.SniffingEnabled = true;
            inbound.DestOverride = ["http", "tls", "quic"];
            inbound.RouteOnly = false;
        }
        var built = UseTunBridge ? await MacTunBridge.Build(Config, profile) : await CoreConfigContextBuilder.BuildAll(Config, profile, includeSubChain: false);
        if (!integrationTest)
            built = built with
            {
                MainResult = built.MainResult with { Context = GlobalTrafficPolicy.Apply(built.MainResult.Context) },
                PreSocksResult = built.PreSocksResult is { } globalPre ? globalPre with { Context = GlobalTrafficPolicy.Apply(globalPre.Context) } : null
            };
        if (!built.Success) throw new InvalidOperationException(Ui.T("This location's settings are not supported. Choose another location.", "这条线路的配置暂不支持，请换一条线路。"));
        try
        {
            _expectsPreCore = built.PreSocksResult is not null;
            await CoreManager.Instance.LoadCore(built.MainResult.Context, built.PreSocksResult?.Context, retainMacTunBridge: UseTunBridge);
            CoreManager.Instance.VerifyRunningProcesses(_expectsPreCore);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                CoreManager.Instance.VerifyRunningProcesses(_expectsPreCore);
                using var portCheck = new TcpClient();
                try { await portCheck.ConnectAsync(IPAddress.Loopback, AppManager.Instance.GetLocalPort(EInboundProtocol.socks), timeout.Token); break; }
                catch (SocketException) { await Task.Delay(150, timeout.Token); }
            }
            if (Settings.Tun)
            {
                using var tunStartup = CancellationTokenSource.CreateLinkedTokenSource(token);
                // A clean Windows installation may still be registering Wintun.
                // Poll immediately, but do not tear down a healthy process after 8s.
                tunStartup.CancelAfter(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 30 : 8));
                try
                {
                    while (true)
                    {
                        try { await VerifyTunCore(tunStartup.Token); break; }
                        catch (HttpRequestException) { await Task.Delay(250, tunStartup.Token); }
                        catch (OperationCanceledException) when (!tunStartup.IsCancellationRequested)
                        { await Task.Delay(250, tunStartup.Token); }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                { throw new TunStartException(ex); }
            }
            // Verify actual proxy egress before changing the system proxy or claiming connected.
            // Windows can expose the control API before its new adapter's DNS and
            // routes converge. Keep the healthy core alive during this bounded warm-up.
            if (OperatingSystem.IsWindows() && Settings.Tun && !integrationTest)
                await ConnectivityProbe.WaitUntilReady(ProbeLive, TimeSpan.FromSeconds(30), token);
            else
                await ProbeLive(token);
            if (!Settings.Tun && !integrationTest)
            {
                _proxySnapshot = await ProxySnapshot.Capture();
                Config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
                try { if (OperatingSystem.IsMacOS()) await _proxySnapshot.Apply(AppManager.Instance.GetLocalPort(EInboundProtocol.socks)); else await SysProxyHandler.UpdateSysProxy(Config, false); }
                finally { Config.SystemProxyItem.SysProxyType = ESysProxyType.Unchanged; }
            }
            _health.Reset(); Connected = true;
            Notice = "";
        }
        catch
        {
            // Cancelling an in-progress connection must not discard an already authorized
            // fixed session. Toggle will suspend it after this operation releases the gate;
            // Quit still performs a full stop. Discarding it here caused the next Connect to prompt again.
            await CoreManager.Instance.CoreStop(keepMacTunBridge: UseTunBridge);
            Config.TunModeItem.EnableTun = false;
            if (_proxySnapshot is not null) { await _proxySnapshot.Restore(); _proxySnapshot = null; }
            throw;
        }
    }
    private async Task DisconnectInternal(bool keepMacBridge = false, bool suspendMacBridge = false)
    {
        try
        {
            if (_proxySnapshot is not null) { await _proxySnapshot.Restore(); _proxySnapshot = null; }
        }
        finally
        {
            // Failure to restore OS proxy settings must never leave the VPN core running.
            try
            {
                if (suspendMacBridge && CoreManager.Instance.HasRetainedMacTunBridge)
                    await CoreManager.Instance.SuspendMacTunBridge();
                else await CoreManager.Instance.CoreStop(keepMacTunBridge: keepMacBridge);
            }
            finally
            {
                Config.TunModeItem.EnableTun = false;
                Availability = null; _availabilityNode = null;
                Connected = false; Notice = "";
            }
        }
    }
    public async Task Import(string text)
    {
        await RunExclusive(async () =>
        {
            if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var sub = new SubItem { Remarks = Ui.T("Subscription ", "订阅 ") + DateTime.Now.ToString("MM-dd HH:mm"), Url = uri.AbsoluteUri };
                await ConfigHandler.AddSubItem(Config, sub);
                await SubscriptionHandler.UpdateProcess(Config, sub.Id, false, Log);
            }
            else
            {
                var count = await ConfigHandler.AddBatchServers(Config, text.Trim(), "", false);
                if (count <= 0) throw new InvalidOperationException(Ui.T("No locations found. Check the link.", "没有识别到可导入的节点，请检查链接格式。"));
            }
            await Refresh();
            Notice = Ui.T($"Imported · {Nodes.Count} locations", $"导入完成 · 共 {Nodes.Count} 个节点");
        });
    }
    private static string UpdateFailedNotice => Ui.T("Unable to update nodes. Your saved nodes are still available.", "节点更新失败，原有节点仍可使用。");
    private static string PartialUpdateNotice => Ui.T("Some subscriptions could not be updated.", "部分订阅更新失败。");
    public async Task<bool> UpdateSubscriptions(bool silent = false, CancellationToken cancellationToken = default)
    {
        if (_powerCts is not null) return false;
        var updated = false;
        await RunExclusive(async () =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(45));
            _subscriptionCts = budget;
            try
            {
            var saved = (await AppManager.Instance.SubItems() ?? []).Where(s => s.Enabled && IsActiveSubscription(s.Id)).ToList();
            // Pasted nodes have no address to download; they stay as saved and never count as a failed update.
            var subs = saved.Where(s => !LinkSubscriptions.IsManual(s)).ToList();
            if (subs.Count == 0 && saved.Count > 0)
            {
                updated = true; Settings.LastSubscriptionRefresh = DateTimeOffset.UtcNow; Settings.Save();
                if (!silent) Notice = Ui.T("Nodes updated.", "节点已更新。");
                return;
            }
            if (subs.Count == 0) { if (!silent) throw new InvalidOperationException(Ui.T("Add a subscription link first.", "请先添加订阅链接。")); return; }
            var wasConnected = Connected; var oldId = Config.IndexId;
            var oldConnectionKey = Nodes.FirstOrDefault(n => n.Id == oldId) is { } oldNode ? MeasurementKey(oldNode) : null;
            var previousNotice = Notice; var succeeded = 0;
            foreach (var sub in subs)
            {
                var success = false;
                try
                {
                    if (await LinkSubscriptions.Refresh(Config, sub, Connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null, budget.Token) is { } usage)
                        Settings.SubscriptionUsage[sub.Id] = usage;
                    success = true;
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { break; }
                catch { /* Keep this subscription intact and continue other saved links. */ }
                if (success) succeeded++;
            }
            await Refresh();
            updated = succeeded > 0;
            if (succeeded == subs.Count) { Settings.LastSubscriptionRefresh = DateTimeOffset.UtcNow; Settings.Save(); }
            if (wasConnected)
            {
                var retained = AvailableLocations.FirstOrDefault(n => MeasurementKey(n) == oldConnectionKey);
                if (retained is not null)
                {
                    await ConfigHandler.SetDefaultServerIndex(Config, retained.Id);
                    foreach (var node in Nodes) { node.Current = node.Id == retained.Id; node.Notify(); }
                    SelectedNode = retained; await ConfigHandler.SaveConfig(Config);
                }
                else
                {
                    await DisconnectInternal(keepMacBridge: UseTunBridge);
                    if (_powerCts is null && _wantConnected && !budget.IsCancellationRequested) await ConnectSmart(budget.Token);
                }
            }
            Notice = !updated ? UpdateFailedNotice :
                succeeded < subs.Count ? PartialUpdateNotice : silent ? previousNotice : Ui.T("Nodes updated.", "节点已更新。");
            Settings.SubscriptionUsage = Settings.SubscriptionUsage.Where(u => subs.Any(s => s.Id == u.Key) || AppSubscriptionIds.Contains(u.Key)).ToDictionary();
            Settings.Save();
            }
            finally { _subscriptionCts = null; }
        });
        return updated;
    }
    public async Task RefreshAndRankLocations()
    {
        if (!CanUpdateNodes || _refreshNodesCts is not null) return;
        UpdatingNodes = true; _refreshNodesCts = new();
        var token = _refreshNodesCts.Token; NodeUpdateStatus = ""; Notify();
        try
        {
            if (!await UpdateSubscriptions(cancellationToken: token)) { NodeUpdateStatus = token.IsCancellationRequested ? Ui.T("Update stopped. Your saved nodes are still available.", "已停止更新，原有节点仍可使用。") : UpdateFailedNotice; return; }
            token.ThrowIfCancellationRequested();
            var partial = Notice == PartialUpdateNotice;
            UpdatingNodes = false; NodeUpdateStatus = Ui.T("Nodes updated · checking speeds…", "节点已更新 · 正在测速…"); Notify();
            await MeasureAvailableLocations(token);
            token.ThrowIfCancellationRequested();
            var measured = AvailableLocations.Count(n => n.HasFreshSpeed);
            NodeUpdateStatus = _lastTestCancelled ? Ui.T("Nodes updated · speed check stopped.", "节点已更新 · 测速已停止。") : partial ? Ui.T("Some subscriptions could not update. Try again later.", "部分订阅更新失败，请稍后再试。") : measured > 0
                ? Ui.T("Nodes updated · fastest first", "节点已更新 · 已按速度排序") : Ui.T("Nodes updated · speed unavailable. Try again later.", "节点已更新 · 暂时测不出速度，请稍后再试。");
        }
        catch (OperationCanceledException) { NodeUpdateStatus = Ui.T("Speed check stopped.", "测速已停止。"); }
        catch (Exception ex) { Logging.SaveLog("Simple.RefreshNodes", ex); NodeUpdateStatus = Ui.T("Unable to update nodes. Please try again.", "节点更新失败，请重试。"); }
        finally { _refreshNodesCts.Dispose(); _refreshNodesCts = null; UpdatingNodes = false; Notify(); }
    }
    internal bool SpeedTestDue(DateTimeOffset now) => Settings.LastNodeSpeedCheck is not { } last
        || now - last >= TimeSpan.FromHours(1) || last > now;
    // The hourly check measures latency only. Download tests used up to 20 MB per node every hour,
    // which quietly drained users' subscription data. Download speed is measured only on request.
    internal async Task CheckScheduledMeasurements(DateTimeOffset now)
    {
        if (!CanUpdateNodes || _refreshNodesCts is not null || ConnectionPending || !AvailableLocations.Any() || !SpeedTestDue(now)) return;
        if (_offline || !integrationTest && !NetworkState.PhysicalNetworkUp()) return;
        Settings.LastNodeSpeedCheck = DateTimeOffset.UtcNow; Settings.Save();
        try { await TestNodes(false, AvailableLocations.ToList(), forceHttp: true, quick: true, quiet: true); }
        catch (Exception ex) { Logging.SaveLog("Simple.ScheduledLatency", ex); }
    }
    private async Task MeasureAvailableLocations(CancellationToken token = default)
    {
        // Record attempts, including unsuccessful or cancelled sweeps, to avoid a retry loop.
        Settings.LastNodeSpeedCheck = DateTimeOffset.UtcNow; Settings.Save();
        await TestNodes(true, AvailableLocations.ToList(), quick: true, quiet: true, cancellationToken: token);
    }
    public async Task MeasureLocations(bool silent = false)
    {
        if (_powerCts is not null || Busy || IsTesting || UpdatingNodes || !AvailableLocations.Any()) return;
        try
        {
            await MeasureAvailableLocations();
            if (!silent) NodeUpdateStatus = _lastTestCancelled ? Ui.T("Speed check stopped.", "测速已停止。") : AvailableLocations.Any(n => n.HasFreshSpeed)
                ? Ui.T("Speed results saved · fastest first", "测速完成 · 已按速度排序") : Ui.T("Speed unavailable. Please try again later.", "暂时测不出速度，请稍后再试。");
        }
        catch (Exception ex)
        { Logging.SaveLog("Simple.MeasureLocations", ex); if (!silent) NodeUpdateStatus = Ui.T("Unable to test speed. Please try again.", "测速失败，请重试。"); }
        finally { Notify(); }
    }
    public async Task RunExclusive(Func<Task> action, bool allowMeasurements = false, bool connectionOperation = false)
    {
        if (_powerCts is not null && !connectionOperation) { Notice = Ui.T("Finish connecting or cancel before changing settings.", "请等连接完成或先取消，再修改设置。"); Notify(); return; }
        if (_testCts is not null && !allowMeasurements) { Notice = Ui.T("Stop the speed test first.", "请先停止测速，再切换连接或设置。"); Notify(); return; }
        if (!await _gate.WaitAsync(0)) { Notice = Ui.T("The previous task is still running. Please wait.", "上一项操作仍在进行，请稍候。"); Notify(); return; }
        Busy = true; Notify();
        try { await action(); }
        catch (OperationCanceledException) { Notice = _wantConnected ? Ui.T("Connection timed out. Check your network and try again.", "连接超时，请检查网络后重试。") : ""; }
        catch (Exception ex) { Logging.SaveLog("Simple", ex); Notice = ex is InvalidOperationException ? ex.Message : Ui.T("Something went wrong. Please try again.", "操作失败，请重试。"); }
        finally { Busy = false; Notify(); _gate.Release(); }
    }
    public Task Test(bool download, bool all) => TestNodes(download,
        all ? VisibleNodes.ToList() : SelectedNode is not null ? [SelectedNode] : [], forceHttp: false);
    public async Task TestNodes(bool download, IEnumerable<NodeRow> selected, bool forceHttp = true, bool quick = false, bool quiet = false, CancellationToken cancellationToken = default)
    {
        if (_powerCts is not null || _testCts is not null || Busy) { Notice = Ui.T("Another task is running. Please wait or cancel it.", "已有任务正在进行，请稍候或取消。"); Notify(); return; }
        var rows = selected.Where(IsUsableNode).DistinctBy(n => n.Id).ToList();
        if (rows.Count == 0) { Notice = Ui.T("Choose a location first.", "请先选择线路。"); Notify(); return; }
        _lastTestCancelled = false;
        _testCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); var token = _testCts.Token;
        if (!quiet) TestSummary = Ui.T($"Testing {rows.Count} location(s)…", $"正在测试 {rows.Count} 条线路…"); Notify();
        long totalBytes = 0; int completed = 0; string? measurementError = null;
        var lastProgress = Stopwatch.StartNew();
        try
        {
            var physicalInterface = integrationTest ? null : await MacVpnConflict.FindPhysicalInterface();
            if (!integrationTest && OperatingSystem.IsMacOS() && physicalInterface is null && await MacVpnConflict.Find() is { } vpn)
            {
                measurementError = Ui.T($"Disconnect {vpn} before testing locations.", $"请先断开 {vpn}，再测速。");
                throw new InvalidOperationException(measurementError);
            }
            foreach (var target in rows)
            {
                token.ThrowIfCancellationRequested();
                // Keep the last completed sample visible until its replacement is ready.
                var row = new NodeRow(target.Profile) { Speed = download ? null : target.Speed };
                _measurementStatus = Ui.T($"Testing {completed + 1} of {rows.Count}…", $"正在测试第 {completed + 1}/{rows.Count} 条…");
                target.Testing = true; target.Notify();
                row.Testing = true; row.Failure = null; if (download) row.Speed = null; row.Delay = null; row.Notify();
                if (!quiet) TestSummary = Ui.T($"{(download ? "Download" : "Latency")} test {completed + 1}/{rows.Count} · {row.LocationName}", $"{(download ? "测速" : "测延迟")} {completed + 1}/{rows.Count} · {row.LocationName}");
                Notify();
                ProcessService? process = null;
                try
                {
                    row.DelayMethod = Settings.LatencyMode;
                    if (!forceHttp && !download && Settings.LatencyMode == "TCP")
                    {
                        if (row.Profile.ConfigType is EConfigType.Hysteria2 or EConfigType.TUIC or EConfigType.WireGuard or EConfigType.MASQUE)
                        { row.Failure = "Failed"; }
                        else
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(Settings.PingTimeoutSeconds));
                            using var tcp = new TcpClient();
                            var clock = Stopwatch.StartNew();
                            await tcp.ConnectAsync(row.Profile.Address, row.Profile.Port, timeout.Token);
                            row.Delay = clock.Elapsed.TotalMilliseconds;
                        }
                    }
                    else
                    {
                        int port;
                        (process, port) = await StartTestCore(row, physicalInterface, token);
                        using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
                        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                        if (!download) try
                        {
                            row.Delay = await LatencyBenchmark.Measure(http, Config.SpeedTestItem.SpeedPingTestUrl,
                                TimeSpan.FromSeconds(Settings.PingTimeoutSeconds), token);
                            row.DelayMethod = "HTTP median";
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                        { row.Delay = null; } // Download availability is independent of the latency endpoint.
                        row.Notify();
                        if (download)
                        {
                            // Ranking needs a comparable sample, not a full benchmark: cap data per location.
                            var result = await StreamingBenchmark.MeasureWithFallback(http, integrationTest ? [Config.SpeedTestItem.SpeedTestUrl] : [Config.SpeedTestItem.SpeedTestUrl, "https://ai.z5a.com/clients/world-science/releases/v1.0.2/World-Science-1.0.2-source.zip"], TimeSpan.FromSeconds(quick ? 3 : 5), quick ? 10_000_000 : 25_000_000, bytes =>
                            {
                                totalBytes += bytes;
                                if (!quiet && lastProgress.ElapsedMilliseconds >= 200)
                                {
                                    lastProgress.Restart();
                                    Dispatcher.UIThread.Post(() => { TestSummary = Ui.T($"{totalBytes / 1_000_000d:0.0} MB · location {completed + 1}/{rows.Count}", $"已下载 {totalBytes / 1_000_000d:0.0} MB · 第 {completed + 1}/{rows.Count} 条"); Notify(); });
                                }
                            }, token);
                            row.Speed = result.MegabytesPerSecond;
                            ProfileExManager.Instance.SetTestSpeed(row.Id, (decimal)row.Speed.Value);
                        }
                    }
                    if (row.Delay.HasValue) ProfileExManager.Instance.SetTestDelay(row.Id, (int)row.Delay.Value);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { row.Failure = "Timeout"; }
                catch (Exception ex) when (!token.IsCancellationRequested) { row.Failure = "Failed"; Logging.SaveLog("Simple.TestNode", ex); }
                finally
                {
                    if (process is not null) { await process.StopAsync(); process.Dispose(); }
                    target.Testing = false;
                    if (!token.IsCancellationRequested)
                    {
                        row.LastTestAt = row.Failure is null && download && row.Speed > 0 ? DateTimeOffset.Now : download ? null : target.LastTestAt;
                        row.LastDelayAt = row.Failure is null && row.Delay > 0 ? DateTimeOffset.Now : null;
                        // A latency-only pass keeps the last completed download sample.
                        if (!download && row.Failure is null) row.Speed = target.Speed;
                        ProfileExManager.Instance.SetTestDelay(row.Id, (int)(row.Delay ?? -1));
                        if (download) ProfileExManager.Instance.SetTestSpeed(row.Id, (decimal)(row.Speed ?? 0));
                        target.Delay = row.Delay; target.Speed = row.Speed; target.LastTestAt = row.LastTestAt; target.LastDelayAt = row.LastDelayAt;
                        target.Failure = row.Failure; target.DelayMethod = row.DelayMethod;
                    }
                    target.Notify(); Notify();
                }
                completed++;
            }
            if (!quiet) Notice = Ui.T("Speed test finished.", "测速完成。");
        }
        catch (OperationCanceledException) { if (!quiet) Notice = Ui.T("Speed test cancelled.", "测速已取消。"); }
        catch (Exception ex) { Logging.SaveLog("Simple.Test", ex); if (!quiet) Notice = Ui.T("Speed test failed. Please try again.", "测速失败，请稍后重试。"); }
        finally
        {
            _lastTestCancelled = token.IsCancellationRequested;
            var measuredCount = rows.Count(n => n.HasFreshSpeed);
            if (!download) measuredCount = rows.Count(n => n.HasFreshDelay);
            _measurementStatus = measurementError ?? (token.IsCancellationRequested ? Ui.T("Speed test stopped.", "测速已停止。") : measuredCount > 0 ? Ui.T($"{measuredCount} of {rows.Count} locations measured.", $"已测 {measuredCount}/{rows.Count} 条线路。") : Ui.T("No results. Check your connection and try again.", "没有测出结果，请检查网络后重试。"));
            if (!quiet) TestSummary = Ui.T($"{(token.IsCancellationRequested ? "Stopped" : "Finished")} · {completed}/{rows.Count} locations · {totalBytes / 1_000_000d:0.0} MB downloaded", $"{(token.IsCancellationRequested ? "已停止" : "已完成")} · {completed}/{rows.Count} 条 · 共下载 {totalBytes / 1_000_000d:0.0} MB");
            try
            {
                Settings.NodeMeasurements = MergeMeasurements(Settings.NodeMeasurements, AvailableLocations, DateTimeOffset.Now);
                Settings.Save();
                await ProfileExManager.Instance.SaveTo(); ApplyFilter(_filter, _sort);
            }
            finally { _testCts.Dispose(); _testCts = null; Notify(); }
        }
    }
    /// <summary>
    /// Keeps earlier results for locations a cancelled or partial pass did not reach, so the list
    /// is not emptied by one interrupted test. Entries expire with the two-hour freshness window.
    /// </summary>
    internal static Dictionary<string, NodeMeasurement> MergeMeasurements(IReadOnlyDictionary<string, NodeMeasurement> saved, IEnumerable<NodeRow> rows, DateTimeOffset now)
    {
        var merged = saved.Where(m => now - m.Value.At < TimeSpan.FromHours(2) && m.Value.At <= now).ToDictionary(m => m.Key, m => m.Value);
        foreach (var row in rows)
        {
            if (!row.HasFreshSpeed && !row.HasFreshDelay) continue;
            var at = new[] { row.HasFreshSpeed ? row.LastTestAt : null, row.HasFreshDelay ? row.LastDelayAt : null }.Max()!.Value;
            merged[MeasurementKey(row)] = new NodeMeasurement(row.HasFreshSpeed ? row.Speed!.Value : 0, row.HasFreshDelay ? row.Delay : null, at);
        }
        return merged;
    }
    public void CancelTest()
    {
        if (integrationTest && _testCts is not null) Logging.SaveLog("Simple.AcceptanceCancellation: " + Environment.StackTrace);
        _testCts?.Cancel();
    }
    public async Task Close()
    {
        _powerCts?.Cancel(); _subscriptionCts?.Cancel(); _refreshNodesCts?.Cancel();
        _wantConnected = false; _connectCts?.Cancel(); CoreAdminManager.Instance.CancelMacAuthorization();
        CancelTest();
        while (_testCts is not null) await Task.Delay(50);
        await _gate.WaitAsync();
        try { _traffic.Dispose(); await DisconnectInternal(); Settings.Save(); await ConfigHandler.SaveConfig(Config); await ProfileExManager.Instance.SaveTo(); await StatisticsManager.Instance.SaveTo(); StatisticsManager.Instance.Close(); }
        finally { _gate.Release(); }
    }
}

/// <summary>The VPN adapter did not come up. Not caused by the node, so failover cannot help.</summary>
public sealed class TunStartException(Exception inner) : InvalidOperationException(Ui.T(
    "The VPN adapter did not start. Quit other VPN or proxy apps and try again. If it still fails, restart your computer.",
    "VPN 虚拟网卡没有启动成功。请退出其他 VPN 或代理软件后重试；仍不行的话，重启电脑一般可以解决。"), inner);
