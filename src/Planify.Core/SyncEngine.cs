namespace Planify.Core;

public sealed record ListSyncFailure(TaskList List, string Message);
public sealed record SyncBatchResult(int Succeeded, int Total, int Conflicts, IReadOnlyList<ListSyncFailure> Failures);
public sealed record SyncBatchProgress(int Current, int Total, string Name);

public sealed class SyncEngine(LocalStore store, string accountKey)
{
    private readonly SemaphoreSlim syncGate = new(1, 1);
    public List<TaskEntry> Tasks { get; private set; } = store.Read<List<TaskEntry>>(accountKey + ":tasks") ?? [];
    public void Save() => store.Write(accountKey + ":tasks", Tasks);
    public void Add(TaskList list, string title, string description)
    {
        Tasks.Add(new() { Url = list.Url.TrimEnd('/') + "/" + Guid.NewGuid() + ".ics", ListUrl = list.Url, CalendarData = CalendarDocument.Create(title, description), Pending = "put" }); Save();
    }
    public void Edit(TaskEntry task, string title, string description, bool complete)
    {
        if (task.Conflict != null) throw new InvalidOperationException("请先解决同步冲突。");
        task.CalendarData = CalendarDocument.Parse(task.CalendarData).Edit(title, description, complete); task.Pending = "put"; Save();
    }
    public void Delete(TaskEntry task)
    {
        if (task.ReadOnly) throw new InvalidOperationException("复杂任务暂时只读。");
        if (task.Conflict != null) throw new InvalidOperationException("请先解决同步冲突。");
        if (task.BaseData == null && task.ETag == null && !task.UploadAttempted) Tasks.Remove(task); else task.Pending = "delete";
        Save();
    }
    public async Task Sync(CalDavClient client, TaskList list)
    {
        await syncGate.WaitAsync();
        try { await SyncList(client, list); }
        finally { syncGate.Release(); }
    }
    public async Task<SyncBatchResult> SyncAll(CalDavClient client, IEnumerable<TaskList> lists, Action<SyncBatchProgress>? progress = null)
    {
        var snapshot = lists.DistinctBy(l => l.Url).ToArray();
        var failures = new List<ListSyncFailure>();
        int succeeded = 0;
        await syncGate.WaitAsync();
        try
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                progress?.Invoke(new(i + 1, snapshot.Length, snapshot[i].Name));
                try { await SyncList(client, snapshot[i]); succeeded++; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Xml.XmlException)
                { failures.Add(new(snapshot[i], ex.Message)); }
            }
            var urls = snapshot.Select(l => l.Url).ToHashSet();
            return new(succeeded, snapshot.Length, Tasks.Count(t => urls.Contains(t.ListUrl) && t.Conflict != null), failures);
        }
        finally { syncGate.Release(); }
    }
    private async Task SyncList(CalDavClient client, TaskList list)
    {
        // Read the full collection before any writes. Incomplete responses never imply deletion.
        var remote = await client.Fetch(list.Url);
        foreach (var task in Tasks.Where(t => t.ListUrl == list.Url && t.Pending != "" && t.Conflict == null).ToArray())
        {
            var current = remote.FirstOrDefault(r => r.Url == task.Url);
            if (current == null && task.Pending == "delete" && task.ETag == null)
            { Tasks.Remove(task); Save(); continue; }
            if (current != null && task.Pending == "delete" && task.ETag == null && task.UploadAttempted && CalendarDocument.Parse(current.CalendarData).Serialize() == CalendarDocument.Parse(task.CalendarData).Serialize())
                task.ETag = current.ETag;
            if (current == null && task.ETag != null)
            {
                if (task.Pending == "delete") Tasks.Remove(task);
                else task.Conflict = "服务器已删除此任务；本地编辑已保留。";
                Save(); continue;
            }
            // A response lost after a successful PUT is safe to retry without overwriting a newer edit.
            if (current != null && CalendarDocument.Parse(current.CalendarData).Serialize() == CalendarDocument.Parse(task.CalendarData).Serialize() && task.Pending == "put")
            { Accept(task, current); Save(); continue; }
            if (current != null && current.ETag != task.ETag)
            { task.Conflict = "服务器任务已改变；本地修改未上传。"; Save(); continue; }
            task.UploadAttempted = true; Save();
            bool success = task.Pending == "delete" ? await client.Delete(task) : await client.Put(task);
            if (!success) task.Conflict = "上传期间服务器版本发生变化；本地修改未覆盖服务器。";
            else if (task.Pending == "delete") Tasks.Remove(task);
            // Keep PUT pending until a fresh REPORT confirms canonical server data.
            Save();
        }
        var refreshed = await client.Fetch(list.Url);
        foreach (var item in refreshed)
        {
            var task = Tasks.FirstOrDefault(t => t.Url == item.Url);
            if (task == null) { task = new() { Url = item.Url, ListUrl = list.Url }; Tasks.Add(task); Accept(task, item); }
            else if (task.Conflict == null && (task.Pending == "" || CalendarDocument.Parse(task.CalendarData).Serialize() == CalendarDocument.Parse(item.CalendarData).Serialize())) Accept(task, item);
            else if (task.Conflict == null && task.Pending == "put") task.Conflict = "服务器返回的内容与本地不同，请检查两份内容。";
        }
        var urls = refreshed.Select(r => r.Url).ToHashSet();
        Tasks.RemoveAll(t => t.ListUrl == list.Url && t.Pending == "" && t.Conflict == null && !urls.Contains(t.Url)); Save();
    }
    private static void Accept(TaskEntry task, RemoteTask remote)
    { task.ETag = remote.ETag; task.CalendarData = remote.CalendarData; task.BaseData = remote.CalendarData; task.Pending = ""; task.Conflict = null; task.UploadAttempted = false; }
    public async Task UseServer(CalDavClient client, TaskEntry task)
    {
        var data = await client.Fetch(task.ListUrl);
        var current = data.FirstOrDefault(r => r.Url == task.Url);
        // Persist the discarded local version for recovery before resolving.
        store.Write(accountKey + ":recovery:" + DateTime.UtcNow.Ticks, task);
        if (current == null) Tasks.Remove(task); else Accept(task, current); Save();
    }
}
