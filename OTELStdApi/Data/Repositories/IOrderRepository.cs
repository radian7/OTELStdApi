using OTELStdApi.Data.Entities;

namespace OTELStdApi.Data.Repositories
{
    public interface IOrderRepository
    {
        Task<Order?> GetByIdAsync(Guid id);
        Task<Order?> GetByOrderNumberAsync(string orderNumber);
        Task<IEnumerable<Order>> GetAllAsync();
        Task<IEnumerable<Order>> GetByCustomerIdAsync(string customerId);
        Task AddAsync(Order order);
        void Update(Order order);
        void Delete(Order order);
    }
}
