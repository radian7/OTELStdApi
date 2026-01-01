# ADR 001: Order Database Schema with PostgreSQL and Entity Framework Core

## Status
Accepted

## Date
2025-01-01

## Context
The OTELStdApi application requires persistent storage for order data. Previously, the `CreateOrder` endpoint in `WeatherForecastController` was simulating order creation without actual database persistence. We need a robust, scalable solution for storing and managing order information.

## Decision
We have decided to implement PostgreSQL as the database backend with Entity Framework Core as the ORM, following the Repository and Unit of Work patterns.

### Technology Stack
- **Database**: PostgreSQL 
  - Connection: `localhost:5432`
  - Database: `OTELStdApiDb`
  - User: `otelstdapi_user`
- **ORM**: Entity Framework Core 10.0.1
- **Provider**: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0

### Architecture Patterns
1. **Repository Pattern** - Abstracts data access logic
2. **Unit of Work Pattern** - Manages transactions and coordinates repository operations
3. **Service Layer** - Business logic separated from controllers

### Database Schema

#### Orders Table
```sql
CREATE TABLE "Orders" (
    "Id" UUID PRIMARY KEY,
    "OrderNumber" VARCHAR(100) NOT NULL UNIQUE,
    "CustomerId" VARCHAR(200) NOT NULL,
    "CustomerType" VARCHAR(100) NOT NULL,
    "TotalAmount" DECIMAL(18,2) NOT NULL,
    "Status" VARCHAR(50) NOT NULL DEFAULT 'Pending',
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" TIMESTAMP NULL
);

CREATE UNIQUE INDEX "IX_Orders_OrderNumber" ON "Orders" ("OrderNumber");
CREATE INDEX "IX_Orders_CustomerId" ON "Orders" ("CustomerId");
```

### Components

#### Entity
- `Order` - Represents an order with properties: Id, OrderNumber, CustomerId, CustomerType, TotalAmount, Status, CreatedAt, UpdatedAt

#### DbContext
- `OrderDbContext` - EF Core DbContext with:
  - Connection pooling enabled
  - Query tracking optimization (AsNoTracking for read-only queries)
  - Proper indexing strategy

#### Repository Layer
- `IOrderRepository` - Interface defining data access operations
- `OrderRepository` - Implementation with optimized queries
- `IUnitOfWork` - Interface for transaction management
- `UnitOfWork` - Coordinates repositories and SaveChanges

#### Service Layer
- `IOrderService` - Interface for business logic operations
- `OrderService` - Implements order creation with:
  - Unique order number generation (`ORD-YYYYMMDD-XXXXXXXX`)
  - Database persistence
  - Full observability (traces, metrics, logs)

### Observability Integration

#### Traces
- `OrderService.CreateOrder` - Main order creation span
- `OrderService.SaveChanges` - Database save operation span
- Tags: `customer.id`, `customer.type`, `order.amount`, `order.id`, `order.number`

#### Metrics
- `orders.created.db` (Counter) - Number of orders created in database
- `orders.db.save.duration` (Histogram) - Database save operation duration in milliseconds

#### Logs
- DEBUG level for:
  - Database operations start/end
  - Query execution times
  - Order creation flow
- INFO level for successful operations
- ERROR level for failures

### Configuration

#### Connection String (appsettings.json)
```json
{
  "ConnectionStrings": {
    "OrderDatabase": "Host=localhost;Port=5432;Database=OTELStdApiDb;Username=otelstdapi_user;Password=your-secure-password-here"
  }
}
```

#### Dependency Injection (Program.cs)
```csharp
// PostgreSQL Database
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

// Repository and Unit of Work
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Services
builder.Services.AddScoped<IOrderService, OrderService>();
```

### Migration
Initial migration: `InitialOrderSchema`
- Creates Orders table
- Creates unique index on OrderNumber
- Creates index on CustomerId

## Consequences

### Positive
1. **Data Persistence** - Orders are now permanently stored in PostgreSQL
2. **ACID Compliance** - Database transactions ensure data consistency
3. **Scalability** - PostgreSQL handles concurrent operations efficiently
4. **Testability** - Repository pattern enables easy unit testing with mocked repositories
5. **Maintainability** - Clear separation of concerns (Controller ? Service ? Repository ? DbContext)
6. **Observability** - Full instrumentation with OpenTelemetry (traces, metrics, logs)
7. **Query Optimization** - AsNoTracking() used for read-only queries reduces memory overhead
8. **Connection Pooling** - Efficient database connection management

### Negative
1. **Additional Complexity** - More layers to maintain (Service, Repository, UnitOfWork)
2. **External Dependency** - Requires PostgreSQL server to be running
3. **Migration Management** - Database schema changes require migrations
4. **Setup Overhead** - Initial database setup and user configuration required

### Neutral
1. **Learning Curve** - Team must understand Repository and Unit of Work patterns
2. **Performance Trade-off** - Additional abstraction layers vs. direct DbContext usage

## Alternatives Considered

### 1. Direct DbContext Usage in Controllers
**Rejected** - Violates separation of concerns and makes testing harder

### 2. SQL Server instead of PostgreSQL
**Rejected** - PostgreSQL is open-source, has better JSON support (JSONB), and is more cost-effective

### 3. NoSQL Database (MongoDB, Cosmos DB)
**Rejected** - Orders require ACID transactions and relational integrity

### 4. In-Memory Database
**Rejected** - Data would be lost on application restart

## Implementation Notes

### Database Setup
```bash
# Create database
createdb -U postgres OTELStdApiDb

# Create user
CREATE USER otelstdapi_user WITH PASSWORD 'your-secure-password-here';
GRANT ALL PRIVILEGES ON DATABASE OTELStdApiDb TO otelstdapi_user;
```

### Apply Migrations
```bash
dotnet ef database update --context OrderDbContext
```

### Verify Schema
```bash
psql -U otelstdapi_user -d OTELStdApiDb -c "\d Orders"
```

## References
- [Entity Framework Core Documentation](https://docs.microsoft.com/en-us/ef/core/)
- [Repository Pattern](https://docs.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/infrastructure-persistence-layer-design)
- [Unit of Work Pattern](https://www.martinfowler.com/eaaCatalog/unitOfWork.html)
- [PostgreSQL Documentation](https://www.postgresql.org/docs/)
- [Npgsql EF Core Provider](https://www.npgsql.org/efcore/)

## Related ADRs
None

## Approvers
- Development Team
- Database Administrator
- DevOps Team
