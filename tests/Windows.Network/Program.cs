using System.Net;
using System.Net.Sockets;
using ServiceLib;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Handler;
using WorldScience.Services;

// Run only inside a disposable Windows VM: this exercises real Wintun routes/DNS.
if (!OperatingSystem.IsWindows() || !args.Contains("--disposable-vm"))
    throw new InvalidOperationException("Requires Windows and --disposable-vm.");
var root = Environment.GetEnvironmentVariable("WORLD_SCIENCE_DATA_DIR");
if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Isolated data directory required.");
Environment.SetEnvironmentVariable(Global.LocalAppData,"1");
if (!AppManager.Instance.InitApp()) throw new Exception("Init failed");
AppManager.Instance.InitComponents();
var client = new ClientController();
try
{
    await client.Initialize();
    if(args.Contains("--legacy-native")) client.Config.TunModeItem.EnableLegacyProtect=false;
    await client.Import("socks://10.0.2.2:18766#Germany%20Windows%20fixture");
    var node = client.Nodes.Single();
    node.Profile.CoreType = args.Contains("--sing-box") ? ECoreType.sing_box : ECoreType.Xray;
    await ConfigHandler.AddServer(AppManager.Instance.Config,node.Profile);
    Console.WriteLine("Core="+node.Profile.CoreType+" Admin="+ServiceLib.Common.Utils.IsAdministrator());
    for (int attempt=1; attempt<=2; attempt++)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        await client.Toggle();
        Console.WriteLine($"Connect {attempt}: {client.Connected}; {client.UserNotice}; {watch.Elapsed}");
        if (!client.Connected) throw new Exception("Connect failed: "+client.Notice);
        using var http = new HttpClient(new SocketsHttpHandler {UseProxy=false}){Timeout=TimeSpan.FromSeconds(15)};
        using var reply=await http.GetAsync("https://cp.cloudflare.com/generate_204");
        if(reply.StatusCode!=HttpStatusCode.NoContent)throw new Exception("Unexpected tunnel response "+reply.StatusCode);
        Console.WriteLine("PASS system HTTPS through TUN");
        // IPv6 DNS transport must remain usable even when website AAAA answers are disabled.
        using var dnsTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dnsSocket=new TcpClient(AddressFamily.InterNetworkV6);
        await dnsSocket.ConnectAsync(IPAddress.Parse("fc00::172:18:0:2"),53,dnsTimeout.Token);
        byte[] question=[0x31,0x31,1,0,0,1,0,0,0,0,0,0,2,(byte)'c',(byte)'p',10,(byte)'c',(byte)'l',(byte)'o',(byte)'u',(byte)'d',(byte)'f',(byte)'l',(byte)'a',(byte)'r',(byte)'e',3,(byte)'c',(byte)'o',(byte)'m',0,0,1,0,1];
        var stream=dnsSocket.GetStream();
        await stream.WriteAsync(new byte[]{0,(byte)question.Length},dnsTimeout.Token);
        await stream.WriteAsync(question,dnsTimeout.Token);
        var prefix=new byte[2];await stream.ReadExactlyAsync(prefix,dnsTimeout.Token);
        var answer=new byte[prefix[0]*256+prefix[1]];await stream.ReadExactlyAsync(answer,dnsTimeout.Token);
        if(answer.Length<12 || (answer[3]&15)!=0 || (answer[6]==0&&answer[7]==0))throw new Exception("IPv6 TCP DNS did not return an A answer");
        Console.WriteLine("PASS IPv6 TCP DNS resolver returns IPv4 answers");
        if (!client.Config.TunModeItem.EnableLegacyProtect) throw new Exception("Legacy native TUN was not normalized");
        Console.WriteLine("PASS Windows uses the validated TUN backend");
        await client.Toggle();
        if(client.Connected)throw new Exception("Disconnect failed");
        using(var listenerCheck=new TcpClient())
        {
            try { await listenerCheck.ConnectAsync(IPAddress.Loopback,AppManager.Instance.GetLocalPort(EInboundProtocol.socks));throw new Exception("Disconnect left the core listener running"); }
            catch(SocketException) { }
        }
        Console.WriteLine("PASS disconnect");
    }
    Console.WriteLine("PASS Windows connect/reconnect");
}
finally { await CoreManager.Instance.CoreStop(); }
