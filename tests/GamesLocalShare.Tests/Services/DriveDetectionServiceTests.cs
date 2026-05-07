namespace GamesLocalShare.Tests.Services;

public class DriveDetectionServiceTests
{
    [Fact]
    public void Constructor_PollsCurrentDrives_NeverThrows()
    {
        Action act = () =>
        {
            using var svc = new DriveDetectionService();
            svc.CurrentDrives.Should().NotBeNull();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void DriveCandidate_ValueEquality_TreatsIdenticalRecordsAsEqual()
    {
        var a = new DriveCandidate("D:\\", "Data", "D:", false, true);
        var b = new DriveCandidate("D:\\", "Data", "D:", false, true);
        var c = new DriveCandidate("E:\\", "Data", "E:", false, true);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var svc = new DriveDetectionService();
        svc.Dispose();
        var second = () => svc.Dispose();
        second.Should().NotThrow();
    }

    [Fact]
    public void DrivesChanged_IsRaisable_WithoutThrowing()
    {
        using var svc = new DriveDetectionService();
        var raised = false;
        svc.DrivesChanged += (_, _) => raised = true;

        // Just confirm the API surface; the timer-based change can't be deterministically forced
        // in a unit test, but subscribing must not throw.
        raised.Should().BeFalse(); // Initial poll already happened in ctor
    }
}
