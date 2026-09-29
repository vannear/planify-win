using System.Net;
using System.Text;
using System.Xml.Linq;
using Planify.Core;

int checks = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
string sample = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTIMEZONE\r\nTZID:Australia/Sydney\r\nEND:VTIMEZONE\r\nBEGIN:VTODO\r\nUID:apple-task\r\nSUMMARY:Apple task\r\nDESCRIPTION:line1\\nline2\r\nDUE;TZID=Australia/Sydney:20261001T090000\r\nRELATED-TO:parent-id\r\nX-APPLE-SORT-ORDER:123\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Keep alarm\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
string edited = CalendarDocument.Parse(sample).Edit("新标题，含中文", "a\nb;c,d\\e", true);
Assert(edited.Contains("DESCRIPTION:Keep alarm") && edited.Contains("TRIGGER:-PT15M"), "alarm preserved");
Assert(edited.Contains("X-APPLE-SORT-ORDER:123") && edited.Contains("RELATED-TO:parent-id") && edited.Contains("DUE;TZID=Australia/Sydney:20261001T090000"), "Apple extensions, parent and due timezone preserved");
Assert(CalendarDocument.Parse(edited).Value("DESCRIPTION") == "a\nb;c,d\\e", "text escaping round-trip");
Assert(CalendarDocument.Parse(edited).Value("STATUS") == "COMPLETED", "completion state");
string reopened = CalendarDocument.Parse(edited).Edit("title", "", false);
Assert(!reopened.Contains("COMPLETED:") && CalendarDocument.Parse(reopened).Value("STATUS") == "NEEDS-ACTION", "reopen clears completion timestamp");
string longData = CalendarDocument.Create(string.Concat(Enumerable.Repeat("中文🙂", 80)), "");
Assert(longData.Split("\r\n").All(l => Encoding.UTF8.GetByteCount(l) <= 75), "UTF-8 folding stays within 75 octets");
Assert(CalendarDocument.Parse(longData).Value("SUMMARY") == string.Concat(Enumerable.Repeat("中文🙂", 80)), "Unicode folding round-trip");
try { CalendarDocument.Parse(sample.Replace("UID:apple-task", "UID:apple-task\r\nRRULE:FREQ=DAILY")).Edit("x", "", true); throw new Exception("recurrence allowed"); } catch (InvalidOperationException) { Assert(true, "recurrence guarded"); }
var fake = new FakeDav(sample);
using var client = new CalDavClient("https://example.test/", "user", "test-secret", fake);
var lists = await client.Discover();
Assert(lists.Count == 1 && lists[0].Name == "Tasks", "principal and VTODO collection discovery");
string dbPath = Path.Combine(Path.GetTempPath(), "planify-test-" + Guid.NewGuid(), "tasks.db");
using var store = new LocalStore(dbPath);
var engine = new SyncEngine(store, "test");
await engine.Sync(client, lists[0]); Assert(engine.Tasks.Count == 1, "initial pull");
engine.Edit(engine.Tasks[0], "Windows change", "notes", true);
await engine.Sync(client, lists[0]); Assert(engine.Tasks[0].Pending == "" && fake.Data.Contains("Windows change"), "conditional update round-trip");
Assert(fake.LastIfMatch != null, "update requires If-Match");
engine.Edit(engine.Tasks[0], "Local offline", "", false); fake.Tag = "\"remote-change\""; fake.Data = CalendarDocument.Parse(fake.Data).Edit("Apple change", "", true);
int before = fake.Writes; await engine.Sync(client, lists[0]);
Assert(fake.Writes == before && engine.Tasks[0].Conflict != null && engine.Tasks[0].Title == "Local offline", "concurrent Apple edit preserved and local conflict retained");
await engine.UseServer(client, engine.Tasks[0]); Assert(engine.Tasks[0].Title == "Apple change", "explicit server resolution");
engine.Add(lists[0], "New offline", "");
var persisted = new SyncEngine(store, "test"); Assert(persisted.Tasks.Any(t => t.Title == "New offline" && t.Pending == "put"), "outbox survives restart");
await engine.Sync(client, lists[0]); Assert(fake.SawCreateGuard && engine.Tasks.All(t => t.Pending == ""), "creation uses If-None-Match");
var first = engine.Tasks.First(); engine.Delete(first); await engine.Sync(client, lists[0]); Assert(!engine.Tasks.Contains(first), "conditional deletion");
fake.Partial = true; int taskCount = engine.Tasks.Count;
try { await engine.Sync(client, lists[0]); throw new Exception("partial report accepted"); } catch (InvalidOperationException) { Assert(engine.Tasks.Count == taskCount, "partial REPORT never clears cache"); }
fake.Partial = false;
engine.Add(lists[0], "Network retry", "");
fake.LosePutResponse = true;
try { await engine.Sync(client, lists[0]); } catch (HttpRequestException) { }
Assert(engine.Tasks.Last().Pending == "put", "lost PUT response retains durable outbox");
before = fake.Writes;
await engine.Sync(client, lists[0]);
Assert(fake.Writes == before && engine.Tasks.Last().Pending == "", "successful PUT with lost response is reconciled without duplicate");
engine.Add(lists[0], "Delete after uncertain upload", "");
fake.LosePutResponse = true;
try { await engine.Sync(client, lists[0]); } catch (HttpRequestException) { }
var uncertain = engine.Tasks.Last(); engine.Delete(uncertain);
Assert(engine.Tasks.Contains(uncertain), "uncertain creation is not discarded before server reconciliation");
await engine.Sync(client, lists[0]);
Assert(!engine.Tasks.Contains(uncertain), "uncertain creation can be conditionally deleted");
var racing = engine.Tasks.First(); engine.Edit(racing, "Race test", "", false); fake.RejectWrite = true;
await engine.Sync(client, lists[0]);
Assert(racing.Conflict != null && racing.Pending == "put", "HTTP 412 retains local edits");
fake.RejectWrite = false;
fake.CrossOrigin = true;
try { await client.Discover(); throw new Exception("cross-origin accepted"); } catch (InvalidOperationException) { Assert(true, "cross-origin credential forwarding blocked"); }
var dateTask = new TaskEntry { CalendarData = sample };
Assert(TaskViews.Matches(dateTask, "today", new DateOnly(2026, 10, 2)), "today includes overdue tasks");
Assert(TaskViews.Matches(dateTask, "scheduled", new DateOnly(2026, 10, 2)), "scheduled includes dated tasks");
dateTask.Pending = "delete";
Assert(!TaskViews.Matches(dateTask, "all", DateOnly.MinValue) && TaskViews.Matches(dateTask, "pending", DateOnly.MinValue), "pending deletions only appear in pending view");
dateTask.Pending = ""; dateTask.CalendarData = edited;
Assert(!TaskViews.Matches(dateTask, "today", DateOnly.MaxValue) && TaskViews.Matches(dateTask, "completed", DateOnly.MinValue), "completed tasks separated from today");

