using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OTELStdApi.Models;
using OTELStdApi.Services;
using Polly;

namespace OTELStdApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ProductController : ControllerBase
    {
        // Traces - ActivitySource
        private static readonly ActivitySource ActivitySource = new("my-dotnet-app");

        // Metrics - Meter
        private static readonly Meter Meter = new("my-dotnet-app");
        private static readonly Counter<long> ProductsRetrieved = Meter.CreateCounter<long>("products.retrieved");
        private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cache.hits");
        private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("cache.misses");
        private static readonly Histogram<double> ProductFetchDuration = Meter.CreateHistogram<double>("products.fetch.duration", "ms");

        // Polly retry policy dla 5xx b³êdów
        private static readonly IAsyncPolicy<HttpResponseMessage> RetryPolicy =
            Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt =>
                        TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        Console.WriteLine($"Retry {retryCount} after {timespan.TotalMilliseconds}ms");
                    });

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ProductController> _logger;
        private readonly ICacheService _cacheService;
        private readonly IConfiguration _configuration;

        public ProductController(IHttpClientFactory httpClientFactory, ILogger<ProductController> logger, ICacheService cacheService, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _cacheService = cacheService;
            _configuration = configuration;
        }

        /// <summary>
        /// Pobiera produkt z zewnêtrznego API (FakeStore) po ID
        /// Wrapper dla: https://fakestoreapi.com/docs#tag/Products/operation/getProductById
        /// 
        /// Obs³uga cache:
        /// 1. SprawdŸ Redis cache
        /// 2. Jeœli nie ma lub jest starsze ni¿ 1 minuta - pobierz z API
        /// 3. Zapisz w cache z TTL=1 minuta
        /// </summary>
        /// <param name="id">ID produktu (1-20)</param>
        /// <returns>Dane produktu</returns>
        // http://localhost:5125/Product/10
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(Product), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetProductById(int id)
        {
            // Rozpocznij w³asny span
            using var activity = ActivitySource.StartActivity("GetProductById");
            activity?.SetTag("product.id", id);

            // Dodaj baggage z kontekstu
            var requestId = HttpContext.Items["RequestId"]?.ToString() ?? Activity.Current?.Id ?? "unknown";
            var environment = HttpContext.Items["DeploymentEnvironment"]?.ToString() ?? "unknown";
            
            activity?.SetTag("request.id", requestId);
            activity?.SetTag("deployment.environment", environment);

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Fetching product with ID {ProductId}", id);

            try
            {
                if (id <= 0)
                {
                    _logger.LogWarning("Invalid product ID: {ProductId}", id);
                    return BadRequest(new { error = "Product ID must be greater than 0" });
                }

                // Klucz cache
                var cacheKey = $"product:{id}";
                var cacheDurationMinutes = int.Parse(_configuration["Redis:CacheDurationMinutes"] ?? "1");
                var cacheDuration = TimeSpan.FromMinutes(cacheDurationMinutes);

                // Krok 1: SprawdŸ Redis cache
                using (var cacheCheckActivity = ActivitySource.StartActivity("CheckCache"))
                {
                    cacheCheckActivity?.SetTag("cache.key", cacheKey);

                    var cachedProduct = await _cacheService.GetAsync<Product>(cacheKey);
                    
                    if (cachedProduct != null)
                    {
                        _logger.LogInformation("Product {ProductId} found in cache", id);
                        CacheHits.Add(1, new KeyValuePair<string, object?>("product.id", id));
                        cacheCheckActivity?.SetTag("cache.hit", true);
                        activity?.SetTag("cache.hit", true);
                        return Ok(cachedProduct);
                    }

                    CacheMisses.Add(1, new KeyValuePair<string, object?>("product.id", id));
                    cacheCheckActivity?.SetTag("cache.hit", false);
                    activity?.SetTag("cache.hit", false);
                }

                // Krok 2: Cache miss - pobierz z API
                var httpClient = _httpClientFactory.CreateClient("FakeStoreAPI");
                HttpResponseMessage response = null;
                Product product = null;

                // U¿yj Polly retry policy dla 5xx b³êdów
                await RetryPolicy.ExecuteAsync(async () =>
                {
                    using (var apiCallActivity = ActivitySource.StartActivity("CallFakeStoreAPI"))
                    {
                        apiCallActivity?.SetTag("http.method", "GET");
                        apiCallActivity?.SetTag("http.url", $"/products/{id}");
                        apiCallActivity?.SetTag("request.id", requestId);
                        apiCallActivity?.SetTag("deployment.environment", environment);

                        // W3C Trace Context headers s¹ automatycznie dodawane przez instrumentation
                        // Baggage headers s¹ równie¿ automatycznie propagowane
                        response = await httpClient.GetAsync($"/products/{id}");

                        apiCallActivity?.SetTag("http.status_code", (int)response.StatusCode);
                    }

                    return response;
                });

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    product = System.Text.Json.JsonSerializer.Deserialize<Product>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    // Krok 3: Zapisz w cache z TTL
                    if (product != null)
                    {
                        using (var cacheSetActivity = ActivitySource.StartActivity("SetCache"))
                        {
                            cacheSetActivity?.SetTag("cache.key", cacheKey);
                            cacheSetActivity?.SetTag("cache.ttl", cacheDuration.TotalMinutes);

                            await _cacheService.SetAsync(cacheKey, product, cacheDuration);
                            _logger.LogInformation("Product {ProductId} cached for {CacheDurationMinutes} minutes", id, cacheDurationMinutes);
                        }
                    }

                    activity?.SetTag("product.title", product?.Title);

                    ProductsRetrieved.Add(1,
                        new KeyValuePair<string, object?>("product.category", product?.Category));

                    _logger.LogInformation("Product retrieved successfully from API: {ProductTitle}", product?.Title);

                    return Ok(product);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Product not found: {ProductId}", id);
                    return NotFound(new { error = $"Product with ID {id} not found" });
                }
                else if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    _logger.LogWarning("Client error {StatusCode} from FakeStore API for product {ProductId}", response.StatusCode, id);
                    return StatusCode((int)response.StatusCode, new { error = $"Client error: {response.StatusCode}" });
                }
                else
                {
                    // 5xx b³êdy powinny byæ obs³ugiwane przez Polly retry
                    _logger.LogError("Server error {StatusCode} from FakeStore API for product {ProductId}", response.StatusCode, id);
                    activity?.SetStatus(ActivityStatusCode.Error, $"Server error: {response.StatusCode}");
                    return StatusCode((int)response.StatusCode, new { error = $"External API error: {response.StatusCode}" });
                }
            }
            catch (HttpRequestException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "HTTP request failed for product {ProductId}", id);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "External API is temporarily unavailable. Please try again later." });
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Unexpected error while fetching product {ProductId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
            }
            finally
            {
                stopwatch.Stop();
                ProductFetchDuration.Record(stopwatch.ElapsedMilliseconds);
                _logger.LogDebug("Product fetch completed in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
