using System.Globalization;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>The one-line summaries shown under each page title.</summary>
public static class QueueText
{
    public static string Albums(int count) => count == 1 ? "1 album" : $"{count} albums";

    public static string PendingSummary(IReadOnlyCollection<BacklogItem> pending, DateTime now)
    {
        if (pending.Count == 0) return "Nothing waiting. Find an album on the left to start.";
        int thisWeek = pending.Count(i => now - i.AddedAt <= TimeSpan.FromDays(7));
        var text = $"{Albums(pending.Count)} waiting";
        return thisWeek > 0 ? $"{text}  ·  {thisWeek} added this week" : text;
    }

    public static string ListenedSummary(IReadOnlyCollection<BacklogItem> listened)
    {
        if (listened.Count == 0) return "Nothing listened yet. Tick the circle next to an album when you've heard it.";
        var rated = listened.Where(i => i.Rating > 0).ToList();
        var text = $"{Albums(listened.Count)} listened";
        if (rated.Count == 0) return text;
        var average = rated.Average(i => i.Rating).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{text}  ·  {rated.Count} rated, average {average} ★";
    }
}
