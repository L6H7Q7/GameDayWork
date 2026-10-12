using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record KnownToolProfile(
    string Name, string ExecutablePattern, string Arguments, string CompletionProcessName,
    string LogPattern, string CompletionKeyword, string FailureKeyword, int MaxRunMinutes);

public sealed class KnownToolProfileService
{
    private static readonly KnownToolProfile[] Profiles =
    [
        new("BGI", "BGI\\BetterGI.exe", "--startOneDragon", "BetterGI.exe", "BGI\\log\\better-genshin-impact*.log", "一条龙和配置组任务结束", "一条龙在启动阶段被取消", 45),
        new("MAA", "MAA\\MAA-*-win-x64\\MAA.exe", "", "MAA.exe", "MAA\\MAA-*-win-x64\\debug\\gui.log", "任务已全部完成！", "", 45),
        new("ZOG", "ZOG\\OneDragon-Launcher.exe", "-o -c", "OneDragon-Launcher.exe", "ZOG\\.log\\log.txt", "指令[ 一条龙 ] 执行成功 返回状态 全部结束", "指令[ 一条龙 ] 执行失败", 45),
        new("MFA", "MAN\\MaaAutoNaruto-*\\MFAAvalonia.exe", "", "MFAAvalonia.exe", "MAN\\MaaAutoNaruto-*\\logs\\log-*.log", "任务已全部完成！", "停止前状态：FAILED", 45),
        new("M7A", "M7A\\March7thAssistant_full\\March7th Launcher.exe", "main -e", "March7th Assistant.exe", "M7A\\March7thAssistant_full\\logs\\*.log", "游戏终止：StarRail", "", 45)
    ];

    public AutomationTaskConfig CreateProfile(string type)
    {
        var installed = Discover().FirstOrDefault(task => task.ToolType == type);
        if (installed is not null) return installed;
        var profile = Profiles.First(item => item.Name == type);
        var task = new AutomationTaskConfig
        {
            Name = type, ToolType = type, Arguments = profile.Arguments,
            CompletionMode = CompletionDetectionMode.SpecifiedProcessExit,
            CompletionProcessName = profile.CompletionProcessName,
            CompletionKeyword = profile.CompletionKeyword,
            CompletionFailureKeyword = profile.FailureKeyword,
            MaxRunMinutes = profile.MaxRunMinutes, RunAsAdministrator = type == "M7A"
        };
        task.ProcessRules.Add(new ProcessRule { ProcessName = profile.CompletionProcessName, AllowNameFallback = true });
        return task;
    }

    public IReadOnlyList<AutomationTaskConfig> Discover()
    {
        var results = new List<AutomationTaskConfig>();
        foreach (var root in SearchRoots())
        foreach (var profile in Profiles)
        {
            if (results.Any(task => task.Name == profile.Name)) continue;
            var executable = profile.Name == "ZOG"
                ? ResolveLatest(root, "ZOG\\OneDragon-RuntimeLauncher.exe") ?? ResolveLatest(root, profile.ExecutablePattern)
                : ResolveLatest(root, profile.ExecutablePattern);
            if (executable is null) continue;
            var logPattern = ResolvePattern(root, profile.LogPattern);
            var task = new AutomationTaskConfig
            {
                Name = profile.Name,
                ToolType = profile.Name,
                ProgramPath = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
                Arguments = profile.Arguments,
                CompletionMode = CompletionDetectionMode.SpecifiedProcessExit,
                CompletionProcessName = profile.Name == "ZOG" ? Path.GetFileName(executable) : profile.CompletionProcessName,
                CompletionLogPath = logPattern,
                CompletionKeyword = profile.CompletionKeyword,
                CompletionFailureKeyword = profile.FailureKeyword,
                RunAsAdministrator = profile.Name == "M7A",
                MaxRunMinutes = profile.MaxRunMinutes,
                Description = $"已适配的 {profile.Name} 自动化任务。"
            };
            task.ProcessRules.Add(new ProcessRule { ProcessName = task.CompletionProcessName, ExecutableDirectory = task.WorkingDirectory, Monitor = true, Cleanup = true, AllowNameFallback = true });
            results.Add(task);
        }
        return results;
    }

