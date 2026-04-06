using System.Diagnostics;
using NetTopologySuite.Index.Strtree;
using OsmSharp.Streams;
using OsmDiscoveryApi.Models;
using NetTopologySuite.Geometries;

namespace OsmDiscoveryApi.Services;

public class OsmSpatialIndex : IHostedService
{
    private readonly ILogger<OsmSpatialIndex> _logger;
    private readonly string _dataDirectory;
    private STRtree<MinimalNode>? _index;
    private readonly object _indexLock = new();

    public bool IsReady { get; private set; }
    public bool HasError { get; private set; }

    public OsmSpatialIndex(ILogger<OsmSpatialIndex> logger, IConfiguration configuration)
    {
        _logger = logger;
        _dataDirectory = configuration.GetValue<string>("DataDirectory") ?? "/app/data";
    }

    public STRtree<MinimalNode>? GetIndex()
    {
        lock (_indexLock)
        {
            return _index;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OsmSpatialIndex starting in background.");

        // Start indexing in background without blocking host startup
        _ = Task.Run(() => BuildIndexAsync(cancellationToken), cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OsmSpatialIndex stopping.");
        return Task.CompletedTask;
    }

    private async Task BuildIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var newIndex = new STRtree<MinimalNode>();

            if (!Directory.Exists(_dataDirectory))
            {
                _logger.LogWarning("Data directory {DataDirectory} does not exist.", _dataDirectory);
                IsReady = true;
                return;
            }

            var pbfFiles = Directory.GetFiles(_dataDirectory, "*.osm.pbf");
            _logger.LogInformation("Found {Count} .osm.pbf files in {DataDirectory}.", pbfFiles.Length, _dataDirectory);

            foreach (var file in pbfFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;
                ProcessPbfFile(file, newIndex, cancellationToken);
            }

            _logger.LogInformation("Building STRtree spatial index...");
            newIndex.Build();
            sw.Stop();

            lock (_indexLock)
            {
                _index = newIndex;
            }

            _logger.LogInformation("OsmSpatialIndex build complete in {ElapsedMilliseconds}ms.", sw.ElapsedMilliseconds);
            IsReady = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building OsmSpatialIndex.");
            HasError = true;
            IsReady = true;
        }
    }

    private void ProcessPbfFile(string filePath, STRtree<MinimalNode> treeIndex, CancellationToken cancellationToken)
    {
        var memBefore = GC.GetTotalMemory(true);
        _logger.LogInformation("Processing {File}. Memory before: {Memory} bytes.", Path.GetFileName(filePath), memBefore);

        var validNodeIds = new List<long>();

        // Pass 1: Find valid ways and collect their node IDs
        using (var fileStream = File.OpenRead(filePath))
        {
            var source = new PBFOsmStreamSource(fileStream);

            foreach (var element in source)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (element.Type == OsmSharp.OsmGeoType.Way && element is OsmSharp.Way way)
                {
                    if (IsValidWay(way))
                    {
                        if (way.Nodes != null)
                        {
                            foreach (var nodeId in way.Nodes)
                            {
                                validNodeIds.Add(nodeId);
                            }
                        }
                    }
                }
            }
        } // fileStream and source disposed

        _logger.LogInformation("Pass 1 complete. Found {Count} node references in valid ways.", validNodeIds.Count);

        validNodeIds.Sort();
        // Remove duplicates in-place
        int uniqueCount = 0;
        if (validNodeIds.Count > 0)
        {
            long last = validNodeIds[0];
            validNodeIds[uniqueCount++] = last;
            for (int i = 1; i < validNodeIds.Count; i++)
            {
                long current = validNodeIds[i];
                if (current != last)
                {
                    validNodeIds[uniqueCount++] = current;
                    last = current;
                }
            }
            validNodeIds.RemoveRange(uniqueCount, validNodeIds.Count - uniqueCount);
        }

        _logger.LogInformation("Deduplicated to {Count} unique node IDs.", validNodeIds.Count);

        // Pass 2: Extract nodes matching the collected IDs
        int addedNodes = 0;
        using (var fileStream = File.OpenRead(filePath))
        {
            var source = new PBFOsmStreamSource(fileStream);

            foreach (var element in source)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (element.Type == OsmSharp.OsmGeoType.Node && element is OsmSharp.Node node)
                {
                    if (node.Id.HasValue && node.Longitude.HasValue && node.Latitude.HasValue)
                    {
                        if (validNodeIds.BinarySearch(node.Id.Value) >= 0)
                        {
                            var minimalNode = new MinimalNode((float)node.Longitude.Value, (float)node.Latitude.Value);
                            var env = new Envelope(minimalNode.Lon, minimalNode.Lon, minimalNode.Lat, minimalNode.Lat);
                            treeIndex.Insert(env, minimalNode);
                            addedNodes++;
                        }
                    }
                }
            }
        } // fileStream and source disposed

        _logger.LogInformation("Pass 2 complete. Inserted {Count} nodes into index.", addedNodes);

        validNodeIds.Clear();
        validNodeIds.TrimExcess();

        GC.Collect();

        var memAfter = GC.GetTotalMemory(true);
        _logger.LogInformation("Finished processing {File}. Memory after GC: {Memory} bytes. Memory diff: {Diff} bytes.",
            Path.GetFileName(filePath), memAfter, memAfter - memBefore);
    }

    private bool IsValidWay(OsmSharp.Way way)
    {
        if (way.Tags == null) return false;

        if (way.Tags.TryGetValue("access", out var accessValue))
        {
            if (accessValue == "private") return false;
        }

        if (way.Tags.TryGetValue("highway", out var highwayValue))
        {
            if (highwayValue == "motorway" || highwayValue == "trunk") return false;

            return highwayValue == "cycleway" ||
                   highwayValue == "residential" ||
                   highwayValue == "tertiary" ||
                   highwayValue == "path" ||
                   highwayValue == "track" ||
                   highwayValue == "living_street";
        }

        return false;
    }
}
