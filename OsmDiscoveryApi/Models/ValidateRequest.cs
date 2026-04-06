namespace OsmDiscoveryApi.Models;

public class ValidateRequest
{
    public PointResponse[]? Points { get; set; }
    public string Profile { get; set; } = "bike"; // bike, foot, car
}
