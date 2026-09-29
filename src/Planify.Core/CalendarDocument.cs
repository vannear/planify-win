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
    {
        if (IsComplex) throw new InvalidOperationException("重复任务、任务实例和协作任务暂时只读，请在 Apple 提醒事项中编辑。");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("请输入任务标题。");
        Set("SUMMARY", Escape(title.Trim())); Set("DESCRIPTION", Escape(description));
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
