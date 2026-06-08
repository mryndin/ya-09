using BionicProAuth.Services;
using BionicProAuth.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("BffCorsPolicy", policy =>
    {
        policy.WithOrigins(builder.Configuration["Frontend:Url"] ?? "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Критично для пропуска кук через CORS
    });
});

builder.Services.AddControllers();
builder.Services.AddDistributedMemoryCache(); // Выделенная RAM сервера под сессии

builder.Services.AddHttpClient<IKeycloakService, KeycloakService>();
builder.Services.AddHttpClient<ProxyController>();

var app = builder.Build();

app.UseCors("BffCorsPolicy");
app.UseRouting();
app.UseAuthorization();
app.MapControllers();

app.Run();