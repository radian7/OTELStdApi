# OTELStdApi - OpenTelemetry Standard API

Projekt demonstracyjny aplikacji ASP.NET Core z pełną integracją OpenTelemetry (Traces, Metrics, Logs) oraz wrapperem dla zewnętrznych API z zaawansowaną obsługą błędów i retry'ami.

## Architektura

### Komponenty OpenTelemetry

Projekt integruje trzy filary observability:

1. **Traces (Distributed Tracing)**
   - Implementacja: `System.Diagnostics.ActivitySource`
   - Eksport: OTLP (OpenTelemetry Protocol) do Alloy na `http://localhost:4317`
   - Użycie: Śledzenie przebiegu żądań, nested spans, custom tags
   - **W3C Trace Context**: Automatyczne propagowanie `traceparent`, `tracestate` headers w outgoing HTTP requests
   - **Baggage Propagation**: Propagowanie baggage items (request.id, deployment.environment) w całej aplikacji i do zewnętrznych API

2. **Metrics**
   - Implementacja: `System.Diagnostics.Metrics.Meter` i `Counter`, `Histogram`
   - Eksport: OTLP do Alloy
   - Przykłady: `orders.created`, `orders.processing.duration`, `products.retrieved`, `products.fetch.duration`

3. **Logs**
   - Implementacja: `ILogger<T>` z OpenTelemetry
   - Eksport: OTLP do Alloy
   - Funkcje: Structured logging, scopes, kontekst żądania

## Endpoints API

### WeatherForecast Controller
- **GET** `/WeatherForecast` - Pobiera prognozę pogody (demo)
  - Includes baggage: `request.id`, `deployment.environment`
- **POST** `/WeatherForecast` - Tworzy zamówienie (demo z full observability)
  - Includes baggage propagation

### Product Controller
- **GET** `/Product/{id}` - Pobiera produkt z FakeStore API
  - Wrapper dla: https://fakestoreapi.com/products/{id}
  - Obsługa błędów: 4xx (return error), 5xx (retry x3, then error)
  - Timeout: 1 sekunda
  - Retry: Do 3 razy dla błędów sieciowych (Polly exponential backoff)
  - **W3C Propagation**: Automatyczne propagowanie trace context headers do FakeStore API
  - **Baggage Propagation**: Request ID i deployment environment są propagowane do zewnętrznego API

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
    
    // Dodaj do context.Items dla dostępu w kontrolerach
    context.Items["RequestId"] = requestId;
    context.Items["DeploymentEnvironment"] = builder.Environment.EnvironmentName;

    // Ustaw w Current Activity (propaguje się automatycznie)
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

## Obsługa Błędów w Product Controller

| Status Code | Akcja |
|---|---|
| 200 OK | ✅ Zwróć produkt, log info |
| 400-499 Client Error | ❌ Zwróć błąd do klienta, log warning |
| 500-599 Server Error | 🔄 Retry x3 z exponential backoff, jeśli się nie uda → zwróć 503, log error |
| Timeout / Network Error | 🔄 Retry x3, jeśli się nie uda → zwróć 503, log error |

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

1. **Cache Check** - Sprawdź Redis cache dla klucza `product:{id}`
   - ✅ **Cache HIT** - Zwróć cached product, metric `cache.hits++`
   - ❌ **Cache MISS** - Przejdź do kroku 2, metric `cache.misses++`

2. **API Call** (Cache Miss) - Wykonaj HTTP request do FakeStore API
   - Polly retry policy: retry x3 dla 5xx errors
   - W3C Trace Context + Baggage propagation

3. **Cache Write** - Zapisz produkt w Redis cache
   - TTL (Time To Live): 1 minuta (konfigurowalny)
   - Key format: `product:{id}`
   - Value: JSON serialized Product object

### Metrics

Dodane metryki do obsługi cache:

```csharp
private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cache.hits");
private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("cache.misses");
```

### Architektura Cache Service

```
ProductController
    ↓
ICacheService (interface)
    ↓
RedisCacheService (implementation)
    ↓
StackExchange.Redis IConnectionMultiplexer
    ↓
Redis Server (localhost:6379)
```

### Operacje Cache

#### GetAsync<T> - Pobierz z cache
```csharp
var cachedProduct = await _cacheService.GetAsync<Product>(cacheKey);
// Zwraca: Product | null
// Obsługa: JSON deserialization, logging, exception handling
```

#### SetAsync<T> - Zapisz do cache
```csharp
await _cacheService.SetAsync(cacheKey, product, cacheDuration);
// cacheDuration: TimeSpan? (np. 1 minuta)
// Konwertuje do Expiration dla Redis
```

#### RemoveAsync - Usuń z cache
```csharp
await _cacheService.RemoveAsync(cacheKey);
```

#### ExistsAsync - Sprawdź istnienie klucza
```csharp
bool exists = await _cacheService.ExistsAsync(cacheKey);
```

### Obserwabilność Cache

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
- `products.retrieved` - łączna liczba pobranych produktów

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

# Drugi request (w ciągu 1 minuty) - cache hit
curl -X GET "http://localhost:5125/Product/1"
# Response: Product data + logs "Cache hit" (szybsze)

# Po 1 minucie - cache expires, znowu cache miss
curl -X GET "http://localhost:5125/Product/1"
# Response: Product data + logs "Cache miss"
```

### Zależności

- **StackExchange.Redis** (2.10.1) - Redis client
- Async/await - obsługa asynchronicznych operacji
- JSON serialization - `System.Text.Json`
- Dependency Injection - `IServiceCollection`, `IServiceProvider`

## TODO

- [ ] Dodać Health Checks dla FakeStore API
- [ ] Dodać Circuit Breaker pattern (Polly)
- [ ] Dodać W3C Trace Context headers ✅
- [ ] Dodać baggage propagation dla distributed tracing ✅
- [ ] Dodać rate limiting dla FakeStore API
- [x] Dodać caching dla produktów ✅

## Przykładowe Zapytanie

Request: GET /Product/1
    ↓
[1] CheckCache("product:1") → Redis
    ├─ HIT? → Return cached product (fast)
    └─ MISS? → Continue to step 2
    ↓
[2] CallFakeStoreAPI("https://fakestoreapi.com/products/1")
    ├─ 5xx error? → Retry x3 (Polly)
    └─ Success? → Continue to step 3
    ↓
[3] SetCache("product:1", product, 1 minute) → Redis
    ↓
Return product to client
