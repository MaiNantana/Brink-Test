# Prompts to build `src/` and `test/` with GitHub Copilot

Use **Copilot Chat → Agent mode** in VS Code. Open an empty folder that contains only
`.github/copilot-instructions.md` (copy it from this repo first; Copilot reads it automatically).

Run the prompts **in order, one per chat message**. After each prompt run
`dotnet build BrinkTest.sln` and, from prompt 3 on, `dotnet test BrinkTest.sln`.
If something fails, paste the error back to Copilot.
Export the chat after each prompt (`...` → Export Chat) into `docs/`.

---

## Prompt 1 – Solution, domain, data, migrations

```text
Follow .github/copilot-instructions.md.

Create a .NET 10 solution for an Orders API.

SOLUTION
- BrinkTest.sln (classic .sln format) containing:
  - src/BrinkTest.Api.csproj (Microsoft.NET.Sdk.Web, net10.0, Nullable and
    ImplicitUsings enabled, RootNamespace BrinkTest.Api)
  - test/BrinkTest.Api.Tests/BrinkTest.Api.Tests.csproj (created in a later prompt)
- src packages:
  Microsoft.EntityFrameworkCore.SqlServer 10.0.0,
  Microsoft.EntityFrameworkCore.Design 10.0.0 (PrivateAssets all),
  Swashbuckle.AspNetCore 10.0.0.
  In a separate ItemGroup with the comment
  "Pin transitive dependencies to patched versions (NU1903)":
  Microsoft.OpenApi 2.7.5, System.Security.Cryptography.Pkcs 9.0.21.
  Do NOT add Microsoft.AspNetCore.OpenApi.

DOMAIN (src/Domain, namespace BrinkTest.Api.Domain, all sealed classes)
- Customer: int Id, required string Name
- Product: int Id, required string Name, decimal Price, int StockQuantity, bool IsActive
- Order: Guid Id, int CustomerId, Customer Customer = null!, DateTime CreatedAtUtc,
  OrderStatus Status, decimal TotalAmount, string? IdempotencyKey,
  List<OrderItem> Items = []
- OrderItem: long Id, Guid OrderId, Order Order = null!, int ProductId,
  Product Product = null!, int Quantity, decimal UnitPrice
- enum OrderStatus { PendingPayment, Paid, Cancelled, ReadyToShip }

DATA (src/Data, namespace BrinkTest.Api.Data)
- OutboxMessage (sealed): Guid Id, required string Type, required string Payload,
  DateTime OccurredAtUtc, DateTime? ProcessedAtUtc. Short XML doc: transactional
  outbox row, a relay publishes it to RabbitMQ using Id as the message id.
- OrdersDbContext (sealed, primary constructor with DbContextOptions<OrdersDbContext>)
  with expression-bodied DbSets: Customers, Products, Orders, OrderItems, OutboxMessages.
  OnModelCreating:
  - Customer: key Id, Name max 200 required
  - Product: key Id, Name max 200 required, Price precision (18,2),
    check constraints CK_Products_Price "[Price] >= 0" and
    CK_Products_StockQuantity "[StockQuantity] >= 0"
  - Order: key Id, Status stored as string max 32, TotalAmount (18,2),
    CreatedAtUtc required, IdempotencyKey max 64 with a UNIQUE index filtered
    "[IdempotencyKey] IS NOT NULL", FK to Customer with Restrict delete, no navigation back
  - OrderItem: key Id, UnitPrice (18,2), unique index (OrderId, ProductId),
    FK to Order (Items) cascade, FK to Product restrict
  - OutboxMessage: key Id, Type max 200 required, Payload required,
    index on ProcessedAtUtc

CONFIG
- src/appsettings.json: ConnectionStrings:OrdersDb =
  "Server=(localdb)\\MSSQLLocalDB;Database=BrinkOrders;Trusted_Connection=True;TrustServerCertificate=True",
  Logging default Information, Microsoft.AspNetCore Warning,
  Microsoft.EntityFrameworkCore.Database.Command Warning, AllowedHosts "*".
- src/appsettings.Development.json: same but EF Database.Command Information.

Leave Program.cs minimal for now (registers the DbContext, see prompt 2).
Then run: dotnet ef migrations add InitialCreate --project src --output-dir Data/Migrations
Build must have zero warnings.
```

**Check:** `dotnet build` shows no NU19xx warnings, the migration includes the filtered unique index and the check constraints.

---

## Prompt 2 – Application service, endpoint, Program.cs

