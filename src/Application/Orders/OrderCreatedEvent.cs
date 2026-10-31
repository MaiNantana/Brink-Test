namespace BrinkTest.Api.Application.Orders;

public sealed record OrderCreatedEvent(
    Guid OrderId,
    int CustomerId,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderCreatedEventItem> Items);

public sealed record OrderCreatedEventItem(int ProductId, int Quantity, decimal UnitPrice);
