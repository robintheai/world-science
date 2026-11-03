using System.Globalization;
using System.Text.RegularExpressions;
namespace WorldScience.Services;

internal static class EnglishUi
{
    public static string Bytes(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.##} GB" : value >= 1_000_000 ? $"{value / 1_000_000d:0.##} MB" : $"{Math.Max(0,value) / 1000d:0.#} KB";
    public static string Location(string text)
    {
        foreach (var (original, name) in new[] { ("美国", "United States"), ("日本", "Japan"), ("德国", "Germany"), ("荷兰", "Netherlands"), ("香港", "Hong Kong"), ("台湾", "Taiwan"), ("新加坡", "Singapore"), ("英国", "United Kingdom"), ("加拿大", "Canada"), ("法国", "France"), ("韩国", "South Korea"), ("澳大利亚", "Australia") })
            if (text.Contains(original)) return name;
        return Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}") ? "Automatic location" : text;
    }
    public static string Notice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Contains("上一项操作") || text is "准备就绪" or "已关闭" or "服务已同步，点击开启即可连接" or "系统代理已连接" or "TUN 已启动" or "已断开" or "已连接") return "";
        return Error(text);
    }
    public static string Error(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Please try again.";
        if (!Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}")) return text;
        if (text.Contains("60 秒")) return "Please wait 60 seconds before requesting another code.";
        if (text.Contains("频繁")) return "Too many requests. Please try again later.";
        if (text.Contains("验证码")) return "Check your email code and try again.";
        if (text.Contains("登录")) return "Please sign in again.";
        if (text.Contains("权限") || text.Contains("授权")) return "Allow the system VPN permission, then try again.";
        if (text.Contains("超时")) return "Connection timed out. Please try again.";
        if (text.Contains("节点") || text.Contains("线路")) return "No working connection yet. Retrying automatically.";
        return "Unable to complete this action. Please try again.";
    }
}
