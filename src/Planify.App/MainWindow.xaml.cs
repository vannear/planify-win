using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Planify.Core;
using System.Security.Cryptography;
using System.Text;

namespace Planify.App;

public sealed class SidebarList(TaskList model, int count, string color)
{
    public TaskList Model => model;
    public string Name => model.Name;
    public int Count => count;
    public SolidColorBrush Color => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(color[..2], 16), Convert.ToByte(color[2..4], 16), Convert.ToByte(color[4..], 16)));
}
public sealed class TaskRow(TaskEntry entry, string listName)
{
    public TaskEntry Entry => entry;
    public string Title => entry.Title;
    public string CheckGlyph => entry.Completed ? "✓" : "";
    public bool CanEdit => !entry.ReadOnly && entry.Conflict == null && entry.Pending != "delete";
    public double Opacity => entry.Completed ? 0.5 : 1;
    public string Metadata => listName + (TaskViews.DueDay(entry) is { } day ? "  ·  " + day.ToString("yyyy/MM/dd") : "") + (string.IsNullOrWhiteSpace(entry.Details) ? "" : "  ·  " + entry.Details.Replace('\n', ' '));
    public string Badge => entry.Conflict != null ? "冲突" : entry.Pending == "delete" ? "待删除" : entry.Pending != "" ? "待同步" : entry.ReadOnly ? "只读" : "";
}

