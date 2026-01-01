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
- **POST** `/WeatherForecast` - Tworzy zamówienie w bazie PostgreSQL
  - Obsługa: Repository + Unit of Work + Service Layer
  - Zapis do bazy danych z full observability
  - Zwraca: OrderId, OrderNumber, Status, CreatedAt
  - Includes baggage propagation

### Product Controller
- **GET** `/Product/{id}` - Pobiera produkt z FakeStore API
  - Wrapper dla: https://fakestoreapi.com/products/{id}
  - Obsługa błędów: 4xx (return error), 5xx (retry x3, then error)
  - Timeout: 1 sekunda
  - Retry: Do 3 razy dla błędów sieciowych (Polly exponential backoff)
  - **W3C Propagation**: Automatyczne propagowanie trace context headers do FakeStore API
  - **Baggage Propagation**: Request ID i deployment environment są propagowane do zewnętrznego API
  - **Redis Cache**: Cache z TTL=1 minuta

## Konfiguracja

### appsettings.json

```json
{
  "ConnectionStrings": {
    "OrderDatabase": "Host=localhost;Port=5432;Database=OTELStdApiDb;Username=otelstdapi_user;Password=your-secure-password-here"
  },
  "ExternalApis": {
    "FakeStore": {
      "BaseUrl": "https://fakestoreapi.com",
      "TimeoutSeconds": 1,
      "RetryMaxAttempts": 3
    }
  },
  "Redis": {
    "ConnectionString": "localhost:6379,password=twoje_silne_haslo_123",
    "CacheDurationMinutes": 1
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

## PostgreSQL Database

### Konfiguracja

```json
{
  "ConnectionStrings": {
    "OrderDatabase": "Host=localhost;Port=5432;Database=OTELStdApiDb;Username=otelstdapi_user;Password=your-secure-password-here"
  }
}
```

### Architecture Patterns

Projekt wykorzystuje następujące wzorce zgodnie z `copilot-instructions.md`:

- **Repository Pattern** - Abstrakcja dostępu do danych
- **Unit of Work Pattern** - Koordynacja operacji na repozytoriach i transakcji
- **Service Layer** - Logika biznesowa oddzielona od kontrolerów

### Database Schema

#### Orders Table

| Column | Type | Constraints |
|---|---|---|
| Id | UUID | PRIMARY KEY |
| OrderNumber | VARCHAR(100) | NOT NULL, UNIQUE |
| CustomerId | VARCHAR(200) | NOT NULL |
| CustomerType | VARCHAR(100) | NOT NULL |
| TotalAmount | DECIMAL(18,2) | NOT NULL |
| Status | VARCHAR(50) | NOT NULL, DEFAULT 'Pending' |
| CreatedAt | TIMESTAMP | NOT NULL, DEFAULT CURRENT_TIMESTAMP |
| UpdatedAt | TIMESTAMP | NULL |

**Indexes:**
- Unique index on `OrderNumber`
- Index on `CustomerId` (for customer queries)

### Components

#### Entity
- `Order` - Model reprezentujący zamówienie

#### DbContext
- `OrderDbContext` - EF Core DbContext z:
  - Connection pooling
  - Query tracking optimization (`AsNoTracking()` for read-only)
  - Proper indexing strategy

#### Repository Layer
- `IOrderRepository` - Interface operacji na danych
- `OrderRepository` - Implementacja z optymalizowanymi zapytaniami
- `IUnitOfWork` - Interface zarządzania transakcjami
- `UnitOfWork` - Koordynacja repozytoriów i SaveChanges

#### Service Layer
- `IOrderService` - Interface logiki biznesowej
- `OrderService` - Implementacja z:
  - Generowanie unikalnych numerów zamówień (`ORD-YYYYMMDD-XXXXXXXX`)
  - Zapis do bazy PostgreSQL
  - Full observability (traces, metrics, logs)

### Order Creation Flow

```
POST /WeatherForecast
    ↓
WeatherForecastController.CreateOrder()
    ↓
IOrderService.CreateOrderAsync()
    ↓
Generate OrderNumber
    ↓
IUnitOfWork.Orders.AddAsync(order)
    ↓
IUnitOfWork.SaveChangesAsync() → PostgreSQL
    ↓
Return Order (Id, OrderNumber, Status, CreatedAt)
```

### Observability

**Traces:**
- `OrderService.CreateOrder` - główny span tworzenia zamówienia
  - Tags: `customer.id`, `customer.type`, `order.amount`, `order.id`, `order.number`
- `OrderService.SaveChanges` - span zapisu do bazy
  - Tag: `order.number`

**Metrics:**
- `orders.created.db` (Counter) - liczba zamówień utworzonych w bazie
- `orders.db.save.duration` (Histogram) - czas zapisu do bazy (ms)

**Logs (DEBUG level):**
- "Creating order in database: {OrderNumber} for customer {CustomerId}"
- "Order saved to database in {ElapsedMilliseconds}ms"
- "Order created successfully: {OrderNumber}, OrderId: {OrderId}" (INFO)

### Database Setup

#### 1. Utwórz bazę danych i użytkownika

```bash
# Login as postgres superuser
psql -U postgres

# Create database
CREATE DATABASE "OTELStdApiDb";

# Create user
CREATE USER otelstdapi_user WITH PASSWORD 'your-secure-password-here';

# Grant privileges
GRANT ALL PRIVILEGES ON DATABASE "OTELStdApiDb" TO otelstdapi_user;

# Connect to database
\c OTELStdApiDb

# Grant schema privileges
GRANT ALL ON SCHEMA public TO otelstdapi_user;
```

#### 2. Uruchom migracje

```bash
# Apply migrations to create schema
dotnet ef database update --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj

# Verify schema
psql -U otelstdapi_user -d OTELStdApiDb -c "\d Orders"
```

#### 3. Testowanie

```bash
# Create order
curl -X POST "http://localhost:5125/WeatherForecast" \
  -H "Content-Type: application/json" \
  -d '{"CustomerId": "CUST-001", "CustomerType": "Premium", "TotalAmount": 1250.50}'

# Response:
# {
#   "orderId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
#   "orderNumber": "ORD-20250101-A1B2C3D4",
#   "status": "Pending",
#   "createdAt": "2025-01-01T10:30:00Z"
# }

# Verify in database
psql -U otelstdapi_user -d OTELStdApiDb -c "SELECT * FROM \"Orders\";"
```

#### 4. ADR (Architecture Decision Record)

Szczegółowa dokumentacja decyzji architektonicznych:
- [ADR-001: Order Database Schema](/docs/adr/001-order-database-schema.md)

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

## Technologie

- **.NET**: 10.0
- **C#**: 14.0
- **OpenTelemetry**: Latest (Api 1.14.0)
- **Entity Framework Core**: 10.0.1
- **Npgsql.EntityFrameworkCore.PostgreSQL**: 10.0.0 (PostgreSQL provider for EF Core)
- **PostgreSQL**: Database backend
- **Polly**: 8.6.5 (Resilience & Transient Fault Handling)
- **StackExchange.Redis**: 2.10.1 (Redis cache client)
- **Swashbuckle**: Latest (Swagger UI)

### Architecture Patterns
- Repository Pattern
- Unit of Work Pattern
- Service Layer Pattern
- Dependency Injection

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
