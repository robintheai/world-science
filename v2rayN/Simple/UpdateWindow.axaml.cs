namespace WorldScience;

/// <summary>One screen-centred update dialog. Downloads remain cancellable and install is explicit.</summary>
public partial class UpdateWindow : Window
{
    private readonly Func<IProgress<double>, CancellationToken, Task<string>> _download;
    private readonly Func<string, Task> _install;
    private readonly CancellationTokenSource _lifetime;
    private Task _operation = Task.CompletedTask;
    private string? _package;
    private bool _closed;

    public UpdateWindow(Version version, Func<IProgress<double>, CancellationToken, Task<string>> download,
        Func<string, Task> install, CancellationToken cancellation = default)
    {
        InitializeComponent();
        VersionLabel.Text = $"World Science {version.ToString(3)}";
        Title = Ui.T("Software update", "软件更新"); Heading.Text = Ui.T("Update available", "有新版本");
        StatusLabel.Text = Ui.T("Your subscriptions and locations stay saved.", "订阅和线路设置都会保留。");
        LaterButton.Content = Ui.T("Not now", "以后再说"); UpdateButton.Content = Ui.T("Update", "立即更新");
        _download = download; _install = install;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };
        Closed += async (_, _) =>
        {
            _closed = true; _lifetime.Cancel();
            try { await _operation; } finally { _lifetime.Dispose(); }
        };
    }
    private void LaterClick(object? sender, RoutedEventArgs e) => Close();
    private async void UpdateClick(object? sender, RoutedEventArgs e)
    {
        if (!_operation.IsCompleted) return;
        _operation = RunUpdate();
        await _operation;
    }
    private async Task RunUpdate()
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (_package is null)
            {
                Heading.Text = Ui.T("Updating World Science", "正在更新 World Science");
                StatusLabel.Text = Ui.T("Downloading…", "正在下载…");
                DownloadProgress.IsVisible = true; DownloadProgress.Value = 0;
                LaterButton.Content = Ui.T("Cancel", "取消");
                _package = await _download(new Progress<double>(value =>
                {
                    if (_closed || _package is not null) return;
                    DownloadProgress.Value = Math.Clamp(value, 0, 100);
                    StatusLabel.Text = value >= 100 ? Ui.T("Verifying update…", "正在校验…") : Ui.T($"Downloading · {Math.Clamp(value, 0, 100):0}%", $"正在下载 · {Math.Clamp(value, 0, 100):0}%");
                }), _lifetime.Token);
                if (_closed) return;
                Heading.Text = Ui.T("Ready to install", "可以安装了");
                StatusLabel.Text = OperatingSystem.IsMacOS()
                    ? Ui.T("Quit World Science, then drag the new app into Applications.", "点击下方按钮退出后，把新版拖进「应用程序」文件夹替换即可。")
                    : Ui.T("World Science will restart to finish updating.", "World Science 将自动重启完成更新。");
                DownloadProgress.IsVisible = false;
                LaterButton.Content = Ui.T("Later", "稍后");
                UpdateButton.Content = OperatingSystem.IsMacOS() ? Ui.T("Quit & install", "退出并安装") : Ui.T("Restart & install", "重启并安装");
            }
            else
            {
                LaterButton.IsEnabled = false;
                await _install(_package);
                if (!_closed) Close();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logging.SaveLog("Simple.UpdateDownload", ex);
            if (_closed) return;
            Heading.Text = Ui.T("Try again", "请重试");
            StatusLabel.Text = ex is InvalidOperationException ? ex.Message : Ui.T("The update was interrupted. Please try again.", "更新中断，请重试。");
            UpdateButton.Content = Ui.T("Retry", "重试"); LaterButton.Content = Ui.T("Not now", "以后再说");
            DownloadProgress.IsVisible = false;
        }
        finally { if (!_closed) { UpdateButton.IsEnabled = true; LaterButton.IsEnabled = true; } }
    }
}
