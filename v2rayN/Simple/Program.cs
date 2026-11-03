using ReactiveUI.Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.IO.Pipes;

namespace WorldScience;
internal static class Program
{
    private static Mutex? _mutex;
    public static string? IntegrationOutput { get; private set; }
    /// <summary>Started by the login item: stay in the menu bar / tray without showing the window.</summary>
    public static bool StartInBackground { get; private set; }
    /// <summary>Raised when the user opens the app again while it is already running.</summary>
    public static event Action? ActivationRequested;
    private static string _pipeName = "";
    [STAThread]
    public static void Main(string[] args)
    {
        IntegrationOutput = args.FirstOrDefault(a => a.StartsWith("--integration-test="))?.Split('=', 2)[1];
        var dataRoot = Environment.GetEnvironmentVariable("WORLD_SCIENCE_DATA_DIR");
        if (IntegrationOutput is not null && string.IsNullOrWhiteSpace(dataRoot))
            throw new InvalidOperationException("Integration tests require an isolated WORLD_SCIENCE_DATA_DIR");
        var suffix = dataRoot is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataRoot)))[..12];
        StartInBackground = args.Contains("--background");
        // string.GetHashCode is randomized per process; both copies must derive the same name.
        _pipeName = "world-science-activate-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..16] + suffix;
        _mutex = new Mutex(true, "io.github.robintheai.worldscience" + suffix, out var first);
        // A second launch used to exit silently while the first copy sat hidden in the tray. Bring it forward instead.
        if (!first) { if (!StartInBackground) SignalRunningInstance(); return; }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logging.SaveLog("Simple.Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Logging.SaveLog("Simple.Unobserved", e.Exception); e.SetObserved(); };
        Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
        Global.SingboxTunName = "worldsci_tun";
        if (!AppManager.Instance.InitApp()) throw new InvalidOperationException("无法初始化独立配置目录");
        if (AppManager.Instance.Config.Inbound[0].LocalPort == 10808) AppManager.Instance.Config.Inbound[0].LocalPort = 18890;
        if (IntegrationOutput is not null) AppManager.Instance.Config.Inbound[0].LocalPort = 29890;
        // Fix the interface language before any window or menu is built.
        var language = IntegrationOutput is not null ? "en" : SimpleSettings.Load().Language;
        if (language != "auto") Ui.Use(language); else _ = Ui.Chinese;
        if (IntegrationOutput is null) _ = ListenForActivation();
        AppBuilder.Configure<SimpleApp>().UsePlatformDetect().With(new AvaloniaNativePlatformOptions { OverlayPopups = true }).UseReactiveUI(_ => { }).LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(_mutex);
    }
    private static void SignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch { /* The running copy may be quitting; nothing else to do. */ }
    }
    private static async Task ListenForActivation()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                if (server.ReadByte() == 1) ActivationRequested?.Invoke();
            }
            catch (Exception ex) { Logging.SaveLog("Simple.Activation", ex); await Task.Delay(5000); }
        }
    }
}
public sealed class SimpleApp : Application
{
    public override void Initialize()
    {
        Name = "World Science";
        Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://WorldScience/"))
        { Source = new Uri("avares://WorldScience/Controls/GlassTheme.axaml") });
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://WorldScience/"))
        { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppManager.Instance.InitComponents();
            desktop.MainWindow = new SimpleWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
