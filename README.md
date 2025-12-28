# OTELStdApi - OpenTelemetry Standard API

Projekt demonstracyjny aplikacji ASP.NET Core z pe³n¹ integracj¹ OpenTelemetry (Traces, Metrics, Logs) oraz wrapperem dla zewnêtrznych API z zaawansowan¹ obs³ug¹ b³êdów i retry'ami.

## Architektura

### Komponenty OpenTelemetry

Projekt integruje trzy filary observability:

1. **Traces (Distributed Tracing)**
   - Implementacja: `System.Diagnostics.ActivitySource`
   - Eksport: OTLP (OpenTelemetry Protocol) do Alloy na `http://localhost:4317`
   - U¿ycie: Œledzenie przebiegu ¿¹dañ, nested spans, custom tags
   - **W3C Trace Context**: Automatyczne propagowanie `traceparent`, `tracestate` headers w outgoing HTTP requests
   - **Baggage Propagation**: Propagowanie baggage items (request.id, deployment.environment) w ca³ej aplikacji i do zewnêtrznych API

2. **Metrics**
   - Implementacja: `System.Diagnostics.Metrics.Meter` i `Counter`, `Histogram`
   - Eksport: OTLP do Alloy
   - Przyk³ady: `orders.created`, `orders.processing.duration`, `products.retrieved`, `products.fetch.duration`

3. **Logs**
   - Implementacja: `ILogger<T>` z OpenTelemetry
   - Eksport: OTLP do Alloy
   - Funkcje: Structured logging, scopes, kontekst ¿¹dania

## Endpoints API

### WeatherForecast Controller
- **GET** `/WeatherForecast` - Pobiera prognozê pogody (demo)
  - Includes baggage: `request.id`, `deployment.environment`
- **POST** `/WeatherForecast` - Tworzy zamówienie (demo z full observability)
  - Includes baggage propagation

### Product Controller
- **GET** `/Product/{id}` - Pobiera produkt z FakeStore API
  - Wrapper dla: https://fakestoreapi.com/products/{id}
  - Obs³uga b³êdów: 4xx (return error), 5xx (retry x3, then error)
  - Timeout: 1 sekunda
  - Retry: Do 3 razy dla b³êdów sieciowych (Polly exponential backoff)
  - **W3C Propagation**: Automatyczne propagowanie trace context headers do FakeStore API
  - **Baggage Propagation**: Request ID i deployment environment s¹ propagowane do zewnêtrznego API

## Konfiguracja

### appsettings.json

```json
{
  "ExternalApis": {
    "FakeStore": {
      "BaseUrl": "https://fakestoreapi.com",
      "TimeoutSeconds": 1,
      "RetryMaxAttempts": 3
    }
  }
}
```

### Program.cs - HttpClientFactory

```csharp
builder.Services.AddHttpClient("FakeStoreAPI", client =>
{
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
```

### Program.cs - W3C Propagators & Baggage Middleware

```csharp
app.Use(async (context, next) =>
{
    var requestId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
    
    // Dodaj do context.Items dla dostêpu w kontrolerach
    context.Items["RequestId"] = requestId;
    context.Items["DeploymentEnvironment"] = builder.Environment.EnvironmentName;

    // Ustaw w Current Activity (propaguje siê automatycznie)
    if (System.Diagnostics.Activity.Current != null)
    {
        System.Diagnostics.Activity.Current.AddTag("request.id", requestId);
        System.Diagnostics.Activity.Current.AddTag("deployment.environment", builder.Environment.EnvironmentName);
    }

    await next();
});
```

### ProductController - Polly Retry Policy

```csharp
private static readonly IAsyncPolicy<HttpResponseMessage> RetryPolicy =
    Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt =>
                TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100));
```

**Exponential backoff strategy:**
- Retry 1: wait 200ms
- Retry 2: wait 400ms
- Retry 3: wait 800ms

## Obs³uga B³êdów w Product Controller

| Status Code | Akcja |
|---|---|
| 200 OK | ? Zwróæ produkt, log info |
| 400-499 Client Error | ? Zwróæ b³¹d do klienta, log warning |
| 500-599 Server Error | ?? Retry x3 z exponential backoff, jeœli siê nie uda ? zwróæ 503, log error |
| Timeout / Network Error | ?? Retry x3, jeœli siê nie uda ? zwróæ 503, log error |

