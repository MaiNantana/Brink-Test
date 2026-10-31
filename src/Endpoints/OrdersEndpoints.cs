using BrinkTest.Api.Contracts;
using BrinkTest.Api.Data;
using BrinkTest.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrinkTest.Api.Endpoints;

public static class OrdersEndpoints
{
    private const decimal MaximumOrderTotal = 9999999999999999.99m;

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders", CreateOrderAsync)
            .WithName("CreateOrder")
            .WithSummary("Creates a pending order and reserves its product stock.")
            .Produces<OrderSummaryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderRequest request,
        OrdersDbContext db,
        ILogger<OrdersEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var quantities = AggregateQuantities(request.Items!);
        if (quantities is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Items)] = ["The combined quantity for a product cannot exceed Int32.MaxValue."]
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await db.Customers.AnyAsync(customer => customer.Id == request.CustomerId, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Customer not found",
                detail: $"Customer {request.CustomerId} does not exist.");
        }

        var productIds = quantities.Keys.ToArray();
        var products = await db.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        foreach (var (productId, quantity) in quantities)
        {
            if (!products.TryGetValue(productId, out var product))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Product not found",
                    detail: $"Product {productId} does not exist.");
            }

            if (!product.IsActive)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Product is inactive",
                    detail: $"Product {productId} is not available for ordering.");
            }

            if (product.StockQuantity < quantity)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Insufficient stock",
                    detail: $"Product {productId} does not have enough stock.");
            }
        }

        var totalAmount = 0m;
        foreach (var (productId, quantity) in quantities)
        {
            var lineTotal = products[productId].Price * quantity;
            if (lineTotal > MaximumOrderTotal - totalAmount)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Order total is too large",
                    detail: "The order total exceeds the supported monetary range.");
            }

            totalAmount += lineTotal;
        }

        foreach (var (productId, quantity) in quantities)
        {
            var affectedRows = await db.Products
                .Where(product =>
                    product.Id == productId &&
                    product.IsActive &&
                    product.StockQuantity >= quantity)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        product => product.StockQuantity,
                        product => product.StockQuantity - quantity),
                    cancellationToken);

            if (affectedRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                logger.LogInformation(
                    "Order creation rejected because stock changed concurrently for product {ProductId}.",
                    productId);
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Inventory changed",
                    detail: $"Product {productId} is no longer available in the requested quantity.");
            }
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            CreatedAtUtc = DateTime.UtcNow,
            Status = OrderStatus.Pending,
            TotalAmount = totalAmount,
            Items = quantities
                .Select(entry => new OrderItem
                {
                    ProductId = entry.Key,
                    Quantity = entry.Value,
                    UnitPrice = products[entry.Key].Price
                })
                .ToList()
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Created order {OrderId} for customer {CustomerId} with total {TotalAmount}.",
            order.Id,
            order.CustomerId,
            order.TotalAmount);

        return Results.Created($"/api/orders/{order.Id}", order.ToSummary());
    }

    private static Dictionary<string, string[]> Validate(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.CustomerId <= 0)
        {
            errors[nameof(request.CustomerId)] = ["CustomerId must be greater than zero."];
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            errors[nameof(request.Items)] = ["At least one order item is required."];
            return errors;
        }

        if (request.Items.Any(item => item is null))
        {
            errors[nameof(request.Items)] = ["Order items cannot be null."];
            return errors;
        }

        if (request.Items.Any(item => item.ProductId <= 0))
        {
            errors[nameof(request.Items)] = ["Every ProductId must be greater than zero."];
        }

        if (request.Items.Any(item => item.Quantity <= 0))
        {
            errors[nameof(request.Items)] = ["Every item Quantity must be greater than zero."];
        }

        return errors;
    }

    private static Dictionary<int, int>? AggregateQuantities(
        IReadOnlyCollection<CreateOrderItemRequest> items)
    {
        var quantities = new Dictionary<int, int>();

        foreach (var item in items)
        {
            if (!quantities.TryGetValue(item.ProductId, out var currentQuantity))
            {
                quantities[item.ProductId] = item.Quantity;
                continue;
            }

            if (currentQuantity > int.MaxValue - item.Quantity)
            {
                return null;
            }

            quantities[item.ProductId] = currentQuantity + item.Quantity;
        }

        return quantities;
    }
}
