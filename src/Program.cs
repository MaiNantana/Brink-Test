using BrinkTest.Api.Data;
using BrinkTest.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException(
        "Connection string 'OrdersDb' is required. Set ConnectionStrings__OrdersDb.");

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOrdersEndpoints();

app.Run();
