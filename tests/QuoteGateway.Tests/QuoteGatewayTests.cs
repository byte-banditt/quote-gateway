using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class QuoteGatewayTests
{
    [Fact] public async Task ValidSymbolStoresLivePrice() { using var f = New(); var response = await f.Client.GetAsync("/quotes/ABC"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("live", (await Json(response)).Source); Assert.NotNull(await f.Store.FindAsync("ABC", default)); }
    [Fact] public async Task SecondRequestUsesCache() { using var f = New(); await f.Client.GetAsync("/quotes/ABC"); var second = await Json(await f.Client.GetAsync("/quotes/ABC")); Assert.Equal("cache", second.Source); Assert.Equal(1, f.Handler.Count); }
    [Fact] public async Task RetryThenSuccess() { using var f = New(new[] { new HttpResponseMessage(HttpStatusCode.InternalServerError), Good() }); var result = await Json(await f.Client.GetAsync("/quotes/ABC")); Assert.Equal("live", result.Source); Assert.Equal(2, f.Handler.Count); }
    [Fact] public async Task TimeoutsReturnStoredPriceAsStale() { using var f = New(new[] { Timeout(), Timeout(), Timeout() }); await f.Store.UpsertAsync(new StoredQuote("ABC", 12.34m, f.Clock.UtcNow.AddMilliseconds(-5000)), default); var result = await Json(await f.Client.GetAsync("/quotes/ABC")); Assert.Equal("stale", result.Source); Assert.Equal(5000, result.AgeMs); }
    [Fact] public async Task FailedWithoutStoredPriceReturns503() { using var f = New(new[] { new HttpResponseMessage(HttpStatusCode.InternalServerError), new HttpResponseMessage(HttpStatusCode.InternalServerError), new HttpResponseMessage(HttpStatusCode.InternalServerError) }); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Client.GetAsync("/quotes/ABC")).StatusCode); }
    [Fact] public async Task InvalidSymbolReturns400() { using var f = New(); Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.GetAsync("/quotes/a")).StatusCode); Assert.Equal(0, f.Handler.Count); }
    [Fact] public async Task Upstream4xxIsNotRetried() { using var f = New(new[] { new HttpResponseMessage(HttpStatusCode.NotFound) }); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Client.GetAsync("/quotes/ABC")).StatusCode); Assert.Equal(1, f.Handler.Count); }

    private static TestFactory New(HttpResponseMessage[]? responses = null) => new(responses ?? new[] { Good() });
    private static HttpResponseMessage Good() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { symbol = "ABC", price = 12.34m, ts = DateTimeOffset.UtcNow }) };
    private static HttpResponseMessage Timeout() => new(HttpStatusCode.RequestTimeout);
    private static async Task<QuoteResult> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<QuoteResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!;
}

public sealed class TestFactory : WebApplicationFactory<Program>
{
    public readonly RecordingHandler Handler; public readonly FakeClock Clock = new(); public QuoteStore Store => Services.GetRequiredService<QuoteStore>(); public HttpClient Client => CreateClient();
    private readonly string _dbPath;
    public TestFactory(HttpResponseMessage[] responses) { Handler = new RecordingHandler(responses); _dbPath = Path.Combine(Path.GetTempPath(), $"quote-{Guid.NewGuid():N}.db"); }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Sqlite:Path"] = _dbPath, ["Upstream:BaseUrl"] = "http://fake", ["Upstream:TimeoutMs"] = "25", ["Upstream:BackoffMs"] = "0" }))
            .ConfigureTestServices(services => { services.AddSingleton<IClock>(Clock); services.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => Handler); });
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
}

public sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses; public int Count { get; private set; }
    public RecordingHandler(IEnumerable<HttpResponseMessage> responses) { _responses = new(responses); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++; if (_responses.TryDequeue(out var response)) { if (response.StatusCode == HttpStatusCode.RequestTimeout) { await Task.Delay(Timeout.Infinite, cancellationToken); } return response; }
        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }
}
