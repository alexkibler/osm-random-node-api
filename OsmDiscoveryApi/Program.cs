using OsmDiscoveryApi.Services;
using OsmDiscoveryApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OsmSpatialIndex>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OsmSpatialIndex>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapDiscoveryEndpoints();

app.Run();
