using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using OsmDiscoveryApi.Models;
using OsmDiscoveryApi.Services;
using System.Text.Json;

namespace OsmDiscoveryApi.Endpoints;

public static class DiscoveryEndpoints
{
    private const double EarthRadiusKm = 6371.0;

    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/discovery/regions", () =>
        {
            return Results.Ok(new { regions = new[] { "region" } });
        });

        routes.MapPost("/api/discovery/validate-nodes", async (
            ValidateRequest request,
            ILogger<OsmSpatialIndex> logger,
            HttpClient httpClient) =>
        {
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

        routes.MapGet("/api/discovery/random-nodes", (
            [AsParameters] DiscoveryRequest request,
            OsmSpatialIndex spatialIndex,
            ILogger<OsmSpatialIndex> logger) =>
        {
            logger.LogInformation("Received request for {Count} random nodes at lat:{Lat}, lon:{Lon} with radius {Radius}m",
                request.Count, request.Lat, request.Lon, request.Radius);

            if (spatialIndex.HasError)
            {
                return Results.Json(new { message = "Spatial indexing failed." }, statusCode: 500);
            }

            if (!spatialIndex.IsReady)
            {
                return Results.Json(new { message = "Service is still indexing spatial data." }, statusCode: 503);
            }

            var index = spatialIndex.GetIndex();
            if (index == null)
            {
                return Results.Json(new { message = "Spatial index failed to initialize." }, statusCode: 500);
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

            // 1. Calculate bounding box for the radius
            // Approx degrees per meter. 1 degree latitude is ~111.32 km.
            double radiusKm = request.Radius / 1000.0;
            double latRad = request.Lat * Math.PI / 180.0;

            double deltaLat = radiusKm / 111.32;
            // Longitude distance varies by latitude
            double deltaLon = radiusKm / (111.32 * Math.Cos(latRad));

            double minLat = request.Lat - deltaLat;
            double maxLat = request.Lat + deltaLat;
            double minLon = request.Lon - deltaLon;
            double maxLon = request.Lon + deltaLon;

            var searchEnvelope = new Envelope(minLon, maxLon, minLat, maxLat);

            // 2. Query STRtree
            var candidates = index.Query(searchEnvelope);

            // 3. Filter strictly by Haversine distance
            var validCandidates = new List<MinimalNode>();
            foreach (var candidate in candidates)
            {
                double dist = HaversineDistance(request.Lat, request.Lon, candidate.Lat, candidate.Lon);
                if (dist <= request.Radius)
                {
                    validCandidates.Add(candidate);
                }
            }

            if (validCandidates.Count == 0)
            {
                return Results.Ok(Array.Empty<PointResponse>());
            }

            // 4. Optimized Selection (Partial Fisher-Yates)
            int countToReturn = Math.Min(request.Count, validCandidates.Count);
            var responseList = new List<PointResponse>(countToReturn);

            logger.LogInformation("Found {Count} valid candidates within true radius.", validCandidates.Count);

            if (countToReturn == validCandidates.Count)
            {
                for (int i = 0; i < validCandidates.Count; i++)
                {
                    responseList.Add(new PointResponse(validCandidates[i].Lon, validCandidates[i].Lat));
                }
            }
            else
            {
                // Initialize array of indices
                var indices = new int[validCandidates.Count];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = i;
                }

                // Partial Fisher-Yates to pick exactly 'countToReturn' unique indices
                for (int i = 0; i < countToReturn; i++)
                {
                    int j = Random.Shared.Next(i, indices.Length);

                    // Swap
                    int temp = indices[i];
                    indices[i] = indices[j];
                    indices[j] = temp;

                    var candidate = validCandidates[indices[i]];
                    responseList.Add(new PointResponse(candidate.Lon, candidate.Lat));
                }
            }

            return Results.Ok(responseList);
        });
    }

    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;

        lat1 = lat1 * Math.PI / 180.0;
        lat2 = lat2 * Math.PI / 180.0;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2) * Math.Cos(lat1) * Math.Cos(lat2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * 1000.0 * c; // returns meters
    }
}
