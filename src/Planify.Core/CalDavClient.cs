using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Planify.Core;

public sealed class CalDavClient : IDisposable
{
    private static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:caldav";
    private readonly HttpClient client;
    private readonly Uri origin;
    public CalDavClient(string server, string user, string password, HttpMessageHandler? handler = null)
    {
        origin = new Uri(server.TrimEnd('/') + "/");
        if (origin.Scheme != "https" || !string.IsNullOrEmpty(origin.UserInfo) || origin.Query != "" || origin.Fragment != "")
            throw new ArgumentException("请输入 HTTPS 服务器地址，不要包含账号、查询参数或片段。");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PlanifyWindowsCommunity/0.1");
    }
    private Uri SafeUri(string href, Uri? relativeTo = null)
    {
        var uri = new Uri(relativeTo ?? origin, href);
        if (uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo != "")
            throw new InvalidOperationException("服务器返回跨站地址，已停止，未向其他站点发送凭据。");
        return uri;
    }
    private async Task<HttpResponseMessage> Send(string method, string url, string? body = null, string? depth = null, string? etag = null, bool create = false)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), SafeUri(url));
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, method == "PUT" ? "text/calendar" : "application/xml");
        if (depth != null) request.Headers.Add("Depth", depth);
        if (etag != null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (create) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var response = await client.SendAsync(request);
        if ((int)response.StatusCode is >= 300 and < 400) { response.Dispose(); throw new InvalidOperationException("服务器返回重定向，请填写最终的 Nextcloud HTTPS 地址。"); }
        return response;
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("认证失败，请检查账号和 Nextcloud 应用专用密码。");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException(response.Headers.Server.Any(s => s.Product?.Name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) == true) ? "请求被服务器或 Cloudflare 拒绝（403），请检查 Cloudflare 安全事件及 CalDAV 访问规则。" : "服务器拒绝访问，请检查任务列表权限。");
        response.EnsureSuccessStatusCode();
    }
    private static IEnumerable<XElement> Props(XElement response) => response.Elements(D + "propstat").Where(p => ((string?)p.Element(D + "status"))?.Split(' ').Contains("200") == true).Elements(D + "prop");
    private async Task<XDocument> Propfind(string url, string properties, string depth)
    {
        using var response = await Send("PROPFIND", url, $"<d:propfind xmlns:d='DAV:' xmlns:c='{C}'><d:prop>{properties}</d:prop></d:propfind>", depth);
        Check(response); return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public async Task<List<TaskList>> Discover()
    {
        var endpoint = origin.AbsolutePath.Contains("/remote.php/dav", StringComparison.OrdinalIgnoreCase) ? origin : new Uri(origin, "remote.php/dav/");
        var principalDoc = await Propfind(endpoint.AbsoluteUri, "<d:current-user-principal/>", "0");
        string principal = principalDoc.Descendants(D + "response").SelectMany(Props).Elements(D + "current-user-principal").Elements(D + "href").Select(x => x.Value).FirstOrDefault() ?? throw new InvalidOperationException("未找到 CalDAV 用户入口。");
        var principalUri = SafeUri(principal, endpoint);
        var homeDoc = await Propfind(principalUri.AbsoluteUri, "<c:calendar-home-set/>", "0");
        string home = homeDoc.Descendants(D + "response").SelectMany(Props).Elements(C + "calendar-home-set").Elements(D + "href").Select(x => x.Value).FirstOrDefault() ?? throw new InvalidOperationException("未找到任务列表目录。");
        var homeUri = SafeUri(home, principalUri);
        var doc = await Propfind(homeUri.AbsoluteUri, "<d:displayname/><d:resourcetype/><c:supported-calendar-component-set/>", "1");
        var lists = new List<TaskList>();
        foreach (var item in doc.Descendants(D + "response"))
        {
            var properties = Props(item).ToArray();
            if (!properties.Elements(D + "resourcetype").Elements(C + "calendar").Any()) continue;
            var components = properties.Elements(C + "supported-calendar-component-set").ToArray();
            if (components.Length > 0 && !components.Elements(C + "comp").Any(x => (string?)x.Attribute("name") == "VTODO")) continue;
            string? href = (string?)item.Element(D + "href");
            if (href == null || href.Contains("deck--board")) continue;
            lists.Add(new(SafeUri(href, homeUri).AbsoluteUri, properties.Elements(D + "displayname").FirstOrDefault()?.Value ?? "任务列表"));
        }
        return lists;
    }
    public async Task<List<RemoteTask>> Fetch(string listUrl)
    {
        const string query = "<c:calendar-query xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name='VCALENDAR'><c:comp-filter name='VTODO'/></c:comp-filter></c:filter></c:calendar-query>";
        using var response = await Send("REPORT", listUrl, query, "1"); Check(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.Root?.Name != D + "multistatus") throw new InvalidOperationException("服务器未返回完整任务集合，已停止同步。");
        var result = new List<RemoteTask>();
        foreach (var item in doc.Root.Elements(D + "response"))
        {
            var props = Props(item).ToArray();
            string? href = (string?)item.Element(D + "href"), data = props.Elements(C + "calendar-data").FirstOrDefault()?.Value, tag = props.Elements(D + "getetag").FirstOrDefault()?.Value;
            if (href == null || data == null || string.IsNullOrEmpty(tag)) throw new InvalidOperationException("任务集合存在缺失数据，已停止同步以保留本地缓存。");
            CalendarDocument.Parse(data);
            var url = SafeUri(href, new Uri(listUrl));
            if (!url.AbsoluteUri.StartsWith(listUrl.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new InvalidOperationException("任务地址不属于所选列表。");
            result.Add(new(url.AbsoluteUri, tag, data));
        }
        return result;
    }
    public async Task<bool> Put(TaskEntry task)
    {
        if (task.ETag == null && task.BaseData != null) throw new InvalidOperationException("缺少任务版本，拒绝覆盖。");
        using var response = await Send("PUT", task.Url, task.CalendarData, etag: task.ETag, create: task.ETag == null);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        Check(response); return true;
    }
    public async Task<bool> Delete(TaskEntry task)
    {
        if (string.IsNullOrEmpty(task.ETag)) throw new InvalidOperationException("缺少任务版本，拒绝删除。");
        using var response = await Send("DELETE", task.Url, etag: task.ETag);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        if (response.StatusCode != HttpStatusCode.NotFound) Check(response);
        return true;
    }
    public void Dispose() => client.Dispose();
}
