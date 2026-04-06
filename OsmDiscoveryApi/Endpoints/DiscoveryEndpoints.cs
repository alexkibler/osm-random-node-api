using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using OsmDiscoveryApi.Models;
using OsmDiscoveryApi.Services;

namespace OsmDiscoveryApi.Endpoints;

public static class DiscoveryEndpoints
{
    private const double EarthRadiusKm = 6371.0;

    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/discovery/random-nodes", (
            [AsParameters] DiscoveryRequest request,
            OsmSpatialIndex spatialIndex,
            ILogger<OsmSpatialIndex> logger) =>
        {
            if (!spatialIndex.IsReady)
            {
                return Results.Json(new { message = "Service is still indexing spatial data." }, statusCode: 503);
            }

            var index = spatialIndex.GetIndex();
            if (index == null)
            {
                return Results.Json(new { message = "Spatial index failed to initialize." }, statusCode: 500);
            }

            if (request.Count <= 0 || request.Radius <= 0)
            {
                return Results.BadRequest(new { message = "Count and Radius must be greater than 0." });
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

            if (countToReturn == validCandidates.Count)
            {
                for (int i = 0; i < validCandidates.Count; i++)
                {
                    responseList.Add(new PointResponse(validCandidates[i].Lon, validCandidates[i].Lat));
                }
            }
            else
            {
                var random = new Random();
                // Initialize array of indices
                var indices = new int[validCandidates.Count];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = i;
                }

                // Partial Fisher-Yates to pick exactly 'countToReturn' unique indices
                for (int i = 0; i < countToReturn; i++)
                {
                    int j = random.Next(i, indices.Length);

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
