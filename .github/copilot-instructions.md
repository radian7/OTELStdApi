# AI Coding Instructions for OTELStdApi

## Project Overview

**OTELStdApi** is a .NET 10.0 demonstration API showcasing comprehensive OpenTelemetry integration (traces, metrics, logs) with PostgreSQL, Redis caching, and external API integration with resilience patterns.

## Architecture Patterns

### Data Access Layer
- **Repository Pattern** + **Unit of Work Pattern** for database operations
- Always use `IUnitOfWork` for transactions, never call `DbContext` directly from controllers
- Repositories use `AsNoTracking()` for all read-only queries
- Service layer (`OrderService`) coordinates business logic between controllers and repositories

**Example flow**: Controller → Service → UnitOfWork → Repository → DbContext

```csharp
// ✅ Correct: Use service layer with UnitOfWork
await _orderService.CreateOrderAsync(customerId, customerType, totalAmount);

// ❌ Wrong: Direct DbContext access from controller
await _context.Orders.AddAsync(order);
```

### Service Layer
- Services (e.g., `OrderService`, `RedisCacheService`) implement interfaces (`IOrderService`, `ICacheService`)
- Services handle business logic, observability instrumentation, and coordinate repository calls
- Register services as scoped: `builder.Services.AddScoped<IOrderService, OrderService>()`

## OpenTelemetry Observability

### Critical Pattern: Instrument ALL new features with traces, metrics, and logs

**ActivitySource & Meter naming**: Use `"my-dotnet-app"` (not service name) for consistency with existing code.

```csharp
// Define at class level
private static readonly ActivitySource ActivitySource = new("my-dotnet-app");
private static readonly Meter Meter = new("my-dotnet-app");
private static readonly Counter<long> MyCounter = Meter.CreateCounter<long>("my.operation.count");
private static readonly Histogram<double> MyDuration = Meter.CreateHistogram<double>("my.operation.duration", "ms");
```

### Trace Spans Pattern
- Create spans for: API calls, database operations, cache operations, business logic
- Use nested spans (`StartActivity()` within parent activity context)
- Add tags: `activity?.SetTag("entity.id", id)`, `activity?.SetTag("operation.status", "success")`

```csharp
// Main operation span
using var activity = ActivitySource.StartActivity("ServiceName.MethodName");
activity?.SetTag("entity.id", entityId);

// Nested span for sub-operation
using (var nestedActivity = ActivitySource.StartActivity("ServiceName.SubOperation"))
{
    nestedActivity?.SetTag("sub.operation.detail", value);
    // ... perform operation
}
```

### W3C Trace Context & Baggage
- **Automatic propagation**: `HttpClientInstrumentation` auto-adds `traceparent`, `tracestate`, `baggage` headers
- **Baggage middleware** (in `Program.cs`) sets: `request.id`, `deployment.environment` for ALL requests
- Access baggage in controllers: `HttpContext.Items["RequestId"]`, `HttpContext.Items["DeploymentEnvironment"]`
- Add to activities: `activity?.SetTag("request.id", requestId)`

### Metrics Guidelines
- Use **Counter** for cumulative counts (e.g., `orders.created`, `cache.hits`)
- Use **Histogram** for duration measurements (e.g., `orders.processing.duration`, `products.fetch.duration`)
- Add dimensional tags: `Counter.Add(1, new KeyValuePair<string, object?>("dimension.key", value))`

### Structured Logging
- Use `ILogger<T>` with structured parameters: `_logger.LogInformation("Order created: {OrderNumber}", orderNumber)`
- Log levels: DEBUG (detailed operations), INFO (success), WARNING (client errors), ERROR (exceptions)
- Include context in logs matching trace tags

## External API Integration

### HttpClient with Polly Resilience
**Pattern for external APIs**: Use `IHttpClientFactory` + Polly retry policy

```csharp
// Register in Program.cs
builder.Services.AddHttpClient("ExternalAPI", client =>
{
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

// Polly retry policy (defined at controller/service class level)
private static readonly IAsyncPolicy<HttpResponseMessage> RetryPolicy =
    Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => 
                TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100));

// Use in methods
await RetryPolicy.ExecuteAsync(async () =>
{
    using var apiActivity = ActivitySource.StartActivity("CallExternalAPI");
    return await httpClient.GetAsync(endpoint);
});
```

