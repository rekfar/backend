using System.Net;
using System.Text.Json;

namespace Rekfar.Api.IntegrationTests;

[Collection(nameof(RekfarApiCollection))]
public class PeaksEndpointTests(RekfarApiFixture fixture)
{
    /// <summary>Jotunheimen. Three seeded peaks sit inside it, two well outside.</summary>
    private const string Jotunheimen = "7.5,61.3,8.8,61.8";

    /// <summary>Mainland Norway plus Svalbard — everything seeded falls inside.</summary>
    private const string Everything = "2,57,32,81";

    private async Task<JsonElement> GetAsync(string query)
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static string[] NamesOf(JsonElement collection) => collection
        .GetProperty("features")
        .EnumerateArray()
        .Select(feature => feature.GetProperty("properties").GetProperty("name").GetString()!)
        .ToArray();

    /// <summary>
    /// The assertion this whole fixture exists for. SQL Server's geography follows the
    /// left-hand rule, so a bounding box wound the wrong way describes the entire planet
    /// except the extent. That failure returns a full, well-formed, plausible-looking
    /// FeatureCollection — of every peak the user is not looking at. Only asserting both
    /// halves, inclusion and exclusion, can tell the two apart.
    /// </summary>
    [Fact]
    public async Task Returns_the_peaks_inside_the_extent_and_not_their_complement()
    {
        var names = NamesOf(await GetAsync(Jotunheimen));

        Assert.Contains("Galdhøpiggen", names);
        Assert.Contains("Glittertind", names);
        Assert.Contains("Store Skagastølstind", names);

        // Outside the extent. These are what a reversed ring would have returned instead.
        Assert.DoesNotContain("Snøhetta", names);
        Assert.DoesNotContain("Newtontoppen", names);
    }

    /// <summary>
    /// Retired peaks stay in the table because somebody may have logged them, but they must
    /// never reach the map. This one is seeded inside the extent so that only the IsActive
    /// filter can be excluding it.
    /// </summary>
    [Fact]
    public async Task Excludes_retired_peaks_that_fall_inside_the_extent()
    {
        Assert.DoesNotContain("Nedlagt topp", NamesOf(await GetAsync(Jotunheimen)));
        Assert.DoesNotContain("Nedlagt topp", NamesOf(await GetAsync(Everything)));
    }

    /// <summary>
    /// geography::Point stores latitude first; GeoJSON writes longitude first. The value
    /// round-trips through a geography column, EF's .Long/.Lat translation and the
    /// serialiser, and a swap anywhere along that path renders a Norwegian peak in the
    /// Indian Ocean without erroring anywhere.
    /// </summary>
    [Fact]
    public async Task Coordinates_survive_the_round_trip_as_longitude_then_latitude()
    {
        var galdhopiggen = (await GetAsync(Jotunheimen))
            .GetProperty("features")
            .EnumerateArray()
            .Single(f => f.GetProperty("properties").GetProperty("name").GetString() == "Galdhøpiggen");

        var coordinates = galdhopiggen.GetProperty("geometry").GetProperty("coordinates");

        Assert.Equal(8.3126, coordinates[0].GetDouble(), precision: 4);
        Assert.Equal(61.6363, coordinates[1].GetDouble(), precision: 4);
    }

    [Fact]
    public async Task Orders_highest_first_and_puts_unsampled_peaks_last()
    {
        var names = NamesOf(await GetAsync(Jotunheimen));

        Assert.Equal(["Galdhøpiggen", "Glittertind", "Store Skagastølstind", "Usamplet topp"], names);
    }

    [Fact]
    public async Task Returns_a_peak_that_has_never_been_sampled()
    {
        var unsampled = (await GetAsync(Jotunheimen))
            .GetProperty("features")
            .EnumerateArray()
            .Single(f => f.GetProperty("properties").GetProperty("name").GetString() == "Usamplet topp");

        // Null, not absent and not zero: the elevation is unknown, not sea level.
        Assert.Equal(
            JsonValueKind.Null,
            unsampled.GetProperty("properties").GetProperty("elevationMeters").ValueKind);
    }

    [Fact]
    public async Task Truncates_to_the_limit_and_reports_that_it_did()
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={Everything}&limit=2");
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(["Galdhøpiggen", "Glittertind"], NamesOf(json));
    }

    [Fact]
    public async Task Does_not_report_truncation_when_everything_fits()
    {
        Assert.False((await GetAsync(Everything)).GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// A minimum elevation excludes peaks below it and peaks whose elevation is unknown —
    /// asking for peaks above a height should not return ones whose height nobody sampled.
    /// </summary>
    [Fact]
    public async Task Minimum_elevation_excludes_lower_and_unsampled_peaks()
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={Everything}&minElevationMeters=2450");
        var names = NamesOf(JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));

        Assert.Equal(["Galdhøpiggen", "Glittertind"], names);
    }

    [Fact]
    public async Task An_extent_with_no_peaks_returns_an_empty_collection()
    {
        var json = await GetAsync("5,58,6,59");

        Assert.Equal("FeatureCollection", json.GetProperty("type").GetString());
        Assert.Empty(json.GetProperty("features").EnumerateArray());
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// Attribution is a condition of Kartverket's CC BY 4.0 licence, not a courtesy, so it
    /// travels with every response rather than relying on each client to remember it.
    /// </summary>
    [Fact]
    public async Task Every_response_carries_attribution()
    {
        Assert.Equal("© Kartverket", (await GetAsync(Everything)).GetProperty("attribution").GetString());
    }

    /// <summary>
    /// Norwegian names cross a Norwegian collation, nvarchar columns, the driver and UTF-8
    /// serialisation. A mistake anywhere in that chain shows up as mojibake rather than as
    /// an error.
    /// </summary>
    [Fact]
    public async Task Norwegian_characters_survive_the_round_trip()
    {
        var names = NamesOf(await GetAsync(Everything));

        Assert.Contains("Galdhøpiggen", names);
        Assert.Contains("Snøhetta", names);
        Assert.Contains("Store Skagastølstind", names);
    }

    [Fact]
    public async Task Reference_data_is_cacheable()
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={Everything}");

        var cacheControl = response.Headers.CacheControl;

        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Public);
        Assert.Equal(TimeSpan.FromSeconds(600), cacheControl.MaxAge);
    }

    /// <summary>
    /// Asserted over real HTTP rather than against the parser, because the error shape is
    /// part of the contract: RFC 9457 problem+json, carrying a correlation id.
    /// </summary>
    [Theory]
    [InlineData("", "required")]
    [InlineData("8,61,9", "exactly four")]
    [InlineData("aa,61,9,62", "not a number")]
    [InlineData("9,61,8,62", "west must be less than east")]
    [InlineData("200,61,9,62", "longitudes")]
    public async Task Rejects_a_bad_extent_as_problem_details(string bbox, string expected)
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={bbox}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Contains(expected, problem.GetProperty("detail").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Rejects_a_limit_outside_the_allowed_range()
    {
        var response = await fixture.Client.GetAsync($"/v1/peaks?bbox={Everything}&limit=1001");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
