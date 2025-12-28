using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OTELStdApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        // Traces - ActivitySource
        private static readonly ActivitySource ActivitySource = new("my-dotnet-app");

        // Metrics - Meter
        private static readonly Meter Meter = new("my-dotnet-app");
        private static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>("orders.created");
        private static readonly Histogram<double> OrderProcessingTime = Meter.CreateHistogram<double>("orders.processing.duration", "ms");

        private readonly ILogger<WeatherForecastController> _logger;

        public WeatherForecastController(ILogger<WeatherForecastController> logger)
        {
            _logger = logger;
        }

        private static readonly string[] Summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];

        // https://localhost:44301/WeatherForecast
        [HttpGet(Name = "GetWeatherForecast")]
        public IEnumerable<WeatherForecast> Get()
        {
           // Rozpocznij w³asny span
            using var activity = ActivitySource.StartActivity("GetWeatherForecast");
            
            // Dodaj baggage z kontekstu
            var requestId = HttpContext.Items["RequestId"]?.ToString() ?? Activity.Current?.Id ?? "unknown";
            var environment = HttpContext.Items["DeploymentEnvironment"]?.ToString() ?? "unknown";
            
            activity?.SetTag("request.id", requestId);
            activity?.SetTag("deployment.environment", environment);

            _logger.LogInformation("Getting GetWeatherForecast");
                        
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
                       
        }

        [HttpPost]
        public async Task<IActionResult> CreateOrder(CreateOrderRequest request)
        {
            // Rozpocznij w³asny span
            using var activity = ActivitySource.StartActivity("CreateOrder");
            activity?.SetTag("order.customer_id", request.CustomerId);

            // Dodaj baggage z kontekstu
            var requestId = HttpContext.Items["RequestId"]?.ToString() ?? Activity.Current?.Id ?? "unknown";
            var environment = HttpContext.Items["DeploymentEnvironment"]?.ToString() ?? "unknown";
            
            activity?.SetTag("request.id", requestId);
            activity?.SetTag("deployment.environment", environment);

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Creating order for customer {CustomerId}", request.CustomerId);

            try
            {
                // Nested span
                using (var validationActivity = ActivitySource.StartActivity("ValidateOrder"))
                {
                    await ValidateOrder(request);
                    validationActivity?.SetTag("validation.result", "success");
                }

                // Symulacja przetwarzania
                await Task.Delay(100);

                // Metryki
                OrdersCreated.Add(1,
                    new KeyValuePair<string, object?>("customer.type", request.CustomerType));

                _logger.LogInformation("Order created successfully for customer {CustomerId}", request.CustomerId);

                return Ok(new { OrderId = Guid.NewGuid() });
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Failed to create order for customer {CustomerId}", request.CustomerId);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                OrderProcessingTime.Record(stopwatch.ElapsedMilliseconds);
            }
        }

        private Task ValidateOrder(CreateOrderRequest request)
        {
            // Logowanie ze scope
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["OrderValue"] = request.TotalAmount
            }))
            {
                _logger.LogDebug("Validating order");
            }

            return Task.CompletedTask;
        }

    }
}

public record CreateOrderRequest(string CustomerId, string CustomerType, decimal TotalAmount);
