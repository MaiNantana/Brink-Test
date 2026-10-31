using BrinkTest.Api.Application.Orders;
using BrinkTest.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BrinkTest.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders", CreateOrderAsync)
            .WithName("CreateOrder")
            .WithSummary("Creates a pending-payment order and reserves its product stock.")
            .WithDescription(
                "Creates an order using current product prices, atomically reserves stock and records an " +
                "OrderCreatedEvent in the outbox. Send an Idempotency-Key header to make retries safe: a repeated " +
                "request returns 200 with the original order. Errors are returned as ProblemDetails.")
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderSummaryResponse>(StatusCodes.Status201Created)
            .Produces<OrderSummaryResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderRequest request,
        [FromHeader(Name = OrderService.IdempotencyKeyHeader)] string? idempotencyKey,
        IOrderService orderService,
        CancellationToken cancellationToken)
    {
        var result = await orderService.CreateOrderAsync(request, idempotencyKey, cancellationToken);

        return result.Error switch
        {
            CreateOrderError.None when result.IsReplay =>
                Results.Ok(result.Order),
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
            CreateOrderError.IdempotencyKeyReused =>
                Problem(StatusCodes.Status422UnprocessableEntity, "Idempotency key reused", result.Detail),
            _ => throw new InvalidOperationException($"Unhandled order error {result.Error}.")
        };
    }

    private static IResult Problem(int statusCode, string title, string? detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);
}
