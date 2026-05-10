using System.Diagnostics;
using MySqlConnector;

namespace LogGenerator.Services;

public record DbResult(bool Success, long ElapsedMs, string Message, IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null);

public class MySqlService
{
    private readonly LogService _log;
    private readonly string? _connectionString;

    public MySqlService(IConfiguration config, LogService log)
    {
        _log = log;
        _connectionString =
            Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
            ?? config.GetConnectionString("MySql");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public string SafeEndpoint
    {
        get
        {
            if (!IsConfigured) return "(no configurada)";
            try
            {
                var b = new MySqlConnectionStringBuilder(_connectionString!);
                return $"{b.Server}:{b.Port}/{b.Database} (user={b.UserID})";
            }
            catch { return "(connection string invalida)"; }
        }
    }

    public async Task<DbResult> TestConnectionAsync()
    {
        if (!IsConfigured)
        {
            _log.LogError("MySQL no configurada", "Falta MYSQL_CONNECTION_STRING o ConnectionStrings:MySql");
            return new(false, 0, "Connection string no configurada");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("SELECT VERSION(), DATABASE(), CURRENT_USER(), @@hostname", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var version = reader.GetString(0);
            var db = reader.IsDBNull(1) ? "(none)" : reader.GetString(1);
            var user = reader.GetString(2);
            var host = reader.GetString(3);
            sw.Stop();

            var msg = $"Conectado a MySQL {version} | host={host} | db={db} | user={user} | {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("MySQL conexion OK", msg);
            return new(true, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, "MySQL TestConnection");
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<DbResult> RunSampleQueryAsync()
    {
        if (!IsConfigured)
            return new(false, 0, "Connection string no configurada");

        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT table_schema, COUNT(*) AS tables
                FROM information_schema.tables
                WHERE table_schema NOT IN ('information_schema','mysql','performance_schema','sys')
                GROUP BY table_schema
                ORDER BY tables DESC
                LIMIT 10";
            await using var cmd = new MySqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>
                {
                    ["table_schema"] = reader.GetValue(0),
                    ["tables"] = reader.GetValue(1)
                };
                rows.Add(row);
            }
            sw.Stop();

            var msg = $"Query OK — {rows.Count} schemas en {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("MySQL query sample", msg);
            return new(true, sw.ElapsedMilliseconds, msg, rows);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogException(ex, "MySQL RunSampleQuery");
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
