using System.Diagnostics;
using Datadog.Trace;
using MySqlConnector;

namespace LogGenerator.Services;

public record DbResult(bool Success, long ElapsedMs, string Message, IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null);

public class MySqlService
{
    private readonly LogService _log;
    private readonly string? _connectionString;
    private readonly string _peerHost;
    private readonly int _peerPort;
    private readonly string _peerDb;
    private static readonly Random _rng = new();

    public MySqlService(IConfiguration config, LogService log)
    {
        _log = log;
        _connectionString =
            Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
            ?? config.GetConnectionString("MySql");

        _peerHost = "(unset)"; _peerPort = 3306; _peerDb = "(unset)";
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                var b = new MySqlConnectionStringBuilder(_connectionString);
                _peerHost = b.Server ?? "(unset)";
                _peerPort = (int)(b.Port == 0 ? 3306 : b.Port);
                _peerDb = b.Database ?? "(unset)";
            }
            catch { }
        }
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

    private IScope StartDbSpan(string operation, string resource, string statement)
    {
        var scope = Tracer.Instance.StartActive("mysql.query");
        var span = scope.Span;
        span.Type = SpanTypes.Sql;
        span.ResourceName = resource;
        span.SetTag(Tags.SpanKind, SpanKinds.Client);
        span.SetTag("component", "MySqlConnector");
        span.SetTag("db.system", "mysql");
        span.SetTag("db.name", _peerDb);
        span.SetTag("db.statement", statement);
        span.SetTag("db.operation", operation);
        span.SetTag("peer.service", $"mysql-{_peerDb}");
        span.SetTag("peer.hostname", _peerHost);
        span.SetTag("out.host", _peerHost);
        span.SetTag("out.port", _peerPort.ToString());
        span.SetTag("network.destination.name", _peerHost);
        span.SetTag("network.destination.port", _peerPort.ToString());
        return scope;
    }

    public async Task<DbResult> TestConnectionAsync()
    {
        if (!IsConfigured)
        {
            _log.LogError("MySQL no configurada", "Falta MYSQL_CONNECTION_STRING o ConnectionStrings:MySql");
            return new(false, 0, "Connection string no configurada");
        }

        using var scope = StartDbSpan("test", "SELECT VERSION()", "SELECT VERSION(), DATABASE(), CURRENT_USER(), @@hostname");
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

            scope.Span.SetTag("db.version", version);
            scope.Span.SetTag("db.user", user);
            scope.Span.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());

            var msg = $"Conectado a MySQL {version} | host={host} | db={db} | user={user} | {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("MySQL conexion OK", msg);
            return new(true, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, "MySQL TestConnection");
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task<DbResult> RunSampleQueryAsync() =>
        RunReadAsync("schemas_overview", "SELECT schemas overview", @"
            SELECT table_schema, COUNT(*) AS tables
            FROM information_schema.tables
            WHERE table_schema NOT IN ('information_schema','mysql','performance_schema','sys')
            GROUP BY table_schema
            ORDER BY tables DESC
            LIMIT 10");

    public Task<DbResult> GetTopCustomersByRevenueAsync(int days = 30, int limit = 10) =>
        RunReadAsync("top_customers", "SELECT top customers by revenue", @"
            SELECT c.id, c.email, c.country, c.segment,
                   COUNT(DISTINCT o.id)            AS orders,
                   SUM(o.total_cents) / 100        AS revenue
            FROM customers c
            JOIN orders o ON o.customer_id = c.id
            WHERE o.created_at >= NOW() - INTERVAL @days DAY
              AND o.status IN ('paid','shipped','delivered')
            GROUP BY c.id, c.email, c.country, c.segment
            ORDER BY revenue DESC
            LIMIT @limit",
            tags: new() { ["query.window_days"] = days, ["query.limit"] = limit },
            parameters: new[] { new MySqlParameter("@days", days), new MySqlParameter("@limit", limit) });

    public Task<DbResult> GetOrdersByStatusAsync(string status = "paid") =>
        RunReadAsync("orders_by_status", $"SELECT orders WHERE status={status}", @"
            SELECT id, customer_id, total_cents, created_at
            FROM orders
            WHERE status = @status
            ORDER BY created_at DESC
            LIMIT 25",
            tags: new() { ["query.status"] = status },
            parameters: new[] { new MySqlParameter("@status", status) });

    public Task<DbResult> GetProductCatalogByCategoryAsync(string? category = null)
    {
        if (category is null)
        {
            return RunReadAsync("catalog_agg", "SELECT catalog aggregated by category", @"
                SELECT category, COUNT(*) AS items, AVG(price_cents)/100 AS avg_price
                FROM products GROUP BY category ORDER BY items DESC");
        }
        return RunReadAsync("catalog_by_category", $"SELECT products WHERE category={category}", @"
            SELECT id, sku, name, price_cents/100 AS price, stock
            FROM products WHERE category = @cat ORDER BY price_cents DESC LIMIT 20",
            tags: new() { ["query.category"] = category },
            parameters: new[] { new MySqlParameter("@cat", category) });
    }

    public Task<DbResult> SearchReviewsAsync(string term) =>
        RunReadAsync("search_reviews", $"SELECT reviews WHERE comment LIKE %{term}%", @"
            SELECT r.id, r.product_id, p.sku, p.name, r.rating, LEFT(r.comment, 80) AS preview, r.created_at
            FROM product_reviews r
            JOIN products p ON p.id = r.product_id
            WHERE r.comment LIKE @term
            ORDER BY r.created_at DESC
            LIMIT 25",
            tags: new() { ["query.term"] = term, ["query.pattern"] = "LIKE %term%", ["query.expected"] = "slow_full_scan" },
            parameters: new[] { new MySqlParameter("@term", $"%{term}%") });

    public Task<DbResult> RunHeavyReportAsync() =>
        RunReadAsync("heavy_report", "SELECT revenue by country and category", @"
            SELECT c.country,
                   p.category,
                   COUNT(DISTINCT o.id)              AS orders,
                   SUM(oi.quantity)                  AS units,
                   ROUND(SUM(oi.quantity * oi.unit_price_cents)/100, 2) AS revenue
            FROM orders o
            JOIN customers c    ON c.id = o.customer_id
            JOIN order_items oi ON oi.order_id = o.id
            JOIN products p     ON p.id = oi.product_id
            WHERE o.status IN ('paid','shipped','delivered')
            GROUP BY c.country, p.category
            ORDER BY revenue DESC",
            tags: new() { ["query.joins"] = 3, ["query.expected"] = "heavy_aggregation" });

    public async Task<DbResult> CreateRandomOrderAsync()
    {
        if (!IsConfigured) return new(false, 0, "Connection string no configurada");

        using var scope = Tracer.Instance.StartActive("checkout.create_order");
        var rootSpan = scope.Span;
        rootSpan.Type = SpanTypes.Custom;
        rootSpan.ResourceName = "checkout.create_order";
        rootSpan.SetTag(Tags.SpanKind, SpanKinds.Internal);
        rootSpan.SetTag("component", "LogGenerator.Checkout");

        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var customerId = _rng.Next(1, 2001);
            var nItems = _rng.Next(1, 6);
            rootSpan.SetTag("customer.id", customerId);
            rootSpan.SetTag("order.item_count", nItems);

            long orderId;
            using (var s = StartDbSpan("insert", "INSERT order", "INSERT INTO orders (customer_id, status, total_cents) VALUES (?, 'pending', 0)"))
            {
                s.Span.SetTag("customer.id", customerId);
                await using var ins = new MySqlCommand(
                    "INSERT INTO orders (customer_id, status, total_cents) VALUES (@cid, 'pending', 0)", conn, tx);
                ins.Parameters.AddWithValue("@cid", customerId);
                await ins.ExecuteNonQueryAsync();
                await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn, tx);
                orderId = Convert.ToInt64(await idCmd.ExecuteScalarAsync());
                s.Span.SetTag("order.id", orderId);
            }
            rootSpan.SetTag("order.id", orderId);

            long total = 0;
            for (int i = 0; i < nItems; i++)
            {
                var pid = _rng.Next(1, 501);
                var qty = _rng.Next(1, 4);

                long price;
                using (var s = StartDbSpan("select", "SELECT product price", "SELECT price_cents FROM products WHERE id=?"))
                {
                    s.Span.SetTag("product.id", pid);
                    await using var priceCmd = new MySqlCommand("SELECT price_cents FROM products WHERE id = @pid", conn, tx);
                    priceCmd.Parameters.AddWithValue("@pid", pid);
                    price = Convert.ToInt64(await priceCmd.ExecuteScalarAsync());
                }

                using (var s = StartDbSpan("insert", "INSERT order_item", "INSERT INTO order_items (order_id, product_id, quantity, unit_price_cents) VALUES (?,?,?,?)"))
                {
                    s.Span.SetTag("order.id", orderId);
                    s.Span.SetTag("product.id", pid);
                    s.Span.SetTag("item.quantity", qty);
                    await using var oiCmd = new MySqlCommand(
                        "INSERT INTO order_items (order_id, product_id, quantity, unit_price_cents) VALUES (@oid, @pid, @qty, @unit)",
                        conn, tx);
                    oiCmd.Parameters.AddWithValue("@oid", orderId);
                    oiCmd.Parameters.AddWithValue("@pid", pid);
                    oiCmd.Parameters.AddWithValue("@qty", qty);
                    oiCmd.Parameters.AddWithValue("@unit", price);
                    await oiCmd.ExecuteNonQueryAsync();
                }

                total += qty * price;
            }

            using (var s = StartDbSpan("update", "UPDATE order finalize", "UPDATE orders SET total_cents=?, status='paid' WHERE id=?"))
            {
                s.Span.SetTag("order.id", orderId);
                s.Span.SetTag("order.total_cents", total);
                await using var up = new MySqlCommand(
                    "UPDATE orders SET total_cents = @t, status = 'paid' WHERE id = @oid", conn, tx);
                up.Parameters.AddWithValue("@t", total);
                up.Parameters.AddWithValue("@oid", orderId);
                await up.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            sw.Stop();
            rootSpan.SetTag("order.total_cents", total);
            rootSpan.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());

            var msg = $"Order #{orderId} creado | customer={customerId} | items={nItems} | total={total/100m:0.00} | {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("Order created", msg);
            return new(true, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            rootSpan.SetException(ex);
            _log.LogException(ex, "MySQL CreateRandomOrder");
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<DbResult> WriteEventLogAsync(string level, string message)
    {
        if (!IsConfigured) return new(false, 0, "Connection string no configurada");
        using var scope = StartDbSpan("insert", "INSERT events_log", "INSERT INTO events_log (level, category, message, trace_id) VALUES (?,?,?,?)");
        scope.Span.SetTag("log.level", level);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(
                "INSERT INTO events_log (level, category, message, trace_id) VALUES (@lvl, @cat, @msg, @tid)", conn);
            cmd.Parameters.AddWithValue("@lvl", level);
            cmd.Parameters.AddWithValue("@cat", "app.demo");
            cmd.Parameters.AddWithValue("@msg", message);
            cmd.Parameters.AddWithValue("@tid", Guid.NewGuid().ToString("N"));
            await cmd.ExecuteNonQueryAsync();
            sw.Stop();
            scope.Span.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());
            return new(true, sw.ElapsedMilliseconds, $"event_log row inserted in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, "MySQL WriteEventLog");
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<DbResult> RunRandomQueryAsync()
    {
        var pick = _rng.Next(0, 6);
        return pick switch
        {
            0 => await GetTopCustomersByRevenueAsync(30, 10),
            1 => await GetOrdersByStatusAsync(new[] { "pending","paid","shipped","delivered","cancelled" }[_rng.Next(5)]),
            2 => await GetProductCatalogByCategoryAsync(null),
            3 => await GetProductCatalogByCategoryAsync(new[] { "electronics","books","home","fashion","sports","toys" }[_rng.Next(6)]),
            4 => await SearchReviewsAsync(new[] { "great","slow","broke","perfect","average","love" }[_rng.Next(6)]),
            _ => await RunHeavyReportAsync(),
        };
    }

    private async Task<DbResult> RunReadAsync(
        string operation,
        string resource,
        string sql,
        Dictionary<string, object>? tags = null,
        MySqlParameter[]? parameters = null)
    {
        if (!IsConfigured) return new(false, 0, "Connection string no configurada");
        using var scope = StartDbSpan(operation, resource, sql.Trim());
        if (tags is not null)
        {
            foreach (var (k, v) in tags) scope.Span.SetTag(k, v?.ToString());
        }
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            if (parameters is not null) foreach (var p in parameters) cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            sw.Stop();
            scope.Span.SetTag("result.row_count", rows.Count.ToString());
            scope.Span.SetTag("result.elapsed_ms", sw.ElapsedMilliseconds.ToString());
            var msg = $"{operation} — {rows.Count} filas en {sw.ElapsedMilliseconds}ms";
            _log.LogInfo("MySQL " + operation, msg);
            return new(true, sw.ElapsedMilliseconds, msg, rows);
        }
        catch (Exception ex)
        {
            sw.Stop();
            scope.Span.SetException(ex);
            _log.LogException(ex, "MySQL " + operation);
            return new(false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
