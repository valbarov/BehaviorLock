using EndpointGuard;
using Microsoft.EntityFrameworkCore;
using SampleApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointGuard(options => builder.Configuration.GetSection("EndpointGuard").Bind(options));

builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseSqlite(services.GetRequiredService<IConfiguration>().GetConnectionString("Shop"))
    .UseEndpointGuard(services));

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<PaymentClient>();

var app = builder.Build();

// Startup code runs outside a request, so the guard does not check it.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreated();
}

app.UseEndpointGuard();

// Every endpoint group below has at least one deliberate weakness. See the README for the list.
app.MapGet("/health", () => "ok");
app.MapProductEndpoints();
app.MapAccountEndpoints();
app.MapOrderEndpoints();
app.MapWebhookEndpoints();

app.Run();
