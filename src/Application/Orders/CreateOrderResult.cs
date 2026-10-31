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
    TotalOutOfRange
}

public sealed record CreateOrderResult
{
    private CreateOrderResult(
        CreateOrderError error,
        OrderSummaryResponse? order,
        string? detail,
        IDictionary<string, string[]>? validationErrors)
    {
        Error = error;
        Order = order;
        Detail = detail;
        ValidationErrors = validationErrors;
    }

    public CreateOrderError Error { get; }
    public OrderSummaryResponse? Order { get; }
    public string? Detail { get; }
    public IDictionary<string, string[]>? ValidationErrors { get; }
    public bool IsSuccess => Error == CreateOrderError.None;

    public static CreateOrderResult Success(OrderSummaryResponse order) =>
        new(CreateOrderError.None, order, null, null);

    public static CreateOrderResult Invalid(IDictionary<string, string[]> validationErrors) =>
        new(CreateOrderError.Validation, null, null, validationErrors);

    public static CreateOrderResult Failure(CreateOrderError error, string detail) =>
        new(error, null, detail, null);
}
