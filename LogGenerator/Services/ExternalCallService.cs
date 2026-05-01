using System.Diagnostics;
using Polly;
using Polly.Retry;

namespace LogGenerator.Services;

public record ExternalCallResult(string Service, bool Success, int StatusCode, long ElapsedMs, string Message);

public class ExternalCallService
{
    private readonly HttpClient _http;
    private readonly LogService _log;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    private static readonly Dictionary<string, string> Targets = new()
    {
        ["Google"]    = "https://www.google.com",
        ["Amazon"]    = "https://www.amazon.com",
        ["YouTube"]   = "https://www.youtube.com",
        ["GitHub API"]= "https://api.github.com/repos/dotnet/runtime",
        ["WorldTime"] = "https://worldtimeapi.org/api/timezone/UTC",
        ["IP Info"]   = "https://ipinfo.io/json",
        ["HTTPBin"]   = "https://httpbin.org/get",
    };

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
    }

    public IEnumerable<string> GetServiceNames() => Targets.Keys;

    public async Task<ExternalCallResult> PingAsync(string serviceName)
    {
        if (!Targets.TryGetValue(serviceName, out var url))
            return new(serviceName, false, 0, 0, "Unknown service");

        _log.LogInfo($"Calling {serviceName}", url);
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(url);
            sw.Stop();
            var msg = $"HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms";

            if (resp.IsSuccessStatusCode)
                _log.LogInfo($"{serviceName} responded OK", msg);
            else
                _log.LogWarning($"{serviceName} returned non-2xx", msg);

            return new(serviceName, resp.IsSuccessStatusCode, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            var msg = $"Timeout after {sw.ElapsedMilliseconds}ms";
            _log.LogError($"{serviceName} timed out", msg);
            return new(serviceName, false, 0, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, $"Ping {serviceName}");
            return new(serviceName, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task PingAllParallelAsync()
    {
        _log.LogInfo("Parallel ping started", $"Firing {Targets.Count} requests simultaneously");
        var tasks = Targets.Keys.Select(PingAsync);
        var results = await Task.WhenAll(tasks);
        var ok = results.Count(r => r.Success);
        _log.LogInfo("Parallel ping completed", $"{ok}/{results.Length} services responded successfully");
    }

    public async Task<ExternalCallResult> SimulateTimeoutAsync()
    {
        const string service = "HTTPBin Delay";
        _log.LogInfo("Simulating timeout", "Calling httpbin.org/delay/10 with 2s timeout");
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _http.GetAsync("https://httpbin.org/delay/10", cts.Token);
            sw.Stop();
            return new(service, true, 200, sw.ElapsedMilliseconds, "Unexpected success");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var msg = $"Request cancelled after {sw.ElapsedMilliseconds}ms (timeout=2s)";
            _log.LogError("Timeout simulated successfully", msg);
            return new(service, false, 0, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateHttp500Async()
    {
        const string service = "HTTPBin 500";
        _log.LogInfo("Simulating HTTP 500", "Calling httpbin.org/status/500");
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync("https://httpbin.org/status/500");
            sw.Stop();
            var msg = $"Received HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms";
            _log.LogError("Server error received", msg);
            return new(service, false, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateDnsFailureAsync()
    {
        const string service = "DNS Failure";
        _log.LogInfo("Simulating DNS failure", "Calling nonexistent.invalid.host");
        var sw = Stopwatch.StartNew();
        try
        {
            await _http.GetAsync("https://nonexistent-host-that-does-not-exist.invalid");
            sw.Stop();
            return new(service, false, 0, sw.ElapsedMilliseconds, "Unexpected success");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async Task<ExternalCallResult> SimulateRetryAsync()
    {
        const string service = "Retry Simulation";
        int attempt = 0;
        _log.LogInfo("Starting retry simulation", "Will fail twice then succeed on 3rd attempt");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _retryPolicy.ExecuteAsync(async () =>
            {
                attempt++;
                if (attempt < 3)
                {
                    _log.LogWarning($"Attempt {attempt} — forcing failure for simulation");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
                }
                _log.LogInfo($"Attempt {attempt} — calling real endpoint");
                return await _http.GetAsync("https://httpbin.org/status/200");
            });
            sw.Stop();
            var msg = $"Succeeded on attempt {attempt} after {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("Retry simulation completed", msg);
            return new(service, true, 200, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, service);
            return new(service, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
