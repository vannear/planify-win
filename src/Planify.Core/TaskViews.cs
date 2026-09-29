using System.Globalization;

namespace Planify.Core;

public static class TaskViews
{
    public static DateOnly? DueDay(TaskEntry task)
    {
        var document = CalendarDocument.Parse(task.CalendarData);
        string? due = document.Value("DUE");
        if (due == null) return null;
        if (due.EndsWith('Z') && DateTime.TryParseExact(due, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return DateOnly.FromDateTime(utc.ToLocalTime());
        // Date-only and floating/TZID values retain the date specified by the calendar.
        return due.Length >= 8 && DateOnly.TryParseExact(due[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }
    public static bool Matches(TaskEntry task, string view, DateOnly today)
    {
        return view switch
        {
            "today" => !task.Completed && task.Pending != "delete" && DueDay(task) is { } day && day <= today,
            "scheduled" => !task.Completed && task.Pending != "delete" && DueDay(task) != null,
            "completed" => task.Completed && task.Pending != "delete",
            "pending" => task.Pending != "",
            "conflicts" => task.Conflict != null,
            _ => task.Pending != "delete"
        };
    }
}
