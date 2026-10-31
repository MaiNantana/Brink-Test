using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrinkTest.Api.Data;
using BrinkTest.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrinkTest.Api.Tests;

public sealed class OrdersEndpointTests
{
    [Fact]
    public async Task CreateOrder_ValidRequest_CreatesOrderAndDecrementsStock()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock: 5, price: 12.50m);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 1,
            items = new[] { new { productId = 1, quantity = 2 } }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, summary.GetProperty("customerId").GetInt32());
        Assert.Equal("PendingPayment", summary.GetProperty("status").GetString());
        Assert.Equal(25.00m, summary.GetProperty("totalAmount").GetDecimal());
        Assert.NotEqual(Guid.Empty, summary.GetProperty("orderId").GetGuid());
        Assert.Equal(2, summary.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(12.50m, summary.GetProperty("items")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal($"/api/orders/{summary.GetProperty("orderId").GetGuid()}", response.Headers.Location?.OriginalString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(3, await db.Products.Where(product => product.Id == 1).Select(product => product.StockQuantity).SingleAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.OrderItems.CountAsync());
    }

    [Theory]
    [InlineData("""{"customerId":0,"items":[{"productId":1,"quantity":1}]}""")]
    [InlineData("""{"customerId":1,"items":[]}""")]
    [InlineData("""{"customerId":1,"items":[{"productId":1,"quantity":0}]}""")]
    [InlineData("""{"customerId":1,"items":[{"productId":0,"quantity":1}]}""")]
    public async Task CreateOrder_InvalidRequest_ReturnsValidationProblem(string payload)
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/orders",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task CreateOrder_UnknownCustomer_ReturnsNotFound()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 404,
            items = new[] { new { productId = 1, quantity = 1 } }
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Customer not found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateOrder_UnknownProduct_ReturnsNotFound()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 1,
            items = new[] { new { productId = 404, quantity = 1 } }
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Product not found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(false, 5, "Product is inactive")]
    [InlineData(true, 1, "Insufficient stock")]
    public async Task CreateOrder_UnavailableProduct_ReturnsConflictAndDoesNotCreateOrder(
        bool isActive,
        int stock,
        string expectedTitle)
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock, isActive: isActive);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 1,
            items = new[] { new { productId = 1, quantity = 2 } }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expectedTitle, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(stock, await db.Products.Where(product => product.Id == 1).Select(product => product.StockQuantity).SingleAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task CreateOrder_RepeatedProductItems_AggregatesQuantities()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock: 5, price: 3m);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 1,
            items = new[]
            {
                new { productId = 1, quantity = 1 },
                new { productId = 1, quantity = 2 }
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(9m, summary.GetProperty("totalAmount").GetDecimal());
        Assert.Single(summary.GetProperty("items").EnumerateArray());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(2, await db.Products.Where(product => product.Id == 1).Select(product => product.StockQuantity).SingleAsync());
    }

    [Fact]
    public async Task CreateOrder_RetriedWithSameIdempotencyKey_ReturnsOkWithOriginalOrder()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock: 5);
        using var client = factory.CreateClient();
        var payload = new { customerId = 1, items = new[] { new { productId = 1, quantity = 2 } } };

        var first = await PostWithIdempotencyKeyAsync(client, payload, "retry-key");
        var retry = await PostWithIdempotencyKeyAsync(client, payload, "retry-key");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var firstOrderId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        var retryOrderId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        Assert.Equal(firstOrderId, retryOrderId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(3, await db.Products.Where(product => product.Id == 1).Select(product => product.StockQuantity).SingleAsync());
    }

    [Fact]
    public async Task CreateOrder_IdempotencyKeyReusedWithDifferentPayload_ReturnsUnprocessableEntity()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock: 5);
        using var client = factory.CreateClient();
        await PostWithIdempotencyKeyAsync(client, new { customerId = 1, items = new[] { new { productId = 1, quantity = 2 } } }, "reused-key");

        var response = await PostWithIdempotencyKeyAsync(client, new { customerId = 1, items = new[] { new { productId = 1, quantity = 1 } } }, "reused-key");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Idempotency key reused", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateOrder_OrderInsertFailure_RollsBackStockUpdate()
    {
        using var factory = new OrdersApiFactory();
        await factory.InitializeDatabaseAsync();
        await SeedCustomerAndProductAsync(factory, stock: 5, price: 10m);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RejectOrderInsert BEFORE INSERT ON Orders BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        }
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = 1,
            items = new[] { new { productId = 1, quantity = 2 } }
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(5, await verifyDb.Products.Where(product => product.Id == 1).Select(product => product.StockQuantity).SingleAsync());
        Assert.Equal(0, await verifyDb.Orders.CountAsync());
        Assert.Equal(0, await verifyDb.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Swagger_DevelopmentEnvironment_ExposesUiAndOrderOperation()
    {
        using var factory = new OrdersApiFactory();
        using var client = factory.CreateClient();

        var uiResponse = await client.GetAsync("/swagger");
        var documentResponse = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, uiResponse.StatusCode);
        Assert.Contains("swagger", await uiResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, documentResponse.StatusCode);
        var document = await documentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/orders", out var orderPath));
        Assert.True(orderPath.TryGetProperty("post", out var operation));
        Assert.Equal("CreateOrder", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.Contains(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("in").GetString() == "header");
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("422", out _));
    }

    private static Task<HttpResponseMessage> PostWithIdempotencyKeyAsync(HttpClient client, object payload, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static async Task SeedCustomerAndProductAsync(
        OrdersApiFactory factory,
        int stock = 5,
        decimal price = 12.50m,
        bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        db.Customers.Add(new Customer { Id = 1, Name = "Test Customer" });
        db.Products.Add(new Product
        {
            Id = 1,
            Name = "Test Product",
            Price = price,
            StockQuantity = stock,
            IsActive = isActive
        });
        await db.SaveChangesAsync();
    }
}
