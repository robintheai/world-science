namespace WorldScience.Services;

/// <summary>Static interface text for XAML (<c>{x:Static}</c>). The language is fixed for the app's lifetime.</summary>
public static class Strings
{
    public static string Back => Ui.T("‹  Back", "‹  返回");
    public static string Subscriptions => Ui.T("Subscriptions", "订阅");
    public static string YourSubscriptions => Ui.T("Your subscriptions", "我的订阅");
    public static string SubscriptionIntro => Ui.T("Paste a link. Choose a location. Connect.", "粘贴订阅链接，选好线路，一键连接。");
    public static string AddSubscription => Ui.T("Add subscription", "添加订阅");
    public static string SubscriptionPlaceholder => Ui.T("https://… or node links (vmess://, ss://, Clash YAML)", "https://… 或节点链接（vmess://、ss://、Clash 配置）");
    public static string AddSubscriptionHint => Ui.T("Add your subscription link", "添加你的订阅链接");
    public static string NoAccountNeeded => Ui.T("No account or email code needed.", "无需注册账号，也不用验证码。");
    public static string ChooseLocation => Ui.T("Choose location", "选择线路");
    public static string Download => Ui.T("↓  Download", "↓  下载");
    public static string Upload => Ui.T("↑  Upload", "↑  上传");
    public static string BuyCoffee => Ui.T("Buy me a coffee", "请作者喝杯咖啡");
    public static string FastestFirst => Ui.T("Fastest first", "速度最快的排在前面");
    public static string Automatic => Ui.T("Automatic", "自动选择");
    public static string AutomaticDetail => Ui.T("Fastest available", "自动连最快的线路");
    public static string Settings => Ui.T("Settings", "设置");
    public static string LaunchAtLogin => Ui.T("Open at login", "开机自动启动");
    public static string LaunchAtLoginDetail => Ui.T("Starts quietly in the menu bar.", "开机后在后台静默运行。");
    public static string AutoConnect => Ui.T("Connect when opened", "打开后自动连接");
    public static string AutoConnectDetail => Ui.T("Uses your last location choice.", "使用上次选择的线路。");
    public static string Fallback => Ui.T("Switch when a location fails", "线路不可用时自动切换");
    public static string FallbackDetail => Ui.T("If your chosen location stops working, the fastest working one is used until it recovers.", "选定的线路连不上时，临时改用最快的可用线路，恢复后自动切回。");
    public static string Language => Ui.T("Language", "语言");
    public static string LanguageAuto => Ui.T("System", "跟随系统");
    public static string LanguageRestart => Ui.T("Takes effect after reopening the app.", "重新打开 App 后生效。");
    public static string ExportDiagnostics => Ui.T("Export diagnostics", "导出诊断信息");
    public static string ExportDiagnosticsDetail => Ui.T("Saves a log file to your desktop to send to support. Visited sites and links are removed.", "在桌面生成日志文件，发给客服排查问题。不包含访问记录和订阅链接。");
    public static string CheckUpdates => Ui.T("Check for updates", "检查更新");
    public static string ServiceCheck => Ui.T("Service check", "服务可用性");
    public static string ServiceCheckHint => Ui.T("Tap to check again", "点击重新检测");
}
