using Npgsql;
using OsmDiscoveryApi.Models;

namespace OsmDiscoveryApi.Services;

public class NodeRepository : IHostedService
{
    private readonly ILogger<NodeRepository> _logger;
    private readonly string _connectionString;

    public bool IsReady { get; private set; }
    public bool HasError { get; private set; }

    public NodeRepository(ILogger<NodeRepository> logger, IConfiguration config)
    {
        _logger = logger;
        _connectionString = config.GetConnectionString("PostGis")
            ?? throw new InvalidOperationException("PostGis connection string is required.");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => CheckReadinessAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CheckReadinessAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM import_meta WHERE key = 'status'";
                var status = await cmd.ExecuteScalarAsync(ct) as string;
                if (status == "complete") { IsReady = true; return; }
                _logger.LogInformation("DB status: {Status}. Waiting...", status ?? "null");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB not reachable (attempt {I}/30)", i + 1);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        _logger.LogError("DB not ready after 5 minutes.");
        HasError = true;
        IsReady = true;
    }

    public async Task<List<PointResponse>> GetRandomNodesAsync(
        double lat, double lon, double radiusMeters, int count,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ST_X(geom)::float8, ST_Y(geom)::float8
            FROM osm_nodes
            WHERE ST_DWithin(
                geom::geography,
                ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography,
                @radius
            )
            ORDER BY RANDOM()
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("lon", lon);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("radius", radiusMeters);
        cmd.Parameters.AddWithValue("count", count);

        var results = new List<PointResponse>(count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new PointResponse(reader.GetDouble(0), reader.GetDouble(1)));
        return results;
    }
}
