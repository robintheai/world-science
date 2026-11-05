using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Media;
using System.IO.Compression;
using Avalonia.Input.Platform;
namespace WorldScience;
public partial class SimpleWindow : Window
{
    private readonly ClientController _client = new(Program.IntegrationOutput is not null);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private MacMenuBar? _mac;
    internal MacMenuBar? MacMenu => _mac;
    private TrayIcon? _tray;
    private bool _ready, _quitting, _checking, _manualUpdateRequested;
    private CancellationTokenSource? _importCts;
    internal NativeMenuItem? UpdateMenuItem { get; private set; }
    private string VersionLabel => ReleaseUpdater.CurrentVersion.ToString(3);
    internal LocationPicker? ActiveLocationPicker { get; private set; }
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private bool _loadingSettings;
    public SimpleWindow()
    {
        InitializeComponent(); DataContext = _client; ShowPage("subscriptions");
        // Avalonia's default fallback can pick Japanese glyph forms for Chinese text on Windows.
        if (OperatingSystem.IsWindows()) FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        Program.ActivationRequested += () => Dispatcher.UIThread.Post(() => { Opacity = 1; Show(); Activate(); });
        Title = $"Owlpath · {VersionLabel}"; VersionBadge.Content = $"v{VersionLabel}";
        if (OperatingSystem.IsMacOS())
        {
            var menu = new NativeMenu();
            var about = new NativeMenuItem(Ui.T($"About Owlpath {VersionLabel}", $"关于 Owlpath {VersionLabel}"));
            about.Click += (_, _) => { Show(); Activate(); _client.Notice = $"Owlpath {VersionLabel}"; _client.Notify(); };
            menu.Add(about);
            UpdateMenuItem = new NativeMenuItem(Program.IntegrationOutput is not null ? "Check for Updates…" : Ui.T("Check for Updates…", "检查更新…"));
            UpdateMenuItem.Click += async (_, _) => await CheckUpdates(true);
            menu.Add(UpdateMenuItem); menu.Add(new NativeMenuItemSeparator());
            NativeMenu.SetMenu(Application.Current!, menu);
        }
        if (Program.IntegrationOutput is not null || Program.StartInBackground) ShowActivated = false;
        if (Program.StartInBackground) Opacity = 0;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.ShutdownRequested += (_, args) => { if (_quitting) return; args.Cancel = true; _ = Quit(); };
        Icon = new WindowIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "simple.png"));
        _client.StatusChanged += () =>
        {
            _mac?.Update(_client);
            if (_tray is not null) _tray.ToolTipText = $"{_client.ConnectionStatus} · {_client.CurrentNode} · {"Global"} · {_client.TrafficText}";
        };
        Opened += async (_, _) =>
        {
            try
            {
                await _client.Initialize();
                _client.Settings.ShowMenuNode = _client.Settings.ShowMenuSpeed = true;
                _client.Settings.Mode = "全局代理";
                _ready = true; CreateTray(); FitToScreen();
                if (OperatingSystem.IsWindows() && Program.IntegrationOutput is null) _ = Task.Run(() => ReleaseUpdater.CleanupWindowsUpdateLeftovers(AppContext.BaseDirectory));
                if (Program.StartInBackground && Program.IntegrationOutput is null) { Hide(); Opacity = 1; }
                var lastAutomation = DateTimeOffset.MinValue;
                _timer.Tick += (_, _) =>
                {
                    _client.ExpireTraffic();
                    if (_client.Connected) _client.Notify(); // Session timer.
                    if (DateTimeOffset.UtcNow >= _nextCheck) _ = CheckUpdates(false);
                    if (DateTimeOffset.UtcNow - lastAutomation > TimeSpan.FromSeconds(5)) { lastAutomation = DateTimeOffset.UtcNow; _ = _client.Automate(); }
                };
                if (Program.IntegrationOutput is not null)
                {
                    Title += " · QA";
                    await LinkIntegrationRunner.Run(_client, this, Program.IntegrationOutput); await Quit();
                }
                else
                {
                    if (!_client.Settings.LegacyImportDone && await ImportLegacy(LegacyImport.Root) > 0) LaunchAtLogin.MigrateLegacy();
                    LaunchAtLogin.Refresh();
                    await RefreshSubscriptionUi(); ShowPage(_client.AvailableLocations.Any() ? "home" : "subscriptions"); _timer.Start(); _ = CheckUpdates(false);
                    ShowUsageWarning();
                    if (_client.Settings.AutoConnect && _client.AvailableLocations.Any() && !_client.Connected) _ = ToggleConnection();
                    else if (!_client.AvailableLocations.Any()) await OfferClipboardLink();
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("Simple.Initialize", ex); SubscriptionFeedback.Text = Ui.T("Unable to start. Please reopen the app.", "启动失败，请重新打开 App。");
                // A failed background start must not leave an invisible window with no tray icon.
                Opacity = 1; Show(); Activate();
            }
        };
        Closing += (_, args) => { if (_quitting) return; args.Cancel = true; if (_mac is not null || _tray is not null) Hide(); else _ = Quit(); };
    }
    private void CreateTray()
    {
        if (OperatingSystem.IsMacOS())
            _mac = new MacMenuBar(Path.Combine(AppContext.BaseDirectory, "Assets", "status.png"), action =>
            { if (Program.IntegrationOutput is not null) return; if (action == "quit") _ = Quit(); else if (action == "toggle") _ = ToggleConnection(); else { Show(); Activate(); } });
        else
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem(Ui.T("Open Owlpath", "打开 Owlpath")); show.Click += (_, _) => { Show(); Activate(); }; menu.Add(show);
            var toggle = new NativeMenuItem(Ui.T("Connect / Disconnect", "连接 / 断开")); toggle.Click += async (_, _) => await ToggleConnection(); menu.Add(toggle);
            var quit = new NativeMenuItem(Ui.T("Quit", "退出")); quit.Click += async (_, _) => await Quit(); menu.Add(quit);
            _tray = new TrayIcon { Icon = Icon, Menu = menu, IsVisible = true }; _tray.Clicked += (_, _) => { Show(); Activate(); };
        }
        _client.Notify();
    }
    private async Task Quit()
    {
        if (_quitting) return;
        _quitting = true; _timer.Stop(); _lifetime.Cancel(); _importCts?.Cancel();
        await _client.Close(); _mac?.Dispose(); _tray?.Dispose(); _http.Dispose();
        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
    private async void ConnectClick(object? sender, RoutedEventArgs args) => await ToggleConnection();
    private async void ChooseLocationClick(object? sender, RoutedEventArgs args)
    {
        if (!_ready || !_client.CanChooseLocation) return;
        if (ActiveLocationPicker is not null) return;
        LocationSelection? selected;
        try
        {
            ActiveLocationPicker = new LocationPicker(_client);
            selected = await ActiveLocationPicker.ShowAt(LocationButton);
        }
        catch (Exception ex) { Logging.SaveLog("Simple.LocationPicker", ex); return; }
        finally { ActiveLocationPicker = null; }
        if (selected is null) return;
        if (selected.NodeId is null) await _client.ChooseLocation(null);
        else if (_client.AvailableLocations.FirstOrDefault(n => n.Id == selected.NodeId) is { } node)
            await _client.ChooseLocation(node);
        else { _client.Notice = Ui.T("This location is no longer available. Choose another location.", "这条线路已失效，请换一条。"); _client.Notify(); }
    }
    private async void UpdateNodesClick(object? sender, RoutedEventArgs args)
    {
        if (!_ready || _quitting) return;
        if (_client.CanCancelNodeUpdate) _client.CancelNodeUpdate();
        else { await _client.RefreshAndRankLocations(); await RefreshSubscriptionUi(); }
    }
    private async Task ToggleConnection()
    {
        if (!_ready) return;
        if (_client.Connected || _client.WantsConnection) { await _client.Toggle(); return; }
        if (!_client.AvailableLocations.Any()) { ShowPage("subscriptions"); Show(); Activate(); return; }
        await _client.Toggle();
    }
    internal void ShowPage(string page) { SubscriptionPanel.IsVisible = page == "subscriptions"; HomePanel.IsVisible = page == "home"; SettingsPanel.IsVisible = page == "settings"; }
    private async void SubscriptionsClick(object? sender, RoutedEventArgs args) { await RefreshSubscriptionUi(); ShowPage("subscriptions"); }
    private void SubscriptionBackClick(object? sender, RoutedEventArgs args) => ShowPage("home");
    private async Task RefreshSubscriptionUi()
    {
        var subs = await AppManager.Instance.SubItems() ?? [];
        SubscriptionDetails.Text = Ui.T($"{subs.Count(s => s.Enabled)} saved · {_client.AvailableLocations.Count()} location{(_client.AvailableLocations.Count() == 1 ? "" : "s")}", $"{subs.Count(s => s.Enabled)} 个订阅 · {_client.AvailableLocations.Count()} 条线路");
        SubscriptionList.Children.Clear();
        foreach (var sub in subs.Where(s => s.Enabled))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4) };
            row.Children.Add(new TextBlock { Text = sub.Remarks, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new Button { Content = Ui.T("Remove", "删除"), Classes = { "link" }, FontSize = 12 };
            var usage = _client.Settings.SubscriptionUsage.GetValueOrDefault(sub.Id)?.Summary(DateTimeOffset.UtcNow);
            if (!string.IsNullOrEmpty(usage))
            {
                var title = (TextBlock)row.Children[0]; row.Children.Remove(title);
                var stack = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                stack.Children.Add(title); stack.Children.Add(new TextBlock { Text = usage, FontSize = 11, Classes = { "secondary" } });
                row.Children.Insert(0, stack);
            }
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            var confirm = false;
            remove.Click += async (_, _) =>
            {
                if (_importCts is not null || _client.Busy || _client.IsTesting) { SubscriptionFeedback.Text = BusyText; return; }
                if (!confirm) { confirm = true; remove.Content = Ui.T("Confirm", "确认删除"); SubscriptionFeedback.Text = Ui.T("Remove this subscription and its saved locations?", "确定删除这个订阅和它的所有线路吗？"); return; }
                if (_client.Nodes.Any(n => n.Current && n.Profile.Subid == sub.Id)) await _client.StopForSignOut();
                await _client.RunExclusive(async () => { await ConfigHandler.DeleteSubItem(_client.Config, sub.Id); await _client.Refresh(); });
                await RefreshSubscriptionUi(); SubscriptionFeedback.Text = Ui.T("Subscription removed.", "订阅已删除。");
            };
            SubscriptionList.Children.Add(row);
        }
    }
    internal async Task ImportLink(string text)
    {
        if (_importCts is not null) { _importCts.Cancel(); return; }
        if (!_ready || _client.Busy || _client.IsTesting) { SubscriptionFeedback.Text = BusyText; return; }
        _importCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ImportButton.Content = Ui.T("Cancel", "取消"); SubscriptionInput.IsEnabled = false; SubscriptionFeedback.Text = DownloadingText;
        var imported = false;
        try
        {
            await _client.RunExclusive(async () =>
            {
                try
                {
                    Uri uri;
                    try { uri = LinkSubscriptions.ParseUrl(text); }
                    catch (InvalidOperationException) when (SubscriptionFormats.LooksLikeNodes(text))
                    {
                        await LinkSubscriptions.ImportText(_client.Config, text, _importCts.Token);
                        await _client.Refresh(); imported = true; return;
                    }
                    var existing = (await AppManager.Instance.SubItems())?.FirstOrDefault(s => s.Url == uri.AbsoluteUri);
                    if (existing is not null) { imported = true; return; }
                    var created = new SubItem { Id = Guid.NewGuid().ToString("N"), Url = uri.AbsoluteUri, Remarks = uri.IdnHost };
                    if (await LinkSubscriptions.Refresh(_client.Config, created, _client.Connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null, _importCts.Token) is { } usage)
                        _client.Settings.SubscriptionUsage[created.Id] = usage;
                    _client.Settings.LastSubscriptionRefresh = DateTimeOffset.UtcNow; _client.Settings.Save();
                    await _client.Refresh(); imported = true;
                }
                catch (OperationCanceledException) { SubscriptionFeedback.Text = Ui.T("Import stopped. Saved subscriptions are unchanged.", "已停止导入，原有订阅不变。"); }
                catch (Exception ex) { SubscriptionFeedback.Text = ex is InvalidOperationException ? ex.Message : Ui.T("Unable to download this subscription. Check the link and try again.", "订阅下载失败，请检查链接后重试。"); }
            });
            if (!imported && SubscriptionFeedback.Text == DownloadingText) SubscriptionFeedback.Text = Ui.T("Another task is running. Please try again.", "另一项操作正在进行，请稍后重试。");
            if (imported) { SubscriptionInput.Text = ""; SubscriptionFeedback.Text = Ui.T("Subscription ready.", "订阅已添加。"); await RefreshSubscriptionUi(); ShowPage("home"); ShowUsageWarning(); }
        }
        finally { _importCts.Dispose(); _importCts = null; ImportButton.Content = Strings.AddSubscription; SubscriptionInput.IsEnabled = true; }
    }
    /// <summary>First launch only: brings over subscription links saved by an earlier edition.
    /// Each link is downloaded and validated; expired ones are left behind, unreachable ones are kept for a later update.</summary>
    internal async Task<int> ImportLegacy(string root)
    {
        _client.Settings.LegacyImportDone = true; _client.Settings.Save();
        if ((await AppManager.Instance.SubItems() ?? []).Any(s => s.Enabled)) return 0;
        var links = LegacyImport.Read(root);
        var count = 0;
        if (links.Count == 0) return 0;
        await _client.RunExclusive(async () =>
        {
            foreach (var uri in links)
            {
                var created = new SubItem { Id = Guid.NewGuid().ToString("N"), Url = uri.AbsoluteUri, Remarks = uri.IdnHost };
                try
                {
                    if (await LinkSubscriptions.Refresh(_client.Config, created, null, _lifetime.Token) is { } usage) _client.Settings.SubscriptionUsage[created.Id] = usage;
                    count++;
                }
                catch (SubscriptionUnreachableException) { created.Enabled = true; await ConfigHandler.AddSubItem(_client.Config, created); count++; }
                catch (InvalidOperationException) { }
            }
            if (count == 0) return;
            if (LegacyImport.AutoConnect(root)) _client.Settings.AutoConnect = true;
            _client.Settings.LastSubscriptionRefresh = DateTimeOffset.UtcNow; _client.Settings.Save();
            await _client.Refresh();
            _client.Notice = Ui.T($"Your {count} subscription(s) from the previous app were imported.", $"已自动导入旧版的 {count} 个订阅。"); _client.Notify();
        });
        return count;
    }
    private async void ImportSubscriptionClick(object? sender, RoutedEventArgs args) => await ImportLink(SubscriptionInput.Text ?? "");
    private void CoffeeClick(object? sender, RoutedEventArgs args)
    {
        if (Program.IntegrationOutput is not null) return;
        try { Process.Start(new ProcessStartInfo("https://ai.z5a.com/support/coffee") { UseShellExecute = true }); }
        catch (Exception ex) { Logging.SaveLog("Simple.Support", ex); ServiceMessage.Text = Ui.T("Open ai.z5a.com/support/coffee in your browser.", "请在浏览器打开 ai.z5a.com/support/coffee"); }
    }
    private async void CheckUpdatesClick(object? sender, RoutedEventArgs args) => await CheckUpdates(true);
    private async Task CheckUpdates(bool manual)
    {
        if (_quitting) return;
        if (manual) { _manualUpdateRequested = true; Show(); Activate(); _client.Notice = Ui.T("Checking for updates…", "正在检查更新…"); _client.Notify(); }
        if (_checking) return;
        _checking = true; _nextCheck = DateTimeOffset.UtcNow.AddHours(24);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var updater = new ReleaseUpdater(_http);
            var os = OperatingSystem.IsMacOS() ? "macos" : "windows";
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var release = await updater.Check(ReleaseUpdater.CurrentVersion, os, arch, timeout.Token);
            _client.Settings.LastUpdateCheck = DateTimeOffset.UtcNow; _client.Settings.Save();
            if (release is null) { if (_manualUpdateRequested) { _client.Notice = Ui.T($"You are up to date · v{VersionLabel}", $"已是最新版本 · v{VersionLabel}"); _client.Notify(); } return; }
            Show(); Activate();
            var dialog = new UpdateWindow(release.Version,
                (progress, token) => updater.Download(release, Path.Combine(Utils.GetTempPath(), "SimpleUpdates"), progress, token),
                async downloaded =>
                {
                    _client.Settings.Save(); await ConfigHandler.SaveConfig(_client.Config); await ProfileExManager.Instance.SaveTo();
                    ReleaseUpdater.BackupConfiguration(Utils.StartupPath());
                    if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { downloaded } });
                    else ReleaseUpdater.StartWindowsUpgrade(downloaded, AppContext.BaseDirectory);
                    await Quit();
                }, _lifetime.Token);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _nextCheck = DateTimeOffset.UtcNow.AddHours(1);
            if (_manualUpdateRequested) { _client.Notice = Ui.T("Unable to check for updates. Please try again.", "检查更新失败，请稍后重试。"); _client.Notify(); }
            Logging.SaveLog("Simple.Update", ex);
        }
        finally { _checking = false; _manualUpdateRequested = false; }
    }
    private static string BusyText => Ui.T("Wait for the current task, or stop the speed test.", "请等当前操作完成，或先停止测速。");
    private static string DownloadingText => Ui.T("Downloading locations…", "正在下载线路…");
    /// <summary>Small laptops at 150 % scaling are shorter than the window; the home page scrolls instead of clipping.</summary>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var available = screen.WorkingArea.Height / screen.Scaling - 40;
        if (available > 300 && Height > available) Height = available;
    }
    private void ShowUsageWarning()
    {
        var now = DateTimeOffset.UtcNow;
        if (_client.UsageWarning is not { } warning || _client.Settings.LastUsageWarning is { } last && now - last < TimeSpan.FromHours(20)) return;
        _client.Settings.LastUsageWarning = now; _client.Settings.Save();
        _client.Notice = warning; _client.Notify();
    }
    /// <summary>First run: a subscription link already on the clipboard is filled in, never imported without a click.</summary>
    private async Task OfferClipboardLink()
    {
        try
        {
            if (Clipboard is null || !string.IsNullOrWhiteSpace(SubscriptionInput.Text)) return;
            var text = (await Clipboard.TryGetTextAsync())?.Trim();
            if (text is null || text.Length > 8192 || text.Contains('\n')) return;
            try { LinkSubscriptions.ParseUrl(text); } catch (InvalidOperationException) { return; }
            SubscriptionInput.Text = text;
            SubscriptionFeedback.Text = Ui.T("Found a link on your clipboard. Tap Add subscription to use it.", "已自动填入剪贴板里的链接，点「添加订阅」即可。");
        }
        catch (Exception ex) { Logging.SaveLog("Simple.Clipboard", ex); }
    }
    private void SettingsClick(object? sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _loadingSettings = true;
        try
        {
            LaunchAtLoginSwitch.IsEnabled = LaunchAtLogin.Supported;
            LaunchAtLoginSwitch.IsChecked = LaunchAtLogin.Supported && LaunchAtLogin.IsEnabled();
            AutoConnectSwitch.IsChecked = _client.Settings.AutoConnect;
            FallbackSwitch.IsChecked = _client.Settings.Fallback;
            LanguageBox.ItemsSource = new[] { Strings.LanguageAuto, "中文", "English" };
            LanguageBox.SelectedIndex = _client.Settings.Language switch { "zh" => 1, "en" => 2, _ => 0 };
            SettingsFeedback.Text = "";
        }
        finally { _loadingSettings = false; }
        ShowPage("settings");
    }
    private void LaunchAtLoginChanged(object? sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        try { LaunchAtLogin.Set(LaunchAtLoginSwitch.IsChecked == true); SettingsFeedback.Text = ""; }
        catch (Exception ex)
        {
            Logging.SaveLog("Simple.LaunchAtLogin", ex);
            SettingsFeedback.Text = ex is InvalidOperationException ? ex.Message : Ui.T("Unable to change this setting.", "无法修改这个设置。");
            _loadingSettings = true; LaunchAtLoginSwitch.IsChecked = LaunchAtLogin.IsEnabled(); _loadingSettings = false;
        }
    }
    private void AutoConnectChanged(object? sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _client.Settings.AutoConnect = AutoConnectSwitch.IsChecked == true; _client.Settings.Save();
    }
    private void FallbackChanged(object? sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _client.Settings.Fallback = FallbackSwitch.IsChecked == true; _client.Settings.Save();
    }
    private void LanguageChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || LanguageBox.SelectedIndex < 0) return;
        _client.Settings.Language = LanguageBox.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "auto" }; _client.Settings.Save();
        SettingsFeedback.Text = Strings.LanguageRestart;
    }
    private async void AvailabilityClick(object? sender, RoutedEventArgs args) => await _client.RefreshAvailability(force: true);
    private async void ExportDiagnosticsClick(object? sender, RoutedEventArgs args)
    {
        DiagnosticsButton.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => Diagnostics.Export(Utils.StartupPath(), VersionLabel));
            SettingsFeedback.Text = Ui.T($"Saved to your desktop: {Path.GetFileName(path)}", $"已保存到桌面：{Path.GetFileName(path)}");
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path } });
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
        }
        catch (Exception ex) { Logging.SaveLog("Simple.Diagnostics", ex); SettingsFeedback.Text = Ui.T("Unable to export diagnostics.", "导出诊断信息失败。"); }
        finally { DiagnosticsButton.IsEnabled = true; }
    }
}
