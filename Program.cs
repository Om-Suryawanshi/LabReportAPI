using LabReportAPI.Services;
using LabReportAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllers();
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.Configure<LabSettings>(builder.Configuration.GetSection("LabSettings"));
builder.Services.AddSingleton<ILogService, LogService>();

var app = builder.Build();

// Static files
app.UseDefaultFiles();
app.UseStaticFiles();

// Routing
app.UseRouting();
app.MapControllers();

// Friendly fallback for unknown routes (non-API)
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    if (!path.StartsWith("/api") && !System.IO.Path.HasExtension(path))
    {
        context.Response.ContentType = "text/html";
        context.Response.StatusCode = 404;
        await context.Response.SendFileAsync("wwwroot/error.html");
        return;
    }

    await next();
});

// Shutdown log
app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("🔄 Application is stopping...");
});

app.Run();
