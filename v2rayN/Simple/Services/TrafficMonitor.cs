using System.Net.WebSockets;
using System.Text.Json;

namespace WorldScience.Services;

/// <summary>Read local core telemetry without the system proxy or rounded upstream accounting units.</summary>
internal sealed class TrafficMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    public void Start(Func<bool> active, Action<long, long> sample) => _running ??= Task.Run(() => Run(active, sample));
    public void Dispose() => _stop.Cancel();

    private async Task Run(Func<bool> active, Action<long, long> sample)
    {
        using var transport = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
        using var http = new HttpClient(transport, false) { Timeout = TimeSpan.FromSeconds(3) };
        using var invoker = new HttpMessageInvoker(transport, false);
        var token = _stop.Token;
        long previousUp = 0, previousDown = 0;
        string previousEndpoint = "";
        var previousTime = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!active()) { previousEndpoint = ""; await Task.Delay(1000, token); continue; }
                if (!AppManager.Instance.Config.TunModeItem.EnableTun && AppManager.Instance.RunningCoreType == ECoreType.Xray)
                {
                    var endpoint = $"http://127.0.0.1:{AppManager.Instance.StatePort}/debug/vars";
                    using var response = await http.GetAsync(endpoint, token);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    long up = 0, down = 0;
                    foreach (var outbound in json.RootElement.GetProperty("stats").GetProperty("outbound").EnumerateObject())
                    {
                        if (!outbound.Name.StartsWith(Global.ProxyTag) && outbound.Name != Global.DirectTag) continue;
                        up += outbound.Value.GetProperty("uplink").GetInt64();
                        down += outbound.Value.GetProperty("downlink").GetInt64();
                    }
                    var elapsed = Stopwatch.GetElapsedTime(previousTime).TotalSeconds;
                    if (previousEndpoint == endpoint && elapsed > 0 && up >= previousUp && down >= previousDown)
                        sample((long)((up - previousUp) / elapsed), (long)((down - previousDown) / elapsed));
                    previousUp = up; previousDown = down; previousTime = Stopwatch.GetTimestamp(); previousEndpoint = endpoint;
                    await Task.Delay(1000, token);
                }
                else
                {
                    previousEndpoint = "";
                    var endpoint = new Uri($"ws://127.0.0.1:{AppManager.Instance.StatePort2}/traffic");
                    await ReadTraffic(endpoint, invoker, sample, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // No raw core configuration or account information is recorded.
                if (active()) Logging.SaveLog("Simple.Traffic: " + ex.GetType().Name);
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    internal static async Task ReadTraffic(Uri endpoint, HttpMessageInvoker transport, Action<long, long> sample, CancellationToken token)
    {
        using var ws = new ClientWebSocket();
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.ConnectAsync(endpoint, transport, startup.Token);
        }
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(4));
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token);
                if (part.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, part.Count);
                if (message.Length > 65536) throw new InvalidDataException("Traffic response too large");
            } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(message.ToArray());
            sample(json.RootElement.GetProperty("up").GetInt64(), json.RootElement.GetProperty("down").GetInt64());
        }
    }
}
