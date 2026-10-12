using GameOrchestrator.Events;
using GameOrchestrator.Models;
using GameOrchestrator.Services;

internal static class ProcessCompletionTests
{
    public static async Task RunAsync(Action<bool, string> assert, bool runStartupTimeout)
    {
        var missing = new ProcessExitCompletionTracker();
        assert(missing.Check(false, TimeSpan.FromSeconds(119)) == ProcessExitCheck.Waiting, "未出现目标在 2 分钟前不得超时");
        assert(missing.Check(false, TimeSpan.FromSeconds(120)) == ProcessExitCheck.StartupTimedOut, "未出现目标在 2 分钟时必须超时");
        var late = new ProcessExitCompletionTracker();
        assert(late.Check(true, TimeSpan.FromSeconds(120)) == ProcessExitCheck.Waiting, "边界时已出现目标不得算启动超时");
        assert(late.Check(true, TimeSpan.FromMinutes(4)) == ProcessExitCheck.Waiting, "目标已出现后不得使用启动超时结束运行");
        late.Check(false, TimeSpan.FromSeconds(241));
        assert(late.Check(true, TimeSpan.FromSeconds(243)) == ProcessExitCheck.Waiting, "进程重新出现时应取消退出等待");
        late.Check(false, TimeSpan.FromSeconds(250));
        assert(late.Check(false, TimeSpan.FromSeconds(252)) == ProcessExitCheck.Waiting
            && late.Check(false, TimeSpan.FromSeconds(253)) == ProcessExitCheck.Completed, "进程消失必须持续 3 秒才算完成");

        var fixture = Path.Combine(Path.GetTempPath(), $"GDW-ZogMigration-{Guid.NewGuid():N}");
        try
        {
            var zog = new AutomationTaskConfig
            {
                ToolType = "ZOG", Name = "自定义名称", ProgramPath = @"D:\ZOG\OneDragon-RuntimeLauncher.exe",
                CompletionMode = CompletionDetectionMode.SpecifiedProcessExit, CompletionProcessName = "OneDragon-Launcher.exe"
            };
            zog.ProcessRules.Add(new ProcessRule { ProcessName = "OneDragon-Launcher.exe", AllowNameFallback = true });
            zog.ProcessRules.Add(new ProcessRule { ProcessName = "custom.exe" });
            var config = new AppConfig { ConfigSchemaVersion = 2, Tasks = [new AutomationTaskConfig { IsGroup = true, Children = [zog] }] };
            var service = new ConfigService(fixture);
            await service.SaveAsync(config);
            var migrated = (await service.LoadAsync()).Tasks[0].Children[0];
            assert(migrated.CompletionProcessName == "OneDragon-RuntimeLauncher.exe"
                && migrated.ProcessRules[0].ProcessName == migrated.CompletionProcessName, "组内 ZOG 旧配置必须匹配实际集成启动器");
            assert(migrated.Name == zog.Name && migrated.Id == zog.Id && migrated.ProcessRules[1].ProcessName == "custom.exe", "ZOG 迁移应保留任务身份与自定义规则");
            assert((await service.LoadAsync()).Tasks[0].Children[0].CompletionProcessName == migrated.CompletionProcessName, "ZOG 迁移应保存并可重复加载");
            migrated.CompletionProcessName = "custom.exe";
            assert(!KnownToolProfileService.NormalizeZogCompletion(migrated), "不得覆盖自定义目标进程");
            migrated.CompletionMode = CompletionDetectionMode.LogKeyword;
            migrated.CompletionProcessName = "OneDragon-Launcher.exe";
            assert(!KnownToolProfileService.NormalizeZogCompletion(migrated), "不得覆盖 ZOG 日志完成模式");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }

        if (!runStartupTimeout) return;
        var command = Environment.GetEnvironmentVariable("ComSpec")!;
        var absent = new AutomationTaskConfig
        {
            Name = "目标未启动测试", ProgramPath = command,
            Arguments = "/d /s /c \"ping.exe 127.0.0.1 -n 180 > nul\"",
            CompletionMode = CompletionDetectionMode.SpecifiedProcessExit,
            CompletionProcessName = $"never-started-{Guid.NewGuid():N}.exe", MaxRunMinutes = 5,
            UseJobObject = false, CleanupWaitSeconds = 1, CleanupRetries = 1
        };
        var next = new AutomationTaskConfig
        {
            Name = "启动超时后下一项", ProgramPath = command, Arguments = "/d /c exit 0",
            CompletionMode = CompletionDetectionMode.SpecifiedProcessExit, CompletionProcessName = "cmd.exe",
            UseJobObject = false
        };
        var log = new LoggingService { FileLoggingEnabled = false };
        log.EntryWritten += entry => Console.WriteLine(entry.Message);
        var events = new TaskEventBus();
        bool cleanupVerified = false, continuedAfterCleanup = false;
        events.Subscribe<TaskCleanupCompletedEvent>(e => { if (e.Session.TaskId == absent.Id) cleanupVerified = true; });
        events.Subscribe<TaskStartedEvent>(e => { if (e.Session.TaskId == next.Id) continuedAfterCleanup = cleanupVerified; });
        var monitor = new ProcessMonitorService();
        var runner = new TaskRunnerService(monitor, new ProcessCleanupService(monitor, log), log, events);
        var queue = new TaskQueueService(runner, log, events);
        Console.WriteLine("开始真实 120 秒目标启动超时验证；不会运行游戏或发送通知。");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await queue.RunAsync([absent, next], 0, FailurePolicy.StopQueue);
        assert(timer.Elapsed >= TimeSpan.FromSeconds(120) && timer.Elapsed < TimeSpan.FromSeconds(150), "实际启动超时应为 120 秒加扫描及清理耗时");
        assert(absent.Status == TaskRunStatus.TimedOut && next.Status == TaskRunStatus.Completed
            && queue.Status == QueueRunStatus.Completed && continuedAfterCleanup,
            "目标启动超时必须保留超时状态，并在清理成功后继续下一项，即使普通失败策略为停止队列");
    }
}
