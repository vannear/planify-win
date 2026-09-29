using System.Text;

namespace Planify.Core;

// Preserve the entire VCALENDAR, including unknown Apple properties, alarms and timezones.
// Only direct properties of the single VTODO are patched. Recurrence is intentionally read-only.
public sealed class CalendarDocument
{
    private readonly List<string> lines;
    private CalendarDocument(List<string> lines) { this.lines = lines; }
    public static CalendarDocument Parse(string text)
    {
        var unfolded = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (unfolded.Count == 0) throw new FormatException("无效的 iCalendar 折行。");
                unfolded[^1] += line[1..];
            }
            else if (line.Length > 0) unfolded.Add(line);
        }
        if (!unfolded.Contains("BEGIN:VCALENDAR") || !unfolded.Contains("END:VCALENDAR") || !unfolded.Contains("BEGIN:VTODO") || !unfolded.Contains("END:VTODO"))
            throw new FormatException("服务器返回的资源不是完整 VTODO。");
        return new(unfolded);
    }
    private static string Name(string line) => line.Split(':', 2)[0].Split(';', 2)[0].ToUpperInvariant();
    private IEnumerable<int> Properties()
    {
        bool inTodo = false; int nested = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] == "BEGIN:VTODO") { inTodo = true; continue; }
            if (!inTodo) continue;
            if (lines[i] == "END:VTODO") yield break;
            if (lines[i].StartsWith("BEGIN:")) { nested++; continue; }
            if (lines[i].StartsWith("END:")) { nested--; continue; }
            if (nested == 0) yield return i;
        }
    }
    public string? Value(string name)
    {
        var index = Properties().Where(i => Name(lines[i]) == name).Select(i => (int?)i).FirstOrDefault();
        return index == null ? null : Unescape(lines[index.Value].Split(':', 2)[1]);
    }
    public bool IsComplex => lines.Count(l => l == "BEGIN:VTODO") != 1 || Properties().Any(i => new[] { "RRULE", "RDATE", "EXDATE", "RECURRENCE-ID", "ORGANIZER", "ATTENDEE" }.Contains(Name(lines[i])));
    private void Set(string key, string? value)
    {
        foreach (int i in Properties().Where(i => Name(lines[i]) == key).Reverse().ToArray()) lines.RemoveAt(i);
        if (value != null) lines.Insert(lines.IndexOf("END:VTODO"), key + ":" + value);
    }
    public string Edit(string title, string description, bool completed)
        => Edit(title, description, completed, false, null, false, null);
    public string Edit(string title, string description, bool completed, bool updateDue, DateOnly? dueDate, bool updateReminder, DateTimeOffset? reminderAt)
    {
        if (IsComplex) throw new InvalidOperationException("重复任务、任务实例和协作任务暂时只读，请在 Apple 提醒事项中编辑。");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("请输入任务标题。");
        Set("SUMMARY", Escape(title.Trim())); Set("DESCRIPTION", Escape(description));
        if (updateDue) SetDue(dueDate);
        if (updateReminder) SetReminder(reminderAt);
        bool wasCompleted = Value("STATUS") == "COMPLETED";
        if (wasCompleted != completed)
        {
            Set("STATUS", completed ? "COMPLETED" : "NEEDS-ACTION");
            Set("PERCENT-COMPLETE", completed ? "100" : "0");
            Set("COMPLETED", completed ? Stamp() : null);
        }
        Set("LAST-MODIFIED", Stamp()); Set("DTSTAMP", Stamp());
        int.TryParse(Value("SEQUENCE"), out int sequence); Set("SEQUENCE", (sequence + 1).ToString());
        return Serialize();
    }
    public DateOnly? DueDate()
    {
        var index = Properties().FirstOrDefault(i => Name(lines[i]) == "DUE", -1);
        if (index < 0) return null;
        var value = lines[index].Split(':', 2)[1];
        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
            return DateOnly.FromDateTime(utc.ToLocalTime());
        return value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
    }
    private void SetDue(DateOnly? dueDate)
    {
        var indexes = Properties().Where(i => Name(lines[i]) == "DUE").ToArray();
        if (dueDate == null) { foreach (var i in indexes.Reverse()) lines.RemoveAt(i); return; }
        var formatted = dueDate.Value.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (indexes.Length == 0) { lines.Insert(lines.IndexOf("END:VTODO"), "DUE;VALUE=DATE:" + formatted); return; }
        int index = indexes[0]; string old = lines[index]; string value = old.Split(':', 2)[1];
        // Keep DATE-TIME, UTC and TZID semantics intact while changing only the calendar day.
        string replacement = formatted + value[8..];
        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var oldUtc))
        {
            var localTime = oldUtc.ToLocalTime().TimeOfDay;
            var local = DateTime.SpecifyKind(dueDate.Value.ToDateTime(TimeOnly.MinValue).Add(localTime), DateTimeKind.Local);
            replacement = local.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }
        lines[index] = old[..(old.Length - value.Length)] + replacement;
        foreach (var i in indexes.Skip(1).Reverse()) lines.RemoveAt(i);
    }
    public DateTimeOffset? ReminderAt()
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] != "BEGIN:VALARM") continue;
            int end = lines.IndexOf("END:VALARM", i + 1); if (end < 0) break;
            var alarm = lines.Skip(i + 1).Take(end - i - 1).ToArray();
            var action = alarm.FirstOrDefault(l => Name(l) == "ACTION");
            var trigger = alarm.FirstOrDefault(l => Name(l) == "TRIGGER");
            if (action == null || !action.EndsWith(":DISPLAY", StringComparison.OrdinalIgnoreCase) || trigger == null) { i = end; continue; }
            string raw = trigger.Split(':', 2)[1];
            if (raw.EndsWith('Z') && DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
                return new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();
            i = end; // Leave relative alarms untouched, but look for another editable display alarm.
        }
        return null;
    }
    public bool HasAlarm => lines.Any(l => l == "BEGIN:VALARM");
    private void SetReminder(DateTimeOffset? reminderAt)
    {
        var start = lines.IndexOf("BEGIN:VTODO"); var end = lines.IndexOf("END:VTODO");
        var indexes = Enumerable.Range(start + 1, end - start - 1).Where(i => lines[i] == "BEGIN:VALARM").ToArray();
        int? displayIndex = null;
        foreach (int i in indexes)
        {
            int alarmEnd = lines.IndexOf("END:VALARM", i + 1); if (alarmEnd < 0) continue;
            if (lines.Skip(i + 1).Take(alarmEnd - i - 1).Any(l => Name(l) == "ACTION" && l.EndsWith(":DISPLAY", StringComparison.OrdinalIgnoreCase))) { displayIndex = i; break; }
        }
        if (reminderAt == null)
        {
            var displayAlarms = new List<(int Start, int End)>();
            foreach (int alarmStart in indexes)
            {
                int alarmEnd = lines.IndexOf("END:VALARM", alarmStart + 1); if (alarmEnd < 0) continue;
                if (lines.Skip(alarmStart + 1).Take(alarmEnd - alarmStart - 1).Any(l => Name(l) == "ACTION" && l.EndsWith(":DISPLAY", StringComparison.OrdinalIgnoreCase)))
                    displayAlarms.Add((alarmStart, alarmEnd));
            }
            foreach (var alarm in displayAlarms.OrderByDescending(a => a.Start)) lines.RemoveRange(alarm.Start, alarm.End - alarm.Start + 1);
            return;
        }
        var stamp = reminderAt.Value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (displayIndex is { } replaceAt)
        {
            int alarmEnd = lines.IndexOf("END:VALARM", replaceAt + 1);
            int trigger = lines.FindIndex(replaceAt + 1, alarmEnd - replaceAt - 1, l => Name(l) == "TRIGGER");
            if (trigger >= 0) lines[trigger] = "TRIGGER;VALUE=DATE-TIME:" + stamp;
            else lines.Insert(alarmEnd, "TRIGGER;VALUE=DATE-TIME:" + stamp);
        }
        else
        {
            int at = lines.IndexOf("END:VTODO");
            lines.InsertRange(at, ["BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Planify reminder", "TRIGGER;VALUE=DATE-TIME:" + stamp, "END:VALARM"]);
        }
    }
    public static string Create(string title, string description)
    {
        var data = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Planify Windows Community//CalDAV Client//EN\r\nBEGIN:VTODO\r\nUID:{Guid.NewGuid()}\r\nDTSTAMP:{Stamp()}\r\nCREATED:{Stamp()}\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        return Parse(data).Edit(title, description, false);
    }
    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Unescape(string value)
    {
        var b = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '\\' && i + 1 < value.Length) { char c = value[++i]; b.Append(c is 'n' or 'N' ? '\n' : c); }
            else b.Append(value[i]);
        return b.ToString();
    }
    public string Serialize()
    {
        var b = new StringBuilder();
        foreach (var line in lines)
        {
            int bytes = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 75) { b.Append("\r\n "); bytes = 1; }
                b.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
            }
            b.Append("\r\n");
        }
        return b.ToString();
    }
}
