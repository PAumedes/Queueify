using System.Globalization;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>The one-line summaries shown under each page title.</summary>
public static class QueueText
{
    public static string Albums(int count) => count == 1 ? Loc.T("queue.one_album") : Loc.F("queue.albums", count);

    public static string PendingSummary(IReadOnlyCollection<BacklogItem> pending, DateTime now)
    {
        if (pending.Count == 0) return Loc.T("queue.summary.empty");
        int thisWeek = pending.Count(i => now - i.AddedAt <= TimeSpan.FromDays(7));
        var text = Loc.F("queue.summary.waiting", Albums(pending.Count));
        return thisWeek > 0 ? Loc.F("queue.summary.this_week", text, thisWeek) : text;
    }

    public static string ListenedSummary(IReadOnlyCollection<BacklogItem> listened)
    {
        if (listened.Count == 0) return Loc.T("listened.summary.empty");
        var rated = listened.Where(i => i.Rating > 0).ToList();
        var text = Loc.F("listened.summary", Albums(listened.Count));
        if (rated.Count == 0) return text;
        var average = rated.Average(i => i.Rating).ToString("0.#", Loc.Culture);
        return Loc.F("listened.summary.rated", text, rated.Count, average);
    }
}
