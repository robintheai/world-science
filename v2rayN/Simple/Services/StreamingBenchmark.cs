using System.Net.Http.Headers;
namespace WorldScience.Services;
public record BenchmarkResult(long Bytes, TimeSpan Duration)
{
    public double MegabytesPerSecond => Duration.TotalSeconds > 0 ? Bytes / 1_000_000d / Duration.TotalSeconds : 0;
}
public static class StreamingBenchmark
{
    public static async Task<BenchmarkResult> MeasureWithFallback(HttpClient client, IEnumerable<string> urls, TimeSpan duration, long limit, Action<int> progress, CancellationToken cancel, TimeProvider? timeProvider = null)
    {
        Exception? failure = null;
        foreach (var url in urls.Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            try { return await Measure(client, url, duration, limit, progress, cancel, timeProvider); }
            catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException) { failure = ex; }
        }
        throw failure ?? new HttpRequestException("No download test endpoint is available.");
    }
    public static async Task<BenchmarkResult> Measure(HttpClient client, string url, TimeSpan duration, long limit, Action<int> progress, CancellationToken cancel, TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        // Connection setup and first-byte wait have their own budget, outside the transfer measurement.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new HttpRequestException("Compressed test data cannot be measured reliably.");
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is not null && (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || media.Contains("json") || media.Contains("xml")))
            throw new HttpRequestException("The download endpoint returned a page instead of test data.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[64 * 1024];
        var first = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit)), timeout.Token);
        if (first == 0) throw new HttpRequestException("The download endpoint returned no data.");
        progress(first);
        var started = timeProvider.GetTimestamp(); long total = 0;
        timeout.CancelAfter(duration);
        try
        {
            while (total + first < limit)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total - first)), timeout.Token);
                if (count == 0) break;
                total += count; progress(count);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && total > 0) { }
        cancel.ThrowIfCancellationRequested();
        var elapsed = timeProvider.GetElapsedTime(started);
        if (total < 1024 || elapsed < TimeSpan.FromMilliseconds(100))
            throw new HttpRequestException("The download sample was too short to measure reliably.");
        return new(total, elapsed);
    }
}
