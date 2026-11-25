using Th3Essentials.Sombreterre;

namespace Tests;

public class LinkThrottleTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc);
    private const string Address = "203.0.113.7";

    private static LinkThrottle Throttle() => new(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));

    [Fact]
    public void ThreeAttemptsAreFreeThenTheWaitDoublesUpToTheCeiling()
    {
        var throttle = Throttle();
        var now = Now;
        var waits = new List<double>();
        for (var lockout = 0; lockout < 6; lockout++)
        {
            for (var i = 0; i < 3; i++) Assert.True(throttle.TryStart(Address, now, out _));
            Assert.False(throttle.TryStart(Address, now, out var wait));
            waits.Add(wait.TotalMinutes);

            // Still waiting just before the end, free again once it is over.
            Assert.False(throttle.TryStart(Address, now + wait - TimeSpan.FromSeconds(1), out _));
            now += wait;
        }
        Assert.Equal(new double[] { 5, 10, 20, 40, 60, 60 }, waits);
    }

    [Fact]
    public void AnHourOfQuietOrASuccessfulLinkStartsOver()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++) throttle.TryStart(Address, Now, out _);
        Assert.False(throttle.TryStart(Address, Now, out _));

        // Waited out, then quiet for over an hour: the next lockout is five minutes again.
        var later = Now.AddMinutes(5).AddHours(1).AddMinutes(1);
        for (var i = 0; i < 3; i++) Assert.True(throttle.TryStart(Address, later, out _));
        Assert.False(throttle.TryStart(Address, later, out var wait));
        Assert.Equal(5, wait.TotalMinutes);

        // A link that went through forgets the address at once.
        throttle.Reset(Address);
        Assert.True(throttle.TryStart(Address, later, out _));
    }

    [Fact]
    public void AddressesAreCountedApart()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++) throttle.TryStart(Address, Now, out _);
        Assert.False(throttle.TryStart(Address, Now, out _));
        Assert.True(throttle.TryStart("198.51.100.4", Now, out _));
    }
}
