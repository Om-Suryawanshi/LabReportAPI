using LabReportAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.Configure<LabSettings>(builder.Configuration.GetSection("LabSettings"));
builder.Services.AddSingleton<ILogService, LogService>();


var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(); 

app.UseRouting();
app.MapControllers();

app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("🔄 Application is stopping...");
});


app.Run();
