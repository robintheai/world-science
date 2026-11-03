namespace WorldScience.Services;

/// <summary>Subscription annotations are not selectable or measurable VPN endpoints.</summary>
internal static class SubscriptionLocations
{
    internal static bool IsNotice(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"剩余|到期|过期|续费|邀请|流量[:：]|邮箱|订阅将|公告|通知|解锁不了|更换节点|重置时间|更新订阅|使用说明|remaining|expire|traffic|\b(?:announcement|notice|quota|usage|subscription\s+info|reset\s+(?:date|time))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
