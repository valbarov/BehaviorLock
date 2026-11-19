using BehaviorLock;
using Microsoft.EntityFrameworkCore;
using SampleApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBehaviorLock(options => builder.Configuration.GetSection("BehaviorLock").Bind(options));

builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseSqlite(services.GetRequiredService<IConfiguration>().GetConnectionString("Shop"))
    .UseBehaviorLock(services));

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<PaymentClient>();

var app = builder.Build();

// Startup code runs outside a request, so the guard does not check it.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreated();
}

app.UseBehaviorLock();

app.MapShopApi();

app.Run();
