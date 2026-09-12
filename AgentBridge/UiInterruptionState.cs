using System;
using System.Collections.Generic;

namespace AgentBridge;

internal sealed record UiBlockerV1
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "unknown";
    public string Screen { get; init; } = "";
    public string State { get; init; } = "blocked";
    public string? Reason { get; init; }
    public string? Title { get; init; }
    public string? Body { get; init; }
    public string[] Actions { get; init; } = Array.Empty<string>();
    public string[] Choices { get; init; } = Array.Empty<string>();
    public string? SelectedChoice { get; init; }
}

internal sealed record UiStateV1
{
    public bool AutoHandlingEnabled { get; init; }
    public bool Ready { get; init; } = true;
    public UiBlockerV1? Blocker { get; init; }
}

// One controller for both automatic and explicitly requested actions. Only managed
// identities/timestamps live here; every native target is resolved again before acting.
internal sealed class UiInterruptionTracker
{
    private readonly string prefix = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> issued = new(StringComparer.Ordinal);
    private readonly HashSet<string> automaticallyIssued = new(StringComparer.Ordinal);
    private bool currentAutomatic; // Ownership persists while native readiness catches up.
    private bool cancelled;
    private string? key;
    private int sequence;
    private double since;
    private double? readySince;
    private double? actedAt;
    private bool readinessTimedOut;
    public string Id { get; private set; } = "";
    public string State { get; private set; } = "waiting";
    public string? Error { get; private set; }
    public bool CanAct { get; private set; }

    public void Observe(string? identity, bool ready, bool automatic, bool awaitingReadiness, double now)
    {
        if (identity == null)
        {
            key = null;
            issued.Clear();
            automaticallyIssued.Clear();
            cancelled = false;
            Error = null;
            CanAct = false;
            return;
        }
        if (identity != key)
        {
            if (cancelled)
            {
                // A successful explicit cancellation returns to a fresh manual
                // decision. Automatic acknowledgements must still never cycle.
                issued.IntersectWith(automaticallyIssued);
                cancelled = false;
            }
            key = identity;
            Id = $"ui-{prefix}-{++sequence}";
            since = now;
            readySince = null;
            actedAt = null;
            Error = null;
            readinessTimedOut = false;
        }
        currentAutomatic = automatic;
        // Readiness can arrive late. No action was issued, so exposing the newly
        // ready action is safe; completed/failed action attempts remain single-shot.
        if (readinessTimedOut && ready)
        {
            readinessTimedOut = false;
            Error = null;
            since = now;
            readySince = null;
        }
        if (Error != null) { State = "failed"; CanAct = false; return; }
        if (actedAt != null)
        {
            CanAct = false;
            State = "acting";
            if (now - actedAt.Value >= 10) Fail("The UI did not complete its action within 10 seconds; it will not be retried.");
            return;
        }
        if (issued.Contains(identity))
        {
            Fail("The UI returned to an already-acted-on step without completing the interruption.");
            return;
        }
        readySince = ready ? readySince ?? now : null;
        CanAct = readySince != null && now - readySince.Value >= 0.2;
        State = automatic || (awaitingReadiness && !ready) ? "waiting" : "blocked";
        if ((automatic || awaitingReadiness) && now - since >= 15 && !CanAct)
        {
            Fail("The UI did not become interactable within 15 seconds.");
            readinessTimedOut = true;
        }
    }

    public bool Begin(string id, double now, bool cancellation = false)
    {
        if (id != Id || !CanAct || key == null || Error != null || issued.Contains(key)) return false;
        if (issued.Count >= 64) { Fail("Interruption exceeded 64 UI transitions."); return false; }
        issued.Add(key);
        if (currentAutomatic) automaticallyIssued.Add(key);
        cancelled = cancellation;
        actedAt = now;
        State = "acting";
        CanAct = false;
        return true;
    }

    public void Fail(string reason)
    {
        Error = reason;
        cancelled = false;
        State = "failed";
        CanAct = false;
    }
}
