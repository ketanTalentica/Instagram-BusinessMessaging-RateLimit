using InstagramGraphMock.Endpoints;
using InstagramGraphMock.State;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TenantStateStore>();

var app = builder.Build();

app.MapGraphApiEndpoints();
app.MapSimulatorControlEndpoints();

app.Run();
