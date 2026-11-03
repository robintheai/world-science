using System.Net;

namespace WorldScience.Services;

/// <summary>Independent endpoints share one deadline; a slow site cannot delay a healthy one.</summary>
internal static class ConnectivityProbe
{
    internal static async Task WaitUntilReady(Func<CancellationToken, Task> probe, TimeSpan budget, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { await probe(deadline.Token); return; }
            catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(250, deadline.Token);
        }
    }

    internal static async Task Check(HttpClient http, IEnumerable<string> urls, TimeSpan budget, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        var pending = urls.Distinct().Select(async url =>
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new HttpRequestException("Connectivity endpoint did not return HTTP 204.");
        }).ToList();
        var all = pending.ToArray();
        Exception? failure = null;
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                try { await finished; token.ThrowIfCancellationRequested(); return; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failure = ex; }
            }
            token.ThrowIfCancellationRequested();
            throw new HttpRequestException("No connectivity endpoint responded successfully.", failure);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await Task.WhenAll(all); } catch { /* Observe losing requests, including cancellation. */ }
        }
    }
}
