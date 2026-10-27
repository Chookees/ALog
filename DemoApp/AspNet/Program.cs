using ALog.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddALog(builder.Configuration);

var app = builder.Build();

app.MapGet("/", (ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("ALog.AspNetSample");
    logger.LogInformation("Hello from {Sample} at {UtcNow}", "AspNetSample", DateTime.UtcNow);
    return Results.Ok(new { message = "logged" });
});

app.Run();
