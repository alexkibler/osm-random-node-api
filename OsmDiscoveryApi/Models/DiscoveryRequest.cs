namespace OsmDiscoveryApi.Models;

public record DiscoveryRequest(double Lat, double Lon, double Radius, int Count);
