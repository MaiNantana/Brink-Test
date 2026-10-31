using BrinkTest.Api.Application.Orders;
using BrinkTest.Api.Contracts;

namespace BrinkTest.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders", CreateOrderAsync)
            .WithName("CreateOrder")
            .WithSummary("Creates a pending order and reserves its product stock.")
            .WithDescription(
                "Creates an order using current product prices and atomically reserves stock. " +
                "Returns validation, missing-resource, and inventory-conflict details as ProblemDetails.")
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderSummaryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderRequest request,
        IOrderService orderService,
        CancellationToken cancellationToken)
    {
        var result = await orderService.CreateOrderAsync(request, cancellationToken);

        return result.Error switch
        {
            CreateOrderError.None =>
                Results.Created($"/api/orders/{result.Order!.OrderId}", result.Order),
            CreateOrderError.Validation =>
                Results.ValidationProblem(result.ValidationErrors!),
            CreateOrderError.CustomerNotFound =>
                Problem(StatusCodes.Status404NotFound, "Customer not found", result.Detail),
            CreateOrderError.ProductNotFound =>
                Problem(StatusCodes.Status404NotFound, "Product not found", result.Detail),
            CreateOrderError.ProductInactive =>
                Problem(StatusCodes.Status409Conflict, "Product is inactive", result.Detail),
            CreateOrderError.InsufficientStock =>
                Problem(StatusCodes.Status409Conflict, "Insufficient stock", result.Detail),
            CreateOrderError.InventoryChanged =>
                Problem(StatusCodes.Status409Conflict, "Inventory changed", result.Detail),
            CreateOrderError.TotalOutOfRange =>
                Problem(StatusCodes.Status422UnprocessableEntity, "Order total is too large", result.Detail),
            _ => throw new InvalidOperationException($"Unhandled order error {result.Error}.")
        };
    }

    private static IResult Problem(int statusCode, string title, string? detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);
}
