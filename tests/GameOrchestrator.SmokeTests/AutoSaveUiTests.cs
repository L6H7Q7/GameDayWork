using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameOrchestrator.Models;
using GameOrchestrator.Services;
using GameOrchestrator.ViewModels;
using GameOrchestrator.Views;

internal static class AutoSaveUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("自动保存 UI 检查超时");
        if (failure is not null) throw new InvalidOperationException("自动保存 UI 检查失败", failure);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var timeout = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow > timeout) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Check(done(), "异步保存未在规定时间内完成");
    }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Verify()
    {
        var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "App.xaml"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("请从项目目录运行 UI 检查");
        // Use a plain Application, without the actual App startup or screen monitoring.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var appXaml = File.ReadAllText(Path.Combine(repository.FullName, "App.xaml"));
        var resources = appXaml.Split("<Application.Resources>")[1].Split("</Application.Resources>")[0];
        app.Resources = (ResourceDictionary)XamlReader.Parse("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + resources + "</ResourceDictionary>");
        var fixture = Path.Combine(Path.GetTempPath(), "GameDayWork-autosave-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow();
        var model = Field<MainViewModel>(window, "_viewModel");
        var config = new ConfigService(fixture);
        typeof(MainViewModel).GetField("_configService", Hidden)!.SetValue(model, config);
        var pageType = typeof(MainWindow).GetNestedType("MainPage", BindingFlags.NonPublic)!;
        void Navigate(string page, AutomationTaskConfig? task = null)
        {
            var navigation = (Task<bool>)Call(window, "NavigateAsync", Enum.Parse(pageType, page), task)!;
            Wait(navigation); Check(navigation.Result, "页面切换应成功");
        }
        AppConfig Saved() { var read = config.LoadAsync(); Wait(read); return read.Result; }
        void Layout(double height)
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(900, height)); root.Arrange(new Rect(0, 0, 900, height)); root.UpdateLayout();
        }
        try
        {
            Call(window, "EnableAutoSave");
            var first = new AutomationTaskConfig { Name = "First" };
            var second = new AutomationTaskConfig { Name = "Second" };
            var group = new AutomationTaskConfig { IsGroup = true, Name = "Group", Children = [new() { Name = "Child" }] };
            model.Tasks.Add(first); model.Tasks.Add(second); model.Tasks.Add(group);
            PumpUntil(() => File.Exists(config.ConfigPath) && File.ReadAllText(config.ConfigPath).Contains("First"));
            Navigate("TaskEdit", first);
            Layout(700);
            var name = (TextBox)window.FindName("TaskNameInput");
            name.Text = "Automatically saved";
            PumpUntil(() => first.Name == "Automatically saved" && !model.HasTaskEdits);
            Check(Saved().Tasks[0].Name == "Automatically saved", "任务修改应自动写入配置");
            Check(ReferenceEquals(model.EditingTaskTarget, first) && model.EditingTask is not null, "自动保存应保持当前任务编辑状态");
            var duration = (TextBox)window.FindName("TaskDurationInput");
            duration.Text = "invalid";
            var invalidSave = (Task<bool>)Call(window, "FlushAutoSaveAsync", false)!;
            Wait(invalidSave);
            Check(!invalidSave.Result && Saved().Tasks[0].MaxRunMinutes == 45 && duration.Text == "invalid", "无效输入不能覆盖已保存值或被静默丢弃");
            duration.Text = "47";
            Navigate("TaskEdit", second);
            Check(Saved().Tasks[0].MaxRunMinutes == 47, "切换任务前应完成待保存输入");
            Check(second.MaxRunMinutes == 45 && model.EditingTaskTarget == second, "切换任务不能串改配置");
            model.ApplyToolType("MAA"); Layout(700);
            var repositoryField = (StackPanel)window.FindName("RepositoryField");
            Check(repositoryField.Visibility == Visibility.Visible, "适配任务应显示仓库按钮");
            var rows = new[] { "TaskIdentityRow", "TaskLaunchRow", "TaskTimingRow" };
            foreach (var rowName in rows)
            {
                var row = (Grid)window.FindName(rowName);
                Check(row.ColumnDefinitions[0].ActualWidth > 0 && Math.Abs(row.ColumnDefinitions[0].ActualWidth - row.ColumnDefinitions[2].ActualWidth) < 1, "每行左右应等宽：" + rowName);
            }
            var heights = new[] { "TaskNameInput", "ToolTypeSelector", "RepositoryButton", "TaskPathInput", "BrowseTaskButton", "TaskArgumentsInput", "TaskDurationInput" }
                .Select(control => ((FrameworkElement)window.FindName(control)).ActualHeight).ToArray();
            Check(heights.All(height => Math.Abs(height - 36) < 1), "任务控件应统一为36高度");
            model.ApplyToolType("自定义任务"); Layout(620);
            Check(repositoryField.Visibility == Visibility.Collapsed, "自定义任务应隐藏仓库按钮");
            var typeArea = (Grid)window.FindName("TypeAndRepository");
            Check(Math.Abs(((ComboBox)window.FindName("ToolTypeSelector")).ActualWidth - typeArea.ActualWidth) < 1, "无仓库时类型应占满右半");
            foreach (var height in new[] { 620d, 700d })
            {
                Layout(height);
                Check(((ScrollViewer)window.FindName("TaskSettingsPanel")).ExtentWidth <= ((ScrollViewer)window.FindName("TaskSettingsPanel")).ViewportWidth + 1, "任务表单不应横向溢出");
            }
            Navigate("TaskEdit", group);
            model.EditingTask!.Name = "Saved group";
            model.EditingTask.GroupTaskIntervalSeconds = 22;
            PumpUntil(() => group.Name == "Saved group" && !model.HasTaskEdits);
            Check(Saved().Tasks[2].GroupTaskIntervalSeconds == 22, "组设置应自动保存");
            Wait((Task)Call(window, "EditGroupMembersAsync", true, null)!);
            Check(Saved().Tasks[2].Children.Count == 2 && model.EditingTaskTarget == group, "添加组任务应立即保存并保持组设置");
            var child = group.Children[0];
            Navigate("TaskEdit", child);
            Layout(700);
            Check(((Grid)window.FindName("TaskDurationFields")).Visibility == Visibility.Visible, "组内任务应能设置自身的最长运行时间");
            Check(((Grid)window.FindName("TaskScheduleFields")).Visibility == Visibility.Collapsed, "组内任务的启动时间仍由任务组统一设置");
            Navigate("Home");
            model.SelectedTask = first; model.MoveDownCommand.Execute(null);
            var flush = (Task<bool>)Call(window, "FlushAutoSaveAsync", false)!; Wait(flush);
            Check(flush.Result && Saved().Tasks[0].Id == second.Id, "列表排序应保存");
            model.SelectedTask = first; model.DuplicateTaskCommand.Execute(null);
            Wait((Task)Call(window, "FlushAutoSaveAsync", false)!);
            Check(Saved().Tasks.Count == 4, "列表复制应保存");
            model.DeleteTaskCommand.Execute(null);
            Wait((Task)Call(window, "FlushAutoSaveAsync", false)!);
            Check(Saved().Tasks.Count == 3, "列表删除应保存");
            Navigate("Settings");
            model.IdleTimeoutMinutes = 9;
            PumpUntil(() => !((SettingsPage)window.FindName("GlobalSettingsPage")).HasChanges);
            Check(Saved().IdleTimeoutMinutes == 9, "全局设置应自动保存");
            model.WeComWebhookUrl = "invalid";
            var invalidSettings = (Task<bool>)Call(window, "FlushAutoSaveAsync", false)!; Wait(invalidSettings);
            Check(!invalidSettings.Result && Saved().Notifications.WeComWebhookUrl == "", "无效Webhook不应落盘");
            var genericSaveRejected = false;
            try { Wait(model.SaveAsync()); }
            catch (InvalidOperationException) { genericSaveRejected = true; }
            Check(genericSaveRejected && Saved().Notifications.WeComWebhookUrl == "", "其他保存入口不能绕过Webhook验证");
            model.WeComWebhookUrl = "";
            Navigate("Home"); Navigate("TaskEdit", second); model.ApplyToolType("MAA"); Layout(700);
            var buttons = Descendants((DependencyObject)window.Content).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
            Check(!buttons.Any(text => text is "保存列表" or "保存并返回" or "重置配置" or "取消" or "返回主页" or "‹ 返回"), "旧保存取消及返回按钮应移除");
            Check(window.FindName("NavigationBar") is null && window.FindName("HomeButton") is Button, "应只有顶部主页导航入口");
            var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Visual)window.Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "compact-task-settings.png"))) encoder.Save(file);
            // Multiple configuration writes should produce one complete, readable file.
            Wait(Task.WhenAll(Enumerable.Range(0, 10).Select(_ => config.SaveAsync(new AppConfig { IdleTimeoutMinutes = 7 }))));
            Check(Saved().IdleTimeoutMinutes == 7 && !File.Exists(config.ConfigPath + ".tmp"), "并发保存应完整写入并移除临时文件");
            Navigate("Home");
            Call(window, "WirePlaceholderControls");
            var logList = (ListBox)window.FindName("LogList");
            ScrollViewer LogScroll() => Descendants(logList).OfType<ScrollViewer>().First();
            void WaitForLogBottom()
            {
                PumpUntil(() =>
                {
                    Layout(700);
                    var scroll = LogScroll();
                    return !Field<bool>(window, "_logScrollPending") && scroll.ScrollableHeight > 0
                        && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1;
                });
            }
            for (var i = 0; i < 120; i++) model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "日志 " + i));
            WaitForLogBottom();
            LogScroll().ScrollToTop(); Layout(700);
            model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "手动上滚后的最新日志"));
            WaitForLogBottom();
            model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, string.Join("\n", Enumerable.Repeat("超长日志末尾", 80))));
            WaitForLogBottom();
            Check(LogScroll().ScrollableHeight > LogScroll().ViewportHeight, "长日志应产生可滚动区域");
            var filter = (ComboBox)window.FindName("LogFilter");
            filter.SelectedIndex = 1;
            model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "不符合筛选的最新日志"));
            WaitForLogBottom();
            Check(((LogEntry)logList.Items[logList.Items.Count - 1]).Level == LogLevel.Info, "筛选后末行应为符合条件的最新日志");
            filter.SelectedIndex = 0;
            WaitForLogBottom();
            Navigate("Settings");
            for (var i = 0; i < 40; i++) model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "隐藏时追加 " + i));
            Navigate("Home"); WaitForLogBottom();
            for (var i = 0; i < 2050; i++)
            {
                model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "连续日志 " + i));
                if (model.Logs.Count > 2000) model.Logs.RemoveAt(0);
            }
            WaitForLogBottom();
            Check(model.Logs.Count == 2000 && ((LogEntry)logList.Items[logList.Items.Count - 1]).Message == "连续日志 2049", "日志裁剪后最新记录仍应在末行");
            model.Logs.Clear(); Layout(700);
            for (var i = 0; i < 80; i++) model.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "清空后日志 " + i));
            WaitForLogBottom();
            Console.WriteLine("PASS: 日志追加、手动上滚后追加、超长日志、筛选、返回主页、连续裁剪及清空后自动置底");
            Console.WriteLine("PASS: 自动保存、无效输入、页面切换、任务组管理、列表持久化及紧凑布局检查");
        }
        finally
        {
            Call(window, "StopAutoSave");
            model.Dispose();
            typeof(MainWindow).GetField("_exitRequested", Hidden)!.SetValue(window, true);
            Field<System.Windows.Forms.NotifyIcon>(window, "_trayIcon").Dispose();
            Field<System.Drawing.Icon>(window, "_idleTrayIcon").Dispose();
            Field<System.Drawing.Icon>(window, "_runningTrayIcon").Dispose();
            window.Close(); app.Shutdown();
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }
}
