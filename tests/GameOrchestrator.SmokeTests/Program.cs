using GameOrchestrator.Models;
using GameOrchestrator.Events;
using GameOrchestrator.Infrastructure;
using GameOrchestrator.Services;
using System.Globalization;
using System.Windows;

var validator = new TaskValidationService();
AutoSaveUiTests.Run();
TimeSelectionTests.Run();
var failures = new List<string>();
void Assert(bool condition, string message) { if (!condition) failures.Add(message); }
await ProcessCompletionTests.RunAsync(Assert, args.Contains("--startup-timeout", StringComparer.OrdinalIgnoreCase));

Assert(!new AppConfig().AutoBlackoutAfterTask, "任务链结束后应返回空闲监控，不应立即进入假息屏");
Assert(!new AppConfig().StartWithWindows, "新安装默认不应自行创建开机启动项");
Assert(!new AppConfig().UseSystemService, "新安装默认不应静默安装系统服务");
Assert(!new AppConfig().LockScreenDisplayTimeoutEnabled, "新安装默认不应修改登录页或锁屏页息屏时间");
Assert(new AppConfig().LockScreenDisplayTimeoutAcSeconds == 60 && new AppConfig().LockScreenDisplayTimeoutDcSeconds == 30,
    "登录页和锁屏页息屏时间默认应为接通电源 60 秒、使用电池 30 秒");
Assert(new AppConfig().StartMinimizedToTray, "新安装默认应隐藏到托盘启动");
Assert(!new AppConfig().Notifications.CaptureTaskScreenshots, "新安装默认不应自行上传屏幕截图");
Assert(new AppConfig().Notifications.RunningScreenshotDelaySeconds == 120, "任务运行截图默认应在启动 120 秒后触发");