    public static void ApplyRecommendedSettings(AutomationTaskConfig target, AutomationTaskConfig profile)
    {
        target.Name = profile.Name;
        target.ToolType = profile.ToolType;
        target.ProgramPath = profile.ProgramPath;
        target.WorkingDirectory = profile.WorkingDirectory;
        target.Arguments = profile.Arguments;
        target.CompletionMode = profile.CompletionMode;
        target.CompletionProcessName = profile.CompletionProcessName;
        target.CompletionLogPath = profile.CompletionLogPath;
        target.CompletionKeyword = profile.CompletionKeyword;
        target.CompletionFailureKeyword = profile.CompletionFailureKeyword;
        target.MaxRunMinutes = profile.MaxRunMinutes;
        target.TrackChildren = true;
        target.UseJobObject = true;
        target.RunAsAdministrator = profile.RunAsAdministrator;
        if (string.IsNullOrWhiteSpace(target.Description) || target.Description.StartsWith("自动化日常任务", StringComparison.Ordinal))
            target.Description = profile.Description;

        var directoryRule = target.ProcessRules.FirstOrDefault(rule => IsGeneratedDirectoryRule(rule) ||
            string.Equals(Path.GetFileNameWithoutExtension(rule.ProcessName), Path.GetFileNameWithoutExtension(profile.CompletionProcessName), StringComparison.OrdinalIgnoreCase));
        if (directoryRule is null)
            target.ProcessRules.Add(new ProcessRule { ProcessName = profile.CompletionProcessName, ExecutableDirectory = target.WorkingDirectory, Monitor = true, Cleanup = true, AllowNameFallback = true });
        else
        {
            directoryRule.ProcessName = profile.CompletionProcessName;
            directoryRule.ExecutableDirectory = target.WorkingDirectory;
            directoryRule.Monitor = true;
            directoryRule.Cleanup = true;
            directoryRule.AllowNameFallback = true;
        }
    }

    private static bool IsGeneratedDirectoryRule(ProcessRule rule) =>
        string.IsNullOrWhiteSpace(rule.ProcessName) &&
        string.IsNullOrWhiteSpace(rule.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(rule.ExecutableDirectory) &&
        rule.Monitor && rule.Cleanup && !rule.AllowNameFallback;

    public static bool NormalizeZogCompletion(AutomationTaskConfig task)
    {
        if (!string.Equals(task.ToolType, "ZOG", StringComparison.OrdinalIgnoreCase)
            || task.CompletionMode != CompletionDetectionMode.SpecifiedProcessExit) return false;
        var executable = Path.GetFileName(task.ProgramPath);
        static bool IsLauncher(string name) =>
            string.Equals(Path.GetFileNameWithoutExtension(name), "OneDragon-Launcher", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(name), "OneDragon-RuntimeLauncher", StringComparison.OrdinalIgnoreCase);
        // Preserve user-selected child targets and other completion modes.
        if (!IsLauncher(executable) || (!string.IsNullOrWhiteSpace(task.CompletionProcessName) && !IsLauncher(task.CompletionProcessName))) return false;
        var changed = !string.Equals(task.CompletionProcessName, executable, StringComparison.OrdinalIgnoreCase);
        task.CompletionProcessName = executable;
        foreach (var rule in task.ProcessRules.Where(rule => IsLauncher(rule.ProcessName)))
        {
            if (!string.Equals(rule.ProcessName, executable, StringComparison.OrdinalIgnoreCase)) changed = true;
            rule.ProcessName = executable;
        }
        return changed;
    }

    private static IEnumerable<string> SearchRoots()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        foreach (var name in new[] { "Tool", "Tools" })
        {
            var path = Path.Combine(drive.RootDirectory.FullName, name);
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? ResolveLatest(string root, string relativePattern)
    {
        var directoryPattern = Path.GetDirectoryName(relativePattern) ?? "";
        var filePattern = Path.GetFileName(relativePattern);
        var directories = ExpandDirectories(root, directoryPattern);
        return directories.SelectMany(directory => Directory.EnumerateFiles(directory, filePattern, SearchOption.TopDirectoryOnly))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string ResolvePattern(string root, string relativePattern)
    {
        var directoryPattern = Path.GetDirectoryName(relativePattern) ?? "";
        var filePattern = Path.GetFileName(relativePattern);
        var directory = ExpandDirectories(root, directoryPattern).FirstOrDefault();
        return directory is null ? Path.Combine(root, relativePattern) : Path.Combine(directory, filePattern);
    }

    private static IEnumerable<string> ExpandDirectories(string root, string relativePattern)
    {
        IEnumerable<string> current = [root];
        foreach (var segment in relativePattern.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            current = current.SelectMany(parent => Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, segment, SearchOption.TopDirectoryOnly)
                : []);
        }
        return current;
    }
}
