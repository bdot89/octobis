using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OctoBis.Scraper;

/// <summary>
/// Deliberately slow, single-threaded HTTP client for the OctoWoW database, with an on-disk cache.
///
/// OctoWoW is a volunteer-run server. One request at a time with a fixed delay between them keeps
/// this well under the load of a single person browsing the site, and the cache means repeated
/// runs cost nothing. Do not parallelise this.
/// </summary>
public sealed class AowowClient : IDisposable
{
    private const string BaseUrl = "https://octowow.st/db/";

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly TimeSpan _delay;
    private readonly Stopwatch _sinceLastRequest = Stopwatch.StartNew();

    public int CacheHits { get; private set; }
    public int NetworkFetches { get; private set; }
    public int RetryCount { get; private set; }
    public int AbandonedPages { get; private set; }

    public AowowClient(string cacheDir, int delayMs = 800)
    {
        _cacheDir = cacheDir;
        _delay = TimeSpan.FromMilliseconds(delayMs);
        Directory.CreateDirectory(_cacheDir);

        // Connections are recycled on a short lifetime. Holding one connection open for thousands of
        // sequential requests made the server progressively slower to answer (seconds per page,
        // while a fresh connection stayed at ~300ms), so the pool is deliberately churned.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "OctoBiS/1.0 (best-in-slot list builder for OctoWoW; contact via the OctoWow forums)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
    }

    public Task<string> GetItemPageAsync(int id) => GetAsync($"?item={id}");

    /// <summary>
    /// Fetches an item page but gives up quickly instead of retrying.
    ///
    /// Render time on this database scales with how many NPCs drop the item, so a page that has not
    /// answered within a few seconds is a world drop on hundreds of loot tables - exactly the items
    /// that are never best in slot. Abandoning them is the intent, not a failure.
    /// </summary>
    public async Task<string> TryGetItemPageAsync(int id, TimeSpan budget)
    {
        var path = Path.Combine(_cacheDir, CacheKey($"?item={id}") + ".html");
        if (File.Exists(path))
        {
            CacheHits++;
            return await File.ReadAllTextAsync(path);
        }

        // Abandoning a page costs the full budget, so remember the decision. --refresh clears these
        // along with everything else if a page needs another chance.
        var skipMarker = path + ".skipped";
        if (File.Exists(skipMarker))
        {
            CacheHits++;
            return "";
        }

        await ThrottleAsync();
        using var cts = new CancellationTokenSource(budget);
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}?item={id}", cts.Token);
            if (!response.IsSuccessStatusCode) return "";

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            await File.WriteAllTextAsync(path, body);
            NetworkFetches++;
            return body;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            AbandonedPages++;
            await File.WriteAllTextAsync(skipMarker, $"abandoned after {budget.TotalSeconds:F0}s on {DateTime.UtcNow:O}");
            return "";
        }
    }
    public Task<string> GetNpcPageAsync(int id) => GetAsync($"?npc={id}");
    public Task<string> GetObjectPageAsync(int id) => GetAsync($"?object={id}");
    public Task<string> GetSearchPageAsync(string term) => GetAsync($"?search={Uri.EscapeDataString(term)}");

    public async Task<string> GetAsync(string query)
    {
        var path = Path.Combine(_cacheDir, CacheKey(query) + ".html");
        if (File.Exists(path))
        {
            CacheHits++;
            return await File.ReadAllTextAsync(path);
        }

        var body = await FetchWithRetryAsync(BaseUrl + query);
        await File.WriteAllTextAsync(path, body);
        NetworkFetches++;
        return body;
    }

    /// <summary>Downloads a binary asset (item icons) straight to disk, skipping any that already exist.</summary>
    public async Task<bool> DownloadAsync(string url, string destination)
    {
        if (File.Exists(destination)) return true;

        await ThrottleAsync();
        try
        {
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var fs = File.Create(destination);
            await response.Content.CopyToAsync(fs);
            NetworkFetches++;
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<string> FetchWithRetryAsync(string url)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync();
            string reason;
            try
            {
                var timer = Stopwatch.StartNew();
                using var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (attempt > 1) Console.WriteLine($"    recovered after {attempt} attempts: {url}");
                    if (timer.Elapsed.TotalSeconds > 3)
                        Console.WriteLine($"    slow fetch {timer.Elapsed.TotalSeconds:F1}s: {url}");
                    return body;
                }

                // 404 is a legitimate answer for an id that does not exist; do not burn retries on it.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return "";

                reason = $"HTTP {(int)response.StatusCode}";
                if (attempt == maxAttempts)
                    throw new HttpRequestException($"{url} returned {reason} after {maxAttempts} attempts");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                reason = ex is TaskCanceledException ? "timeout" : ex.Message;
                if (attempt == maxAttempts) throw;
            }

            RetryCount++;
            Console.WriteLine($"    retry {attempt}/{maxAttempts} ({reason}): {url}");

            // Back off progressively so a struggling server gets room to recover.
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        }
    }

    private async Task ThrottleAsync()
    {
        var remaining = _delay - _sinceLastRequest.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        _sinceLastRequest.Restart();
    }

    private static string CacheKey(string query)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(query)))[..12];
        var readable = new string(query.Where(char.IsLetterOrDigit).ToArray());
        if (readable.Length > 40) readable = readable[..40];
        return $"{readable}_{hash}";
    }

    public void Dispose() => _http.Dispose();
}
