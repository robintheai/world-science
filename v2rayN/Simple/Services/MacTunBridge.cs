using ServiceLib.Models.CoreConfigs;

namespace WorldScience.Services;

/// <summary>A fixed privileged TUN bridge, with replaceable unprivileged outbound cores (macOS and Windows).</summary>
internal static class MacTunBridge
{
    internal static async Task<CoreConfigContextBuilderAllResult> Build(Config config, ProfileItem node, string? physicalInterface = null)
    {
        var outboundConfig = JsonUtils.DeepCopy(config);
        outboundConfig.TunModeItem.EnableTun = false;
        // The bridge owns the single statistics listener/cache. The outbound process
        // is replaceable without touching routes, DNS or administrator authorization.
        outboundConfig.CoreBasicItem.EnableCacheFile4Sbox = false;
        // The selected-node socket must never be routed back into our privileged TUN.
        // Process-path exclusions alone are insufficient when another route is retained.
        // On Windows the bridge's process-path rule sends our cores directly; there is no interface to bind.
        var bindPhysical = OperatingSystem.IsMacOS();
        if (bindPhysical) physicalInterface ??= await MacVpnConflict.FindPhysicalInterface();
        if (bindPhysical && string.IsNullOrWhiteSpace(physicalInterface))
            throw new InvalidOperationException("No active Wi-Fi or Ethernet connection was found. Connect to a network and try again.");
        outboundConfig.CoreBasicItem.BindInterface = "";
        outboundConfig.CoreBasicItem.SendThrough = "";
        var main = await CoreConfigContextBuilder.Build(outboundConfig, node, includeSubChain: false);
        // Upstream validation strips BindInterface from non-TUN macOS contexts.
        // This split architecture deliberately has TUN in a separate process, so
        // bind the validated outbound context after building it.
        var boundConfig = JsonUtils.DeepCopy(main.Context.AppConfig);
        if (bindPhysical) boundConfig.CoreBasicItem.BindInterface = physicalInterface!;
        main = main with { Context = main.Context with { AppConfig = boundConfig, DisableClashApi = true } };

        var bridgeConfig = JsonUtils.DeepCopy(config);
        bridgeConfig.TunModeItem.EnableTun = true;
        bridgeConfig.CoreBasicItem.EnableCacheFile4Sbox = false;
        bridgeConfig.CoreBasicItem.LogEnabled = false; // captured by the session log; no date-dependent config path
        // The fixed bridge only forwards to loopback. Sniffing here and again in
        // the outbound core delays first packets twice; domain policy belongs there.
        // The generated non-sniffing path still hijacks destination port 53.
        foreach (var inbound in bridgeConfig.Inbound) inbound.SniffingEnabled = false;
        var bridge = await CoreConfigContextBuilder.Build(bridgeConfig, new ProfileItem
        {
            CoreType = ECoreType.sing_box, ConfigType = EConfigType.SOCKS,
            Address = Global.Loopback, Port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks)
        });
        bridge = bridge with { Context = GlobalTrafficPolicy.Apply(bridge.Context) with
        {
            // Own-core sockets bypass TUN; all other public traffic goes to the
            // loopback proxy. The selected node belongs to the replaceable main core.
            StableDnsTags = true,
            ProtectCoreTypeList = [ECoreType.Xray, ECoreType.sing_box],
            ProtectDomainList = []
        }};
        return new(main, bridge);
    }
}
