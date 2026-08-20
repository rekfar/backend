using System.Text.Json;
using Rekfar.Catalogue;

namespace Rekfar.Catalogue.Tests;

/// <summary>
/// The response is a wire contract two map renderers consume directly, so its shape is
/// asserted as JSON rather than as objects.
/// </summary>
public class PeakFeatureCollectionTests
{
    // What ASP.NET Core serialises with: camelCase, and the same options minimal APIs use.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static PeakFeatureCollection Galdhopiggen() => new()
    {
        Attribution = "© Kartverket",
        Truncated = false,
        Features =
        [
            new PeakFeature
            {
                Id = 1,
                Geometry = new PointGeometry { Coordinates = [8.3126, 61.6363] },
                Properties = new PeakProperties
                {
                    Name = "Galdhøpiggen",
                    ElevationMeters = 2469,
                    ProminenceMeters = 2372,
                    UtnoUrl = null,
                },
            },
        ],
    };

    private static JsonElement Serialise(PeakFeatureCollection collection) =>
        JsonDocument.Parse(JsonSerializer.Serialize(collection, Web)).RootElement;

    [Fact]
    public void Is_a_geojson_feature_collection()
    {
        var json = Serialise(Galdhopiggen());

        Assert.Equal("FeatureCollection", json.GetProperty("type").GetString());
        Assert.Equal("Feature", json.GetProperty("features")[0].GetProperty("type").GetString());
        Assert.Equal("Point", json.GetProperty("features")[0].GetProperty("geometry").GetProperty("type").GetString());
    }

    /// <summary>
    /// GeoJSON orders a position longitude-first, the reverse of how a coordinate is spoken
    /// and of how SQL Server's geography::Point takes one. Reversed, every Norwegian peak
    /// lands in the Indian Ocean — and still renders, just in the wrong place.
    /// </summary>
    [Fact]
    public void Coordinates_are_longitude_then_latitude()
    {
        var coordinates = Serialise(Galdhopiggen())
            .GetProperty("features")[0]
            .GetProperty("geometry")
            .GetProperty("coordinates");

        Assert.Equal(8.3126, coordinates[0].GetDouble());
        Assert.Equal(61.6363, coordinates[1].GetDouble());

        // Longitude first also means the smaller number first at Norwegian latitudes, which
        // is the quickest eyeball check on a payload.
        Assert.True(coordinates[0].GetDouble() < coordinates[1].GetDouble());
    }

    [Fact]
    public void Names_are_camel_cased_for_the_client()
    {
        var properties = Serialise(Galdhopiggen()).GetProperty("features")[0].GetProperty("properties");

        Assert.Equal("Galdhøpiggen", properties.GetProperty("name").GetString());
        Assert.Equal(2469, properties.GetProperty("elevationMeters").GetInt32());
        Assert.Equal(2372, properties.GetProperty("prominenceMeters").GetInt32());
    }

    /// <summary>
    /// Attribution is a licence condition on Kartverket's CC BY 4.0 data (NFR-LEGAL-2), so
    /// it travels with the payload rather than depending on each client to remember it.
    /// </summary>
    [Fact]
    public void Carries_attribution_and_the_truncation_flag()
    {
        var json = Serialise(Galdhopiggen());

        Assert.Equal("© Kartverket", json.GetProperty("attribution").GetString());
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Renders_a_peak_with_no_sampled_elevation()
    {
        var collection = new PeakFeatureCollection
        {
            Attribution = "© Kartverket",
            Truncated = true,
            Features =
            [
                new PeakFeature
                {
                    Id = 2,
                    Geometry = new PointGeometry { Coordinates = [8.4, 61.5] },
                    Properties = new PeakProperties { Name = "Usamplet topp" },
                },
            ],
        };

        var properties = Serialise(collection).GetProperty("features")[0].GetProperty("properties");

        // Null rather than absent or zero: the elevation is unknown, not sea level.
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("elevationMeters").ValueKind);
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("utnoUrl").ValueKind);
    }

    [Fact]
    public void An_empty_extent_is_an_empty_collection_not_a_null_one()
    {
        var json = Serialise(new PeakFeatureCollection
        {
            Attribution = "© Kartverket",
            Truncated = false,
            Features = [],
        });

        Assert.Equal(JsonValueKind.Array, json.GetProperty("features").ValueKind);
        Assert.Equal(0, json.GetProperty("features").GetArrayLength());
    }
}
