using System.Globalization;
using NetTopologySuite.Algorithm;
using Rekfar.Catalogue;

namespace Rekfar.Catalogue.Tests;

public class BoundingBoxTests
{
    [Fact]
    public void Parses_the_four_values_in_wsen_order()
    {
        Assert.True(BoundingBox.TryParse("7.5,61.3,8.8,61.8", out var box, out var error));

        Assert.Null(error);
        Assert.Equal(7.5, box.West);
        Assert.Equal(61.3, box.South);
        Assert.Equal(8.8, box.East);
        Assert.Equal(61.8, box.North);
    }

    [Fact]
    public void Tolerates_whitespace_around_values()
    {
        Assert.True(BoundingBox.TryParse(" 7.5 , 61.3 , 8.8 , 61.8 ", out var box, out _));
        Assert.Equal(7.5, box.West);
        Assert.Equal(61.8, box.North);
    }

    [Fact]
    public void Accepts_negative_and_zero_coordinates()
    {
        Assert.True(BoundingBox.TryParse("-5.5,-1.25,0,0.5", out var box, out _));
        Assert.Equal(-5.5, box.West);
        Assert.Equal(-1.25, box.South);
        Assert.Equal(0, box.East);
    }

    /// <summary>
    /// The reason parsing is pinned to InvariantCulture. nb-NO is the application's first
    /// locale and writes decimals with a comma — the same character that separates the four
    /// values — so under the ambient culture "8.31" would read as 831 and put the extent in
    /// the wrong hemisphere, on a Norwegian developer's machine only.
    /// </summary>
    [Theory]
    [InlineData("nb-NO")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Parses_the_same_under_any_ambient_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.True(BoundingBox.TryParse("8.31,61.63,8.56,61.66", out var box, out _));
            Assert.Equal(8.31, box.West);
            Assert.Equal(61.63, box.South);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A Norwegian decimal comma does not quietly become a different extent: it arrives as
    /// eight values and is refused, rather than being read as some other box.
    /// </summary>
    [Fact]
    public void Rejects_a_bbox_written_with_norwegian_decimal_commas()
    {
        Assert.False(BoundingBox.TryParse("8,5,61,0,9,5,62,0", out _, out var error));
        Assert.Contains("exactly four", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_a_value(string? value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("required", error);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("8,61,9")]
    [InlineData("8,61,9,62,63")]
    public void Requires_exactly_four_values(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("exactly four", error);
    }

    [Theory]
    [InlineData("aa,61,9,62")]
    [InlineData("8,,9,62")]
    [InlineData("8,61,9,x")]
    public void Rejects_values_that_are_not_numbers(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("not a number", error);
    }

    /// <summary>
    /// NumberStyles.Float accepts these under InvariantCulture, and both would pass the
    /// range comparisons below by being false rather than by being in range.
    /// </summary>
    [Theory]
    [InlineData("NaN,61,9,62")]
    [InlineData("Infinity,61,9,62")]
    [InlineData("8,-Infinity,9,62")]
    public void Rejects_non_finite_values(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("not a number", error);
    }

    [Theory]
    [InlineData("-181,61,9,62")]
    [InlineData("8,61,181,62")]
    public void Rejects_longitudes_outside_the_world(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("longitudes", error);
    }

    [Theory]
    [InlineData("8,-91,9,62")]
    [InlineData("8,61,9,91")]
    public void Rejects_latitudes_outside_the_world(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("latitudes", error);
    }

    [Theory]
    [InlineData("9,61,8,62")]
    [InlineData("8,61,8,62")]
    public void Requires_west_to_be_west_of_east(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("west must be less than east", error);
    }

    [Theory]
    [InlineData("8,62,9,61")]
    [InlineData("8,61,9,61")]
    public void Requires_south_to_be_south_of_north(string value)
    {
        Assert.False(BoundingBox.TryParse(value, out _, out var error));
        Assert.Contains("south must be less than north", error);
    }

    [Fact]
    public void Polygon_carries_the_wgs84_srid()
    {
        BoundingBox.TryParse("7.5,61.3,8.8,61.8", out var box, out _);

        // Without this the geometry reaches SQL Server as SRID 0 and cannot be compared
        // against a 4326 column at all.
        Assert.Equal(4326, box.ToPolygon().SRID);
    }

    [Fact]
    public void Polygon_is_a_closed_ring_of_the_four_corners()
    {
        BoundingBox.TryParse("7.5,61.3,8.8,61.8", out var box, out _);
        var ring = box.ToPolygon().ExteriorRing.Coordinates;

        Assert.Equal(5, ring.Length);
        Assert.Equal(ring[0], ring[^1]);

        Assert.Equal(new[] { 7.5, 8.8, 8.8, 7.5 }, ring[..4].Select(c => c.X));
        Assert.Equal(new[] { 61.3, 61.3, 61.8, 61.8 }, ring[..4].Select(c => c.Y));
    }

    /// <summary>
    /// The one that matters. SQL Server's geography type follows the left-hand rule, so a
    /// clockwise ring describes everything except the extent — the query then returns every
    /// peak the user is not looking at, and nothing about the result's shape says so.
    /// </summary>
    [Fact]
    public void Polygon_is_wound_counter_clockwise()
    {
        BoundingBox.TryParse("7.5,61.3,8.8,61.8", out var box, out _);

        Assert.True(Orientation.IsCCW(box.ToPolygon().ExteriorRing.Coordinates));
    }
}
