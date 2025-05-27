var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.Configure<LabSettings>(builder.Configuration.GetSection("LabSettings"));

var app = builder.Build();

app.UseRouting();
app.MapControllers();

app.Run();