var migrationDirectory = Path.Combine(Path.GetTempPath(), $"GameOrchestrator-ConfigMigration-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(migrationDirectory, "data"));
await File.WriteAllTextAsync(Path.Combine(migrationDirectory, "data", "config.json"),
    """{"Notifications":{"runningScreenshotDelaySeconds":60}}""");
var migrationConfigService = new ConfigService(migrationDirectory);
var migratedConfig = await migrationConfigService.LoadAsync();
Assert(migratedConfig.ConfigSchemaVersion == 2 && migratedConfig.Notifications.RunningScreenshotDelaySeconds == 120
    && migratedConfig.StartMinimizedToTray && !migratedConfig.UseSystemService && !migratedConfig.LockScreenDisplayTimeoutEnabled,
    "旧配置应迁移为 120 秒截图延迟、默认隐藏到托盘，且不得静默启用服务或电源修改");
migratedConfig.Notifications.RunningScreenshotDelaySeconds = 60;
await migrationConfigService.SaveAsync(migratedConfig);
var reloadedConfig = await migrationConfigService.LoadAsync();
Assert(reloadedConfig.Notifications.RunningScreenshotDelaySeconds == 60,
    "迁移完成后用户自定义的 60 秒截图延迟应保留");
var migratedJson = await File.ReadAllTextAsync(Path.Combine(migrationDirectory, "data", "config.json"));
Assert(!migratedJson.Contains("blackoutAfterLogin", StringComparison.OrdinalIgnoreCase)
    && !migratedJson.Contains("blackoutAfterUnlock", StringComparison.OrdinalIgnoreCase),
    "配置文件不应继续保存登录或解锁后立即遮罩选项");
Directory.Delete(migrationDirectory, true);

var groupConfigDirectory = Path.Combine(Path.GetTempPath(), $"GameOrchestrator-Groups-{Guid.NewGuid():N}");
try
{
    var configService = new ConfigService(groupConfigDirectory);
    var fresh = await configService.LoadAsync();
    Assert(fresh.Tasks.Count == 0, "首次启动任务列表必须为空，不应预置五项任务");
    Assert(fresh.IdleTimeoutMinutes == 5, "空闲息屏默认应为五分钟");
    var independent = new AutomationTaskConfig { Name = "独立任务", CompletionAction = TaskCompletionAction.RunNext };
    var unrelated = new AutomationTaskConfig { Name = "其他独立任务" };
    var first = new AutomationTaskConfig { Name = "第一项", ScheduledStartTime = "01:00" };
    var skipped = new AutomationTaskConfig { Name = "禁用项", Enabled = false };
    var second = new AutomationTaskConfig { Name = "第二项", CompletionAction = TaskCompletionAction.None };
    var group = new AutomationTaskConfig { Name = "任务组", IsGroup = true, ScheduledStartTime = "09:30", Children = [first, skipped, second] };
    fresh.Tasks.Add(independent); fresh.Tasks.Add(group); fresh.Tasks.Add(unrelated);
    Assert(TaskPlanService.Expand([independent]).SequenceEqual([independent]), "独立任务不得继续执行其他任务，即使遗留完成后操作为 RunNext");
    Assert(TaskPlanService.Expand([group]).SequenceEqual([first, second]), "任务组必须按顺序执行启用的组内任务，忽略成员完成后操作");
    Assert(group.GroupTaskIntervalSeconds == 5, "任务组默认清理后间隔5秒");
    Assert(first.MaxRunMinutes == 45 && second.MaxRunMinutes == 45 && first.IntervalAfterSeconds == 5 && second.IntervalAfterSeconds == 0,
        "组内任务各自的最长运行时间不由任务组参与，任务间隔由任务组决定，组末尾不附加等待");
    Assert(first.IsGroupChild && second.IsGroupChild, "组内任务应被标记为由所属任务组调度");
    first.MaxRunMinutes = 23; group.GroupTaskIntervalSeconds = 7;
    TaskPlanService.Expand([group, independent]);
    Assert(first.MaxRunMinutes == 23 && second.MaxRunMinutes == 45 && first.IntervalAfterSeconds == 7 && independent.MaxRunMinutes == 45 && independent.IntervalAfterSeconds == 0,
        "组内任务自身的运行上限必须生效，且任务组设置不得影响独立任务");
    first.ProgramPath = Environment.ProcessPath!;
    first.ScheduledStartTime = "不使用的子任务时间";
    Assert(!validator.Validate([first]).Any(issue => issue.Message.Contains("定时启动")), "组内任务不应校验自身时间，应由任务组统一调度");
    unrelated.ScheduledStartTime = "无效时间";
    Assert(validator.Validate([unrelated]).Any(issue => issue.Message.Contains("定时启动")), "独立任务仍须校验自身启动时间");
    Assert(TaskPlanService.ManualPlan(fresh.Tasks, first).SequenceEqual([first, second]), "从组内任务启动时应继续组内后续任务");
    Assert(TaskPlanService.ManualPlan(fresh.Tasks, second).SequenceEqual([second]), "组内最后一项不得继续组外任务");
    group.Enabled = false;
    Assert(TaskPlanService.Expand([group]).Count == 0 && TaskPlanService.ManualPlan(fresh.Tasks, first).Count == 0, "禁用任务组不得执行组内任务");
    group.Enabled = true; group.IsExpanded = true;
    first.ToolType = "BGI"; first.Name = "任意名称";
    Assert(first.RepositoryUrl.Contains("better-genshin-impact"), "适配类型应独立于任务名称");
    first.ToolType = "自定义任务";
    Assert(first.RepositoryUrl == "", "自定义任务不得根据名称推断适配类型");
    await configService.SaveAsync(fresh);
    var roundtrip = await configService.LoadAsync();
    Assert(roundtrip.Tasks.Count == 3 && roundtrip.Tasks[1].IsGroup && roundtrip.Tasks[1].Children.Select(child => child.Id).SequenceEqual(group.Children.Select(child => child.Id)), "任务组成员及顺序应持久保存");
    Assert(!roundtrip.Tasks[1].IsExpanded, "重新打开时任务组默认折叠");
    Assert(roundtrip.Tasks[1].ScheduledStartTime == "09:30", "任务组统一运行时间应持久保存");
    Assert(roundtrip.Tasks[1].GroupTaskIntervalSeconds == 7, "任务组间隔应持久保存");
    Assert(roundtrip.Tasks[1].Children[0].MaxRunMinutes == 23 && roundtrip.Tasks[1].Children[0].IntervalAfterSeconds is null,
        "子任务自身的运行上限应持久保存，组运行参数不得写入子任务配置");
    Assert(new AutomationTaskConfig().CompletionAction == TaskCompletionAction.None, "独立任务默认完成后无操作");
}
finally
{
    // The directory is an exact unique test fixture path under the system temp directory.
    if (Directory.Exists(groupConfigDirectory)) Directory.Delete(groupConfigDirectory, true);
}

var schemeOne = Guid.NewGuid();
var schemeTwo = Guid.NewGuid();
var fakePower = new FakeLockScreenPowerApi(schemeOne,
    new Dictionary<Guid, PowerTimeoutValues> { [schemeOne] = new(120, 90), [schemeTwo] = new(300, 180) });
var powerConfig = new MachineServiceConfig { LockScreenTimeoutEnabled = true, AcSeconds = 60, DcSeconds = 30 };
var powerPolicy = new LockScreenPowerPolicy(fakePower);
powerPolicy.ApplyActiveScheme(powerConfig);
Assert(fakePower.Values[schemeOne] == new PowerTimeoutValues(60, 30) && powerConfig.OriginalTimeouts[schemeOne.ToString("D")] == new PowerTimeoutValues(120, 90),
    "应用锁屏息屏策略前应保存当前方案的 AC/DC 原值");
fakePower.ActiveScheme = schemeTwo;
powerPolicy.ApplyActiveScheme(powerConfig);
Assert(fakePower.Values[schemeTwo] == new PowerTimeoutValues(60, 30) && powerConfig.OriginalTimeouts.Count == 2,
    "切换活动电源方案后应保存并应用新方案");
Assert(powerPolicy.RestoreAll(powerConfig).Count == 0 && fakePower.Values[schemeOne] == new PowerTimeoutValues(120, 90)
    && fakePower.Values[schemeTwo] == new PowerTimeoutValues(300, 180) && powerConfig.OriginalTimeouts.Count == 0,
    "关闭策略时应恢复每个电源方案的原值");

var restartPolicy = new RestartPolicy();
var restartNow = DateTimeOffset.UtcNow;
var expectedRestartDelays = new[] { 2d, 5d, 15d, 30d, 60d };
foreach (var expected in expectedRestartDelays)
    Assert(restartPolicy.RegisterUnexpectedExit(restartNow)?.TotalSeconds == expected, "异常退出应采用有上限的递增退避");
Assert(restartPolicy.RegisterUnexpectedExit(restartNow) is null, "10 分钟内第六次异常退出不得继续形成崩溃循环");
Assert(ServicePathSafety.ParseExecutablePath("\"C:\\Program Files\\GameDayWork\\GameDayWork.Service.exe\" run") == @"C:\Program Files\GameDayWork\GameDayWork.Service.exe",
    "服务卸载路径校验应精确解析带引号的 ImagePath");

var cleanupDirectory = Path.Combine(Path.GetTempPath(), $"GameOrchestrator-LogCleanup-{Guid.NewGuid():N}");
Directory.CreateDirectory(cleanupDirectory);
var expiredLog = Path.Combine(cleanupDirectory, "2026-01-01.log");
await File.WriteAllTextAsync(expiredLog, "expired");
File.SetLastWriteTimeUtc(expiredLog, DateTime.UtcNow.AddDays(-31));
var cleanupResult = await new LoggingService(cleanupDirectory).CleanupAsync();
Assert(cleanupResult.DeletedFiles == 1 && !File.Exists(expiredLog), "日志清理应删除超过 30 天的日志文件");
Directory.Delete(cleanupDirectory, true);

var empty = new AutomationTaskConfig { Name = "测试任务" };
Assert(validator.Validate([empty]).Any(x => x.Message.Contains("程序路径")), "空程序路径应校验失败");

var inferredDirectoryTask = new AutomationTaskConfig { WorkingDirectory = "旧目录" };
var inferredProgramPath = Path.Combine(Path.GetTempPath(), "tool", "sample.exe");
inferredDirectoryTask.ProgramPath = inferredProgramPath;
Assert(inferredDirectoryTask.WorkingDirectory == Path.GetDirectoryName(inferredProgramPath), "更换程序路径时应自动同步工作目录");

var valid = new AutomationTaskConfig
{
    Name = "有效任务", ProgramPath = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
    CompletionMode = CompletionDetectionMode.MainProcessExit
};
Assert(validator.Validate([valid]).Count == 0, "有效任务不应产生校验错误");

valid.CompletionMode = CompletionDetectionMode.SpecifiedProcessExit;
Assert(validator.Validate([valid]).Any(x => x.Message.Contains("进程名称")), "指定进程模式必须填写进程名称");
valid.CompletionProcessName = "sample.exe";
Assert(validator.Validate([valid]).Count == 0, "填写指定进程名称后应通过校验");
valid.ProcessRules.Add(new ProcessRule());
Assert(validator.Validate([valid]).Any(x => x.Message.Contains("空进程规则")), "空进程规则应校验失败");

var logDirectory = Path.Combine(Path.GetTempPath(), $"GameOrchestrator-Test-{Guid.NewGuid():N}");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "tool-20260915.log");
await File.WriteAllTextAsync(logPath, "历史完成标志\n");
var detector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "tool-*.log"), "全部完成");
await File.AppendAllTextAsync(logPath, "任务已全部完成！\n");
Assert(await detector.CheckAsync(CancellationToken.None), "日志检测器应识别本次新增的完成关键字");
var rollingDetector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "rolling-*.log"), "游戏终止");
var nextLogPath = Path.Combine(logDirectory, "rolling-20260916.log");
await File.WriteAllTextAsync(nextLogPath, "游戏终止：TestGame\n");
Assert(await rollingDetector.CheckAsync(CancellationToken.None), "日志检测器应识别运行后新建的轮转日志");
var failureDetector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "rolling-*.log"), "指令[ 一条龙 ] 执行失败");
await File.AppendAllTextAsync(nextLogPath, "指令[ 一条龙 ] 执行失败 返回状态 异常\n");
Assert(await failureDetector.CheckAsync(CancellationToken.None), "日志检测器应识别本次新增的失败关键字");
Directory.Delete(logDirectory, true);

