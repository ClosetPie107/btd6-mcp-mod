namespace AgentBridge;

internal static class UiInterruptionTests
{
    public static void Run()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        static UiInterruptionTracker Ready(string key = "popup")
        {
            var tracker = new UiInterruptionTracker();
            tracker.Observe(key, true, true, true, 0);
            tracker.Observe(key, true, true, true, 0.3);
            return tracker;
        }
        var once = Ready();
        Check(once.Begin(once.Id, 0.3), "Ready tutorial should accept acknowledgement");
        once.Observe("popup", true, true, true, 1);
        Check(!once.Begin(once.Id, 1), "An in-flight UI action must not repeat");
        once.Observe("popup", true, true, true, 11);
        Check(once.State == "failed" && !once.CanAct, "A stuck transition must become an explicit failure");
        once.Observe("popup", false, true, true, 12);
        once.Observe("popup", true, true, true, 13);
        Check(once.State == "failed" && !once.Begin(once.Id, 13), "New readiness must not retry an already-issued action");
        Console.WriteLine("PASS UI acknowledgement is single-shot and bounded");

        var stale = Ready();
        string oldId = stale.Id;
        stale.Observe("another-popup", true, true, true, 1);
        stale.Observe("another-popup", true, true, true, 1.3);
        Check(!stale.Begin(oldId, 1.3) && stale.Begin(stale.Id, 1.3), "A stale target must never affect its replacement");
        Console.WriteLine("PASS stale UI response cannot act on replacement");

        var loading = new UiInterruptionTracker();
        loading.Observe("loading", false, true, true, 0);
        loading.Observe("loading", false, true, true, 16);
        Check(loading.State == "failed", "Unfinished UI loading must not wait forever");
        Console.WriteLine("PASS UI readiness timeout reports failure");
        loading.Observe("loading", true, false, true, 17);
        Check(!loading.CanAct, "Late readiness must still stabilize before an explicit action");
        loading.Observe("loading", true, false, true, 17.3);
        Check(loading.State == "blocked" && loading.Begin(loading.Id, 17.3),
            "A readiness timeout must not permanently disable a screen that later becomes actionable");
        Console.WriteLine("PASS late UI readiness recovers without repeating an issued action");

        var manual = new UiInterruptionTracker();
        manual.Observe("choice", true, false, true, 0);
        manual.Observe("choice", true, false, true, 0.3);
        Check(manual.State == "blocked" && manual.CanAct, "A ready choice needs an explicit decision, not automatic dismissal");
        Console.WriteLine("PASS manual and unknown UI remain decision blockers");

        var unstable = Ready();
        unstable.Observe("popup", false, true, true, 0.4);
        unstable.Observe("popup", true, true, true, 0.5);
        Check(!unstable.CanAct, "Interactability must remain stable before acknowledgement");
        Console.WriteLine("PASS UI interactability must stabilize");

        var cycle = Ready("A");
        cycle.Begin(cycle.Id, 0.3);
        cycle.Observe("B", true, true, true, 1);
        cycle.Observe("B", true, true, true, 1.3);
        cycle.Begin(cycle.Id, 1.3);
        cycle.Observe("A", true, true, true, 2);
        Check(cycle.State == "failed", "A cyclic UI sequence must not produce an acknowledgement loop");
        cycle.Observe(null, false, false, false, 3);
        cycle.Observe("A", true, true, true, 4);
        cycle.Observe("A", true, true, true, 4.3);
        Check(cycle.Begin(cycle.Id, 4.3), "A completed interruption must not poison future screens");
        Console.WriteLine("PASS UI cycle detection resets only after completion");

        var cancellation = new UiInterruptionTracker();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            double now = attempt * 3;
            cancellation.Observe("defeat", true, false, true, now);
            cancellation.Observe("defeat", true, false, true, now + 0.3);
            Check(cancellation.Begin(cancellation.Id, now + 0.3), "Cancelling restart must leave the defeat screen usable");
            cancellation.Observe("restart-confirmation", true, false, true, now + 1);
            cancellation.Observe("restart-confirmation", true, false, true, now + 1.3);
            Check(cancellation.Begin(cancellation.Id, now + 1.3, cancellation: true), "A reopened confirmation must accept a new explicit decision");
            cancellation.Observe("restart-confirmation", true, false, true, now + 1.4);
            Check(!cancellation.Begin(cancellation.Id, now + 1.4, cancellation: true), "An in-flight cancellation must not repeat");
        }
        Console.WriteLine("PASS explicit cancellation permits returning to the parent decision");

        var cancelledAutomatic = Ready("tutorial");
        cancelledAutomatic.Begin(cancelledAutomatic.Id, 0.3);
        cancelledAutomatic.Observe("choice", true, false, true, 1);
        cancelledAutomatic.Observe("choice", true, false, true, 1.3);
        cancelledAutomatic.Begin(cancelledAutomatic.Id, 1.3, cancellation: true);
        cancelledAutomatic.Observe("tutorial", true, true, true, 2);
        Check(cancelledAutomatic.State == "failed", "Manual cancellation must not permit an automatic acknowledgement loop");
        Console.WriteLine("PASS cancellation retains automatic cycle protection");
    }
}
