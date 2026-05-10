using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Datadog.Trace;
using Polly;
using Polly.Retry;

namespace LogGenerator.Services;

public record ExternalCallResult(string Service, bool Success, int StatusCode, long ElapsedMs, string Message);

public class ExternalCallService
{
    private readonly HttpClient _http;
    private readonly LogService _log;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private static readonly Random _rng = new();

    private readonly Dictionary<string, Func<Task<List<ExternalCallResult>>>> _flows;

    public ExternalCallService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;

        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (outcome, delay, attempt, _) =>
                    _log.LogWarning($"Retry attempt {attempt}/3", $"Waiting {delay.TotalSeconds:F1}s — {outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString()}")
            );

        _flows = new()
        {
            ["Posts Flow"]   = RunPostsFlowAsync,
            ["Users Flow"]   = RunUsersFlowAsync,
            ["GitHub Flow"]  = RunGitHubFlowAsync,
            ["Crypto Flow"]  = RunCryptoFlowAsync,
            ["HTTPBin Flow"] = RunHttpBinFlowAsync,
            ["Quote Flow"]   = RunQuoteFlowAsync,
        };
    }

    public IEnumerable<string> GetServiceNames() => _flows.Keys;

    public async Task<List<ExternalCallResult>> RunFlowAsync(string flowName)
    {
        if (!_flows.TryGetValue(flowName, out var flow))
            return new() { new(flowName, false, 0, 0, "Unknown flow") };

        using var scope = Tracer.Instance.StartActive("external.flow");
        scope.Span.ResourceName = flowName;
        scope.Span.SetTag(Tags.SpanKind, SpanKinds.Internal);
        scope.Span.SetTag("component", "LogGenerator.ExternalCallService");
        scope.Span.SetTag("flow.name", flowName);

        var sw = Stopwatch.StartNew();
        try
        {
            var results = await flow();
            sw.Stop();
            scope.Span.SetTag("flow.steps", results.Count.ToString());
            scope.Span.SetTag("flow.success_count", results.Count(r => r.Success).ToString());
            scope.Span.SetTag("flow.fail_count", results.Count(r => !r.Success).ToString());
            scope.Span.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());
            _log.LogInfo($"{flowName} completed", $"{results.Count} steps in {sw.ElapsedMilliseconds}ms");
            return results;
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, $"Flow {flowName}");
            return new() { new(flowName, false, 0, sw.ElapsedMilliseconds, ex.Message) };
        }
    }

    public async Task RunAllFlowsParallelAsync()
    {
        using var scope = Tracer.Instance.StartActive("external.all_flows");
        scope.Span.ResourceName = "all flows (parallel)";
        scope.Span.SetTag(Tags.SpanKind, SpanKinds.Internal);
        scope.Span.SetTag("flow.count", _flows.Count.ToString());

        _log.LogInfo("Parallel flows started", $"Firing {_flows.Count} flows in parallel");
        var tasks = _flows.Keys.Select(RunFlowAsync);
        var all = await Task.WhenAll(tasks);
        var total = all.Sum(r => r.Count);
        var ok = all.SelectMany(r => r).Count(x => x.Success);
        scope.Span.SetTag("flow.total_steps", total.ToString());
        scope.Span.SetTag("flow.total_success", ok.ToString());
        _log.LogInfo("Parallel flows completed", $"{ok}/{total} HTTP calls succeeded across {_flows.Count} flows");
    }

    // ===== FLOWS =====

    private async Task<List<ExternalCallResult>> RunPostsFlowAsync()
    {
        const string svc = "JSONPlaceholder";
        var results = new List<ExternalCallResult>();

        var listResp = await CallAsync(svc, "GET", "https://jsonplaceholder.typicode.com/posts?_limit=10", results);
        int postId = 1;
        if (listResp is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(listResp);
                var items = doc.RootElement.EnumerateArray().ToList();
                if (items.Count > 0)
                    postId = items[_rng.Next(items.Count)].GetProperty("id").GetInt32();
            }
            catch { }
        }

        await CallAsync(svc, "GET", $"https://jsonplaceholder.typicode.com/posts/{postId}", results);
        await CallAsync(svc, "GET", $"https://jsonplaceholder.typicode.com/posts/{postId}/comments", results);

        var body = JsonSerializer.Serialize(new { title = $"trace-demo-{Guid.NewGuid():N}".Substring(0, 24), body = "log generator demo", userId = 1 });
        await CallAsync(svc, "POST", "https://jsonplaceholder.typicode.com/posts", results, body);

        return results;
    }

    private async Task<List<ExternalCallResult>> RunUsersFlowAsync()
    {
        const string svc = "JSONPlaceholder";
        var results = new List<ExternalCallResult>();

        var users = await CallAsync(svc, "GET", "https://jsonplaceholder.typicode.com/users", results);
        int userId = 1;
        if (users is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(users);
                var items = doc.RootElement.EnumerateArray().ToList();
                if (items.Count > 0)
                    userId = items[_rng.Next(items.Count)].GetProperty("id").GetInt32();
            }
            catch { }
        }

        await CallAsync(svc, "GET", $"https://jsonplaceholder.typicode.com/users/{userId}", results);
        await CallAsync(svc, "GET", $"https://jsonplaceholder.typicode.com/users/{userId}/todos", results);
        await CallAsync(svc, "GET", $"https://jsonplaceholder.typicode.com/users/{userId}/albums", results);

        return results;
    }

    private async Task<List<ExternalCallResult>> RunGitHubFlowAsync()
    {
        const string svc = "GitHub";
        var repos = new[] { "dotnet/runtime", "dotnet/aspnetcore", "DataDog/dd-trace-dotnet", "mysql-net/MySqlConnector", "App-vNext/Polly" };
        var repo = repos[_rng.Next(repos.Length)];
        var results = new List<ExternalCallResult>();

        await CallAsync(svc, "GET", $"https://api.github.com/repos/{repo}", results);
        await CallAsync(svc, "GET", $"https://api.github.com/repos/{repo}/contributors?per_page=5", results);
        await CallAsync(svc, "GET", $"https://api.github.com/repos/{repo}/issues?state=open&per_page=5", results);
        await CallAsync(svc, "GET", $"https://api.github.com/repos/{repo}/releases/latest", results);

        return results;
    }

    private async Task<List<ExternalCallResult>> RunCryptoFlowAsync()
    {
        const string svc = "CoinGecko";
        var results = new List<ExternalCallResult>();

        await CallAsync(svc, "GET", "https://api.coingecko.com/api/v3/ping", results);
        await CallAsync(svc, "GET", "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=10&page=1", results);

        var coins = new[] { "bitcoin", "ethereum", "solana", "cardano", "polkadot" };
        var coin = coins[_rng.Next(coins.Length)];
        await CallAsync(svc, "GET", $"https://api.coingecko.com/api/v3/simple/price?ids={coin}&vs_currencies=usd,eur,mxn", results);
        await CallAsync(svc, "GET", $"https://api.coingecko.com/api/v3/coins/{coin}/market_chart?vs_currency=usd&days=7", results);

        return results;
    }

    private async Task<List<ExternalCallResult>> RunHttpBinFlowAsync()
    {
        const string svc = "HTTPBin";
        var results = new List<ExternalCallResult>();

        await CallAsync(svc, "GET", "https://httpbin.org/uuid", results);
        await CallAsync(svc, "GET", "https://httpbin.org/ip", results);
        await CallAsync(svc, "GET", "https://httpbin.org/headers", results);
        var body = JsonSerializer.Serialize(new { source = "LogGenerator", ts = DateTime.UtcNow });
        await CallAsync(svc, "POST", "https://httpbin.org/post", results, body);
        await CallAsync(svc, "PUT", "https://httpbin.org/put", results, body);
        return results;
    }

    private async Task<List<ExternalCallResult>> RunQuoteFlowAsync()
    {
        const string svc = "Quotable";
        var results = new List<ExternalCallResult>();

        var listResp = await CallAsync(svc, "GET", "https://api.quotable.io/quotes/random?limit=3", results);
        string? tag = null;
        if (listResp is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(listResp);
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("tags", out var tags))
                {
                    var tagList = tags.EnumerateArray().ToList();
                    if (tagList.Count > 0) tag = tagList[0].GetString();
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(tag))
            await CallAsync(svc, "GET", $"https://api.quotable.io/quotes?tags={Uri.EscapeDataString(tag)}&limit=5", results);

        await CallAsync(svc, "GET", "https://api.quotable.io/authors?limit=5", results);
        return results;
    }

    // ===== HTTP CALL HELPER WITH SPANS =====

    private async Task<string?> CallAsync(string peerService, string method, string url, List<ExternalCallResult> results, string? jsonBody = null)
    {
        using var scope = StartHttpSpan(peerService, method, url);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (jsonBody is not null)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                scope.Span.SetTag("http.request.body.size", jsonBody.Length.ToString());
            }
            var resp = await _http.SendAsync(req);
            sw.Stop();
            var bodyText = await resp.Content.ReadAsStringAsync();
            scope.Span.SetTag("http.status_code", ((int)resp.StatusCode).ToString());
            scope.Span.SetTag("http.response.body.size", bodyText.Length.ToString());
            scope.Span.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());
            var msg = $"HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms ({bodyText.Length}B)";

            if (resp.IsSuccessStatusCode)
            {
                _log.LogInfo($"{peerService} {method} {url}", msg);
            }
            else
            {
                scope.Span.Error = true;
                _log.LogWarning($"{peerService} {method} non-2xx", msg);
            }

            results.Add(new($"{peerService} {method}", resp.IsSuccessStatusCode, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg));
            return resp.IsSuccessStatusCode ? bodyText : null;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            scope.Span.Error = true;
            scope.Span.SetTag("error.type", "timeout");
            var msg = $"Timeout after {sw.ElapsedMilliseconds}ms";
            _log.LogError($"{peerService} timed out", msg);
            results.Add(new($"{peerService} {method}", false, 0, sw.ElapsedMilliseconds, msg));
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, $"{peerService} {method} {url}");
            results.Add(new($"{peerService} {method}", false, 0, sw.ElapsedMilliseconds, ex.Message));
            return null;
        }
    }

    private static IScope StartHttpSpan(string peerService, string method, string url)
    {
        var uri = TryParseUri(url);
        var scope = Tracer.Instance.StartActive("http.client.request");
        var span = scope.Span;
        span.Type = SpanTypes.Http;
        span.ResourceName = $"{method} {uri?.AbsolutePath ?? url}";
        span.SetTag(Tags.SpanKind, SpanKinds.Client);
        span.SetTag("component", "HttpClient");
        span.SetTag("http.method", method);
        span.SetTag("http.url", url);
        if (uri is not null)
        {
            span.SetTag("http.host", uri.Host);
            span.SetTag("http.scheme", uri.Scheme);
            span.SetTag("peer.hostname", uri.Host);
            span.SetTag("out.host", uri.Host);
            span.SetTag("out.port", uri.Port.ToString());
            span.SetTag("network.destination.name", uri.Host);
            span.SetTag("network.destination.port", uri.Port.ToString());
        }
        span.SetTag("peer.service", peerService);
        return scope;
    }

    private static Uri? TryParseUri(string url)
    {
        try { return new Uri(url); } catch { return null; }
    }

    // ===== ERROR SCENARIO SIMULATIONS (unchanged behavior, instrumented) =====

    public async Task<ExternalCallResult> SimulateTimeoutAsync()
    {
        const string service = "HTTPBin Delay";
        const string url = "https://httpbin.org/delay/10";
        using var scope = StartHttpSpan(service, "GET", url);
        scope.Span.SetTag("simulation", "timeout");
        scope.Span.SetTag("timeout.ms", "2000");

        _log.LogInfo("Simulating timeout", "Calling httpbin.org/delay/10 with 2s timeout");
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _http.GetAsync(url, cts.Token);
            sw.Stop();
            return new(service, true, 200, sw.ElapsedMilliseconds, "Unexpected success");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            scope.Span.Error = true;
            scope.Span.SetTag("error.type", "timeout");
            var msg = $"Request cancelled after {sw.ElapsedMilliseconds}ms (timeout=2s)";
            _log.LogError("Timeout simulated successfully", msg);
            return new(service, false, 0, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateHttp500Async()
    {
        const string service = "HTTPBin 500";
        const string url = "https://httpbin.org/status/500";
        using var scope = StartHttpSpan(service, "GET", url);
        scope.Span.SetTag("simulation", "http_500");

        _log.LogInfo("Simulating HTTP 500", "Calling httpbin.org/status/500");
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(url);
            sw.Stop();
            scope.Span.Error = true;
            scope.Span.SetTag("http.status_code", ((int)resp.StatusCode).ToString());
            var msg = $"Received HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms";
            _log.LogError("Server error received", msg);
            return new(service, false, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateDnsFailureAsync()
    {
        const string service = "DNS Failure";
        const string url = "https://nonexistent-host-that-does-not-exist.invalid";
        using var scope = StartHttpSpan(service, "GET", url);
        scope.Span.SetTag("simulation", "dns_failure");

        _log.LogInfo("Simulating DNS failure", "Calling nonexistent.invalid.host");
        var sw = Stopwatch.StartNew();
        try
        {
            await _http.GetAsync(url);
            sw.Stop();
            return new(service, false, 0, sw.ElapsedMilliseconds, "Unexpected success");
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateRetryAsync()
    {
        const string service = "Retry Simulation";
        const string url = "https://httpbin.org/status/200";

        using var scope = Tracer.Instance.StartActive("http.retry_flow");
        scope.Span.Type = SpanTypes.Http;
        scope.Span.ResourceName = "retry flow (3 attempts)";
        scope.Span.SetTag(Tags.SpanKind, SpanKinds.Client);
        scope.Span.SetTag("component", "Polly.Retry");
        scope.Span.SetTag("retry.max_attempts", "3");

        int attempt = 0;
        _log.LogInfo("Starting retry simulation", "Will fail twice then succeed on 3rd attempt");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _retryPolicy.ExecuteAsync(async () =>
            {
                attempt++;
                using var attemptScope = StartHttpSpan(service, "GET", url);
                attemptScope.Span.SetTag("retry.attempt", attempt.ToString());
                if (attempt < 3)
                {
                    attemptScope.Span.Error = true;
                    attemptScope.Span.SetTag("http.status_code", "503");
                    attemptScope.Span.SetTag("simulation", "forced_failure");
                    _log.LogWarning($"Attempt {attempt} — forcing failure for simulation");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
                }
                _log.LogInfo($"Attempt {attempt} — calling real endpoint");
                var resp = await _http.GetAsync(url);
                attemptScope.Span.SetTag("http.status_code", ((int)resp.StatusCode).ToString());
                return resp;
            });
            sw.Stop();
            scope.Span.SetTag("retry.final_attempt", attempt.ToString());
            var msg = $"Succeeded on attempt {attempt} after {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("Retry simulation completed", msg);
            return new(service, true, 200, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