var multi = new MultiListDav();
using var multiClient = new CalDavClient("https://example.test/", "user", "secret", multi);
var multiLists = new[] { new TaskList("https://example.test/cal/one/", "One"), new TaskList("https://example.test/cal/two/", "Two"), new TaskList("https://example.test/cal/three/", "Three") };
foreach (var list in multiLists) multi.Seed(list.Url, list.Name);
var batchEngine = new SyncEngine(store, "multi");
var progressEvents = new List<SyncBatchProgress>();
var batch = await batchEngine.SyncAll(multiClient, multiLists.Append(multiLists[0]), progressEvents.Add);
Assert(batch.Succeeded == 3 && batch.Total == 3 && batchEngine.Tasks.Count == 3, "all-list sync deduplicates collections and pulls each list");
Assert(progressEvents.Select(p => p.Current).SequenceEqual(new[] { 1, 2, 3 }) && progressEvents.All(p => p.Total == 3), "all-list progress reports current and total");
foreach (var entry in batchEngine.Tasks.ToArray()) batchEngine.Edit(entry, entry.Title + " edited", "", false);
multi.Failures.Add(multiLists[1].Url);
batch = await batchEngine.SyncAll(multiClient, multiLists);
Assert(batch.Succeeded == 2 && batch.Failures.Single().List.Name == "Two", "one failed list does not stop other lists");
Assert(batchEngine.Tasks.Single(t => t.ListUrl == multiLists[1].Url).Pending == "put" && batchEngine.Tasks.Single(t => t.ListUrl == multiLists[2].Url).Pending == "", "failed-list outbox preserved while later list uploads");
multi.Failures.Clear(); await batchEngine.SyncAll(multiClient, multiLists);
Assert(batchEngine.Tasks.All(t => t.Pending == ""), "retry syncs failed list without duplicating successful writes");
var batchTask = batchEngine.Tasks.First(); batchEngine.Edit(batchTask, "local conflict", "", false);
multi.ChangeRemote(batchTask.Url);
batch = await batchEngine.SyncAll(multiClient, multiLists);
Assert(batch.Conflicts == 1 && batchTask.Conflict != null, "batch summarizes conflicts without overwriting server");
batch = await batchEngine.SyncAll(multiClient, []);
Assert(batch.Total == 0 && batchEngine.Tasks.Count == 3, "empty discovered collection never deletes cached tasks");
multi.PeakRequests = 0;
await Task.WhenAll(batchEngine.SyncAll(multiClient, multiLists), batchEngine.SyncAll(multiClient, multiLists));
Assert(multi.PeakRequests == 1, "concurrent batch requests are serialized per engine");
Assert(new SyncEngine(store, "multi").Tasks.Single(t => t.Url == batchTask.Url).Conflict != null, "batch conflict survives restart");
var credentialStore = new FakeCredentials();
var loginService = new SavedLogin(credentialStore);
var loginAccount = new LoginAccount("https://example.test/", "user");
Assert(loginAccount.CredentialKey == new LoginAccount(" https://EXAMPLE.test ", " user ").CredentialKey, "credential identity normalizes hostname and trailing slash");
Assert(loginAccount.CredentialKey != new LoginAccount("https://other.test/", "user").CredentialKey && loginAccount.CredentialKey != new LoginAccount("https://example.test/", "other").CredentialKey, "credentials isolated by server and account");
Assert(loginAccount.CredentialKey != new LoginAccount("https://example.test/subdir/", "user").CredentialKey, "Nextcloud subdirectory is part of credential identity");
try { await loginService.Connect<string>(loginAccount, "bad-secret", true, _ => throw new HttpRequestException("authentication failed")); } catch (HttpRequestException) { }
Assert(credentialStore.Saves == 0 && credentialStore.Removes == 0, "authentication failure never saves or deletes a password");
var loginResult = await loginService.Connect(loginAccount, "test-secret", true, _ => Task.FromResult("connected"));
Assert(loginResult.PreferenceApplied && credentialStore.Contains(loginAccount), "remember opt-in saves after successful authentication");
var nextLaunch = new SavedLogin(credentialStore);
bool restoredSecret = false;
var restoredLogin = await nextLaunch.Restore(loginAccount, secret => { restoredSecret = secret == "test-secret"; return Task.FromResult("connected"); });
Assert(restoredLogin == "connected" && restoredSecret, "next startup restores saved credential");
await loginService.Connect(loginAccount, "new-secret", true, _ => Task.FromResult("connected"));
Assert(credentialStore.Read(loginAccount) == "new-secret", "explicit replacement updates saved password");
bool fallbackUsed = false;
await loginService.Connect(loginAccount, "", true, secret => { fallbackUsed = secret == "new-secret"; return Task.FromResult("connected"); });
Assert(fallbackUsed, "empty password field can reuse saved credential");
try { await loginService.Connect<string>(loginAccount, "bad-replacement", true, _ => throw new HttpRequestException()); } catch (HttpRequestException) { }
Assert(credentialStore.Read(loginAccount) == "new-secret", "failed replacement preserves existing saved password");
await loginService.Connect(loginAccount, "session-only", false, _ => Task.FromResult("connected"));
Assert(!credentialStore.Contains(loginAccount), "remember opt-out removes saved password after successful login");
int authCalls = 0;
Assert(await loginService.Restore(loginAccount, _ => { authCalls++; return Task.FromResult("connected"); }) == null && authCalls == 0, "forgotten credential causes no automatic network login");
credentialStore.FailWrites = true;
loginResult = await loginService.Connect(loginAccount, "test-secret", true, _ => Task.FromResult("connected"));
Assert(!loginResult.PreferenceApplied && loginResult.Value == "connected", "vault failure preserves successful session and reports unsaved state");
credentialStore.FailWrites = false;
credentialStore.Save(loginAccount, "test-secret");
try { await loginService.Restore<string>(loginAccount, _ => throw new HttpRequestException("offline")); } catch (HttpRequestException) { }
Assert(credentialStore.Contains(loginAccount), "temporary offline startup does not erase saved credential");
credentialStore.Remove(loginAccount); credentialStore.Remove(loginAccount);
Assert(!credentialStore.Contains(loginAccount), "forget is idempotent");
Console.WriteLine($"All {checks} checks passed.");