var profiles = new KnownToolProfileService().Discover();
Assert(profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == profiles.Count, "本机工具识别结果不应重复");
var expectedProcesses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["BGI"] = "BetterGI.exe",
    ["MAA"] = "MAA.exe",
    ["ZOG"] = "OneDragon-Launcher.exe",
    ["MFA"] = "MFAAvalonia.exe",
    ["M7A"] = "March7th Assistant.exe"
};
foreach (var profile in profiles)
{
    Assert(File.Exists(profile.ProgramPath), $"识别出的 {profile.Name} 程序必须存在");
    Assert(profile.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit, $"识别出的 {profile.Name} 应使用指定进程退出检测");
    Assert(expectedProcesses.TryGetValue(profile.Name, out var expectedProcess) && profile.CompletionProcessName == (profile.Name == "ZOG" ? Path.GetFileName(profile.ProgramPath) : expectedProcess),
        $"识别出的 {profile.Name} 应监控正确的任务进程");
    Assert(profile.ProcessRules.Any(rule => rule.Monitor && rule.Cleanup && rule.AllowNameFallback &&
        string.Equals(rule.ProcessName, profile.CompletionProcessName, StringComparison.OrdinalIgnoreCase)),
        $"识别出的 {profile.Name} 应具有安全的进程监控与清理规则");
}

