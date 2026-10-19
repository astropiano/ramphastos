using Astropiano.Ramphastos.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddRamphastos<CounterVm>();

var app = builder.Build();
app.UseCors();
app.MapRamphastos();
app.Run();
