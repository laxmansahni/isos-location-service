using ISOS.LocationService.Policy;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Tests;

public class PolicyEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero); // a Wednesday
    private static readonly PolicyEvaluator Evaluator = new(Options.Create(new PolicyOptions()));

    private static ClientPolicy Client(bool sharing = true, bool schedule = true, bool eventWindow = true, DateTimeOffset? fence = null) =>
        new("c", sharing, TypeAlways: true, schedule, TypeActiveTravel: true, eventWindow, true, 14, 60, fence, 1);

    private static ConsentRecord Granted(DateTimeOffset? at = null, string version = "1", DateTimeOffset? fence = null) =>
        new(ConsentStatuses.Granted, version, at ?? Now.AddDays(-10), null, fence);

    private static PolicySnapshot Snapshot(
        ClientPolicy? client = null, ConsentRecord? consent = null, SharingPreference? preference = null, params EventWindow[] windows) =>
        new(client ?? Client(), new UserPolicyState(consent ?? Granted(), preference, windows));

    private static SharingPreference Always() => new(SharingTypes.Always, null, []);

    private static SharingPreference Schedule(string? home, params ScheduleWindow[] windows) => new(SharingTypes.Schedule, home, windows);

    private static ScheduleWindow Window(DayOfWeek day, string start, string end) =>
        new((int)day, TimeOnly.Parse(start), TimeOnly.Parse(end));

    [Fact]
    public void Always_is_allowed_with_consent()
    {
        Assert.True(Evaluator.Evaluate(Snapshot(preference: Always()), Now, null, null).Allowed);
    }

    [Fact]
    public void Client_that_has_not_enabled_sharing_is_denied()
    {
        var decision = Evaluator.Evaluate(Snapshot(Client(sharing: false), preference: Always()), Now, null, null);

        Assert.Equal(PolicyDenials.SharingNotAllowed, decision.Reason);
    }

    [Theory]
    [InlineData(ConsentStatuses.Revoked, "1")]
    [InlineData(ConsentStatuses.Granted, "0")]
    public void Revoked_or_outdated_consent_is_denied(string status, string version)
    {
        var consent = new ConsentRecord(status, version, Now.AddDays(-10), null, null);

        Assert.Equal(PolicyDenials.ConsentRequired, Evaluator.Evaluate(Snapshot(consent: consent, preference: Always()), Now, null, null).Reason);
    }

    [Fact]
    public void Missing_consent_is_denied()
    {
        var snapshot = new PolicySnapshot(Client(), new UserPolicyState(null, Always(), []));

        Assert.Equal(PolicyDenials.ConsentRequired, Evaluator.Evaluate(snapshot, Now, null, null).Reason);
    }

    [Fact]
    public void No_chosen_type_is_denied()
    {
        Assert.Equal(PolicyDenials.NoActiveSharingType, Evaluator.Evaluate(Snapshot(), Now, null, null).Reason);
    }

    [Fact]
    public void A_type_the_client_no_longer_offers_is_denied()
    {
        var snapshot = Snapshot(Client(schedule: false), preference: Schedule(null, Window(DayOfWeek.Wednesday, "00:00", "23:00")));

        Assert.Equal(PolicyDenials.NoActiveSharingType, Evaluator.Evaluate(snapshot, Now, null, null).Reason);
    }

    [Fact]
    public void Active_travel_never_allows_until_trips_exist()
    {
        var snapshot = Snapshot(preference: new SharingPreference(SharingTypes.ActiveTravel, null, []));

        Assert.Equal(PolicyDenials.OutsideSharingWindow, Evaluator.Evaluate(snapshot, Now, null, null).Reason);
    }

    [Fact]
    public void Point_captured_before_consent_is_denied_but_a_small_clock_skew_is_tolerated()
    {
        var snapshot = Snapshot(consent: Granted(at: Now), preference: Always());

        Assert.Equal(PolicyDenials.CapturedBeforeConsent, Evaluator.Evaluate(snapshot, Now.AddMinutes(-10), null, null).Reason);
        Assert.True(Evaluator.Evaluate(snapshot, Now.AddSeconds(-30), null, null).Allowed);
    }

    [Fact]
    public void Point_captured_at_or_before_the_fence_is_denied()
    {
        var fence = Now.AddHours(-1);
        var byConsent = Snapshot(consent: Granted(fence: fence), preference: Always());
        var byClient = Snapshot(Client(fence: fence), preference: Always());

        foreach (var snapshot in new[] { byConsent, byClient })
        {
            Assert.Equal(PolicyDenials.CapturedBeforeConsent, Evaluator.Evaluate(snapshot, fence, null, null).Reason);
            Assert.True(Evaluator.Evaluate(snapshot, fence.AddSeconds(1), null, null).Allowed);
        }
    }

    [Fact]
    public void Schedule_uses_the_local_time_of_the_point_offset()
    {
        // Wednesday 12:00 UTC is Wednesday 13:00 at +60 and Wednesday 17:30 at +330.
        var snapshot = Snapshot(preference: Schedule(null, Window(DayOfWeek.Wednesday, "09:00", "13:30")));

        Assert.True(Evaluator.Evaluate(snapshot, Now, utcOffsetMinutes: 60, null).Allowed);
        Assert.Equal(PolicyDenials.OutsideSharingWindow, Evaluator.Evaluate(snapshot, Now, utcOffsetMinutes: 330, null).Reason);
    }

    [Fact]
    public void Schedule_falls_back_to_device_zone_then_home_zone()
    {
        // 12:00 UTC on 25 Nov is 21:00 in Tokyo and 12:00 in London (winter time).
        var evening = Schedule("Europe/London", Window(DayOfWeek.Wednesday, "20:00", "22:00"));

        Assert.True(Evaluator.Evaluate(Snapshot(preference: evening), Now, null, "Asia/Tokyo").Allowed);
        Assert.Equal(PolicyDenials.OutsideSharingWindow, Evaluator.Evaluate(Snapshot(preference: evening), Now, null, null).Reason);
        Assert.Equal(PolicyDenials.OutsideSharingWindow, Evaluator.Evaluate(Snapshot(preference: evening), Now, null, "Not/AZone").Reason);
    }

    [Fact]
    public void Schedule_window_end_is_exclusive()
    {
        var snapshot = Snapshot(preference: Schedule(null, Window(DayOfWeek.Wednesday, "10:00", "12:00")));

        Assert.True(Evaluator.Evaluate(snapshot, Now.AddSeconds(-1), null, null).Allowed);
        Assert.False(Evaluator.Evaluate(snapshot, Now, null, null).Allowed);
    }

    [Fact]
    public void Window_that_crosses_midnight_covers_both_sides()
    {
        var snapshot = Snapshot(preference: Schedule(null, Window(DayOfWeek.Tuesday, "22:00", "06:00")));
        var tuesdayLate = new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero);
        var wednesdayEarly = new DateTimeOffset(2026, 11, 25, 5, 0, 0, TimeSpan.Zero);
        var wednesdayLate = new DateTimeOffset(2026, 11, 25, 23, 0, 0, TimeSpan.Zero);

        Assert.True(Evaluator.Evaluate(snapshot, tuesdayLate, null, null).Allowed);
        Assert.True(Evaluator.Evaluate(snapshot, wednesdayEarly, null, null).Allowed);
        Assert.False(Evaluator.Evaluate(snapshot, wednesdayLate, null, null).Allowed);
    }

    [Fact]
    public void Event_window_opens_sharing_even_when_the_chosen_type_is_closed_but_only_with_consent()
    {
        var window = new EventWindow(EventTriggers.Aec, "aec-1", Now.AddMinutes(-30), Now.AddMinutes(30));
        var closedSchedule = Schedule(null, Window(DayOfWeek.Sunday, "00:00", "01:00"));

        Assert.True(Evaluator.Evaluate(Snapshot(preference: closedSchedule, windows: window), Now, null, null).Allowed);
        Assert.False(Evaluator.Evaluate(Snapshot(preference: closedSchedule, windows: window), Now.AddMinutes(31), null, null).Allowed);
        Assert.False(Evaluator.Evaluate(Snapshot(Client(eventWindow: false), preference: closedSchedule, windows: window), Now, null, null).Allowed);
        var noConsent = new PolicySnapshot(Client(), new UserPolicyState(null, closedSchedule, [window]));
        Assert.Equal(PolicyDenials.ConsentRequired, Evaluator.Evaluate(noConsent, Now, null, null).Reason);
    }

    [Fact]
    public void Next_evaluation_is_the_next_schedule_boundary()
    {
        var snapshot = Snapshot(preference: Schedule(null, Window(DayOfWeek.Wednesday, "13:00", "15:00")));

        var next = Evaluator.NextEvaluationAt(snapshot, Now, null);

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_evaluation_inside_a_window_is_its_end_and_follows_the_zone()
    {
        // 12:00 UTC is 21:00 in Tokyo, inside 20:00 to 22:00, which ends at 13:00 UTC.
        var snapshot = Snapshot(preference: Schedule("Asia/Tokyo", Window(DayOfWeek.Wednesday, "20:00", "22:00")));

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero), Evaluator.NextEvaluationAt(snapshot, Now, null));
    }

    [Fact]
    public void Next_evaluation_includes_event_windows_and_is_null_when_nothing_is_scheduled()
    {
        var window = new EventWindow(EventTriggers.Aec, null, Now.AddMinutes(-5), Now.AddMinutes(55));

        Assert.Equal(Now.AddMinutes(55), Evaluator.NextEvaluationAt(Snapshot(preference: Always(), windows: window), Now, null));
        Assert.Null(Evaluator.NextEvaluationAt(Snapshot(preference: Always()), Now, null));
    }

    [Fact]
    public void Next_evaluation_skips_a_daylight_saving_gap()
    {
        // 2026-03-29 01:30 does not exist in London (clocks go from 01:00 to 02:00).
        var snapshot = Snapshot(preference: Schedule("Europe/London", Window(DayOfWeek.Sunday, "01:30", "03:00")));
        var before = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

        var next = Evaluator.NextEvaluationAt(snapshot, before, null);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), next); // 02:30 BST
    }
}
