using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OTELStdApi.Models;
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

        public ProductController(IHttpClientFactory httpClientFactory, ILogger<ProductController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Pobiera produkt z zewnêtrznego API (FakeStore) po ID
        /// Wrapper dla: https://fakestoreapi.com/docs#tag/Products/operation/getProductById
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

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Fetching product with ID {ProductId}", id);

            try
            {
                if (id <= 0)
                {
                    _logger.LogWarning("Invalid product ID: {ProductId}", id);
                    return BadRequest(new { error = "Product ID must be greater than 0" });
                }

                var httpClient = _httpClientFactory.CreateClient("FakeStoreAPI");

                HttpResponseMessage response = null;

                // U¿yj Polly retry policy dla 5xx b³êdów
                await RetryPolicy.ExecuteAsync(async () =>
                {
                    using (var apiCallActivity = ActivitySource.StartActivity("CallFakeStoreAPI"))
                    {
                        apiCallActivity?.SetTag("http.method", "GET");
                        apiCallActivity?.SetTag("http.url", $"/products/{id}");

                        response = await httpClient.GetAsync($"/products/{id}");

                        apiCallActivity?.SetTag("http.status_code", (int)response.StatusCode);
                    }

                    return response;
                });

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var product = System.Text.Json.JsonSerializer.Deserialize<Product>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    activity?.SetTag("product.title", product?.Title);

                    ProductsRetrieved.Add(1,
                        new KeyValuePair<string, object?>("product.category", product?.Category));

                    _logger.LogInformation("Product retrieved successfully: {ProductTitle}", product?.Title);

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
