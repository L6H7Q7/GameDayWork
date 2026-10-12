using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record TaskValidationIssue(Guid TaskId, string TaskName, string Message);

public sealed class TaskValidationService
{
    public IReadOnlyList<TaskValidationIssue> Validate(IEnumerable<AutomationTaskConfig> tasks)
    {
        var issues = new List<TaskValidationIssue>();
        foreach (var task in tasks)
        {
            void Add(string message) => issues.Add(new(task.Id, string.IsNullOrWhiteSpace(task.Name) ? "未命名任务" : task.Name, message));
            if (string.IsNullOrWhiteSpace(task.Name)) Add("任务名称不能为空");
            if (string.IsNullOrWhiteSpace(task.ProgramPath)) Add("尚未设置程序路径");
            else if (!File.Exists(task.ProgramPath)) Add($"程序不存在：{task.ProgramPath}");
            if (!string.IsNullOrWhiteSpace(task.WorkingDirectory) && !Directory.Exists(task.WorkingDirectory)) Add($"工作目录不存在：{task.WorkingDirectory}");
            if (task.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit && string.IsNullOrWhiteSpace(task.CompletionProcessName)) Add("指定进程退出模式需要填写进程名称");
            if (task.CompletionMode == CompletionDetectionMode.LogKeyword)
            {
                if (string.IsNullOrWhiteSpace(task.CompletionLogPath)) Add("日志关键字模式需要填写日志文件路径");
                else
                {
                    try
                    {
                        if (!LogKeywordCompletionDetector.HasMatchingFile(task.CompletionLogPath)) Add($"没有找到匹配的完成检测日志：{task.CompletionLogPath}");
                    }
                    catch (Exception ex) { Add($"完成检测日志路径无效：{ex.Message}"); }
                }
                if (string.IsNullOrWhiteSpace(task.CompletionKeyword)) Add("日志关键字模式需要填写完成关键字");
            }
            if (task.CompletionMode == CompletionDetectionMode.Custom) Add("自定义完成检测尚未实现");
            if (task.MaxRunMinutes < 1) Add("最大运行时间必须大于 0 分钟");
            if (!task.IsGroupChild && !string.IsNullOrWhiteSpace(task.ScheduledStartTime)
                && !SchedulerService.TryParseTime(task.ScheduledStartTime, out _)) Add("定时启动时间格式应为 HH:mm，例如 08:00");
            if (task.CleanupWaitSeconds < 1) Add("清理等待时间必须大于 0 秒");
            if (task.CleanupRetries < 1) Add("清理重试次数必须大于 0");
            foreach (var rule in task.ProcessRules)
                if (string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.ExecutablePath) && string.IsNullOrWhiteSpace(rule.ExecutableDirectory)) Add("存在未填写进程名、路径或目录的空进程规则");
                else if (!string.IsNullOrWhiteSpace(rule.ExecutableDirectory) && !Directory.Exists(rule.ExecutableDirectory)) Add($"进程规则目录不存在：{rule.ExecutableDirectory}");
        }
        return issues;
    }
}
