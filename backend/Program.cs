using BotPanel.Hubs;
using BotPanel.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SignalR for real-time logs
builder.Services.AddSignalR();

// Services
builder.Services.AddSingleton<IBotRepository, BotRepository>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<ILogStreamingService, LogStreamingService>();

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy
            .WithOrigins("http://localhost:3000", "http://localhost:5173", "null")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // Required for SignalR
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("FrontendPolicy");
app.UseStaticFiles(); // Serve frontend from wwwroot
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapHub<LogHub>("/hubs/logs");

// Serve frontend SPA
app.MapFallbackToFile("index.html");

app.Run();
