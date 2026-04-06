using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OsmDiscoveryApi.Tests;

public class PerformanceTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "http://localhost:8091";

    public PerformanceTests()
    {
        _httpClient = new HttpClient();
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private record Location(double Lat, double Lon, string Name);

    private static readonly Location[] TestLocations = new[]
    {
        new Location(40.7128, -74.0060, "NYC"),
        new Location(40.79097917793314, -80.09847309676428, "Ohio"),
        new Location(34.0522, -118.2437, "Los Angeles"),
        new Location(41.8781, -87.6298, "Chicago"),
    };

    [Fact]
    public async Task NodeGeneration_Performance()
    {
        var results = new List<(string Location, int Count, long Ms)>();

        foreach (var location in TestLocations)
        {
            var sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/api/discovery/random-nodes?lat={location.Lat}&lon={location.Lon}&count=1000&radius=50000");
            sw.Stop();

            var json = await response.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(json))
            {
                var count = doc.RootElement.GetArrayLength();
                results.Add((location.Name, count, sw.ElapsedMilliseconds));
            }
        }

        // Report
        Console.WriteLine("\n=== NODE GENERATION PERFORMANCE ===");
        Console.WriteLine($"{"Location",-15} {"Count",-10} {"Time (ms)",-12}");
        Console.WriteLine(new string('-', 37));
        foreach (var (loc, count, ms) in results)
        {
            Console.WriteLine($"{loc,-15} {count,-10} {ms,-12}");
        }

        var avgMs = results.Average(r => r.Ms);
        Console.WriteLine($"\nAverage: {avgMs:F2}ms for 1000 nodes");

        // All generations should complete in <5s
        Assert.All(results, r => Assert.True(r.Ms < 5000, $"{r.Location} took {r.Ms}ms"));
    }

    [Fact]
    public async Task NodeValidation_BikeProfile()
    {
        await ValidateProfile("bike", 100);
    }

    [Fact]
    public async Task NodeValidation_FootProfile()
    {
        await ValidateProfile("foot", 100);
    }

    private async Task ValidateProfile(string profile, int nodeCount)
    {
        var location = TestLocations[0]; // NYC

        // Get nodes
        var nodesResponse = await _httpClient.GetAsync(
            $"{BaseUrl}/api/discovery/random-nodes?lat={location.Lat}&lon={location.Lon}&count={nodeCount}&radius=50000");
        var nodesJson = await nodesResponse.Content.ReadAsStringAsync();

        // Validate
        var sw = Stopwatch.StartNew();
        var validateRequest = new { points = JsonDocument.Parse(nodesJson).RootElement, profile };
        var validateContent = new StringContent(
            JsonSerializer.Serialize(validateRequest),
            Encoding.UTF8,
            "application/json");

        var validateResponse = await _httpClient.PostAsync(
            $"{BaseUrl}/api/discovery/validate-nodes",
            validateContent);
        sw.Stop();

        var resultJson = await validateResponse.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(resultJson))
        {
            var root = doc.RootElement;
            var total = root.GetProperty("total").GetInt32();
            var valid = root.GetProperty("valid").GetInt32();
            var validityRate = (valid * 100.0) / total;

            Console.WriteLine($"\n=== {profile.ToUpper()} PROFILE VALIDATION ===");
            Console.WriteLine($"Total nodes: {total}");
            Console.WriteLine($"Valid nodes: {valid}");
            Console.WriteLine($"Validity rate: {validityRate:F2}%");
            Console.WriteLine($"Total time: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Average per node: {(sw.ElapsedMilliseconds / (double)total):F2}ms");

            // Expect high validity rate
            Assert.True(validityRate >= 95, $"Validity rate too low: {validityRate}%");
        }
    }

    [Fact]
    public async Task LargeScaleValidation_1000Nodes()
    {
        var location = TestLocations[0]; // NYC

        // Get 1000 nodes
        Console.WriteLine("\nGenerating 1000 nodes...");
        var genSw = Stopwatch.StartNew();
        var nodesResponse = await _httpClient.GetAsync(
            $"{BaseUrl}/api/discovery/random-nodes?lat={location.Lat}&lon={location.Lon}&count=1000&radius=50000");
        genSw.Stop();
        Console.WriteLine($"Generation time: {genSw.ElapsedMilliseconds}ms");

        var nodesJson = await nodesResponse.Content.ReadAsStringAsync();

        // Validate all 1000
        Console.WriteLine("Validating 1000 nodes...");
        var valSw = Stopwatch.StartNew();
        var validateRequest = new { points = JsonDocument.Parse(nodesJson).RootElement, profile = "bike" };
        var validateContent = new StringContent(
            JsonSerializer.Serialize(validateRequest),
            Encoding.UTF8,
            "application/json");

        var validateResponse = await _httpClient.PostAsync(
            $"{BaseUrl}/api/discovery/validate-nodes",
            validateContent);
        valSw.Stop();

        var resultJson = await validateResponse.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(resultJson))
        {
            var root = doc.RootElement;
            var total = root.GetProperty("total").GetInt32();
            var valid = root.GetProperty("valid").GetInt32();
            var validityRate = (valid * 100.0) / total;

            Console.WriteLine($"\n=== LARGE SCALE TEST (1000 NODES) ===");
            Console.WriteLine($"Valid nodes: {valid}/{total}");
            Console.WriteLine($"Validity rate: {validityRate:F2}%");
            Console.WriteLine($"Generation time: {genSw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Validation time: {valSw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Validation per node: {(valSw.ElapsedMilliseconds / (double)total):F2}ms");
            Console.WriteLine($"Total time: {(genSw.ElapsedMilliseconds + valSw.ElapsedMilliseconds)}ms");

            // Expect high validity
            Assert.True(validityRate >= 95, $"Validity rate too low: {validityRate}%");
        }
    }

    [Fact]
    public async Task MultiProfile_Comparison()
    {
        var location = TestLocations[0]; // NYC

        // Get nodes once
        var nodesResponse = await _httpClient.GetAsync(
            $"{BaseUrl}/api/discovery/random-nodes?lat={location.Lat}&lon={location.Lon}&count=200&radius=50000");
        var nodesJson = await nodesResponse.Content.ReadAsStringAsync();

        var results = new Dictionary<string, (int Valid, long Ms)>();

        foreach (var profile in new[] { "bike", "foot", "car" })
        {
            var sw = Stopwatch.StartNew();
            var validateRequest = new { points = JsonDocument.Parse(nodesJson).RootElement, profile };
            var validateContent = new StringContent(
                JsonSerializer.Serialize(validateRequest),
                Encoding.UTF8,
                "application/json");

            var validateResponse = await _httpClient.PostAsync(
                $"{BaseUrl}/api/discovery/validate-nodes",
                validateContent);
            sw.Stop();

            var resultJson = await validateResponse.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(resultJson))
            {
                var root = doc.RootElement;
                var valid = root.GetProperty("valid").GetInt32();
                results[profile] = (valid, sw.ElapsedMilliseconds);
            }
        }

        Console.WriteLine($"\n=== MULTI-PROFILE COMPARISON (200 nodes) ===");
        Console.WriteLine($"{"Profile",-10} {"Valid",-8} {"Time (ms)",-12}");
        Console.WriteLine(new string('-', 30));
        foreach (var (profile, (valid, ms)) in results)
        {
            Console.WriteLine($"{profile,-10} {valid,-8} {ms,-12}");
        }
    }

}
