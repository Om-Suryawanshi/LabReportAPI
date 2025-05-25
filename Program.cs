var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddHostedService<TcpListenerService>();

var app = builder.Build();

app.UseRouting();
app.MapControllers();

app.Run();