## OpenTelemetry Monitoring

### Swagger UI
```
http://localhost:5125/swagger/index.html
```

### OpenAPI Contract
```
http://localhost:5125/openapi/v1.json
```

### Traces, Metrics & Logs
Dostêpne w Alloy/Grafana na podstawie konfiguracji OTLP:
- Endpoint: `http://localhost:4317` (gRPC)
- Service Name: `OTELStdApi`
- Service Version: `1.0.0`

## W3C Trace Context & Baggage Propagation

### Incoming Requests
Aplikacja automatycznie ekstraktuje z incoming HTTP headers:
- `traceparent` - W3C Trace Context format (version, trace-id, parent-id, trace-flags)
- `tracestate` - W3C trace state
- `baggage` - W3C Baggage header

### Outgoing Requests (do FakeStore API)
Aplikacja automatycznie propaguje do wychodz¹cych requestów:
- `traceparent` - Kontynuuje distributed trace
- `tracestate` - Stan trace'u
- `baggage` - Baggage items (request.id, deployment.environment)

**Automatyczne propagowanie jest obs³ugiwane przez:**
- `AddHttpClientInstrumentation()` w OpenTelemetry configuration
- W3C Propagators (wbudowane w .NET 10)
- Activity tags w baggage

### Przyk³ad Flow:

```
Client Request (with traceparent header)
    ?
App receives & ekstraktuje W3C headers
    ?
Middleware ustawia RequestId & DeploymentEnvironment w Activity
    ?
ProductController propaguje headers do FakeStore API
    ?
FakeStore API receives (traceparent, baggage headers)
    ?
Response back with Activity continuation
```

## Przyk³ad Obserwacji w Kodzie

### Traces z Baggage
```csharp
using var activity = ActivitySource.StartActivity("GetProductById");
activity?.SetTag("product.id", id);

// Baggage z context
var requestId = HttpContext.Items["RequestId"]?.ToString() ?? "unknown";
var environment = HttpContext.Items["DeploymentEnvironment"]?.ToString() ?? "unknown";

activity?.SetTag("request.id", requestId);
activity?.SetTag("deployment.environment", environment);

// Nested span propaguje baggage automatycznie
using (var apiCallActivity = ActivitySource.StartActivity("CallFakeStoreAPI"))
{
    apiCallActivity?.SetTag("http.method", "GET");
    apiCallActivity?.SetTag("http.url", $"/products/{id}");
    // Baggage headers s¹ propagowane automatycznie do FakeStore API
    response = await httpClient.GetAsync($"/products/{id}");
}
```

### Metrics
```csharp
ProductsRetrieved.Add(1,
    new KeyValuePair<string, object?>("product.category", product?.Category));

ProductFetchDuration.Record(stopwatch.ElapsedMilliseconds);
```

### Logs
```csharp
_logger.LogInformation("Fetching product with ID {ProductId}", id);
_logger.LogError(ex, "HTTP request failed for product {ProductId}", id);
```

## Technologie

- **.NET**: 10.0
- **C#**: 14.0
- **OpenTelemetry**: Latest (Api 1.14.0, Core)
- **Polly**: 8.6.5 (Resilience & Transient Fault Handling)
- **Swashbuckle**: Latest (Swagger UI)

## Uruchomienie

```bash
dotnet run
```

Aplikacja nas³uchuje na `http://localhost:5125`

## Testowanie Product Endpoint

```bash
# Zainstalowany produkt
curl -X GET "http://localhost:5125/Product/1"

# Nie zainstalowany produkt
curl -X GET "http://localhost:5125/Product/999"

# Niewa¿ne ID
curl -X GET "http://localhost:5125/Product/0"
```

### Testowanie W3C Propagation

```bash
# Request z custom W3C headers
curl -X GET "http://localhost:5125/Product/1" \
  -H "traceparent: 00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" \
  -H "baggage: userId=alice,serverNode=DF:28,isProduction=false"
```

## TODO

- [ ] Dodaæ Health Checks dla FakeStore API
- [ ] Dodaæ Circuit Breaker pattern (Polly)
- [x] Dodaæ W3C Trace Context headers
- [x] Dodaæ baggage propagation dla distributed tracing
- [ ] Dodaæ rate limiting dla FakeStore API
- [ ] Dodaæ caching dla produktów
