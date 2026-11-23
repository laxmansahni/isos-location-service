using ISOS.LocationService.Ingestion;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ISOS.LocationService.Tests;

public class PointValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly PointValidator _validator =
        new(Options.Create(new IngestionOptions()), new FakeTimeProvider(Now));

    private static PointDto ValidPoint() => new(
        PointId: Guid.NewGuid(),
        CapturedAt: Now.AddMinutes(-1),
        UtcOffsetMinutes: 60,
        Latitude: 51.5074,
        Longitude: -0.1276,
        Accuracy: 12.5,
        Speed: 1.4,
        Course: 270,
        Altitude: 18,
        MockLocation: false,
        Shared: true);

    [Fact]
    public void Valid_point_is_accepted() => Assert.Null(_validator.Validate(ValidPoint()));

    [Fact]
    public void Optional_fields_may_be_missing() =>
        Assert.Null(_validator.Validate(ValidPoint() with { Speed = null, Course = null, Altitude = null, UtcOffsetMinutes = null }));

    [Theory]
    [InlineData(nameof(PointDto.PointId))]
    [InlineData(nameof(PointDto.CapturedAt))]
    [InlineData(nameof(PointDto.Latitude))]
    [InlineData(nameof(PointDto.Longitude))]
    [InlineData(nameof(PointDto.Accuracy))]
    [InlineData(nameof(PointDto.Shared))]
    public void Missing_required_field_is_rejected(string field)
    {
        var point = field switch
        {
            nameof(PointDto.PointId) => ValidPoint() with { PointId = null },
            nameof(PointDto.CapturedAt) => ValidPoint() with { CapturedAt = null },
            nameof(PointDto.Latitude) => ValidPoint() with { Latitude = null },
            nameof(PointDto.Longitude) => ValidPoint() with { Longitude = null },
            nameof(PointDto.Shared) => ValidPoint() with { Shared = null },
            _ => ValidPoint() with { Accuracy = null },
        };

        Assert.Equal(PointRejectionReasons.MissingField, _validator.Validate(point));
    }

    [Fact]
    public void Both_sharing_values_are_valid()
    {
        Assert.Null(_validator.Validate(ValidPoint() with { Shared = true }));
        Assert.Null(_validator.Validate(ValidPoint() with { Shared = false }));
    }

    [Fact]
    public void Empty_point_id_is_rejected() =>
        Assert.Equal(PointRejectionReasons.MissingField, _validator.Validate(ValidPoint() with { PointId = Guid.Empty }));

    [Theory]
    [InlineData(90.1, 0)]
    [InlineData(-90.1, 0)]
    [InlineData(0, 180.1)]
    [InlineData(0, -180.1)]
    [InlineData(double.NaN, 0)]
    public void Out_of_range_coordinates_are_rejected(double latitude, double longitude) =>
        Assert.Equal(
            PointRejectionReasons.InvalidCoordinates,
            _validator.Validate(ValidPoint() with { Latitude = latitude, Longitude = longitude }));

    [Theory]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    public void Boundary_coordinates_are_accepted(double latitude, double longitude) =>
        Assert.Null(_validator.Validate(ValidPoint() with { Latitude = latitude, Longitude = longitude }));

    [Theory]
    [InlineData(-1)]
    [InlineData(10_000.1)]
    public void Invalid_accuracy_is_rejected(double accuracy) =>
        Assert.Equal(PointRejectionReasons.InvalidAccuracy, _validator.Validate(ValidPoint() with { Accuracy = accuracy }));

    [Fact]
    public void Negative_speed_is_rejected() =>
        Assert.Equal(PointRejectionReasons.InvalidSpeed, _validator.Validate(ValidPoint() with { Speed = -0.1 }));

    [Theory]
    [InlineData(-1)]
    [InlineData(360.1)]
    public void Invalid_course_is_rejected(double course) =>
        Assert.Equal(PointRejectionReasons.InvalidCourse, _validator.Validate(ValidPoint() with { Course = course }));

    [Fact]
    public void Small_clock_skew_is_tolerated() =>
        Assert.Null(_validator.Validate(ValidPoint() with { CapturedAt = Now.AddMinutes(4) }));

    [Fact]
    public void Point_far_in_the_future_is_rejected() =>
        Assert.Equal(PointRejectionReasons.CapturedInFuture, _validator.Validate(ValidPoint() with { CapturedAt = Now.AddMinutes(6) }));

    [Fact]
    public void Point_older_than_retention_is_rejected() =>
        Assert.Equal(PointRejectionReasons.CapturedTooOld, _validator.Validate(ValidPoint() with { CapturedAt = Now.AddDays(-14).AddSeconds(-1) }));

    [Fact]
    public void Point_at_the_edge_of_retention_is_accepted() =>
        Assert.Null(_validator.Validate(ValidPoint() with { CapturedAt = Now.AddDays(-14).AddSeconds(1) }));
}
