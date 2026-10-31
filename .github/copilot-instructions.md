# AI Assistant Development Rules

These rules govern **all code generated or modified by AI assistants** (GitHub Copilot, Claude, etc.) in this repository.
They are written for the AI and for the developer reviewing its output.

- Code that breaks a **MUST** rule is rejected and regenerated or fixed before it is committed.
- **SHOULD** rules may be broken only with a short reason in the prompt response or code review.
- If a request conflicts with these rules, the AI must point out the conflict instead of silently ignoring a rule.

---

## 1. Project context

| Item | Value |
|---|---|
| Runtime | .NET 10 (`net10.0`), C# with nullable reference types and implicit usings enabled |
| Web | ASP.NET Core **Minimal APIs** (no MVC controllers) |
| Data | EF Core 10 with SQL Server; SQLite in-memory for tests |
| API docs | Swashbuckle (`/swagger`, Development only) |
| Tests | xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`), `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) |
| Solution | `BrinkTest.sln` → `src/BrinkTest.Api.csproj`, `test/BrinkTest.Api.Tests/BrinkTest.Api.Tests.csproj` |

### Scope

- In scope: `POST /api/orders` – validate, check customer/product/stock, reserve stock, create a `PendingPayment` order, write an `OrderCreatedEvent` to the outbox, support `Idempotency-Key`, return the `OrderId`.
- **Out of scope – MUST NOT implement unless explicitly asked:** real payment integration, real shipping integration, RabbitMQ / messaging infrastructure (including the outbox relay/publisher), Redis / distributed cache, authentication.
- The target design is `docs/1-Diagram/Diagram.drawio` (Part 1). Code and diagram **MUST** stay consistent: when one changes (route, status codes, statuses, events), update the other in the same change.
- If a feature would need one of the above, add a short `// TODO:` only when asked, never a stub implementation.

---

## 2. Architecture and folder responsibilities

```text
src/
├── Endpoints/            HTTP layer: route mapping, OpenAPI metadata, result → HTTP translation
├── Application/<Feature>/ Business logic: services, result types, validation
├── Contracts/            Request / response DTOs and mapping to responses
├── Domain/               Entities and enums (no EF or HTTP dependencies)
├── Data/                 OrdersDbContext, entity configuration, Migrations/
└── Program.cs            DI registration and middleware pipeline only
```

Dependency direction: `Endpoints → Application → Data → Domain`. `Contracts` may be used by `Endpoints` and `Application`.

- **MUST** keep endpoints thin. An endpoint handler only:
  1. receives the request DTO and services via parameter binding,
  2. calls one application service method,
  3. maps the service result to `Results.*` (see section 6).
- **MUST NOT** use `OrdersDbContext`, write business rules, or calculate prices inside `Endpoints/`.
- **MUST** put each business feature behind an interface (`IOrderService`) registered as **scoped** in `Program.cs`.
- **MUST** return expected business failures as a result type (`CreateOrderResult` + `CreateOrderError`), not by throwing exceptions. Exceptions are for unexpected failures only.
- **MUST NOT** return EF entities from endpoints; map them to a `Contracts` response (e.g. `order.ToSummary()`).
- **MUST NOT** introduce a repository / unit-of-work layer over EF Core. `DbContext` already is the unit of work.
- **SHOULD NOT** add new layers, projects, MediatR, AutoMapper or FluentValidation without being asked.

---

## 3. C# coding style

Follow the [Microsoft C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions). Specifically:

### Naming

| Element | Convention | Example |
|---|---|---|
| Types, methods, properties, enum members | `PascalCase` | `OrderService`, `CreateOrderAsync`, `StockQuantity` |
| Interfaces | `I` + `PascalCase` | `IOrderService` |
| Locals, parameters | `camelCase` | `productIds`, `cancellationToken` |
| Private fields | `_camelCase` | `_connection` |
| Constants | `PascalCase` | `MaximumOrderTotal` |
| Async methods | suffix `Async` | `CreateOrderAsync` |
| Lambda parameters | descriptive names, not single letters | `product => product.Id`, not `p => p.Id` |

### Language features

