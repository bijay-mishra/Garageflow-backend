using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;

namespace GarageFlow.Api.Services;

/// <summary>Appends entries to the dashboard's recent-activity feed.</summary>
public class ActivityLog(GarageFlowDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Queues an activity row. Does not save — the caller's
    /// <c>SaveChangesAsync</c> commits it alongside the change it describes, so
    /// the feed can never drift from what actually happened.
    /// </summary>
    /// <param name="text">Sentence shown in the feed, e.g. "JOB-1042 marked Completed".</param>
    /// <param name="kind">One of <see cref="Vocabulary.ActivityKinds"/>.</param>
    public void Add(string text, string kind)
    {
        var now = clock.GetLocalNow().DateTime;
        db.Activities.Add(new Activity
        {
            // Ticks keep ids unique and ordered even for several entries in one request.
            Id = $"ACT-{now.Ticks}",
            At = now,
            Text = text,
            Kind = kind,
        });
    }
}