if (profiles.FirstOrDefault(profile => profile.Name == "MFA") is { } mfaProfile)
{
    var existingId = Guid.NewGuid();
    var existing = new AutomationTaskConfig
    {
        Id = existingId,
        Name = "MAN",
        Enabled = false,
        ProgramPath = Environment.ProcessPath!,
        CompletionMode = CompletionDetectionMode.MainProcessExit
    };
    existing.ProcessRules.Add(new ProcessRule { ProcessName = "custom.exe", AllowNameFallback = true });
    KnownToolProfileService.ApplyRecommendedSettings(existing, mfaProfile);
    Assert(existing.Id == existingId && !existing.Enabled, "应用推荐适配时应保留任务 ID 和启用状态");
    Assert(existing.Name == "MFA" && existing.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit, "已有 MFA 任务也应更新为指定进程退出检测");
    Assert(existing.CompletionProcessName == "MFAAvalonia.exe", "MFA 应监控其实际任务进程");
    Assert(existing.WorkingDirectory == mfaProfile.WorkingDirectory, "应用推荐适配时应同步程序工作目录");
    Assert(existing.ProcessRules.Any(rule => rule.ProcessName == "custom.exe") && existing.ProcessRules.Any(rule => rule.ExecutableDirectory == existing.WorkingDirectory), "应用推荐适配时应保留自定义规则并加入安装目录规则");
}

