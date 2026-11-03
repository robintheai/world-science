namespace WorldScience.Services;

internal static class ConnectionFailure
{
    internal static string Message(Exception? error, bool automatic) => error switch
    {
        InvalidOperationException => error.Message,
        HttpRequestException => Ui.T("The connection check failed. Check your network or choose another location. Retrying automatically.",
            "线路连通检查没有通过。请检查网络或换一条线路，正在自动重试。"),
        OperationCanceledException => Ui.T("The connection timed out. Check your network or choose another location. Retrying automatically.",
            "连接超时。请检查网络或换一条线路，正在自动重试。"),
        _ => automatic ? Ui.T("No working connection yet. Check your network or subscription. Retrying automatically.", "暂时没有可用线路。请检查网络或订阅是否到期，正在自动重试。")
            : Ui.T("This location is not responding. Choose another location or try again.", "这条线路没有响应，请换一条线路或稍后再试。")
    };
}
