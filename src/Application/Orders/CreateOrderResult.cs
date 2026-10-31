using BrinkTest.Api.Contracts;

namespace BrinkTest.Api.Application.Orders;

public enum CreateOrderError
{
    None,
    Validation,
    CustomerNotFound,
    ProductNotFound,
    ProductInactive,
    InsufficientStock,
    InventoryChanged,
    TotalOutOfRange,
    IdempotencyKeyReused
}

public sealed record CreateOrderResult
{
    private CreateOrderResult(
        CreateOrderError error,
        OrderSummaryResponse? order,
        bool isReplay,
        string? detail,
        IDictionary<string, string[]>? validationErrors)
    {
        Error = error;
        Order = order;
        IsReplay = isReplay;
        Detail = detail;
        ValidationErrors = validationErrors;
    }

    public CreateOrderError Error { get; }
    public OrderSummaryResponse? Order { get; }

    /// <summary>True when the order already existed for the request's idempotency key.</summary>
    public bool IsReplay { get; }

    public string? Detail { get; }
    public IDictionary<string, string[]>? ValidationErrors { get; }
    public bool IsSuccess => Error == CreateOrderError.None;

    public static CreateOrderResult Created(OrderSummaryResponse order) =>
        new(CreateOrderError.None, order, false, null, null);

    public static CreateOrderResult Replayed(OrderSummaryResponse order) =>
        new(CreateOrderError.None, order, true, null, null);

    public static CreateOrderResult Invalid(IDictionary<string, string[]> validationErrors) =>
        new(CreateOrderError.Validation, null, false, null, validationErrors);

    public static CreateOrderResult Failure(CreateOrderError error, string detail) =>
        new(error, null, false, detail, null);
}
