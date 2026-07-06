using System;
using NzbWebDAV.Clients.Usenet.Connections;
using Xunit;

namespace NzbWebDAV.Tests;

public class LatencyCheckGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    private static DateTimeOffset T(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void FirstCall_Begins()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
    }

    [Fact]
    public void SecondCall_WhileInFlight_IsBlocked()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        Assert.False(gate.TryBegin(T(0))); // single-flight: previous not ended
    }

    [Fact]
    public void AfterEnd_WithinInterval_IsThrottled()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.False(gate.TryBegin(T(20))); // 20s <= 45s
    }

    [Fact]
    public void AfterEnd_PastInterval_Begins()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.True(gate.TryBegin(T(46))); // 46s > 45s
    }

    [Fact]
    public void End_ReleasesFlag_EvenAfterSimulatedFailure()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End(); // ping finished/threw -> finally released the flag
        Assert.True(gate.TryBegin(T(50))); // released AND past interval
    }

    [Fact]
    public void ExactInterval_IsStillThrottled()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.False(gate.TryBegin(T(45))); // boundary: <= is throttled
    }
}
