# BrinkTest Orders API

A .NET 10 Minimal API that creates orders using EF Core and SQL Server.

## Project structure

```text
BrinkTest.sln
├── src/                                  BrinkTest.Api
│   ├── Endpoints/OrdersEndpoints.cs      HTTP mapping only: request → service → HTTP result / ProblemDetails
│   ├── Application/Orders/               Business logic (OrderService) and result types
│   ├── Contracts/                        Request / response DTOs
│   ├── Domain/                           Entities
│   └── Data/                             OrdersDbContext and EF Core migrations
├── test/BrinkTest.Api.Tests/
│   ├── OrderServiceTests.cs              Unit tests for OrderService business rules
│   └── OrdersEndpointTests.cs            Integration tests through the HTTP pipeline
├── docs/                                 AI chat history (Part 3 evidence)
└── .github/copilot-instructions.md       Rules that govern AI-generated code
```

## Run

The default connection string in `src/appsettings.json` targets SQL Server LocalDB. Override it with `ConnectionStrings__OrdersDb` if needed.

```powershell
dotnet tool install --global dotnet-ef --version 10.0.0   # once
dotnet ef database update --project src
dotnet run --project src
```

The database schema expects existing `Customers` and `Products` data to be managed by the application that owns those records.

When running in the `Development` environment, Swagger UI is available at `/swagger` and the OpenAPI document at `/swagger/v1/swagger.json`.

## Test

```powershell
dotnet test BrinkTest.sln
```

- **Unit tests** (`OrderServiceTests`) call `OrderService` directly with an in-memory SQLite database, a `FakeTimeProvider` and a `FakeLogger`. They cover successful order creation, insufficient stock, inactive product and validation, asserting the result, stock, saved order and log output.
- **Integration tests** (`OrdersEndpointTests`) run the full HTTP pipeline via `WebApplicationFactory` and verify status codes, ProblemDetails, transaction rollback and the Swagger document.

SQLite is used instead of the EF Core InMemory provider because the service relies on real transactions and `ExecuteUpdateAsync`, which InMemory does not support.

## Create an order

`POST /api/orders` with an optional `Idempotency-Key` header (1–64 characters, e.g. a client-generated GUID):

```http
Idempotency-Key: 3f0c2a8e-7c1d-4a55-9a51-2f3b6f4f8d10
```

```json
{
  "customerId": 12,
  "items": [
    { "productId": 34, "quantity": 2 }
  ]
}
```

In one SQL transaction the API:

1. reserves stock with a guarded atomic update (`WHERE StockQuantity >= @quantity`), so concurrent requests cannot oversell;
2. inserts the order with status `PendingPayment`, its items (prices taken from the database) and the idempotency key;
3. inserts an `OrderCreatedEvent` row into `OutboxMessages` (transactional outbox).

| Status | When |
|---|---|
| `201` | Order created; body contains `orderId` and the order summary |
| `200` | Retry with the same `Idempotency-Key` and same body; returns the original order, stock is not reserved again |
| `400` | Validation failure (including an invalid `Idempotency-Key`) |
| `404` | Unknown customer or product |
| `409` | Product inactive, insufficient stock, or stock changed concurrently |
| `422` | Order total exceeds the supported range, or `Idempotency-Key` reused with a different body |
| `500` | Unexpected error (handled by `UseExceptionHandler`) |

## Relation to the Part 1 design

`docs/Diagram.drawio` shows the full target flow. Part 3 implements the API and SQL Server part of it:

| Design step | Part 3 |
|---|---|
| Idempotency-Key check | Implemented with a unique filtered index on `Orders.IdempotencyKey` (Redis fast-path lock not implemented) |
| Stock reservation | Implemented as the guarded SQL update, which is the authoritative check (Redis Lua pre-check not implemented) |
| Insert order `PendingPayment` + outbox | Implemented |
| Outbox relay → RabbitMQ, payment and shipping workers | Not implemented (out of scope); `OutboxMessages.Id` is the message id that consumers use for de-duplication |
