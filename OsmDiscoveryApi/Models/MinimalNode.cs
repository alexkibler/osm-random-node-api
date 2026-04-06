namespace OsmDiscoveryApi.Models;

public readonly struct MinimalNode
{
    public float Lon { get; }
    public float Lat { get; }

    public MinimalNode(float lon, float lat)
    {
        Lon = lon;
        Lat = lat;
    }
}