```text
Follow .github/copilot-instructions.md.

CONTRACTS (src/Contracts, namespace BrinkTest.Api.Contracts)
- CreateOrderRequest (sealed class, init props): int CustomerId,
  List<CreateOrderItemRequest>? Items
- CreateOrderItemRequest (sealed class, init props): int ProductId, int Quantity
- sealed record OrderSummaryResponse(Guid OrderId, int CustomerId, string Status,
  decimal TotalAmount, DateTime CreatedAtUtc, IReadOnlyList<OrderItemSummaryResponse> Items)
- sealed record OrderItemSummaryResponse(int ProductId, int Quantity,
  decimal UnitPrice, decimal LineTotal)
- static class OrderSummaryMapper with extension ToSummary(this Order order)
  (Status = enum name, LineTotal = UnitPrice * Quantity)

APPLICATION (src/Application/Orders, namespace BrinkTest.Api.Application.Orders)
- enum CreateOrderError { None, Validation, CustomerNotFound, ProductNotFound,
  ProductInactive, InsufficientStock, InventoryChanged, TotalOutOfRange,
  IdempotencyKeyReused }
- sealed record CreateOrderResult with private constructor and properties
  Error, Order (OrderSummaryResponse?), IsReplay (XML doc: true when the order
  already existed for the idempotency key), Detail, ValidationErrors
  (IDictionary<string,string[]>?), IsSuccess => Error == None.
  Factories: Created(order), Replayed(order), Invalid(errors), Failure(error, detail).
- sealed record OrderCreatedEvent(Guid OrderId, int CustomerId, decimal TotalAmount,
  DateTime CreatedAtUtc, IReadOnlyList<OrderCreatedEventItem> Items) and
  sealed record OrderCreatedEventItem(int ProductId, int Quantity, decimal UnitPrice)
- IOrderService: Task<CreateOrderResult> CreateOrderAsync(CreateOrderRequest request,
  string? idempotencyKey, CancellationToken cancellationToken)
- OrderService (sealed, primary constructor: OrdersDbContext db, TimeProvider timeProvider,
  ILogger<OrderService> logger). Public consts IdempotencyKeyHeader = "Idempotency-Key",
  IdempotencyKeyMaxLength = 64; private const MaximumOrderTotal = 9999999999999999.99m.

  CreateOrderAsync steps, in this order:
  1. Validate (private static, returns Dictionary<string,string[]>):
     - idempotencyKey not null but blank or > 64 chars → error under "Idempotency-Key"
     - CustomerId <= 0 → "CustomerId must be greater than zero."
     - Items null/empty → "At least one order item is required." (stop)
     - any null item → "Order items cannot be null." (stop)
     - any ProductId <= 0 → "Every ProductId must be greater than zero."
     - any Quantity <= 0 → "Every item Quantity must be greater than zero."
     If errors: LogWarning and return Invalid.
  2. AggregateQuantities: merge duplicate ProductIds into Dictionary<int,int>;
     return null on int overflow → LogWarning, Invalid with
     "The combined quantity for a product cannot exceed Int32.MaxValue."
  3. If idempotencyKey given: find the order by key (AsNoTracking, Include Items).
     If found → Replay (see below).
  4. await using transaction = BeginTransactionAsync.
  5. Customer must exist (AnyAsync) else CustomerNotFound "Customer {id} does not exist."
  6. Load all requested products in ONE AsNoTracking query into a dictionary.
     For each product: missing → ProductNotFound "Product {id} does not exist.";
     !IsActive → ProductInactive "Product {id} is not available for ordering.";
     StockQuantity < quantity → InsufficientStock "Product {id} does not have enough stock."
  7. Total = sum(Price * quantity); if it would exceed MaximumOrderTotal →
     TotalOutOfRange "The order total exceeds the supported monetary range."
  8. For each product: ExecuteUpdateAsync on
     Where(Id == productId && IsActive && StockQuantity >= quantity)
     setting StockQuantity = StockQuantity - quantity.
     affectedRows != 1 → InventoryChanged
     "Product {id} is no longer available in the requested quantity."
  9. Create Order (Guid.NewGuid, CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
     Status PendingPayment, IdempotencyKey, items with UnitPrice from DB).
     Add it AND an OutboxMessage (Type = nameof(OrderCreatedEvent), Payload =
     JsonSerializer.Serialize(event, JsonSerializerOptions.Web),
     OccurredAtUtc = order.CreatedAtUtc).
  10. SaveChangesAsync. catch (DbUpdateException) when key is not null:
      rollback, ChangeTracker.Clear(), find the order by key; if null rethrow,
      else return Replay. (Comment: a concurrent request with the same key
      committed first; the unique index rejected this insert.)
  11. CommitAsync, LogInformation "Created order {OrderId} for customer
      {CustomerId} with total {TotalAmount}.", return Created(order.ToSummary()).
  Every failure path: RollbackAsync(CancellationToken.None) first, then exactly one
  structured LogWarning starting with "Order creation rejected ..." that includes
  {CustomerId} and, where relevant, {ProductId}, {RequestedQuantity}, {AvailableQuantity}.

  Replay(existingOrder, customerId, quantities): the request matches when the
  customer is the same and the item set (ProductId → Quantity) is identical.
  Match → LogInformation "Returned existing order {OrderId} for customer
  {CustomerId} because the request was a duplicate." and Replayed(summary).
  No match → LogWarning and Failure(IdempotencyKeyReused,
  "The Idempotency-Key was already used with a different request.").

ENDPOINT (src/Endpoints/OrdersEndpoints.cs, static class)
- MapOrdersEndpoints(this IEndpointRouteBuilder) maps POST /api/orders with
  WithName("CreateOrder"), WithSummary, WithDescription (mention outbox and
  Idempotency-Key), Accepts<CreateOrderRequest>, Produces<OrderSummaryResponse>(201),
  Produces<OrderSummaryResponse>(200), ProducesValidationProblem,
  ProducesProblem(404), (409), (422).
- Handler parameters: CreateOrderRequest request,
  [FromHeader(Name = OrderService.IdempotencyKeyHeader)] string? idempotencyKey,
  IOrderService orderService, CancellationToken.
- Map with a switch expression:
  None + IsReplay → Results.Ok(order); None → Results.Created("/api/orders/{id}", order);
  Validation → Results.ValidationProblem;
  CustomerNotFound 404 "Customer not found"; ProductNotFound 404 "Product not found";
  ProductInactive 409 "Product is inactive"; InsufficientStock 409 "Insufficient stock";
  InventoryChanged 409 "Inventory changed"; TotalOutOfRange 422 "Order total is too large";
  IdempotencyKeyReused 422 "Idempotency key reused";
  default → throw InvalidOperationException.
  Use a private helper Problem(statusCode, title, detail).

PROGRAM.CS (top-level statements)
- AddDbContext<OrdersDbContext> reading ConnectionStrings:OrdersDb from
  IConfiguration inside the options callback; throw InvalidOperationException
  "Connection string 'OrdersDb' is required. Set ConnectionStrings__OrdersDb." if missing.
- AddSingleton(TimeProvider.System), AddScoped<IOrderService, OrderService>,
  AddProblemDetails, AddEndpointsApiExplorer, AddSwaggerGen.
- UseExceptionHandler, UseStatusCodePages, Swagger + SwaggerUI in Development only,
  MapOrdersEndpoints, Run.
- End with: public partial class Program;

Then add migration AddIdempotencyKeyAndOutbox (if the model changed after
InitialCreate) and include migrationBuilder.Sql to convert Status 'Pending' →
'PendingPayment' in Up and back in Down.
```