var processMonitor = new ProcessMonitorService();
var emptySession = new RuntimeSession { RootPid = 0 };
Assert(processMonitor.Scan(emptySession, valid).Count == 0, "未启动成功时不得把 PID 0 当作任务进程");

var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
if (File.Exists(commandProcessor))
{
    AutomationTaskConfig CreateShortProcessTask(string name) => new()
    {
        Name = name,
        ProgramPath = commandProcessor,
        Arguments = "/d /s /c \"ping.exe 127.0.0.1 -n 3 > nul\"",
        WorkingDirectory = Environment.SystemDirectory,
        CompletionMode = CompletionDetectionMode.SpecifiedProcessExit,
        CompletionProcessName = "cmd.exe",
        MaxRunMinutes = 1,
        CleanupWaitSeconds = 1,
        CleanupRetries = 1,
        TrackChildren = true,
        UseJobObject = false
    };

    var firstProcessTask = CreateShortProcessTask("进程退出测试一");
    var secondProcessTask = CreateShortProcessTask("进程退出测试二");
    var processEvents = new TaskEventBus();
    var completionOrder = new List<string>();
    DateTimeOffset? firstFinished = null;
    DateTimeOffset? secondStarted = null;
    processEvents.Subscribe<TaskCompletedEvent>(message => completionOrder.Add(message.Session.TaskName));
    processEvents.Subscribe<TaskCompletedEvent>(message => { if (message.Session.TaskName == firstProcessTask.Name) firstFinished = DateTimeOffset.UtcNow; });
    processEvents.Subscribe<TaskStartedEvent>(message => { if (message.Session.TaskName == secondProcessTask.Name) secondStarted = DateTimeOffset.UtcNow; });
    var processLog = new LoggingService();
    var processRunner = new TaskRunnerService(processMonitor, new ProcessCleanupService(processMonitor, processLog), processLog, processEvents);
    var processQueue = new TaskQueueService(processRunner, processLog, processEvents);
    var timedGroup = new AutomationTaskConfig { IsGroup = true, Children = [firstProcessTask, secondProcessTask], GroupTaskIntervalSeconds = 1 };
    await processQueue.RunAsync(TaskPlanService.Expand([timedGroup]), 0, FailurePolicy.ForceCleanupAndContinue);
    Assert(processQueue.Status == QueueRunStatus.Completed, "指定进程退出后队列应正常完成");
    Assert(firstProcessTask.Status == TaskRunStatus.Completed && secondProcessTask.Status == TaskRunStatus.Completed,
        "指定进程退出后应自动执行并完成下一项");
    Assert(completionOrder.SequenceEqual([firstProcessTask.Name, secondProcessTask.Name]),
        "指定进程退出后的任务完成顺序应保持不变");
    Assert(firstFinished.HasValue && secondStarted.HasValue && secondStarted.Value - firstFinished.Value >= TimeSpan.FromMilliseconds(950),
        "组内间隔必须从第一项完成清理后计算，而非从启动时间计算");

    var forcedStopTask = CreateShortProcessTask("强制终止记录测试");
    forcedStopTask.Arguments = "/d /s /c \"ping.exe 127.0.0.1 -n 30 > nul\"";
    using var forcedStop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var forcedStopResult = await processRunner.RunAsync(forcedStopTask, forcedStop.Token);
    var terminated = forcedStopResult.Session.SnapshotTerminatedProcesses();
    Assert(forcedStopResult.Session.Status == TaskRunStatus.Stopped, "取消运行后任务状态应为已停止");
    Assert(terminated.Any(process => process.Success), "强制终止后应记录至少一个成功退出的关联进程");
}