- **MUST** use file-scoped namespaces that match the folder: `namespace BrinkTest.Api.Application.Orders;`.
- **MUST** mark classes `sealed` unless they are designed for inheritance.
- **SHOULD** use primary constructors for DI in services: `public sealed class OrderService(OrdersDbContext db, ...)`.
- **SHOULD** use `sealed record` for response DTOs and `sealed class` with `init` properties for request DTOs.
- **SHOULD** use collection expressions (`[]`, `["message"]`) and `var` when the type is obvious.
- **MUST NOT** use the null-forgiving operator `!` unless the value was validated just before (e.g. `request.Items!` after `Validate`).
- **MUST NOT** leave unused `using` directives, commented-out code, or empty `catch` blocks.
- **MUST** keep comments rare: explain *why*, never restate *what* the code does.

### Dependencies and time

- **MUST** inject `ILogger<T>` (not `ILoggerFactory` or static loggers).
- **MUST** inject `TimeProvider` and use `timeProvider.GetUtcNow()`; **MUST NOT** call `DateTime.Now` / `DateTime.UtcNow` in business logic.
- **MUST** store times in UTC; property names end with `Utc` (`CreatedAtUtc`).

---

## 4. Asynchronous code

- **MUST** use async EF Core APIs (`AnyAsync`, `ToDictionaryAsync`, `SaveChangesAsync`, `ExecuteUpdateAsync`, `BeginTransactionAsync`).
- **MUST** pass the request `CancellationToken` to every async call, through every layer.
- **MUST NOT** block on async code (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
- **MUST NOT** use `async void`.
- **MUST** dispose async resources with `await using` (e.g. the transaction).
- Rollback is the exception to cancellation: use `RollbackAsync(CancellationToken.None)` so a cancelled request still rolls back.

---

## 5. Data access and consistency

- **MUST** use `AsNoTracking()` for read-only queries.
- **MUST** load related rows in one query (`Where(product => productIds.Contains(product.Id))`), never one query per item in a loop when avoidable.
- **MUST** run all writes of one business operation (stock reservation + order insert) inside **one transaction**, and commit only after `SaveChangesAsync` succeeds.
- **MUST** decrement stock with a **guarded atomic update** so concurrent requests cannot oversell:

  ```csharp
  var affectedRows = await db.Products
      .Where(product => product.Id == productId && product.IsActive && product.StockQuantity >= quantity)
      .ExecuteUpdateAsync(setters => setters.SetProperty(
          product => product.StockQuantity,
          product => product.StockQuantity - quantity), cancellationToken);
  ```

  If `affectedRows != 1`, roll back and return `InventoryChanged`.
- **MUST NOT** read stock, subtract in memory and save it back (read-modify-write race condition).
- **MUST** write integration events (e.g. `OrderCreatedEvent`) as `OutboxMessages` rows in the **same transaction** as the business change. **MUST NOT** publish to a broker directly from a request (dual-write problem).
- **MUST** make order creation idempotent with the optional `Idempotency-Key` header: a unique filtered index on `Orders.IdempotencyKey` is the source of truth; a retry with the same request returns the existing order, a different request returns 422.
- **MUST** calculate unit prices and totals on the server from the database; **MUST NOT** accept prices from the client.
- **MUST** merge duplicate product IDs in one request before checking stock.
- **MUST** configure money as `decimal` with `HasPrecision(18, 2)`.
- **MUST** add an EF Core migration for every model change:
  `dotnet ef migrations add <Name> --project src --output-dir Data/Migrations`.
- **MUST NOT** use `EnsureCreated` outside tests, or raw SQL built with string concatenation.

---

## 6. Validation and error handling

### Validation

- **MUST** validate the request before any database call.
- Rules for `CreateOrderRequest`:
  - `CustomerId > 0`
  - `Items` is not null or empty and contains no null items
  - every `ProductId > 0` and every `Quantity > 0`
  - the combined quantity per product does not overflow `int`
- Validation errors are a dictionary keyed by property name (`nameof(request.Items)`).

### Error → HTTP mapping

All non-success responses **MUST** be RFC 7807 `ProblemDetails` (`application/problem+json`).

| Situation | `CreateOrderError` | Status | Title |
|---|---|---|---|
| Invalid request | `Validation` | 400 | (ValidationProblem) |
| Customer does not exist | `CustomerNotFound` | 404 | `Customer not found` |
| Product does not exist | `ProductNotFound` | 404 | `Product not found` |
| Product is inactive | `ProductInactive` | 409 | `Product is inactive` |
| Not enough stock | `InsufficientStock` | 409 | `Insufficient stock` |
| Stock changed during the transaction | `InventoryChanged` | 409 | `Inventory changed` |
| Total exceeds `decimal(18,2)` | `TotalOutOfRange` | 422 | `Order total is too large` |
| `Idempotency-Key` already used with a different request | `IdempotencyKeyReused` | 422 | `Idempotency key reused` |
| Same `Idempotency-Key` and same request (retry) | `None` + `IsReplay` | 200 | original `OrderSummaryResponse` |
| Unexpected exception | – | 500 | handled by `UseExceptionHandler` |
| Success | `None` | 201 | `Location: /api/orders/{orderId}` + `OrderSummaryResponse` |

- **MUST** keep titles stable; tests and clients depend on them.
- **MUST NOT** catch general exceptions in endpoints or services just to return 500; let `UseExceptionHandler` + `AddProblemDetails` handle them.
- **MUST NOT** return exception messages, stack traces, SQL or connection details to clients.
- `detail` **SHOULD** name the failing id (`"Product 34 does not exist."`) to help the client.

### OpenAPI

- Every endpoint **MUST** declare `WithName`, `WithSummary`, `Accepts<T>`, `Produces<T>` and every `ProducesProblem` status it can return.

---

## 7. Logging

- **MUST** use structured message templates with named placeholders; **MUST NOT** use string interpolation or concatenation in log messages.

  ```csharp
  // Correct
  logger.LogWarning("Order creation rejected because product {ProductId} is inactive.", productId);
  // Wrong
  logger.LogWarning($"Order creation rejected because product {productId} is inactive.");
  ```

- Placeholder names are `PascalCase` and consistent across the code base: `{OrderId}`, `{CustomerId}`, `{ProductId}`, `{RequestedQuantity}`, `{AvailableQuantity}`, `{TotalAmount}`.

| Level | Use for | Example |
|---|---|---|
| `Information` | Successful business events | Order created |
| `Warning` | Every rejected request / expected business failure | Validation failed, customer/product not found, product inactive, insufficient stock, concurrency conflict, total out of range |
| `Error` | Unexpected failures (normally logged by the exception handler) | Database unavailable |
| `Debug` | Diagnostic detail, off in production | – |

- **MUST** log exactly one `Warning` for each rejected path and one `Information` on success.
- **MUST** log **after** rollback / commit so the log reflects the final outcome.
- **MUST NOT** log personal data (names, addresses, emails), secrets, tokens or connection strings, or whole request bodies.
- Log levels are configured in `appsettings*.json`, not in code.

---

## 8. Security and configuration

- **MUST NOT** hard-code secrets or production connection strings. Only the LocalDB development connection string may be in `appsettings.json`; other environments use `ConnectionStrings__OrdersDb`.
- **MUST** keep Swagger UI limited to the Development environment.
- **MUST NOT** remove the pinned packages `Microsoft.OpenApi` and `System.Security.Cryptography.Pkcs` from `BrinkTest.Api.csproj`. They patch vulnerable transitive versions (NU1903).
- **MUST** state why a new NuGet package is needed and use a fixed, current, non-vulnerable version.
- `dotnet build` **MUST** produce no new warnings, including NuGet vulnerability warnings.

---

## 9. Testing

### Test types

| Type | File | How | Purpose |
|---|---|---|---|
| Unit | `OrderServiceTests.cs` | `new OrderService(db, FakeTimeProvider, FakeLogger)` with SQLite `:memory:` | Business rules, side effects, logging |
| Integration | `OrdersEndpointTests.cs` | `OrdersApiFactory` (`WebApplicationFactory<Program>`) + `HttpClient` | Routing, status codes, ProblemDetails, rollback, Swagger |

- **MUST** use SQLite in-memory, **not** the EF Core InMemory provider. InMemory does not support transactions or `ExecuteUpdateAsync`, so tests would pass while production code fails.
- **MUST** give each test its own database (new connection / new factory). No shared state between tests.

### Rules

- **MUST** add or update tests for every new or changed behaviour, in the same change.
- **MUST** name tests `MethodOrFeature_Scenario_ExpectedResult`:
  - `CreateOrderAsync_InsufficientStock_ReturnsInsufficientStockAndDoesNotCreateOrder`
  - `CreateOrder_UnknownProduct_ReturnsNotFound`
- **MUST** structure tests as Arrange / Act / Assert (comments are used in unit tests).
- **MUST** assert side effects, not only the returned status:
  - success → order saved, stock decremented, `Information` log written;
  - failure → no order saved, stock unchanged, `Warning` log written.
- **MUST** use `FakeTimeProvider` with a fixed time when asserting timestamps.
- **SHOULD** use `[Theory]` + `[InlineData]` for variations of the same rule.
- **MUST NOT** use `Thread.Sleep`, real SQL Server, network calls, or test-order dependencies.

### Required scenarios for `POST /api/orders`

- [x] Successful order creation (201, `OrderId`, stock decremented)
- [x] Invalid request (400 ValidationProblem)
- [x] Unknown customer / unknown product (404)
- [x] Product unavailable: inactive or insufficient stock (409, nothing changed)
- [x] Duplicate product lines are aggregated
- [x] Failure after stock update rolls back the whole transaction (no order, no outbox message)
- [x] Successful order writes exactly one `OrderCreatedEvent` outbox message
- [x] Retry with the same `Idempotency-Key` returns the original order and reserves stock once
- [x] Reusing an `Idempotency-Key` with a different request returns 422
- [ ] Two concurrent requests with the same `Idempotency-Key` (unique-index `DbUpdateException` path) – not covered; needs a real SQL Server concurrency test

### Commands

```powershell
dotnet build BrinkTest.sln
dotnet test BrinkTest.sln
```

Both **MUST** succeed before code is accepted.

---

## 10. How to prompt the AI in this repository

When asking an AI assistant for code:

1. Reference this file: *"Follow `.github/copilot-instructions.md`."*
2. State the layer and file: *"Add the rule in `Application/Orders/OrderService.cs`, map it in `OrdersEndpoints.cs`."*
3. Ask for tests in the same request: *"Add a unit test in `OrderServiceTests` and an integration test in `OrdersEndpointTests`."*
4. Ask for a self-check: *"List any rule in copilot-instructions.md this change might violate."*

The AI **MUST**:

- inspect existing code before generating, and reuse existing types and patterns;
- change only what the request needs (no unrelated refactoring or reformatting);
- report what it could not verify (e.g. build or tests not run).

---

## 11. Review checklist for AI-generated code

The developer checks every AI change before committing:

**Correctness**
- [ ] `dotnet build` has no new warnings; `dotnet test` passes.
- [ ] Business rules match the requirements (active product, stock, server-side price).
- [ ] Edge cases handled: null / empty items, null item, zero / negative values, duplicate product IDs, `int` and `decimal` overflow.
- [ ] Concurrency: stock update is guarded and inside the transaction; rollback on every failure path.

**Design**
- [ ] Endpoint is thin; logic is in `Application/`.
- [ ] No entity returned from an endpoint; no new unnecessary abstraction, package or file.
- [ ] All async calls receive the `CancellationToken`.

**Errors and logging**
- [ ] Every failure returns the ProblemDetails status/title from section 6.
- [ ] One structured `Warning` per rejected path, one `Information` on success, no personal data.

**Tests**
- [ ] New behaviour has tests that assert side effects and follow the naming convention.

**Security**
- [ ] No secrets, no vulnerable packages, no internal details in responses.

---

## 12. Maintaining these rules

- When a review finds a problem in AI output that could happen again, **add or tighten a rule here in the same commit as the fix**, and note it in the changelog below.
- Rules must be concrete and checkable ("inject `ILogger<T>`"), not vague ("write good logging").
- Remove or update rules that no longer match the code; this file must describe the code base as it is.

### Changelog

| Date | Change | Reason |
|---|---|---|
| 2026-10-31 | Initial rules: coding standards, data access, ProblemDetails, logging, testing, review checklist | Formalise the conventions used for AI generation |
| 2026-10-31 | Added thin-endpoint / `Application` layer, result type, `ILogger<T>`, `TimeProvider`, unit vs integration test rules | Review found all business logic in the endpoint, `ILoggerFactory` with a string category, and no unit tests |
| 2026-10-31 | Added "do not remove pinned packages" rule | Pinned packages were mistakenly reported as unused; removing them reintroduced NU1903 vulnerabilities |
| 2026-10-31 | Required a `Warning` log on every rejected path | Review found rejected requests were not logged |
| 2026-10-31 | Expanded into detailed sections (architecture, style, async, error mapping, prompting guide) | Make the rules specific enough to check during review |
| 2026-10-31 | Added idempotency, outbox and `PendingPayment` rules; required code ↔ diagram consistency | Comparing code with the Part 1 diagram found mismatches (status, idempotency, events, status codes) |
