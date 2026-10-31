using System.Text.Json;
using BrinkTest.Api.Application.Orders;
using BrinkTest.Api.Contracts;
using BrinkTest.Api.Data;
using BrinkTest.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace BrinkTest.Api.Tests;

public sealed class OrderServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeLogger<OrderService> _logger = new();
    private OrdersDbContext _db = null!;
    private OrderService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new OrderService(_db, new FakeTimeProvider(Now), _logger);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task CreateOrderAsync_AvailableProduct_CreatesPendingOrderAndReservesStock()
    {
        // Arrange
        await SeedAsync(stock: 10, price: 15.00m);
        var request = CreateRequest(quantity: 3);

        // Act
        var result = await _service.CreateOrderAsync(request, idempotencyKey: null, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Order);
        Assert.NotEqual(Guid.Empty, result.Order.OrderId);
        Assert.Equal("PendingPayment", result.Order.Status);
        Assert.Equal(45.00m, result.Order.TotalAmount);
        Assert.Equal(Now.UtcDateTime, result.Order.CreatedAtUtc);

        _db.ChangeTracker.Clear();
        var savedOrder = await _db.Orders.Include(order => order.Items).SingleAsync();
        Assert.Equal(result.Order.OrderId, savedOrder.Id);
        Assert.Equal(15.00m, Assert.Single(savedOrder.Items).UnitPrice);
        Assert.Equal(7, (await _db.Products.SingleAsync()).StockQuantity);
        Assert.Contains(_logger.Collector.GetSnapshot(), log =>
            log.Level == LogLevel.Information && log.Message.Contains($"Created order {savedOrder.Id}"));

        var outboxMessage = await _db.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(OrderCreatedEvent), outboxMessage.Type);
        Assert.Null(outboxMessage.ProcessedAtUtc);
        var orderCreated = JsonSerializer.Deserialize<OrderCreatedEvent>(outboxMessage.Payload, JsonSerializerOptions.Web);
        Assert.Equal(savedOrder.Id, orderCreated!.OrderId);
        Assert.Equal(45.00m, orderCreated.TotalAmount);
        Assert.Equal(3, Assert.Single(orderCreated.Items).Quantity);
    }

    [Fact]
    public async Task CreateOrderAsync_SameIdempotencyKeyTwice_ReturnsOriginalOrderAndReservesStockOnce()
    {
        // Arrange
        await SeedAsync(stock: 10);
        var request = CreateRequest(quantity: 3);
        var first = await _service.CreateOrderAsync(request, "order-key-1", CancellationToken.None);

        // Act
        var second = await _service.CreateOrderAsync(request, "order-key-1", CancellationToken.None);

        // Assert
        Assert.False(first.IsReplay);
        Assert.True(second.IsSuccess);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Order!.OrderId, second.Order!.OrderId);

        _db.ChangeTracker.Clear();
        Assert.Equal(1, await _db.Orders.CountAsync());
        Assert.Equal(1, await _db.OutboxMessages.CountAsync());
        Assert.Equal(7, (await _db.Products.SingleAsync()).StockQuantity);
        Assert.Equal(LogLevel.Information, _logger.LatestRecord.Level);
        Assert.Contains("duplicate", _logger.LatestRecord.Message);
    }

    [Fact]
    public async Task CreateOrderAsync_IdempotencyKeyReusedWithDifferentRequest_ReturnsIdempotencyKeyReused()
    {
        // Arrange
        await SeedAsync(stock: 10);
        await _service.CreateOrderAsync(CreateRequest(quantity: 3), "order-key-1", CancellationToken.None);

        // Act
        var result = await _service.CreateOrderAsync(CreateRequest(quantity: 5), "order-key-1", CancellationToken.None);

        // Assert
        Assert.Equal(CreateOrderError.IdempotencyKeyReused, result.Error);
        _db.ChangeTracker.Clear();
        Assert.Equal(1, await _db.Orders.CountAsync());
        Assert.Equal(7, (await _db.Products.SingleAsync()).StockQuantity);
        Assert.Equal(LogLevel.Warning, _logger.LatestRecord.Level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this-idempotency-key-is-longer-than-the-sixty-four-characters-limit")]
    public async Task CreateOrderAsync_InvalidIdempotencyKey_ReturnsValidationError(string idempotencyKey)
    {
        // Arrange
        await SeedAsync(stock: 10);

        // Act
        var result = await _service.CreateOrderAsync(CreateRequest(quantity: 1), idempotencyKey, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOrderError.Validation, result.Error);
        Assert.True(result.ValidationErrors!.ContainsKey(OrderService.IdempotencyKeyHeader));
        await AssertNothingChangedAsync(expectedStock: 10);
    }

    [Fact]
    public async Task CreateOrderAsync_InsufficientStock_ReturnsInsufficientStockAndDoesNotCreateOrder()
    {
        // Arrange
        await SeedAsync(stock: 2);
        var request = CreateRequest(quantity: 3);

        // Act
        var result = await _service.CreateOrderAsync(request, idempotencyKey: null, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(CreateOrderError.InsufficientStock, result.Error);
        await AssertNothingChangedAsync(expectedStock: 2);
        Assert.Equal(LogLevel.Warning, _logger.LatestRecord.Level);
        Assert.Contains("insufficient stock", _logger.LatestRecord.Message);
    }

    [Fact]
    public async Task CreateOrderAsync_InactiveProduct_ReturnsProductInactiveAndDoesNotCreateOrder()
    {
        // Arrange
        await SeedAsync(stock: 10, isActive: false);
        var request = CreateRequest(quantity: 1);

        // Act
        var result = await _service.CreateOrderAsync(request, idempotencyKey: null, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOrderError.ProductInactive, result.Error);
        await AssertNothingChangedAsync(expectedStock: 10);
        Assert.Equal(LogLevel.Warning, _logger.LatestRecord.Level);
        Assert.Contains("is inactive", _logger.LatestRecord.Message);
    }

    [Fact]
    public async Task CreateOrderAsync_InvalidQuantity_ReturnsValidationErrorWithoutQueryingProducts()
    {
        // Arrange
        await SeedAsync(stock: 10);
        var request = CreateRequest(quantity: 0);

        // Act
        var result = await _service.CreateOrderAsync(request, idempotencyKey: null, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOrderError.Validation, result.Error);
        Assert.True(result.ValidationErrors!.ContainsKey(nameof(CreateOrderRequest.Items)));
        await AssertNothingChangedAsync(expectedStock: 10);
        Assert.Equal(LogLevel.Warning, _logger.LatestRecord.Level);
    }

    private static CreateOrderRequest CreateRequest(int quantity) => new()
    {
        CustomerId = 1,
        Items = [new CreateOrderItemRequest { ProductId = 1, Quantity = quantity }]
    };

    private async Task SeedAsync(int stock, decimal price = 10.00m, bool isActive = true)
    {
        _db.Customers.Add(new Customer { Id = 1, Name = "Test Customer" });
        _db.Products.Add(new Product
        {
            Id = 1,
            Name = "Test Product",
            Price = price,
            StockQuantity = stock,
            IsActive = isActive
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task AssertNothingChangedAsync(int expectedStock)
    {
        _db.ChangeTracker.Clear();
        Assert.Equal(0, await _db.Orders.CountAsync());
        Assert.Equal(0, await _db.OutboxMessages.CountAsync());
        Assert.Equal(expectedStock, (await _db.Products.SingleAsync()).StockQuantity);
    }
}
