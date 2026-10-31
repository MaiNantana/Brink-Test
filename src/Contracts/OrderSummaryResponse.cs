using BrinkTest.Api.Domain;

namespace BrinkTest.Api.Contracts;

public sealed record OrderSummaryResponse(
    Guid OrderId,
    int CustomerId,
    string Status,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderItemSummaryResponse> Items);

public sealed record OrderItemSummaryResponse(
    int ProductId,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public static class OrderSummaryMapper
{
    public static OrderSummaryResponse ToSummary(this Order order) =>
        new(
            order.Id,
            order.CustomerId,
            order.Status.ToString(),
            order.TotalAmount,
            order.CreatedAtUtc,
            order.Items
                .Select(item => new OrderItemSummaryResponse(
                    item.ProductId,
                    item.Quantity,
                    item.UnitPrice,
                    item.UnitPrice * item.Quantity))
                .ToArray());
}