**Error handling strategy**:
- 2xx → Success
- 4xx → Return error to client (no retry)
- 5xx → Retry 3x with exponential backoff (200ms, 400ms, 800ms), then return 503
- Network errors (HttpRequestException) → Retry 3x, then return 503

## Redis Caching Pattern

**Standard flow**: Check cache → Cache miss → Fetch data → Write cache with TTL

```csharp
// 1. Check cache
var cachedData = await _cacheService.GetAsync<MyType>(cacheKey);
if (cachedData != null)
{
    CacheHits.Add(1, new KeyValuePair<string, object?>("entity.type", "mytype"));
    return Ok(cachedData);
}
CacheMisses.Add(1);

// 2. Fetch data (database/API)
var data = await FetchFromSource();

// 3. Write to cache
var ttl = TimeSpan.FromMinutes(_configuration["Redis:CacheDurationMinutes"]);
await _cacheService.SetAsync(cacheKey, data, ttl);
```

**Cache key format**: Use colon-separated namespaces: `"entity:id"` (e.g., `"product:10"`)

## Database Operations

### Entity Framework Core Conventions
- Migrations: Run `dotnet ef migrations add MigrationName --context OrderDbContext` for schema changes
- Always define indexes in `OnModelCreating()` for foreign keys and frequently queried columns
- Use `AsNoTracking()` in repositories for read-only queries
- Connection pooling is enabled by default via `AddDbContext()`

### Order Number Generation Pattern
Format: `ORD-YYYYMMDD-XXXXXXXX` (8 uppercase hex chars from GUID)

```csharp
var orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
```

## Configuration Management

**Settings hierarchy**: `appsettings.json` → `appsettings.Development.json` (override)

**Required settings sections**:
- `ConnectionStrings:OrderDatabase` - PostgreSQL connection string
- `ExternalApis:FakeStore` - External API config (BaseUrl, TimeoutSeconds, RetryMaxAttempts)
- `Redis` - Cache config (ConnectionString, CacheDurationMinutes)

Access via `IConfiguration`: `_configuration["Redis:CacheDurationMinutes"]` or `_configuration.GetSection("ExternalApis:FakeStore")`

## Development Commands

```bash
# Build & Run
dotnet build
dotnet run --project OTELStdApi/OTELStdApi.csproj

# Database migrations
dotnet ef migrations add MigrationName --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj
dotnet ef database update --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj

# Docker
docker build -t otelstdapi .
docker run -p 5125:8080 otelstdapi
```

## Architecture Decision Records (ADRs)

**Document architectural changes** in `/docs/adr/{number}-{description}.md` using ADR format:
- Status (Proposed/Accepted/Deprecated)
- Date
- Context (why the decision is needed)
- Decision (what was decided)
- Consequences (trade-offs)

**Create ADRs for**:
1. Major dependency changes
2. New architectural patterns (e.g., CQRS, Event Sourcing)
3. Database schema changes
4. New integration patterns (e.g., message queues)

Reference: [ADR-001: Order Database Schema](../docs/adr/001-order-database-schema.md)

## Key Files & Components

- [Program.cs](../OTELStdApi/Program.cs) - OpenTelemetry setup, middleware, DI configuration
- [ProductController.cs](../OTELStdApi/Controllers/ProductController.cs) - External API + Redis caching + Polly retry
- [OrderService.cs](../OTELStdApi/Services/OrderService.cs) - Business logic + observability instrumentation
- [OrderRepository.cs](../OTELStdApi/Data/Repositories/OrderRepository.cs) - Data access with AsNoTracking()
- [UnitOfWork.cs](../OTELStdApi/Data/Repositories/UnitOfWork.cs) - Transaction coordination
- [RedisCacheService.cs](../OTELStdApi/Services/RedisCacheService.cs) - Cache abstraction

## Testing Endpoints

```bash
# Health check
GET http://localhost:5125/health

# Get product (with caching)
GET http://localhost:5125/Product/1

# Create order (writes to PostgreSQL)
POST http://localhost:5125/WeatherForecast
Content-Type: application/json
{"CustomerId": "CUST-001", "CustomerType": "Premium", "TotalAmount": 1250.50}

# Swagger UI
http://localhost:5125/swagger/index.html
```
