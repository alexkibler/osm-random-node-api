namespace OsmDiscoveryApi.Models;

public class ValidateResult
{
    public PointResponse? Original { get; set; }
    public PointResponse? Snapped { get; set; }
    public double DistanceMeters { get; set; }
    public bool IsValid { get; set; }
    public string? RoadName { get; set; }
    public string? Error { get; set; }
}
