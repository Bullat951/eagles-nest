using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class RunwayQueueTests
{
    private static QueueObs Air(long id, double nm, bool closing = true) => new(id, nm, 600, 120, closing);
    private static QueueObs Ground(long id, double speedMs = 0, double nm = 0.5) => new(id, nm, 2, speedMs, false);

    [Fact]
    public void Order_moves_and_priority()
    {
        var q = new RunwayQueue("af:1");
        q.Add(1, QueueKind.Arrival);
        q.Add(2, QueueKind.Departure);
        q.Add(3, QueueKind.Arrival);
        Assert.Equal(2, q.Place(2));
        q.Move(3, -1);
        Assert.Equal([1L, 3, 2], q.Entries.Select(e => e.Unit));
        q.Priority(2);
        Assert.Equal([2L, 1, 3], q.Entries.Select(e => e.Unit));
        q.Move(2, 10);
        Assert.Equal(3, q.Place(2));
        q.Add(1, QueueKind.Departure);  // already in: only the kind changes
        Assert.Equal((1, QueueKind.Departure), (q.Place(1), q.Entries[0].Kind));
    }

    [Fact]
    public void A_unit_is_in_one_queue_at_a_time()
    {
        var all = new RunwayQueues();
        all.Add(RunwayQueues.AirfieldKey(7), 5, QueueKind.Arrival);
        all.Add(RunwayQueues.CarrierKey(4289), 5, QueueKind.Arrival);
        Assert.False(all.For("af:7").Contains(5));
        Assert.True(all.For("cv:4289").Contains(5));
    }

    [Fact]
    public void Done_strips_drop_out()
    {
        var q = new RunwayQueue("af:1");
        q.Add(1, QueueKind.Arrival);    // landed, still fast on the rollout: stays
        q.Add(2, QueueKind.Arrival);    // landed and slowed: done
        q.Add(3, QueueKind.Departure);  // just airborne over the field: stays
        q.Add(4, QueueKind.Departure);  // airborne 4 NM out: done
        q.Add(5, QueueKind.Arrival);    // gone from the picture
        var obs = new[] { Ground(1, 50), Ground(2, 8), new QueueObs(3, 0.8, 120, 90, false), new QueueObs(4, 4, 900, 140, false) }.ToDictionary(o => o.Id);
        Assert.True(q.Prune(obs));
        Assert.Equal([1L, 3], q.Entries.Select(e => e.Unit));
    }

    [Fact]
    public void States_follow_the_aircraft()
    {
        Assert.Equal("INBOUND", RunwayQueue.StateOf(QueueKind.Arrival, Air(1, 9)));
        Assert.Equal("FINAL", RunwayQueue.StateOf(QueueKind.Arrival, Air(1, 4)));
        Assert.Equal("LANDED", RunwayQueue.StateOf(QueueKind.Arrival, Ground(1, 40)));
        Assert.Equal("HOLDING", RunwayQueue.StateOf(QueueKind.Departure, Ground(1)));
        Assert.Equal("TAXI", RunwayQueue.StateOf(QueueKind.Departure, Ground(1, 8)));
        Assert.Equal("ROLLING", RunwayQueue.StateOf(QueueKind.Departure, Ground(1, 60)));
        Assert.Equal("AIRBORNE", RunwayQueue.StateOf(QueueKind.Departure, Air(1, 1, closing: false)));
    }

    [Fact]
    public void Candidates_are_closing_arrivals_and_taxiing_departures()
    {
        var q = new RunwayQueue("af:1");
        q.Add(9, QueueKind.Arrival);
        var obs = new[] { Air(1, 12), Air(2, 12, closing: false), Air(3, 30), Ground(4, 5), Ground(5, 0), Ground(6, 5, nm: 8), Air(9, 3) };
        var c = q.Candidates(obs).ToList();
        Assert.Equal([(4L, QueueKind.Departure), (1L, QueueKind.Arrival)], c.Select(x => (x.Obs.Id, x.Kind)));
    }

    [Fact]
    public void Departure_gap_from_the_nearest_queued_arrival()
    {
        var q = new RunwayQueue("af:1");
        Assert.Null(q.DepartureGap(new Dictionary<long, QueueObs>()));
        q.Add(1, QueueKind.Departure);
        var obs = new Dictionary<long, QueueObs> { [1] = Ground(1), [2] = Air(2, 8.2), [3] = Air(3, 4) };
        Assert.StartsWith("No arrivals", q.DepartureGap(obs));
        q.Add(2, QueueKind.Arrival);
        Assert.StartsWith("Gap: next arrival 8.2", q.DepartureGap(obs));
        q.Add(3, QueueKind.Arrival);
        Assert.StartsWith("No gap: next arrival 4.0", q.DepartureGap(obs));
    }

    [Fact]
    public void Observation_on_a_moving_carrier_uses_relative_speed()
    {
        // Parked on a deck steaming north at 13 m/s: not moving relative to the ship.
        var parked = QueueObs.Of(1, 10, 0, 22, 0, 13, 0, 0, 20, bvx: 13);
        Assert.False(parked.Airborne);
        Assert.True(parked.RelSpeedMs < 0.01);
        // 5 NM south of an airfield, flying north at 120 m/s, 600 m up: closing.
        var inbound = QueueObs.Of(2, -5 * 1852, 0, 700, 0, 120, 0, 0, 100);
        Assert.Equal((5.0, true, true), (Math.Round(inbound.RangeNm, 3), inbound.Airborne, inbound.Closing));
    }
}
