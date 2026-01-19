using OTELStdApi.Data.Entities;

namespace OTELStdApi.Services
{
    public interface IOrderService
    {
        Task<Order> CreateOrderAsync(string customerId, string customerType, decimal totalAmount, string? description = null);
        Task<Order?> GetOrderByIdAsync(Guid id);
        Task<Order?> GetOrderByNumberAsync(string orderNumber);
        Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(string customerId);
    }
}
