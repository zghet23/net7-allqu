using System.Collections.Concurrent;

namespace LogGenerator.Services;

public record LogEntry(DateTime Timestamp, string Level, string Message, string? Detail = null);

public class LogService
{
    private readonly ILogger<LogService> _logger;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 200;

    public event Action? OnLogsChanged;

    public LogService(ILogger<LogService> logger) => _logger = logger;

    public IEnumerable<LogEntry> GetEntries() => _entries.Reverse();

    public void LogInfo(string message, string? detail = null)
    {
        _logger.LogInformation("{Message} {Detail}", message, detail);
        Append("INFO", message, detail);
    }

    public void LogWarning(string message, string? detail = null)
    {
        _logger.LogWarning("{Message} {Detail}", message, detail);
        Append("WARNING", message, detail);
    }

    public void LogError(string message, string? detail = null)
    {
        _logger.LogError("{Message} {Detail}", message, detail);
        Append("ERROR", message, detail);
    }

    public void LogDebug(string message, string? detail = null)
    {
        _logger.LogDebug("{Message} {Detail}", message, detail);
        Append("DEBUG", message, detail);
    }

    public void LogCritical(string message, string? detail = null)
    {
        _logger.LogCritical("{Message} {Detail}", message, detail);
        Append("CRITICAL", message, detail);
    }

    public void LogException(Exception ex, string context)
    {
        _logger.LogError(ex, "Exception in {Context}", context);
        Append("ERROR", $"Exception in {context}: {ex.GetType().Name}", ex.Message);
    }

    private void Append(string level, string message, string? detail)
    {
        _entries.Enqueue(new LogEntry(DateTime.UtcNow, level, message, detail));
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
        OnLogsChanged?.Invoke();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        OnLogsChanged?.Invoke();
    }
}