sealed class FakeDav : HttpMessageHandler
{
    public string Data, Tag = "\"1\"";
    public int Writes; public string? LastIfMatch; public bool SawCreateGuard, Partial, CrossOrigin, LosePutResponse, RejectWrite;
    private readonly Dictionary<string, (string Data, string Tag)> extra = new();
    private bool originalExists = true;
    public FakeDav(string data) { Data = data; }
    private static HttpResponseMessage Reply(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string method = request.Method.Method, path = request.RequestUri!.AbsolutePath;
        if (method == "PROPFIND")
        {
            string props = path == "/remote.php/dav/" ? $"<d:current-user-principal><d:href>{(CrossOrigin ? "https://evil.test/" : "/principal/")}</d:href></d:current-user-principal>" : path == "/principal/" ? "<c:calendar-home-set><d:href>/cal/</d:href></c:calendar-home-set>" : "<d:displayname>Tasks</d:displayname><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name='VTODO'/></c:supported-calendar-component-set>";
            return Reply(HttpStatusCode.MultiStatus, $"<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:response><d:href>/cal/tasks/</d:href><d:propstat><d:prop>{props}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>");
        }
        if (method == "REPORT")
        {
            XNamespace d = "DAV:", c = "urn:ietf:params:xml:ns:caldav";
            XElement Entry(string url, string data, string tag) => new(d + "response", new XElement(d + "href", url), new XElement(d + "propstat", new XElement(d + "prop", new XElement(d + "getetag", tag), new XElement(c + "calendar-data", data)), new XElement(d + "status", "HTTP/1.1 200 OK")));
            var root = new XElement(d + "multistatus");
            if (originalExists) root.Add(Entry("/cal/tasks/original.ics", Data, Tag));
            foreach (var pair in extra) root.Add(Entry(pair.Key, pair.Value.Data, pair.Value.Tag));
            if (Partial) root.Add(new XElement(d + "response", new XElement(d + "href", "/cal/tasks/missing.ics")));
            return Reply(HttpStatusCode.MultiStatus, root.ToString());
        }
        if (method is "PUT" or "DELETE")
        {
            if (RejectWrite) return Reply(HttpStatusCode.PreconditionFailed);
            LastIfMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.First() : null;
            bool create = request.Headers.Contains("If-None-Match"); SawCreateGuard |= create;
            bool original = path.EndsWith("original.ics"); bool exists = original ? originalExists : extra.ContainsKey(path);
            string? existingTag = original ? Tag : exists ? extra[path].Tag : null;
            if ((create && exists) || (!create && existingTag != LastIfMatch)) return Reply(HttpStatusCode.PreconditionFailed);
            Writes++;
            if (method == "DELETE") { if (original) originalExists = false; else extra.Remove(path); return Reply(HttpStatusCode.NoContent); }
            string data = await request.Content!.ReadAsStringAsync(cancellationToken), tag = "\"" + Guid.NewGuid() + "\"";
            if (original) { Data = data; Tag = tag; } else extra[path] = (data, tag);
            if (LosePutResponse) { LosePutResponse = false; throw new HttpRequestException("Simulated response loss"); }
            return Reply(HttpStatusCode.Created);
        }
        return Reply(HttpStatusCode.BadRequest);
    }
}

