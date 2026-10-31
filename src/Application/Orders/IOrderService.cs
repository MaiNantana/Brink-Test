using BrinkTest.Api.Contracts;

namespace BrinkTest.Api.Application.Orders;

public interface IOrderService
{
    Task<CreateOrderResult> CreateOrderAsync(
        CreateOrderRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}
