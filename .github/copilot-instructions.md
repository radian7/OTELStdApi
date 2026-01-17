# AI Coding Instructions for OTELStdApi

## Project Overview

**OTELStdApi** is a .NET 10.0 demonstration API showcasing comprehensive OpenTelemetry integration (traces, metrics, logs) with PostgreSQL, Redis caching, and external API integration with Polly resilience patterns.

**Stack**: ASP.NET Core 10.0 • PostgreSQL + EF Core • Redis • OpenTelemetry (OTLP) • Polly • Swagger

## Architecture Patterns

### Data Access Layer (Repository + UnitOfWork)
**Controller → Service → UnitOfWork → Repository → DbContext**

- Always use `IUnitOfWork` for transactions; never call `DbContext` directly from controllers
- Repositories use `AsNoTracking()` for read-only queries to optimize memory
- Services coordinate business logic and observability instrumentation

```csharp
// ✅ Correct: Use service layer
await _orderService.CreateOrderAsync(customerId, customerType, totalAmount);

// ❌ Wrong: Direct DbContext access from controller
await _context.Orders.AddAsync(order);
```

### Service Registration
```csharp
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ICacheService, RedisCacheService>();
```

## OpenTelemetry Observability

### ⚠️ CRITICAL: Naming Convention
**Always use `"my-dotnet-app"` for ActivitySource and Meter names** (not "OTELStdApi" or service name).

```csharp
// ✅ Required pattern - used throughout the codebase
private static readonly ActivitySource ActivitySource = new("my-dotnet-app");
private static readonly Meter Meter = new("my-dotnet-app");
private static readonly Counter<long> MyCounter = Meter.CreateCounter<long>("my.operation.count");
private static readonly Histogram<double> MyDuration = Meter.CreateHistogram<double>("my.operation.duration", "ms");
```

### Instrumentation Pattern: ALL new features must include traces, metrics, and logs

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

### W3C Trace Context & Baggage Propagation
- **Automatic**: `HttpClientInstrumentation` propagates `traceparent`, `tracestate`, `baggage` headers to external APIs
- **Baggage middleware** ([Program.cs](../OTELStdApi/Program.cs#L105)) sets context for ALL requests:
  ```csharp
  context.Items["RequestId"] = requestId;
  context.Items["DeploymentEnvironment"] = builder.Environment.EnvironmentName;
  Activity.Current?.AddTag("request.id", requestId);
  ```
- **Access in controllers**: 
  ```csharp
  var requestId = HttpContext.Items["RequestId"]?.ToString() ?? "unknown";
  activity?.SetTag("request.id", requestId);
  ```

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

## Configuration Management

**Settings hierarchy**: `appsettings.json` → `appsettings.Development.json` (overrides) → User Secrets

### Required Configuration Sections
```json
{
  "ConnectionStrings": {
    "OrderDatabase": "Host=localhost;Port=5432;Database=OTELStdApiDb;Username=otelstdapi_user;Password=***"
  },
  "ExternalApis": {
    "FakeStore": {
      "BaseUrl": "https://fakestoreapi.com",
      "TimeoutSeconds": 1,
      "RetryMaxAttempts": 3
    }
  },
  "Redis": {
    "ConnectionString": "localhost:6379,password=***",
    "CacheDurationMinutes": 1
  }
}
```

**Access pattern**: `_configuration["Redis:CacheDurationMinutes"]` or `_configuration.GetSection("ExternalApis:FakeStore")`

## Database Conventions

### Entity Framework Core Best Practices
- **Migrations**: `dotnet ef migrations add MigrationName --context OrderDbContext`
- **Indexes**: Define in `OnModelCreating()` for foreign keys and frequently queried columns
- **Read queries**: Use `AsNoTracking()` in repositories for all read-only operations
- **Connection pooling**: Enabled by default via `AddDbContext()`

### Order Number Generation
Format: `ORD-YYYYMMDD-XXXXXXXX` (8 uppercase hex chars from GUID)

```csharp
var orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
// Example: "ORD-20260117-A1B2C3D4"
```


## Development Workflows

### Build & Run
```bash
dotnet build
dotnet run --project OTELStdApi/OTELStdApi.csproj
# API: http://localhost:5125
# Swagger: http://localhost:5125/swagger/index.html
```

### Database Setup & Migrations
```bash
# Create PostgreSQL database (psql)
CREATE DATABASE "OTELStdApiDb";
CREATE USER otelstdapi_user WITH PASSWORD 'your-secure-password-here';
GRANT ALL PRIVILEGES ON DATABASE "OTELStdApiDb" TO otelstdapi_user;

# Create migration
dotnet ef migrations add MigrationName --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj

# Apply migrations
dotnet ef database update --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj

# Verify schema
psql -U otelstdapi_user -d OTELStdApiDb -c "\d Orders"
```

### Docker
```bash
docker build -t otelstdapi .
docker run -p 5125:8080 otelstdapi
```

### Redis Setup
```bash
# Start Redis with Docker
docker run -d --name redis -p 6379:6379 redis:alpine redis-server --requirepass twoje_silne_haslo_123

# Verify connection
redis-cli -a twoje_silne_haslo_123 ping
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

## API Endpoints & Testing

### Health Check
```bash
GET http://localhost:5125/health
# Response: 200 OK
```

### Product Controller (External API + Redis Cache)
```bash
# First call - Cache MISS, fetches from FakeStore API
GET http://localhost:5125/Product/1
# Response: {"id":1,"title":"Fjallraven...","price":109.95,...}

# Second call (within 1 min) - Cache HIT
GET http://localhost:5125/Product/1
# Response: Same, but faster (from Redis)
```

### WeatherForecast Controller (Order Creation with PostgreSQL)
```bash
POST http://localhost:5125/WeatherForecast
Content-Type: application/json
{"CustomerId": "CUST-001", "CustomerType": "Premium", "TotalAmount": 1250.50}

# Response:
{
  "orderId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "orderNumber": "ORD-20260117-A1B2C3D4",
  "status": "Pending",
  "createdAt": "2026-01-17T10:30:00Z"
}

# Verify in database
psql -U otelstdapi_user -d OTELStdApiDb -c "SELECT * FROM \"Orders\";"
```

### Swagger UI
http://localhost:5125/swagger/index.html