sealed class MultiListDav : HttpMessageHandler
{
    private readonly Dictionary<string, RemoteTask> tasks = new();
    public HashSet<string> Failures = [];
    public int PeakRequests;
    private int activeRequests;
    public void Seed(string list, string name)
    {
        string url = list + "seed.ics";
        tasks[url] = new(url, "\"1\"", CalendarDocument.Create(name, ""));
    }
    public void ChangeRemote(string url) => tasks[url] = new(url, "\"changed\"", CalendarDocument.Parse(tasks[url].CalendarData).Edit("Remote change", "", false));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int current = Interlocked.Increment(ref activeRequests); PeakRequests = Math.Max(PeakRequests, current);
        try
        {
            await Task.Delay(2, cancellationToken);
            string url = request.RequestUri!.AbsoluteUri;
            if (Failures.Any(url.StartsWith)) return new(HttpStatusCode.ServiceUnavailable);
            if (request.Method.Method == "REPORT")
            {
                XNamespace d = "DAV:", c = "urn:ietf:params:xml:ns:caldav";
                var xml = new XElement(d + "multistatus", tasks.Values.Where(t => t.Url.StartsWith(url, StringComparison.Ordinal)).Select(t => new XElement(d + "response", new XElement(d + "href", t.Url), new XElement(d + "propstat", new XElement(d + "prop", new XElement(d + "getetag", t.ETag), new XElement(c + "calendar-data", t.CalendarData)), new XElement(d + "status", "HTTP/1.1 200 OK")))));
                return new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString()) };
            }
            if (request.Method.Method == "PUT")
            {
                if (tasks.TryGetValue(url, out var existing) && (!request.Headers.TryGetValues("If-Match", out var tags) || tags.Single() != existing.ETag)) return new(HttpStatusCode.PreconditionFailed);
                tasks[url] = new(url, "\"" + Guid.NewGuid() + "\"", await request.Content!.ReadAsStringAsync(cancellationToken));
                return new(HttpStatusCode.Created);
            }
            return new(HttpStatusCode.BadRequest);
        }
        finally { Interlocked.Decrement(ref activeRequests); }
    }
}

sealed class FakeCredentials : ICredentialStore
{
    private readonly Dictionary<string, string> values = new();
    public int Saves, Removes;
    public bool FailWrites;
    public bool Contains(LoginAccount account) => values.ContainsKey(account.CredentialKey);
    public string? Read(LoginAccount account) => values.GetValueOrDefault(account.CredentialKey);
    public void Save(LoginAccount account, string password) { if (FailWrites) throw new InvalidOperationException(); Saves++; values[account.CredentialKey] = password; }
    public void Remove(LoginAccount account) { if (FailWrites) throw new InvalidOperationException(); Removes++; values.Remove(account.CredentialKey); }
}
