namespace BrinkTest.Api.Contracts;

public sealed class CreateOrderRequest
{
    public int CustomerId { get; init; }
    public List<CreateOrderItemRequest>? Items { get; init; }
}

public sealed class CreateOrderItemRequest
{
    public int ProductId { get; init; }
    public int Quantity { get; init; }
}
