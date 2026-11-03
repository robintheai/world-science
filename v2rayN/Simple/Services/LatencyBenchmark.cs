using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
namespace WorldScience.Services;

/// <summary>Unloaded HTTP response latency through the proxy, not ICMP or cold connection setup.</summary>
public static class LatencyBenchmark
{
    public static async Task<double> Measure(HttpClient client, string url, TimeSpan timeout, CancellationToken cancel, TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var samples = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            budget.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            var started = timeProvider.GetTimestamp();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new HttpRequestException("The latency endpoint did not return HTTP 204.");
            // Consume the empty response and reuse the same connection. The first request warms DNS/TLS.
            await response.Content.CopyToAsync(Stream.Null, budget.Token);
            if (i > 0) samples.Add(timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        samples.Sort();
        return samples[1]; // Median of three; never clamp a genuinely slow path.
    }
}
