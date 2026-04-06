using Microsoft.AspNetCore.Mvc;
using OsmDiscoveryApi.Models;
using OsmDiscoveryApi.Services;
using System.Text.Json;

namespace OsmDiscoveryApi.Endpoints;

public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/discovery/regions", () =>
        {
            return Results.Ok(new { regions = new[] { "region" } });
        });

        routes.MapPost("/api/discovery/validate-nodes", async (
            ValidateRequest request,
            ILoggerFactory loggerFactory,
            HttpClient httpClient) =>
        {
            var logger = loggerFactory.CreateLogger("DiscoveryApi");
            if (request.Points == null || request.Points.Length == 0)
                return Results.BadRequest(new { message = "Points array is required" });

            var results = new List<ValidateResult>();
            const double maxDistanceMeters = 20; // Points must snap within 20m to be valid

            foreach (var point in request.Points)
            {
                try
                {
                    // Query GraphHopper's snap endpoint with the appropriate profile
                    var vehicle = request.Profile.ToLower() switch
                    {
                        "foot" or "walk" => "foot",
                        "car" => "car",
                        _ => "bike"
                    };
                    var url = $"http://graphhopper:8989/nearest?point={point.Lat},{point.Lon}&vehicle={vehicle}&type=json";
                    var response = await httpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;
                            var coords = root.GetProperty("coordinates");
                            var snappedLon = coords[0].GetDouble();
                            var snappedLat = coords[1].GetDouble();
                            var distanceFromGraphHopper = root.GetProperty("distance").GetDouble();

                            results.Add(new ValidateResult
                            {
                                Original = point,
                                Snapped = new PointResponse(snappedLon, snappedLat),
                                DistanceMeters = distanceFromGraphHopper,
                                IsValid = distanceFromGraphHopper <= maxDistanceMeters,
                                RoadName = $"Valid {request.Profile} route"
                            });
                        }
                    }
                    else
                    {
                        results.Add(new ValidateResult
                        {
                            Original = point,
                            IsValid = false,
                            Error = "GraphHopper lookup failed"
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to validate point {Lat},{Lon}", point.Lat, point.Lon);
                    results.Add(new ValidateResult
                    {
                        Original = point,
                        IsValid = false,
                        Error = ex.Message
                    });
                }
            }

            var validCount = results.Count(r => r.IsValid);
            return Results.Ok(new { total = results.Count, valid = validCount, results });
        });

        routes.MapGet("/api/discovery/random-nodes", async (
            [AsParameters] DiscoveryRequest request,
            NodeRepository nodeRepo,
            ILogger<NodeRepository> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("Received request for {Count} random nodes at lat:{Lat}, lon:{Lon} with radius {Radius}m",
                request.Count, request.Lat, request.Lon, request.Radius);

            if (nodeRepo.HasError)
            {
                return Results.Json(new { message = "Database connection failed." }, statusCode: 500);
            }

            if (!nodeRepo.IsReady)
            {
                return Results.Json(new { message = "Service is waiting for database to be ready." }, statusCode: 503);
            }

            if (request.Lat < -90 || request.Lat > 90 || request.Lon < -180 || request.Lon > 180)
            {
                return Results.BadRequest(new { message = "Invalid lat/lon coordinates." });
            }

            if (request.Count <= 0 || request.Radius <= 0)
            {
                return Results.BadRequest(new { message = "Count and Radius must be greater than 0." });
            }

            if (request.Count > 10000)
            {
                return Results.BadRequest(new { message = "Count cannot exceed 10,000." });
            }

            if (request.Radius > 100000)
            {
                return Results.BadRequest(new { message = "Radius cannot exceed 100,000 meters." });
            }

            var nodes = await nodeRepo.GetRandomNodesAsync(request.Lat, request.Lon, request.Radius, request.Count, ct);
            return Results.Ok(nodes);
        });
    }
}
