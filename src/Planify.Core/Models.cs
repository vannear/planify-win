namespace Planify.Core;

public sealed record TaskList(string Url, string Name);
public sealed record RemoteTask(string Url, string ETag, string CalendarData);
public sealed class TaskEntry
{
    public string Url { get; set; } = "";
    public string ListUrl { get; set; } = "";
    public string? ETag { get; set; }
    public string CalendarData { get; set; } = "";
    public string? BaseData { get; set; }
    public string Pending { get; set; } = "";
    public string? Conflict { get; set; }
    public bool UploadAttempted { get; set; }
    public string Title => CalendarDocument.Parse(CalendarData).Value("SUMMARY") ?? "无标题";
    public bool Completed => CalendarDocument.Parse(CalendarData).Value("STATUS") == "COMPLETED";
    public string Details => CalendarDocument.Parse(CalendarData).Value("DESCRIPTION") ?? "";
    public bool ReadOnly => CalendarDocument.Parse(CalendarData).IsComplex;
    public string State => Conflict != null ? "冲突 · 本地修改已保留" : Pending == "delete" ? "待删除" : Pending != "" ? "等待上传" : ReadOnly ? "复杂/重复任务 · 只读" : Completed ? "已完成" : "已同步";
}
