using System.Diagnostics;
using System.Diagnostics.Metrics;
using OTELStdApi.Data.Entities;
using OTELStdApi.Data.Repositories;

namespace OTELStdApi.Services
{
    public class OrderService : IOrderService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<OrderService> _logger;
        
        // Observability
        private static readonly ActivitySource ActivitySource = new("my-dotnet-app");
        private static readonly Meter Meter = new("my-dotnet-app");
        private static readonly Counter<long> OrdersCreatedDb = Meter.CreateCounter<long>("orders.created.db");
        private static readonly Histogram<double> DbSaveDuration = Meter.CreateHistogram<double>("orders.db.save.duration", "ms");

        public OrderService(IUnitOfWork unitOfWork, ILogger<OrderService> logger)
        {
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        public async Task<Order> CreateOrderAsync(string customerId, string customerType, decimal totalAmount, string? description = null)
        {
            using var activity = ActivitySource.StartActivity("OrderService.CreateOrder");
            activity?.SetTag("customer.id", customerId);
            activity?.SetTag("customer.type", customerType);
            activity?.SetTag("order.amount", totalAmount);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Generate unique order number
                var orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";

                var order = new Order
                {
                    Id = Guid.NewGuid(),
                    OrderNumber = orderNumber,
                    CustomerId = customerId,
                    CustomerType = customerType,
                    TotalAmount = totalAmount,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow,
                    Description = description
                };

                _logger.LogDebug("Creating order in database: {OrderNumber} for customer {CustomerId}", orderNumber, customerId);

                // Add to repository
                await _unitOfWork.Orders.AddAsync(order);

                // Save to database with observability
                using (var saveActivity = ActivitySource.StartActivity("OrderService.SaveChanges"))
                {
                    saveActivity?.SetTag("order.number", orderNumber);
                    
                    var saveStopwatch = Stopwatch.StartNew();
                    await _unitOfWork.SaveChangesAsync();
                    saveStopwatch.Stop();

                    DbSaveDuration.Record(saveStopwatch.ElapsedMilliseconds);
                    _logger.LogDebug("Order saved to database in {ElapsedMilliseconds}ms", saveStopwatch.ElapsedMilliseconds);
                }

                // Metrics
                OrdersCreatedDb.Add(1, new KeyValuePair<string, object?>("customer.type", customerType));

                activity?.SetTag("order.id", order.Id);
                activity?.SetTag("order.number", orderNumber);

                _logger.LogInformation("Order created successfully: {OrderNumber}, OrderId: {OrderId}", orderNumber, order.Id);

                return order;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Failed to create order for customer {CustomerId}", customerId);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogDebug("CreateOrderAsync completed in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            }
        }

        public async Task<Order?> GetOrderByIdAsync(Guid id)
        {
            using var activity = ActivitySource.StartActivity("OrderService.GetOrderById");
            activity?.SetTag("order.id", id);

            _logger.LogDebug("Fetching order by ID: {OrderId}", id);

            var order = await _unitOfWork.Orders.GetByIdAsync(id);

            if (order == null)
            {
                _logger.LogDebug("Order not found: {OrderId}", id);
            }
            else
            {
                _logger.LogDebug("Order found: {OrderNumber}", order.OrderNumber);
            }

            return order;
        }

        public async Task<Order?> GetOrderByNumberAsync(string orderNumber)
        {
            using var activity = ActivitySource.StartActivity("OrderService.GetOrderByNumber");
            activity?.SetTag("order.number", orderNumber);

            _logger.LogDebug("Fetching order by number: {OrderNumber}", orderNumber);

            var order = await _unitOfWork.Orders.GetByOrderNumberAsync(orderNumber);

            if (order == null)
            {
                _logger.LogDebug("Order not found: {OrderNumber}", orderNumber);
            }
            else
            {
                _logger.LogDebug("Order found: {OrderId}", order.Id);
            }

            return order;
        }

        public async Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(string customerId)
        {
            using var activity = ActivitySource.StartActivity("OrderService.GetOrdersByCustomerId");
            activity?.SetTag("customer.id", customerId);

            _logger.LogDebug("Fetching orders for customer: {CustomerId}", customerId);

            var orders = await _unitOfWork.Orders.GetByCustomerIdAsync(customerId);
            var ordersList = orders.ToList();

            _logger.LogDebug("Found {OrderCount} orders for customer {CustomerId}", ordersList.Count, customerId);

            return ordersList;
        }
    }
}
