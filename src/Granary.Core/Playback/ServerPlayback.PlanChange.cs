namespace Granary.Core.Playback;

// A plan change in flight, as the web player's `replanning || replacing` (player/components/VideoPlayer.tsx,
// planChangeInFlight): a recovery started for one names it, and it lasts until a stream plays again, so the viewer sees
// the wait for the new stream too.
public sealed partial class ServerPlayback
{
    public event EventHandler? PlanChangeChanged;

    public PlanChange PlanChange { get; private set; }

    // Over once a stream plays, or when nothing is left that would bring one: a refusal leaves the stream that plays, a
    // failure or the stop leaves none. A load still on its way, or a recovery after it, keeps the change going.
    private void SettlePlanChange()
    {
        if (PlanChange != PlanChange.None && !_recovering
            && (_started || _loadId == 0 || _stopping || Status.Phase is ServerPlaybackPhase.Failed or ServerPlaybackPhase.Ended))
        {
            SetPlanChange(PlanChange.None);
        }
    }

    private void SetPlanChange(PlanChange change)
    {
        if (PlanChange == change)
        {
            return;
        }
        PlanChange = change;
        PlanChangeChanged?.Invoke(this, EventArgs.Empty);
    }
}
