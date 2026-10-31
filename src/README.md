# BrinkTest Orders API

A .NET 10 Minimal API that creates orders using EF Core and SQL Server.

## Configuration

Set `ConnectionStrings__OrdersDb` to a SQL Server connection string before starting the API. For local development, for example:

```powershell
$env:ConnectionStrings__OrdersDb = "Server=(localdb)\MSSQLLocalDB;Database=BrinkOrders;Trusted_Connection=True;TrustServerCertificate=True"
```

Create and apply an EF Core migration before running against a new database:

```powershell
dotnet ef migrations add InitialCreate
dotnet ef database update
dotnet run
```

The database schema expects existing `Customers` and `Products` data to be managed by the application that owns those records.

## Create an order

`POST /api/orders`

```json
{
  "customerId": 12,
  "items": [
    { "productId": 34, "quantity": 2 }
  ]
}
```

The API validates request values and customer/product availability, calculates prices from the database, and creates the pending order while decrementing stock in one transaction. Each stock decrement is guarded by an atomic SQL update, preventing concurrent requests from reserving more stock than is available. Validation failures return `400` ProblemDetails; unknown customers/products return `404`; unavailable or concurrently changed inventory returns `409`.
