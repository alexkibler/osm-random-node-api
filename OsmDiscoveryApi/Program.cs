using OsmDiscoveryApi.Services;
using OsmDiscoveryApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeRepository>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapDiscoveryEndpoints();

app.Run();
