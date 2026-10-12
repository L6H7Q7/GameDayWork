using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using GameOrchestrator.Infrastructure;

namespace GameOrchestrator.Models;

public sealed class AutomationTaskConfig : ObservableObject
{
    private string _name = "新任务";
    private bool _enabled = true;
    private string _programPath = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private CompletionDetectionMode _completionMode = CompletionDetectionMode.MainProcessExit;
    private string _completionProcessName = "";
    private string _completionLogPath = "";
    private string _completionKeyword = "";
    private string _completionFailureKeyword = "";
    private string _description = "";
    private int _maxRunMinutes = 45;
    private int _cleanupWaitSeconds = 3;
    private int _cleanupRetries = 3;
    private bool _runAsAdministrator;
    private string _scheduledStartTime = "";
    private TaskCompletionAction _completionAction = TaskCompletionAction.None;
    private string _toolType = "自定义任务";
    private bool _isExpanded;
    private int _groupTaskIntervalSeconds = 5;
    private TaskRunStatus _status = TaskRunStatus.Idle;
    private int _displayIndex;

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsGroup { get; set; }
    public ObservableCollection<AutomationTaskConfig> Children { get; set; } = [];
    public int GroupTaskIntervalSeconds { get => _groupTaskIntervalSeconds; set => SetProperty(ref _groupTaskIntervalSeconds, Math.Max(0, value)); }
    [JsonIgnore] public int? IntervalAfterSeconds { get; internal set; }
    public string ToolType { get => _toolType; set { if (SetProperty(ref _toolType, value)) { OnPropertyChanged(nameof(RepositoryUrl)); OnPropertyChanged(nameof(IconText)); } } }
    [JsonIgnore] public bool IsExpanded { get => _isExpanded; set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(GroupIndicator)); } }
    [JsonIgnore] public string GroupIndicator => IsGroup ? (IsExpanded ? "▾" : "▸") : "";
    [JsonIgnore] public bool IsGroupChild { get; internal set; }
    [JsonIgnore] public string GroupScheduledStartTime { get; internal set; } = "";
    [JsonIgnore] public string ScheduleLabel => $"下次运行时间：{NextExecutionText}";
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(IconText));
            OnPropertyChanged(nameof(RepositoryUrl));
        }
    }
    [JsonIgnore] public string IconText => IsGroup ? "组" : string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string RepositoryUrl
    {
        get
        {
            var name = ToolType.Trim().ToUpperInvariant();
            if (name.StartsWith("BGI", StringComparison.Ordinal)) return "https://github.com/babalae/better-genshin-impact/releases";
            if (name.StartsWith("MAA", StringComparison.Ordinal) || name.StartsWith("MMA", StringComparison.Ordinal)) return "https://github.com/MaaAssistantArknights/MaaAssistantArknights/releases";
            if (name.StartsWith("ZOG", StringComparison.Ordinal)) return "https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon/releases";
            if (name.StartsWith("MFA", StringComparison.Ordinal) || name.StartsWith("MAN", StringComparison.Ordinal)) return "https://github.com/duorua/narutomobile/releases";
            if (name.StartsWith("M7A", StringComparison.Ordinal)) return "https://github.com/moesnow/March7thAssistant/releases";
            return "";
        }
    }
    [JsonIgnore] public int DisplayIndex { get => _displayIndex; internal set => SetProperty(ref _displayIndex, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string ProgramPath { get => _programPath; set { if (SetProperty(ref _programPath, value)) WorkingDirectory = Path.GetDirectoryName(value) ?? ""; } }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public CompletionDetectionMode CompletionMode { get => _completionMode; set => SetProperty(ref _completionMode, value); }
    public string CompletionProcessName { get => _completionProcessName; set => SetProperty(ref _completionProcessName, value); }
    public string CompletionLogPath { get => _completionLogPath; set => SetProperty(ref _completionLogPath, value); }
    public string CompletionKeyword { get => _completionKeyword; set => SetProperty(ref _completionKeyword, value); }
    public string CompletionFailureKeyword { get => _completionFailureKeyword; set => SetProperty(ref _completionFailureKeyword, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public int MaxRunMinutes { get => _maxRunMinutes; set => SetProperty(ref _maxRunMinutes, Math.Max(1, value)); }
    public int CleanupWaitSeconds { get => _cleanupWaitSeconds; set => SetProperty(ref _cleanupWaitSeconds, Math.Max(1, value)); }
    public int CleanupRetries { get => _cleanupRetries; set => SetProperty(ref _cleanupRetries, Math.Max(1, value)); }
    public bool RunAsAdministrator { get => _runAsAdministrator; set => SetProperty(ref _runAsAdministrator, value); }
    public string ScheduledStartTime
    {
        get => _scheduledStartTime;
        set
        {
            if (SetProperty(ref _scheduledStartTime, value?.Trim() ?? "")) RefreshNextExecutionText();
        }
    }
    public TaskCompletionAction CompletionAction { get => _completionAction; set => SetProperty(ref _completionAction, value); }
    public bool TrackChildren { get; set; } = true;
    public bool UseJobObject { get; set; } = true;
    public ObservableCollection<ProcessRule> ProcessRules { get; set; } = [];
    [JsonIgnore] public TaskRunStatus Status { get => _status; set { if (SetProperty(ref _status, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsActive)); } } }
    [JsonIgnore] public bool IsActive => Status is TaskRunStatus.Starting or TaskRunStatus.Running or TaskRunStatus.CompletionDetected or TaskRunStatus.Cleaning or TaskRunStatus.CleanupVerifying;
    [JsonIgnore] public string NextExecutionText
    {
        get
        {
            if (!TimeSpan.TryParse(IsGroupChild ? GroupScheduledStartTime : ScheduledStartTime, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return "未定时";
            var now = DateTime.Now;
            var next = now.Date + time;
            if (next <= now) next = next.AddDays(1);
            return next.Date == now.Date ? $"今天 {next:HH:mm}" : $"明天 {next:HH:mm}";
        }
    }
    internal void RefreshNextExecutionText() { OnPropertyChanged(nameof(NextExecutionText)); OnPropertyChanged(nameof(ScheduleLabel)); }
    [JsonIgnore] public string StatusText => Status switch
    {
        TaskRunStatus.Idle => "等待执行", TaskRunStatus.Waiting => "等待执行", TaskRunStatus.Starting => "正在启动",
        TaskRunStatus.Running => "正在运行", TaskRunStatus.CompletionDetected => "已检测完成", TaskRunStatus.Cleaning => "正在清理",
        TaskRunStatus.CleanupVerifying => "正在确认清理", TaskRunStatus.Completed => "已完成", TaskRunStatus.Failed => "执行失败",
        TaskRunStatus.TimedOut => "超时", TaskRunStatus.Skipped => "已跳过", TaskRunStatus.Stopped => "已停止", _ => Status.ToString()
    };
}

public sealed class ProcessRule : ObservableObject
{
    private string _processName = "";
    private string _executablePath = "";
    private string _executableDirectory = "";
    private bool _monitor = true;
    private bool _cleanup = true;
    private bool _allowNameFallback;
    public string ProcessName { get => _processName; set => SetProperty(ref _processName, value); }
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
    public string ExecutableDirectory { get => _executableDirectory; set => SetProperty(ref _executableDirectory, value); }
    public bool Monitor { get => _monitor; set => SetProperty(ref _monitor, value); }
    public bool Cleanup { get => _cleanup; set => SetProperty(ref _cleanup, value); }
    public bool AllowNameFallback { get => _allowNameFallback; set => SetProperty(ref _allowNameFallback, value); }
}

public sealed class ScheduleConfig : ObservableObject
{
    private bool _enabled;
    private ScheduleRepeat _repeat = ScheduleRepeat.Daily;
    private TimeSpan _time = new(3, 0, 0);
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public ScheduleRepeat Repeat { get => _repeat; set => SetProperty(ref _repeat, value); }
    public TimeSpan Time { get => _time; set => SetProperty(ref _time, value); }
    public HashSet<DayOfWeek> SelectedDays { get; set; } = [];
}

public sealed class NotificationConfig
{
    public bool Enabled { get; set; }
    [JsonPropertyName("weComWebhookUrl")]
    public string WeComWebhookUrl { get; set; } = "";
    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnTimeout { get; set; } = true;
    [JsonPropertyName("notifyOnForcedStop")]
    public bool NotifyOnForcedStop { get; set; } = true;
    public bool CaptureOnStart { get; set; }
    public bool CaptureOnComplete { get; set; }
    public bool CaptureOnFailure { get; set; }
    public bool CaptureOnTimeout { get; set; }
    [JsonPropertyName("captureTaskScreenshots")]
    public bool CaptureTaskScreenshots { get; set; }
    [JsonPropertyName("runningScreenshotDelaySeconds")]
    public int RunningScreenshotDelaySeconds { get; set; } = 120;
}

public sealed class AppConfig
{
    [JsonPropertyName("configSchemaVersion")]
    public int ConfigSchemaVersion { get; set; }
    public ObservableCollection<AutomationTaskConfig> Tasks { get; set; } = [];
    public QueueExecutionMode ExecutionMode { get; set; } = QueueExecutionMode.Sequential;
    public int TaskIntervalSeconds { get; set; } = 5;
    public FailurePolicy FailurePolicy { get; set; } = FailurePolicy.ForceCleanupAndContinue;
    public bool GenerateExecutionLog { get; set; } = true;
    public ScheduleConfig Schedule { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }
    [JsonPropertyName("useSystemService")]
    public bool UseSystemService { get; set; }
    [JsonPropertyName("lockScreenDisplayTimeoutEnabled")]
    public bool LockScreenDisplayTimeoutEnabled { get; set; }
    [JsonPropertyName("lockScreenDisplayTimeoutAcSeconds")]
    public int LockScreenDisplayTimeoutAcSeconds { get; set; } = 60;
    [JsonPropertyName("lockScreenDisplayTimeoutDcSeconds")]
    public int LockScreenDisplayTimeoutDcSeconds { get; set; } = 30;
    [JsonPropertyName("startMinimizedToTray")]
    public bool StartMinimizedToTray { get; set; } = true;
    [JsonPropertyName("enableScreenManager")]
    public bool EnableScreenManager { get; set; } = true;
    [JsonPropertyName("idleTimeoutMinutes")]
    public int IdleTimeoutMinutes { get; set; } = 5;
    [JsonPropertyName("wakeBeforeTaskSeconds")]
    public int WakeBeforeTaskSeconds { get; set; } = 30;
    [JsonPropertyName("autoBlackoutAfterTask")]
    public bool AutoBlackoutAfterTask { get; set; }
}