**Check:** the endpoint has no `OrdersDbContext`; every failure path rolls back before logging; the outbox row is added before the single `SaveChangesAsync`.

---

## Prompt 3 – Test project (unit + integration)

```text
Follow .github/copilot-instructions.md.

Create test/BrinkTest.Api.Tests/BrinkTest.Api.Tests.csproj (net10.0, IsPackable false,
global using Xunit) referencing ../../src/BrinkTest.Api.csproj, and add it to BrinkTest.sln.
Packages: xunit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.12.0,
coverlet.collector 6.0.2, Microsoft.AspNetCore.Mvc.Testing 10.0.0,
Microsoft.EntityFrameworkCore.Sqlite 10.0.0, SQLitePCLRaw.lib.e_sqlite3 2.1.12,
Microsoft.Extensions.Diagnostics.Testing 10.0.0, Microsoft.Extensions.TimeProvider.Testing 10.0.0.
Namespace BrinkTest.Api.Tests. Use SQLite ":memory:" (NOT EF InMemory, it has no
transactions or ExecuteUpdateAsync). Every test has its own database.

1) OrdersApiFactory : WebApplicationFactory<Program>
   - opens a SqliteConnection("Data Source=:memory:") in the constructor
   - ConfigureWebHost: RemoveAll OrdersDbContext, DbContextOptions<OrdersDbContext>
     and IDbContextOptionsConfiguration<OrdersDbContext>, then AddDbContext with UseSqlite(connection)
   - InitializeDatabaseAsync() → EnsureCreatedAsync
   - Dispose(bool) disposes the connection

2) OrderServiceTests (UNIT, IAsyncLifetime) – creates OrderService directly with
   the SQLite DbContext, new FakeTimeProvider(2026-10-31 09:00 UTC) and
   FakeLogger<OrderService>. Helpers: CreateRequest(quantity) for customer 1 /
   product 1; SeedAsync(stock, price = 10, isActive = true) then ChangeTracker.Clear();
   AssertNothingChangedAsync(expectedStock) → 0 orders, 0 outbox messages, stock unchanged.
   Use // Arrange // Act // Assert comments. Tests:
   - CreateOrderAsync_AvailableProduct_CreatesPendingOrderAndReservesStock
     (stock 10, price 15, qty 3 → total 45, status "PendingPayment",
     CreatedAtUtc = fake time, saved UnitPrice 15, stock 7, Information log
     "Created order {id}", exactly one outbox row of type OrderCreatedEvent with
     null ProcessedAtUtc whose JSON payload has the OrderId, total 45, quantity 3)
   - CreateOrderAsync_SameIdempotencyKeyTwice_ReturnsOriginalOrderAndReservesStockOnce
   - CreateOrderAsync_IdempotencyKeyReusedWithDifferentRequest_ReturnsIdempotencyKeyReused
   - CreateOrderAsync_InvalidIdempotencyKey_ReturnsValidationError
     [Theory] "", "   ", a 65+ char key
   - CreateOrderAsync_InsufficientStock_ReturnsInsufficientStockAndDoesNotCreateOrder
     (stock 2, qty 3; Warning log containing "insufficient stock")
   - CreateOrderAsync_InactiveProduct_ReturnsProductInactiveAndDoesNotCreateOrder
     (Warning log containing "is inactive")
   - CreateOrderAsync_InvalidQuantity_ReturnsValidationErrorWithoutQueryingProducts

3) OrdersEndpointTests (INTEGRATION) – each test: new OrdersApiFactory,
   InitializeDatabaseAsync, seed customer 1 + product 1, CreateClient.
   - CreateOrder_ValidRequest_CreatesOrderAndDecrementsStock (201, body fields,
     Location header "/api/orders/{orderId}", stock 5 → 3, 1 order, 1 item)
   - CreateOrder_InvalidRequest_ReturnsValidationProblem [Theory] with raw JSON:
     customerId 0; empty items; quantity 0; productId 0 →
     400 application/problem+json with "errors"
   - CreateOrder_UnknownCustomer_ReturnsNotFound (title "Customer not found")
   - CreateOrder_UnknownProduct_ReturnsNotFound (title "Product not found")
   - CreateOrder_UnavailableProduct_ReturnsConflictAndDoesNotCreateOrder
     [Theory] (inactive, stock 5, "Product is inactive") and
     (active, stock 1, "Insufficient stock"); stock unchanged, 0 orders
   - CreateOrder_RepeatedProductItems_AggregatesQuantities
     (price 3, two lines of product 1 with qty 1 and 2, stock 5 → 201,
     total 9, a single summary item, stock 2)
   - CreateOrder_RetriedWithSameIdempotencyKey_ReturnsOkWithOriginalOrder
     (201 then 200, same orderId, 1 order, stock reserved once)
   - CreateOrder_IdempotencyKeyReusedWithDifferentPayload_ReturnsUnprocessableEntity
     (title "Idempotency key reused")
   - CreateOrder_OrderInsertFailure_RollsBackStockUpdate: create a SQLite trigger
     "BEFORE INSERT ON Orders ... RAISE(ABORT, 'test failure')" → 500, stock
     unchanged, 0 orders, 0 outbox messages
   - Swagger_DevelopmentEnvironment_ExposesUiAndOrderOperation: /swagger is 200,
     /swagger/v1/swagger.json has POST /api/orders with operationId CreateOrder,
     responses 200, 201, 400, 404, 409, 422 and a header parameter "Idempotency-Key"
   Private helpers: PostWithIdempotencyKeyAsync(client, payload, key) and
   SeedCustomerAndProductAsync(factory, stock = 5, price = 12.50m, isActive = true).

Run dotnet test BrinkTest.sln; all tests must pass with zero build warnings.
Finally list any behaviour that is still untested.
```

**Check:** temporarily remove `StockQuantity >= quantity` from the guarded update; `InsufficientStock`/`UnavailableProduct` tests must turn red. Revert afterwards.

---

## Prompt 4 – Self-review

```text
Review the whole solution against every MUST rule and the review checklist in
.github/copilot-instructions.md. Output a table: rule → file:line → pass/fail.
Fix failures, then rerun dotnet build and dotnet test.
```