var visibilityConverter = new EnumEqualsToVisibilityConverter();
Assert((Visibility)visibilityConverter.Convert(CompletionDetectionMode.LogKeyword, typeof(Visibility), "LogKeyword", CultureInfo.InvariantCulture) == Visibility.Visible, "匹配的完成检测方式应显示对应字段");
Assert((Visibility)visibilityConverter.Convert(CompletionDetectionMode.MainProcessExit, typeof(Visibility), "LogKeyword", CultureInfo.InvariantCulture) == Visibility.Collapsed, "不匹配的完成检测方式应隐藏对应字段");

var integrationName = args.FirstOrDefault(value => value.StartsWith("--integration=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
if (!string.IsNullOrWhiteSpace(integrationName))
{
    var task = profiles.FirstOrDefault(profile => string.Equals(profile.Name, integrationName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"未识别到 {integrationName}，无法执行真实集成测试");
    var integrationLog = new LoggingService();
    integrationLog.EntryWritten += entry => Console.WriteLine($"{entry.TimeText} [{entry.LevelText}] {entry.Message}");
    var integrationMonitor = new ProcessMonitorService();
    var runner = new TaskRunnerService(integrationMonitor, new ProcessCleanupService(integrationMonitor, integrationLog), integrationLog, new TaskEventBus());
    Console.WriteLine($"INTEGRATION: 即将运行 {task.ProgramPath} {task.Arguments}");
    var result = await runner.RunAsync(task, CancellationToken.None);
    Assert(result.Success, $"{task.Name} 真实运行失败：{result.Error ?? result.Session.ExitReason}");
    Console.WriteLine($"INTEGRATION: {task.Name} 结束状态={result.Session.Status}，原因={result.Session.ExitReason}");
}

if (args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase))
{
    var screenshot = await new DesktopScreenshotService().CaptureDesktopAsync();
    Assert(screenshot is not null, "桌面截图服务应返回图像");
    if (screenshot is not null)
    {
        Assert(screenshot.Width > 0 && screenshot.Height > 0, "桌面截图尺寸必须有效");
        Assert(screenshot.Data.Length > 0 && screenshot.Data.Length <= 1_900_000, "桌面截图必须符合企业微信大小限制");
        Assert(screenshot.MimeType is "image/png" or "image/jpeg", "桌面截图格式必须为 PNG 或 JPEG");
        Console.WriteLine($"SCREENSHOT: {screenshot.Width}x{screenshot.Height} {screenshot.Format} {screenshot.Data.Length / 1024d:F1} KB");
    }
}

await MuMuCleanupTests.RunAsync(Assert);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "FAIL: " + x)));
    return 1;
}
Console.WriteLine("PASS: 任务配置校验冒烟测试全部通过");
return 0;

sealed class FakeLockScreenPowerApi(Guid activeScheme, Dictionary<Guid, PowerTimeoutValues> values) : ILockScreenPowerApi
{
    public Guid ActiveScheme { get; set; } = activeScheme;
    public Dictionary<Guid, PowerTimeoutValues> Values { get; } = values;
    public Guid GetActiveScheme() => ActiveScheme;
    public PowerTimeoutValues Read(Guid scheme) => Values[scheme];
    public void Write(Guid scheme, PowerTimeoutValues value, bool activate = true)
    {
        Values[scheme] = value;
        if (activate) ActiveScheme = scheme;
    }
}
