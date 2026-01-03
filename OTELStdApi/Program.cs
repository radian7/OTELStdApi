using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics.Metrics;
using OTELStdApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Konfiguracja
var serviceName = "OTELStdApi";
var serviceVersion = "1.0.0";
var otlpEndpoint = new Uri("http://localhost:4317"); // gRPC
//var otlpEndpoint = new Uri("http://localhost:4318"); // http

// Resource builder - wsp�lne atrybuty dla wszystkich sygna��w
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName, serviceVersion: serviceVersion)
    .AddAttributes(new Dictionary<string, object>
    {
        ["deployment.environment"] = builder.Environment.EnvironmentName,
        ["host.name"] = Environment.MachineName
    });

var resource = resourceBuilder.Build();

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(); // http://localhost:5125/swagger/index.html
builder.Services.AddOpenApi();

// HttpClientFactory dla FakeStore API
var fakeStoreConfig = builder.Configuration.GetSection("ExternalApis:FakeStore");
var baseUrl = fakeStoreConfig["BaseUrl"];
var timeoutSeconds = int.Parse(fakeStoreConfig["TimeoutSeconds"] ?? "1");

builder.Services.AddHttpClient("FakeStoreAPI", client =>
{
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

// Redis Cache
var redisConfig = builder.Configuration.GetSection("Redis");
var redisConnectionString = redisConfig["ConnectionString"];
var cacheDurationMinutes = int.Parse(redisConfig["CacheDurationMinutes"] ?? "1");

builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
{
    var options = StackExchange.Redis.ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    return StackExchange.Redis.ConnectionMultiplexer.Connect(options);
});

// Register cache service
builder.Services.AddScoped<ICacheService, RedisCacheService>();

// PostgreSQL Database
var connectionString = builder.Configuration.GetConnectionString("OrderDatabase");
builder.Services.AddDbContext<OTELStdApi.Data.OrderDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

// Register Repository and Unit of Work
builder.Services.AddScoped<OTELStdApi.Data.Repositories.IUnitOfWork, OTELStdApi.Data.Repositories.UnitOfWork>();

// Register Services
builder.Services.AddScoped<IOrderService, OrderService>();

// OpenTelemetry - traces, metrics, propagators i baggage
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(opts =>
            {
                // Filtruj health checks itp.
                opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
            })
            .AddHttpClientInstrumentation(opts =>
            {
                // Automatyczne propagowanie W3C Trace Context headers
                opts.RecordException = true;
            })
            .AddSource(serviceName) // W�asne ActivitySource
            // W3C Propagators: traceparent, tracestate, baggage
            .SetResourceBuilder(resourceBuilder)
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpoint;
            });
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            //.AddRuntimeInstrumentation()
            .AddMeter(serviceName) // W�asne Meter
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpoint;
            });
    });

// Logging z OpenTelemetry
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.AddOtlpExporter(opts =>
    {
        opts.Endpoint = otlpEndpoint;
    });
});

var app = builder.Build();

// Middleware do obs�ugi baggage i W3C Trace Context
app.Use(async (context, next) =>
{
    // Generuj Request ID je�li nie istnieje
    var requestId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
    if (string.IsNullOrEmpty(requestId))
    {
        requestId = Guid.NewGuid().ToString();
    }

    // Dodaj do context.Items dla �atwego dost�pu w kontrolerach
    context.Items["RequestId"] = requestId;
    context.Items["DeploymentEnvironment"] = builder.Environment.EnvironmentName;

    // Ustaw baggage dla Current Activity
    if (System.Diagnostics.Activity.Current != null)
    {
        System.Diagnostics.Activity.Current.AddTag("request.id", requestId);
        System.Diagnostics.Activity.Current.AddTag("deployment.environment", builder.Environment.EnvironmentName);
    }

    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok());

app.Run();
