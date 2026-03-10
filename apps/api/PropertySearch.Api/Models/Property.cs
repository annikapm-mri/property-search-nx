namespace PropertySearch.Api.Models;

public class Property
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = "";
    public string Region { get; set; } = "";
    public decimal Price { get; set; }
    public string Type { get; set; } = "";
    public double SqFeet { get; set; }
    public int Beds { get; set; }
    public double Baths { get; set; }
    public int? CatsAllowed { get; set; }
    public int? DogsAllowed { get; set; }
    public int? SmokingAllowed { get; set; }
    public int? WheelchairAccess { get; set; }
    public int? ComesFurnished { get; set; }
    public string? LaundryOptions { get; set; }
    public string? ParkingOptions { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public double? Lat { get; set; }
    public double? Long { get; set; }
    public string State { get; set; } = "";
}