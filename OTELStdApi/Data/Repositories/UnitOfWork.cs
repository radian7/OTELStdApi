namespace OTELStdApi.Data.Repositories
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly OrderDbContext _context;
        private IOrderRepository? _orderRepository;

        public UnitOfWork(OrderDbContext context)
        {
            _context = context;
        }

        public IOrderRepository Orders => _orderRepository ??= new OrderRepository(_context);

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