public sealed partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly ICredentialStore credentials = new WindowsCredentialStore();
    private LoginAccount? connectedAccount;
    private SyncEngine? engine;
    private CalDavClient? client;
    private string accountKey = "";
    private bool busy, initialized, refreshing, editorOpen, accountDialogOpen, closed, restoreAttempted;
    private TaskEntry? selected;
    private string view = "all", baselineTitle = "", baselineNotes = "";
    private bool baselineDone, baselineHasDue, baselineHasReminder;
    private DateOnly? baselineDue;
    private DateTimeOffset? baselineReminder;
    private string? currentListUrl;
    private List<TaskList> taskLists = [];
    private sealed record Account(string Server, string User);
    private DateOnly? CurrentDue => HasDueDate.IsChecked == true ? DateOnly.FromDateTime(DueDate.Date.DateTime) : null;
    private DateTimeOffset? CurrentReminder => HasReminder.IsChecked == true ? new DateTimeOffset(ReminderDate.Date.DateTime.Date + ReminderTime.Time) : null;
    private bool HasDraft => editorOpen && (TaskTitle.Text != baselineTitle || TaskNotes.Text != baselineNotes || (Done.IsChecked == true) != baselineDone || (HasDueDate.IsChecked == true) != baselineHasDue || CurrentDue != baselineDue || (HasReminder.IsChecked == true) != baselineHasReminder || CurrentReminder != baselineReminder);
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "planify.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        string dataDirectory = Environment.GetEnvironmentVariable("PLANIFY_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlanifyWindowsCommunity");
        store = new LocalStore(Path.Combine(dataDirectory, "tasks.db"));
        initialized = true;
        var account = store.Read<Account>("last-account");
        if (account != null)
        {
            Server.Text = account.Server; Username.Text = account.User; LoadAccount();
            Status.Text = "已加载本地任务。打开账户设置并连接后，可一次同步所有列表。";
        }
        Refresh();
        Root.SizeChanged += (_, _) => UpdateEditorWidth();
        Root.Loaded += RestoreConnection;
        Closed += (_, _) => { closed = true; client?.Dispose(); store.Dispose(); };
    }
    private LoginAccount DialogAccount() => new(Server.Text.Trim(), Username.Text.Trim());
    private void CredentialAccount_Changed(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        Password.Password = "";
        RefreshCredentialControls();
    }
    private void RefreshCredentialControls()
    {
        bool saved = false;
        string message = "勾选后，连接成功时由 Windows 凭据管理器保存。";
        if (!string.IsNullOrWhiteSpace(Server.Text) && !string.IsNullOrWhiteSpace(Username.Text))
        {
            try { saved = credentials.Contains(DialogAccount()); }
            catch (ArgumentException) { message = "请填写有效的 HTTPS 地址和用户名。"; }
            catch (UriFormatException) { message = "请填写有效的 HTTPS 地址。"; }
            catch (Exception) { message = "暂时无法访问 Windows 凭据管理器，可输入密码仅连接本次会话。"; }
        }
        RememberPassword.IsChecked = saved;
        Password.PlaceholderText = saved ? "已保存；留空使用原密码，输入可更新" : "输入应用专用密码";
        CredentialStatus.Text = saved ? "此账户的密码已由 Windows 保存。" : message;
        ForgetPasswordButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task<(CalDavClient Client, List<TaskList> Lists)> Authenticate(LoginAccount account, string password)
    {
        var candidate = new CalDavClient(account.Server, account.User, password);
        try
        {
            var discovered = await candidate.Discover();
            if (closed) throw new OperationCanceledException();
            return (candidate, discovered);
        }
        catch { candidate.Dispose(); throw; }
    }
    private void AcceptConnection(LoginAccount account, CalDavClient candidate, List<TaskList> discovered)
    {
        client?.Dispose(); client = candidate; connectedAccount = account; LoadAccount(); taskLists = discovered;
        store.Write("last-account", new Account(account.Server, account.User)); store.Write(accountKey + ":lists", taskLists);
    }
    private async void RestoreConnection(object sender, RoutedEventArgs e)
    {
        if (restoreAttempted || closed) return;
        restoreAttempted = true;
        if (engine == null) return;
        SetBusy(true);
        try
        {
            var account = DialogAccount();
            Status.Text = "正在使用已保存的密码连接…";
            // Only discovery is automatic. Pending task changes require an explicit sync.
            var restored = await new SavedLogin(credentials).Restore(account, async password =>
            {
                var result = await Authenticate(account, password);
                return new RestoredConnection(result.Client, result.Lists);
            });
            if (closed) { restored?.Client.Dispose(); return; }
            if (restored == null) { Status.Text = "已加载本地任务。打开账户设置连接 Nextcloud。"; return; }
            AcceptConnection(account, restored.Client, restored.Lists);
            Status.Text = $"已自动连接 · {taskLists.Count} 个列表。点击“同步所有列表”更新任务。";
        }
        catch (Exception) { if (!closed) Status.Text = "自动连接未成功，本地任务仍可使用。请检查网络，或在账户设置中重新输入应用专用密码。"; }
        finally { if (!closed) { SetBusy(false); Refresh(); RefreshCredentialControls(); } }
    }
    private sealed record RestoredConnection(CalDavClient Client, List<TaskList> Lists);
    private void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var account = DialogAccount();
            credentials.Remove(account);
            if (connectedAccount?.CredentialKey == account.CredentialKey)
            { client?.Dispose(); client = null; connectedAccount = null; }
            Password.Password = ""; RefreshCredentialControls(); Refresh();
            AccountError.Text = "";
            CredentialStatus.Text = "已忘记密码。应用不会撤销 Nextcloud 上的应用专用密码。";
            Status.Text = "已移除所选账户的已保存密码；本地任务和待同步修改仍保留。";
        }
        catch (Exception) { AccountError.Text = "Windows 未能移除已保存密码，请稍后重试。"; }
    }
    private void LoadAccount()
    {
        accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Server.Text.Trim().TrimEnd('/') + "\n" + Username.Text.Trim())));
        engine = new SyncEngine(store, accountKey);
        taskLists = store.Read<List<TaskList>>(accountKey + ":lists") ?? [];
        AccountLabel.Text = Username.Text.Trim() + " · Nextcloud";
    }
    private bool CanNavigate()
    {
        if (!HasDraft) return true;
        Status.Text = "当前任务有未保存的修改。请先保存，或关闭详情并放弃修改。"; return false;
    }
    private void SetBusy(bool value)
    {
        busy = value; Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Lists.IsEnabled = Tasks.IsEnabled = Search.IsEnabled = QuickAdd.IsEnabled = ShowCompleted.IsEnabled = !value;
        foreach (var tile in new[] { AllTile, TodayTile, ScheduledTile, PendingTile, ConflictTile, CompletedTile }) tile.IsEnabled = !value;
        TaskTitle.IsEnabled = TaskNotes.IsEnabled = Done.IsEnabled = Destination.IsEnabled = DueDate.IsEnabled = HasDueDate.IsEnabled = HasReminder.IsEnabled = !value;
        ReminderDate.IsEnabled = ReminderTime.IsEnabled = !value;
        SyncButton.IsEnabled = !value && client != null;
        CurrentSyncButton.IsEnabled = !value && client != null && currentListUrl != null;
        SaveButton.IsEnabled = DeleteButton.IsEnabled = ResolveButton.IsEnabled = AddButton.IsEnabled = !value;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || !CanNavigate()) return;
        SetBusy(true);
        try { await action(); }
        catch (Exception ex) { Status.Text = "操作未完成：" + ex.Message + " 本地修改仍保留。"; }
        finally
        {
            SetBusy(false);
            if (editorOpen && selected != null)
            {
                var task = engine?.Tasks.FirstOrDefault(t => t.Url == selected.Url);
                if (task == null) HideEditor(); else OpenEditor(task);
            }
            Refresh();
        }
    }
    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (busy || accountDialogOpen || !CanNavigate()) return;
        AccountDialog.XamlRoot = Root.XamlRoot; AccountError.Text = "";
        RefreshCredentialControls();
        accountDialogOpen = true;
        try { await AccountDialog.ShowAsync(); } finally { accountDialogOpen = false; }
    }
    private async void Connect_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (busy) return;
        var deferral = args.GetDeferral();
        sender.IsPrimaryButtonEnabled = false; SetBusy(true); AccountError.Text = "正在发现任务列表…";
        Server.IsEnabled = Username.IsEnabled = Password.IsEnabled = RememberPassword.IsEnabled = ForgetPasswordButton.IsEnabled = false;
        try
        {
            var account = DialogAccount();
            _ = account.CredentialKey;
            var login = await new SavedLogin(credentials).Connect(account, Password.Password, RememberPassword.IsChecked == true, password => Authenticate(account, password));
            if (closed) { login.Value.Client.Dispose(); return; }
            AcceptConnection(account, login.Value.Client, login.Value.Lists);
            Password.Password = "";
            HideEditor(); currentListUrl = null; view = "all";
            Status.Text = $"已连接，发现 {taskLists.Count} 个列表。点击“同步所有列表”即可一起同步。";
            if (!login.PreferenceApplied) Status.Text += " Windows 凭据更新失败，请重新打开账户设置确认密码保存状态。";
            args.Cancel = false;
        }
        catch (Exception ex) { AccountError.Text = ex.Message; }
        finally
        {
            if (!closed) { SetBusy(false); Server.IsEnabled = Username.IsEnabled = Password.IsEnabled = RememberPassword.IsEnabled = ForgetPasswordButton.IsEnabled = true; sender.IsPrimaryButtonEnabled = true; Refresh(); RefreshCredentialControls(); }
            deferral.Complete();
        }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (client == null || engine == null) return;
        Status.Text = "正在刷新任务列表…";
        taskLists = await client.Discover(); store.Write(accountKey + ":lists", taskLists);
        if (currentListUrl != null && !taskLists.Any(l => l.Url == currentListUrl)) { currentListUrl = null; view = "all"; }
        var result = await engine.SyncAll(client, taskLists, progress => Status.Text = $"正在同步 {progress.Current}/{progress.Total} · {progress.Name}");
        Status.Text = result.Total == 0 ? "账户中没有可同步的任务列表。" : $"已同步 {result.Succeeded}/{result.Total} 个列表 · {DateTime.Now:HH:mm:ss}" + (result.Conflicts > 0 ? $" · {result.Conflicts} 项冲突" : "");
        if (result.Failures.Count > 0)
        {
            Status.Text += " · 失败：" + string.Join("、", result.Failures.Select(f => f.List.Name));
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "部分列表同步未完成", Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = string.Join("\n\n", result.Failures.Select(f => f.List.Name + "\n" + f.Message)), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "知道了" }.ShowAsync();
        }
    });
    private async void CurrentSync_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var list = taskLists.FirstOrDefault(l => l.Url == currentListUrl);
        if (client == null || engine == null || list == null) return;
        Status.Text = "正在同步 · " + list.Name; await engine.Sync(client, list);
        Status.Text = "已同步 · " + list.Name + $" · {DateTime.Now:HH:mm:ss}";
    });
    private void Refresh()
    {
        if (!initialized || refreshing) return;
        refreshing = true;
        try
        {
            var all = engine?.Tasks ?? [];
            var today = DateOnly.FromDateTime(DateTime.Now);
            string[] colors = ["3584E4", "27A66A", "A267CB", "D28024", "D85363", "28A6B5"];
            Lists.ItemsSource = taskLists.Select((list, i) => new SidebarList(list, all.Count(t => t.ListUrl == list.Url && !t.Completed && t.Pending != "delete"), colors[i % colors.Length])).ToList();
            Lists.SelectedItem = Lists.Items.Cast<SidebarList>().FirstOrDefault(l => l.Model.Url == currentListUrl);
            var shown = all.Where(t => (currentListUrl == null || t.ListUrl == currentListUrl) && TaskViews.Matches(t, view, today));
            if (view == "all" && ShowCompleted.IsChecked != true) shown = shown.Where(t => !t.Completed);
            shown = shown.Where(t => (t.Title + "\n" + t.Details).Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase));
            var rows = shown.OrderBy(t => t.Completed).ThenBy(t => TaskViews.DueDay(t) ?? DateOnly.MaxValue).ThenBy(t => t.Title).Select(t => new TaskRow(t, taskLists.FirstOrDefault(l => l.Url == t.ListUrl)?.Name ?? "本地保留的列表")).ToList();
            Tasks.ItemsSource = rows; Tasks.SelectedItem = rows.FirstOrDefault(r => r.Entry.Url == selected?.Url);
            PageTitle.Text = currentListUrl != null ? taskLists.FirstOrDefault(l => l.Url == currentListUrl)?.Name ?? "任务列表" : view switch { "today" => "今天", "scheduled" => "计划", "completed" => "已完成", "pending" => "待同步", "conflicts" => "同步冲突", _ => "全部任务" };
            PageSubtitle.Text = $"{rows.Count} 项任务" + (view == "today" ? " · 包含逾期任务" : currentListUrl == null ? $" · {taskLists.Count} 个列表" : "");
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = Search.Text.Length > 0 ? "没有找到匹配的任务" : "这里还没有任务";
            EmptyHint.Text = taskLists.Count == 0 ? "打开账户设置，连接并同步 Nextcloud。" : "试试其他视图，或点击右下角 ＋ 添加任务。";
            AllCount.Text = all.Count(t => !t.Completed && t.Pending != "delete").ToString();
            TodayCount.Text = all.Count(t => TaskViews.Matches(t, "today", today)).ToString();
            ScheduledCount.Text = all.Count(t => TaskViews.Matches(t, "scheduled", today)).ToString();
            PendingCount.Text = all.Count(t => t.Pending != "").ToString();
            ConflictCount.Text = all.Count(t => t.Conflict != null).ToString();
            CompletedCount.Text = all.Count(t => TaskViews.Matches(t, "completed", today)).ToString();
            foreach (var tile in new[] { AllTile, TodayTile, ScheduledTile, PendingTile, ConflictTile, CompletedTile })
            { tile.BorderBrush = tile.Foreground; tile.BorderThickness = new Thickness(currentListUrl == null && (string)tile.Tag == view ? 2 : 0); }
            ConnectionLabel.Text = client != null ? $"已连接 · {taskLists.Count} 个列表" : engine != null ? "离线缓存 · 连接后可同步" : "尚未连接 · 点击齿轮设置账户";
            ShowCompleted.Visibility = view == "all" ? Visibility.Visible : Visibility.Collapsed;
            SyncButton.IsEnabled = client != null && !busy;
            CurrentSyncButton.IsEnabled = client != null && currentListUrl != null && !busy;
            AddButton.IsEnabled = engine != null && taskLists.Count > 0 && !busy;
            UpdateEditorControls();
        }
        finally { refreshing = false; }
    }
    private void Lists_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || refreshing || busy || Lists.SelectedItem is not SidebarList list) return;
        if (!CanNavigate()) { Refresh(); return; }
        HideEditor(); currentListUrl = list.Model.Url; view = "all"; Search.Text = ""; Refresh();
    }
    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !CanNavigate()) return;
        HideEditor(); currentListUrl = null; view = (string)((Button)sender).Tag; Search.Text = ""; Refresh();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Refresh();
    private void ShowCompleted_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Task_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || refreshing || busy || Tasks.SelectedItem is not TaskRow row) return;
        if (!CanNavigate()) { Refresh(); return; }
        OpenEditor(row.Entry);
    }
    private void OpenEditor(TaskEntry? task)
    {
        selected = task; editorOpen = true;
        TaskTitle.Text = baselineTitle = task?.Title ?? ""; TaskNotes.Text = baselineNotes = task?.Details ?? ""; Done.IsChecked = baselineDone = task?.Completed ?? false;
        var calendar = task == null ? null : CalendarDocument.Parse(task.CalendarData);
        var due = calendar?.DueDate(); HasDueDate.IsChecked = baselineHasDue = due != null; baselineDue = due;
        DueDate.Date = (due?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today).Date;
        var reminder = calendar?.ReminderAt(); HasReminder.IsChecked = baselineHasReminder = reminder != null; baselineReminder = reminder;
        var reminderSeed = reminder?.LocalDateTime ?? due?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today.AddHours(9);
        ReminderDate.Date = reminderSeed.Date; ReminderTime.Time = reminderSeed.TimeOfDay;
        DueDate.Visibility = HasDueDate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReminderControls.Visibility = HasReminder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Destination.ItemsSource = taskLists;
        Destination.SelectedItem = taskLists.FirstOrDefault(l => l.Url == (task?.ListUrl ?? currentListUrl)) ?? taskLists.FirstOrDefault();
        EditorHeading.Text = task == null ? "新建任务" : "任务详情";
        TaskInfo.Text = task?.Conflict ?? (task?.ReadOnly == true ? "重复或协作任务暂时只读，请在 Apple 提醒事项中编辑。" : task?.Pending == "delete" ? "此任务将在下次同步时删除。" : "截止日期与提醒会写入 CalDAV；保存后需同步到其他设备。" + (calendar?.HasAlarm == true && reminder == null ? " 原有相对提醒会保留，设置新提醒可替换一个提醒。" : ""));
        EditorPanel.Visibility = Visibility.Visible; UpdateEditorWidth(); UpdateEditorControls();
    }
    private void UpdateEditorControls()
    {
        bool editable = !busy && selected?.ReadOnly != true && selected?.Conflict == null && selected?.Pending != "delete";
        TaskTitle.IsReadOnly = TaskNotes.IsReadOnly = !editable; Done.IsEnabled = HasDueDate.IsEnabled = DueDate.IsEnabled = HasReminder.IsEnabled = editable;
        ReminderDate.IsEnabled = ReminderTime.IsEnabled = editable;
        Destination.IsEnabled = !busy && selected == null;
        SaveButton.IsEnabled = engine != null && taskLists.Count > 0 && editable;
        DeleteButton.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.IsEnabled = selected != null && editable;
        ResolveButton.Visibility = selected?.Conflict != null ? Visibility.Visible : Visibility.Collapsed;
        ResolveButton.IsEnabled = !busy && client != null;
    }
    private void UpdateEditorWidth() => EditorColumn.Width = new GridLength(editorOpen ? Math.Min(340, Math.Max(280, Root.ActualWidth * 0.28)) : 0);
    private void HideEditor() { editorOpen = false; selected = null; EditorPanel.Visibility = Visibility.Collapsed; EditorColumn.Width = new GridLength(0); }
    private void New_Click(object sender, RoutedEventArgs e) { if (busy || !CanNavigate()) return; OpenEditor(null); TaskTitle.Focus(FocusState.Programmatic); }
    private void QuickAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (busy || engine == null || string.IsNullOrWhiteSpace(QuickAdd.Text)) return;
        var list = taskLists.FirstOrDefault(l => l.Url == currentListUrl) ?? taskLists.FirstOrDefault();
        if (list == null) { Status.Text = "请先连接 Nextcloud 并同步任务列表。"; return; }
        try
        {
            engine.Add(list, QuickAdd.Text, ""); QuickAdd.Text = ""; Status.Text = $"已添加到“{list.Name}” · 等待同步。"; Refresh();
        }
        catch (Exception ex) { Status.Text = "未能添加任务：" + ex.Message; }
    }
    private void HasDueDate_Click(object sender, RoutedEventArgs e) => DueDate.Visibility = HasDueDate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    private void HasReminder_Click(object sender, RoutedEventArgs e)
    {
        ReminderControls.Visibility = HasReminder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (HasReminder.IsChecked == true && baselineReminder == null)
        {
            ReminderDate.Date = CurrentDue?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
            ReminderTime.Time = TimeSpan.FromHours(9);
        }
    }
    private async void CloseEditor_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (HasDraft && !await Confirm("放弃未保存的修改？", "已保存到本机的任务不会受到影响。")) return;
        HideEditor(); Refresh();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine == null || Destination.SelectedItem is not TaskList list) return;
        try
        {
            if (selected == null) { engine.Add(list, TaskTitle.Text, TaskNotes.Text); selected = engine.Tasks.Last(); }
            bool updateDue = selected.ETag == null || (HasDueDate.IsChecked == true) != baselineHasDue || CurrentDue != baselineDue;
            bool updateReminder = selected.ETag == null || (HasReminder.IsChecked == true) != baselineHasReminder || CurrentReminder != baselineReminder;
            engine.Edit(selected, TaskTitle.Text, TaskNotes.Text, Done.IsChecked == true, updateDue, CurrentDue, updateReminder, CurrentReminder);
            Status.Text = "已保存 · 点击“同步所有列表”上传到 Nextcloud。"; OpenEditor(selected); Refresh();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine == null || !CanNavigate() || ((Button)sender).Tag is not TaskEntry task) return;
        try { engine.Edit(task, task.Title, task.Details, !task.Completed); if (selected == task) OpenEditor(task); Status.Text = "完成状态已保存在本机 · 等待同步。"; Refresh(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async Task<bool> Confirm(string title, string message) => await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = message, PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || selected == null || engine == null) return;
        var task = selected;
        if (await Confirm("删除任务？", "下次同步会从 Nextcloud 删除此任务，Apple 提醒事项也会同步删除。"))
        { try { engine.Delete(task); HideEditor(); Status.Text = "已标记删除，等待同步。"; Refresh(); } catch (Exception ex) { Status.Text = ex.Message; } }
    }
    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if (busy || selected == null || client == null || engine == null) return;
        var task = selected;
        if (await Confirm("采用服务器版本？", "本地版本会备份到恢复记录，然后采用服务器内容。"))
            await Run(async () => { await engine.UseServer(client, task); Status.Text = "已采用服务器版本，本地旧版本已备份。"; });
    }
}
