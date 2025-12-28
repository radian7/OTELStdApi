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

## Redis Caching

### Konfiguracja

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379,password=twoje_silne_haslo_123",
    "CacheDurationMinutes": 1
  }
}
```

### Cache Flow w ProductController

1. **Cache Check** - SprawdŸ Redis cache dla klucza `product:{id}`
   - ? **Cache HIT** - Zwróæ cached product, metric `cache.hits++`
   - ? **Cache MISS** - PrzejdŸ do kroku 2, metric `cache.misses++`

2. **API Call** (Cache Miss) - Wykonaj HTTP request do FakeStore API
   - Polly retry policy: retry x3 dla 5xx errors
   - W3C Trace Context + Baggage propagation

3. **Cache Write** - Zapisz produkt w Redis cache
   - TTL (Time To Live): 1 minuta (konfigurowalny)
   - Key format: `product:{id}`
   - Value: JSON serialized Product object

### Metrics

Dodane metryki do obs³ugi cache:

```csharp
private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cache.hits");
private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("cache.misses");
```

### Architektura Cache Service

```
ProductController
    ?
ICacheService (interface)
    ?
RedisCacheService (implementation)
    ?
StackExchange.Redis IConnectionMultiplexer
    ?
Redis Server (localhost:6379)
```

### Operacje Cache

#### GetAsync<T> - Pobierz z cache
```csharp
var cachedProduct = await _cacheService.GetAsync<Product>(cacheKey);
// Zwraca: Product | null
// Obs³uga: JSON deserialization, logging, exception handling
```

#### SetAsync<T> - Zapisz do cache
```csharp
await _cacheService.SetAsync(cacheKey, product, cacheDuration);
// cacheDuration: TimeSpan? (np. 1 minuta)
// Konwertuje do Expiration dla Redis
```

#### RemoveAsync - Usuñ z cache
```csharp
await _cacheService.RemoveAsync(cacheKey);
```

#### ExistsAsync - SprawdŸ istnienie klucza
```csharp
bool exists = await _cacheService.ExistsAsync(cacheKey);
```

### Obserwabilnoœæ Cache

**Traces:**
- `CheckCache` span - sprawdzanie cache
  - Tag: `cache.key`
  - Tag: `cache.hit` (true/false)
- `SetCache` span - zapis do cache
  - Tag: `cache.key`
  - Tag: `cache.ttl` (w minutach)

**Metrics:**
- `cache.hits` - liczba cache hits
- `cache.misses` - liczba cache misses
- `products.retrieved` - ³¹czna liczba pobranych produktów

**Logs:**
- "Product {ProductId} found in cache" (DEBUG)
- "Cache hit for key: {CacheKey}" (DEBUG)
- "Cache miss for key: {CacheKey}" (DEBUG)
- "Product {ProductId} cached for {CacheDurationMinutes} minutes" (INFO)
- Cache errors (ERROR)

### Testowanie Cache

```bash
# Pierwszy request - cache miss, pobiera z API
curl -X GET "http://localhost:5125/Product/1"
# Response: Product data + logs "Cache miss"

# Drugi request (w ci¹gu 1 minuty) - cache hit
curl -X GET "http://localhost:5125/Product/1"
# Response: Product data + logs "Cache hit" (szybsze)

# Po 1 minucie - cache expires, znowu cache miss
curl -X GET "http://localhost:5125/Product/1"
# Response: Product data + logs "Cache miss"
```

### Zale¿noœci

- **StackExchange.Redis** (2.10.1) - Redis client
- Async/await - obs³uga asynchronicznych operacji
- JSON serialization - `System.Text.Json`
- Dependency Injection - `IServiceCollection`, `IServiceProvider`

## TODO

- [ ] Dodaæ Health Checks dla FakeStore API
- [ ] Dodaæ Circuit Breaker pattern (Polly)
- [ ] Dodaæ W3C Trace Context headers ?
- [ ] Dodaæ baggage propagation dla distributed tracing ?
- [ ] Dodaæ rate limiting dla FakeStore API
- [x] Dodaæ caching dla produktów ?
