using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter((category, _) => category == typeof(QuoteService).FullName || category == typeof(Program).FullName);
builder.Services.Configure<GatewayOptions>(builder.Configuration);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<QuoteStore>();
builder.Services.AddHttpClient("upstream", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<QuoteService>();

var app = builder.Build();
app.MapGet("/quotes/{symbol}", async (string symbol, QuoteService service, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z]{1,10}$"))
    {
        logger.LogInformation("symbol={Symbol} source=invalid attempts=0 elapsedMs=0", symbol);
        return Results.BadRequest(new { error = "symbol must match ^[A-Z]{1,10}$" });
    }
    return await service.GetAsync(symbol, cancellationToken);
});
app.Run();
public partial class Program { }

public sealed class GatewayOptions { public UpstreamOptions Upstream { get; set; } = new(); public SqliteOptions Sqlite { get; set; } = new(); }
public sealed class UpstreamOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5101";
    public int TimeoutMs { get; set; } = 300; public int MaxRetries { get; set; } = 2;
    public int BackoffMs { get; set; } = 50; public int CacheTtlMs { get; set; } = 2000;
}
public sealed class SqliteOptions { public string Path { get; set; } = "quote-gateway.db"; }
public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed record StoredQuote(string Symbol, decimal Price, DateTimeOffset AsOfUtc);
public sealed record QuoteResult(string Symbol, decimal Price, DateTimeOffset AsOfUtc, string Source, long? AgeMs);

public sealed class QuoteStore
{
    private readonly string _connectionString;
    public QuoteStore(IConfiguration configuration)
    {
        var path = configuration.GetSection("Sqlite")["Path"] ?? "quote-gateway.db";
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS quotes (symbol TEXT PRIMARY KEY, price TEXT NOT NULL, as_of_utc TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public async Task UpsertAsync(StoredQuote quote, CancellationToken cancellationToken)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO quotes(symbol, price, as_of_utc) VALUES ($symbol, $price, $asof) ON CONFLICT(symbol) DO UPDATE SET price=$price, as_of_utc=$asof;";
        command.Parameters.AddWithValue("$symbol", quote.Symbol);
        // TEXT preserves decimal precision without binary floating-point conversion.
        command.Parameters.AddWithValue("$price", quote.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$asof", quote.AsOfUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<StoredQuote?> FindAsync(string symbol, CancellationToken cancellationToken)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT price, as_of_utc FROM quotes WHERE symbol=$symbol"; command.Parameters.AddWithValue("$symbol", symbol);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StoredQuote(symbol, decimal.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
}

public sealed class QuoteService
{
    private sealed record CacheItem(decimal Price, DateTimeOffset AsOfUtc);
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new(); private readonly QuoteStore _store;
    private readonly IHttpClientFactory _clients; private readonly GatewayOptions _options; private readonly IClock _clock; private readonly ILogger<QuoteService> _logger;
    public QuoteService(QuoteStore store, IHttpClientFactory clients, Microsoft.Extensions.Options.IOptions<GatewayOptions> options, IClock clock, ILogger<QuoteService> logger)
    { _store = store; _clients = clients; _options = options.Value; _clock = clock; _logger = logger; }
    public async Task<IResult> GetAsync(string symbol, CancellationToken cancellationToken)
    {
        var started = _clock.UtcNow; var attempts = 0; QuoteResult? result = null;
        if (_cache.TryGetValue(symbol, out var cached) && AgeMs(cached.AsOfUtc) < _options.Upstream.CacheTtlMs)
            result = new QuoteResult(symbol, cached.Price, cached.AsOfUtc, "cache", AgeMs(cached.AsOfUtc));
        else
        {
            var client = _clients.CreateClient("upstream"); var url = $"{_options.Upstream.BaseUrl.TrimEnd('/')}/price/{symbol}";
            for (var retry = 0; retry <= _options.Upstream.MaxRetries; retry++)
            {
                attempts++;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.Upstream.TimeoutMs));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    using var response = await client.GetAsync(url, linked.Token);
                    if ((int)response.StatusCode >= 500) throw new HttpRequestException("upstream 5xx");
                    if (!response.IsSuccessStatusCode) break;
                    var upstream = await response.Content.ReadFromJsonAsync<UpstreamQuote>(cancellationToken: linked.Token);
                    if (upstream is null) throw new HttpRequestException("empty upstream response");
                    var stored = new StoredQuote(symbol, upstream.Price, _clock.UtcNow);
                    await _store.UpsertAsync(stored, cancellationToken); _cache[symbol] = new CacheItem(stored.Price, stored.AsOfUtc);
                    result = new QuoteResult(symbol, stored.Price, stored.AsOfUtc, "live", 0); break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (HttpRequestException) { }
                if (retry < _options.Upstream.MaxRetries)
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.Upstream.BackoffMs * Math.Pow(2, retry)), cancellationToken);
            }
            if (result is null)
            {
                var stored = await _store.FindAsync(symbol, cancellationToken);
                if (stored is not null) result = new QuoteResult(symbol, stored.Price, stored.AsOfUtc, "stale", AgeMs(stored.AsOfUtc));
            }
        }
        var elapsed = AgeMs(started); _logger.LogInformation("symbol={Symbol} source={Source} attempts={Attempts} elapsedMs={Elapsed}", symbol, result?.Source ?? "error", attempts, elapsed);
        return result is null ? Results.StatusCode((int)HttpStatusCode.ServiceUnavailable) : Results.Ok(result);
    }
    private long AgeMs(DateTimeOffset time) => Math.Max(0, (long)(_clock.UtcNow - time).TotalMilliseconds);
    private sealed record UpstreamQuote(string Symbol, decimal Price, DateTimeOffset Ts);
}
