using System.Diagnostics;
using GameOrchestrator.Events;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record TaskRunResult(RuntimeSession Session, bool Success, bool TimedOut, string? Error)
{
    public bool TargetStartupTimedOut { get; init; }
}

public sealed class TaskRunnerService(ProcessMonitorService monitor, ProcessCleanupService cleanup, LoggingService log, TaskEventBus events, TaskScreenshotCoordinator? screenshots = null, MuMuCleanupService? mumu = null)
{
    private readonly MuMuCleanupService _mumu = mumu ?? new MuMuCleanupService(log);
    public event Action<RuntimeSession>? SessionChanged;
    public async Task<TaskRunResult> RunAsync(AutomationTaskConfig task, CancellationToken queueToken)
    {
        var session = new RuntimeSession { TaskId = task.Id, TaskName = task.Name, Status = TaskRunStatus.Starting };
        task.Status = TaskRunStatus.Starting; events.Publish(new TaskStartingEvent(session)); SessionChanged?.Invoke(session);
        Process? root = null; JobObjectService? job = null; bool timedOut = false; bool targetStartupTimedOut = false; string? error = null;
        MuMuTarget? mumuTarget = null;
        try
        {
            if (KnownToolProfileService.NormalizeZogCompletion(task))
                await log.WriteAsync(LogLevel.Info, $"{task.Name} 完成检测已匹配实际启动程序：{task.CompletionProcessName}");
            if (!File.Exists(task.ProgramPath)) throw new FileNotFoundException("找不到任务程序", task.ProgramPath);
            mumuTarget = await _mumu.CaptureAsync(task, queueToken);
            session.MuMuVmId = mumuTarget?.VmId;
            session.MuMuInstanceIndex = mumuTarget?.Index ?? "";
            foreach (var process in Process.GetProcesses())
            {
                try { session.BaselineProcessIds.Add(process.Id); }
                finally { process.Dispose(); }
            }
            var startInfo = new ProcessStartInfo(task.ProgramPath, task.Arguments)
            {
                UseShellExecute = task.RunAsAdministrator,
                Verb = task.RunAsAdministrator ? "runas" : "",
                WorkingDirectory = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? Path.GetDirectoryName(task.ProgramPath)! : task.WorkingDirectory
            };
            if (!task.RunAsAdministrator)
            {
                // 构建/测试宿主可能注入私有 .NET 路径，外部自动化工具应按其正常系统环境解析运行时。
                startInfo.Environment.Remove("DOTNET_HOST_PATH");
                startInfo.Environment.Remove("DOTNET_ROOT");
                startInfo.Environment.Remove("DOTNET_ROOT_X64");
                startInfo.Environment.Remove("DOTNET_ROOT_X86");
            }
            root = Process.Start(startInfo) ?? throw new InvalidOperationException("进程启动失败");
            var startupElapsed = Stopwatch.StartNew();
            session.RootPid = root.Id;
            session.RootExecutablePath = Path.GetFullPath(task.ProgramPath);
            try { session.RootProcessStartTime = root.StartTime; } catch { }
            if (task.UseJobObject)
            {
                try
                {
                    job = new JobObjectService($"GameDayWork-{session.SessionId:N}");
                    if (!job.TryAssign(root)) await log.WriteAsync(LogLevel.Warning, "无法将主进程加入 Job Object，将继续使用进程树和安全规则监控");
                }
                catch (Exception ex) { await log.WriteAsync(LogLevel.Warning, $"Job Object 不可用：{ex.Message}"); }
            }
            task.Status = session.Status = TaskRunStatus.Running; events.Publish(new TaskStartedEvent(session)); SessionChanged?.Invoke(session);
            await log.WriteAsync(LogLevel.Info, $"{task.Name} 已启动，PID {root.Id}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(task.MaxRunMinutes));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, timeout.Token);
            try { targetStartupTimedOut = await WaitForCompletionAsync(root, task, session, startupElapsed, linked.Token); timedOut = targetStartupTimedOut; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !queueToken.IsCancellationRequested) { timedOut = true; }
            if (queueToken.IsCancellationRequested) session.ExitReason = "用户停止";
            else if (timedOut) { task.Status = session.Status = TaskRunStatus.TimedOut; session.ExitReason = targetStartupTimedOut ? $"目标进程 {task.CompletionProcessName} 在启动后 2 分钟内未出现" : "超过最大运行时间"; await log.WriteAsync(LogLevel.Error, $"{task.Name} 已超时：{session.ExitReason}"); }
            else { task.Status = session.Status = TaskRunStatus.CompletionDetected; session.ExitReason = "完成条件满足"; events.Publish(new TaskCompletionDetectedEvent(session)); await log.WriteAsync(LogLevel.Info, $"检测到 {task.Name} 已完成"); }
        }
        catch (OperationCanceledException) { session.ExitReason = "用户停止"; }
        catch (Exception ex) { error = ex.Message; session.ExitReason = ex.Message; await log.WriteAsync(LogLevel.Error, $"{task.Name} 执行失败：{ex.Message}"); }
        finally
        {
            task.Status = session.Status = TaskRunStatus.Cleaning; events.Publish(new TaskCleanupStartedEvent(session)); SessionChanged?.Invoke(session);
            await log.WriteAsync(LogLevel.Info, $"开始清理 {task.Name} 关联进程");
            ProcessCleanupResult cleanupResult;
            ProcessCleanupResult mumuResult = new(true, [], 0);
            ProcessCleanupResult serviceResult = new(true, [], 0);
            using var mumuDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            // Close and verify the VM before JobObject/process-tree termination.
            if (mumuTarget is not null && session.RootPid > 0)
                mumuResult = await _mumu.CloseInstanceAsync(mumuTarget, mumuDeadline.Token);
            try { cleanupResult = await cleanup.CleanupAsync(session, task, job, CancellationToken.None); }
            catch (Exception ex) { cleanupResult = new(false, [], 0); error ??= ex.Message; }
            if (mumuTarget is not null && session.RootPid > 0)
                serviceResult = await _mumu.FinishCleanupAsync(mumuTarget, mumuDeadline.Token);
            session.SetTerminatedProcesses(mumuResult.Processes.Concat(cleanupResult.Processes).Concat(serviceResult.Processes));
            task.Status = session.Status = TaskRunStatus.CleanupVerifying; SessionChanged?.Invoke(session);
            var remaining = (session.RootPid > 0 ? monitor.Scan(session, task).Count : 0) + serviceResult.RemainingCount;
            if (!mumuResult.Success || !serviceResult.Success) error ??= "MuMu 虚拟机或辅助服务清理验证失败";
            if (cleanupResult.Success && mumuResult.Success && serviceResult.Success && remaining == 0) { events.Publish(new TaskCleanupCompletedEvent(session)); await log.WriteAsync(LogLevel.Success, $"{task.Name} 清理完成"); }
            else { error ??= $"仍有 {remaining} 个关联进程未退出"; await log.WriteAsync(LogLevel.Error, $"{task.Name} 清理验证失败：{error}"); }
            job?.Dispose(); root?.Dispose(); session.EndTime = DateTimeOffset.Now;
        }
        var success = error is null && !timedOut && !queueToken.IsCancellationRequested;
        task.Status = session.Status = queueToken.IsCancellationRequested ? TaskRunStatus.Stopped : timedOut ? TaskRunStatus.TimedOut : success ? TaskRunStatus.Completed : TaskRunStatus.Failed;
        if (success) { events.Publish(new TaskCompletedEvent(session)); await log.WriteAsync(LogLevel.Success, $"{task.Name} 已完成，耗时 {(session.EndTime!.Value - session.StartTime).ToString(@"hh\:mm\:ss")}"); }
        else if (queueToken.IsCancellationRequested) { events.Publish(new TaskStoppedEvent(session)); await log.WriteAsync(LogLevel.Warning, $"{task.Name} 已强制终止并完成清理"); }
        else if (timedOut) events.Publish(new TaskTimedOutEvent(session));
        else if (!timedOut) events.Publish(new TaskFailedEvent(session, error ?? session.ExitReason));
        if (screenshots is not null) await screenshots.CaptureEndAsync(session, CancellationToken.None);
        SessionChanged?.Invoke(session);
        return new(session, success, timedOut, error) { TargetStartupTimedOut = targetStartupTimedOut };
    }

    private async Task<bool> WaitForCompletionAsync(Process root, AutomationTaskConfig task, RuntimeSession session, Stopwatch elapsed, CancellationToken token)
    {
        if (task.CompletionMode == CompletionDetectionMode.Custom) throw new NotSupportedException("自定义完成检测接口已预留，第一版暂未实现");
        var logDetector = task.CompletionMode == CompletionDetectionMode.LogKeyword
            ? new LogKeywordCompletionDetector(task.CompletionLogPath, task.CompletionKeyword)
            : null;
        var failureDetector = task.CompletionMode == CompletionDetectionMode.LogKeyword && !string.IsNullOrWhiteSpace(task.CompletionFailureKeyword)
            ? new LogKeywordCompletionDetector(task.CompletionLogPath, task.CompletionFailureKeyword)
            : null;
        var target = Path.GetFileNameWithoutExtension(task.CompletionProcessName);
        // Process.Start already proves that the root was started, even if it exits
        // before the first scan. Child targets must actually be observed.
        var exitTracker = new ProcessExitCompletionTracker(string.Equals(
            Path.GetFileNameWithoutExtension(session.RootExecutablePath), target, StringComparison.OrdinalIgnoreCase));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            monitor.Scan(session, task); SessionChanged?.Invoke(session);
            if (task.CompletionMode == CompletionDetectionMode.MainProcessExit && root.HasExited) return false;
            if (logDetector is not null && await logDetector.CheckAsync(token)) return false;
            if (failureDetector is not null && await failureDetector.CheckAsync(token))
                throw new InvalidOperationException($"检测到任务失败日志：{task.CompletionFailureKeyword}");
            if (task.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit)
            {
                var matches = session.SnapshotTrackedProcesses().Where(p => string.Equals(p.ProcessName, target, StringComparison.OrdinalIgnoreCase)).ToList();
                var result = exitTracker.Check(matches.Count > 0, elapsed.Elapsed);
                if (result == ProcessExitCheck.Completed) return false;
                if (result == ProcessExitCheck.StartupTimedOut) return true;
            }
            await Task.Delay(700, token);
        }
    }
}
