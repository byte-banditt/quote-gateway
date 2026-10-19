using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var random = new Random(1729); var prices = new ConcurrentDictionary<string, decimal>();
var delayMs = ReadInt("DELAY_MS", 0); var failRate = ReadDouble("FAIL_RATE", 0);
app.MapGet("/price/{symbol}", async (string symbol, CancellationToken cancellationToken) =>
{
    if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
    lock (random)
    {
        if (random.NextDouble() < failRate) return Results.StatusCode(500);
        var old = prices.GetOrAdd(symbol, 100m); var next = Math.Max(0.01m, old + (decimal)(random.NextDouble() * 4 - 2));
        next = decimal.Round(next, 2); prices[symbol] = next;
        return Results.Ok(new { symbol, price = next, ts = DateTimeOffset.UtcNow });
    }
});
app.Run();
static int ReadInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
static double ReadDouble(string name, double fallback) => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1 ? value : fallback;
