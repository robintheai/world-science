using ServiceLib.Models.CoreConfigs;
namespace WorldScience.Services;

/// <summary>All public destinations use the proxy; local devices remain reachable.</summary>
internal static class GlobalTrafficPolicy
{
    internal static CoreConfigContext Apply(CoreConfigContext context)
    {
        var rules = new List<RulesItem>
        {
            new() { Type = "field", Enabled = true, OutboundTag = Global.DirectTag, Ip = ["geoip:private"] },
            new() { Type = "field", Enabled = true, OutboundTag = Global.DirectTag, Domain = ["geosite:private"] },
            new() { Type = "field", Enabled = true, OutboundTag = Global.BlockTag, Ip = ["::/0"] },
            new() { Type = "field", Enabled = true, OutboundTag = Global.ProxyTag, Port = "0-65535" }
        };
        var dns = JsonUtils.DeepCopy(context.SimpleDnsItem);
        dns.BlockAAAAQuery = true;
        dns.Strategy4Proxy = "UseIPv4";
        dns.Strategy4ProxyDial = "ForceIPv4";
        dns.Strategy4Freedom = "ForceIPv4";
        return context with { IPv4Only = true, SimpleDnsItem = dns, RoutingItem = new RoutingItem
        {
            Remarks = "Simple Global", RuleSet = JsonUtils.Serialize(rules), RuleNum = rules.Count
        }};
    }
}
