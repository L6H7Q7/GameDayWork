using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.Json;
using GameOrchestrator.Events;
using GameOrchestrator.Infrastructure;
using GameOrchestrator.Models;
using GameOrchestrator.Services;

namespace GameOrchestrator.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _configService = new();
    private readonly StartupService _startupService = new();
    private readonly LoggingService _log = new();
    private readonly SchedulerService _scheduler = new();
    private readonly TaskValidationService _validator = new();
    private readonly KnownToolProfileService _toolProfiles = new();
    private readonly TaskQueueService _queue;
    private readonly BrightnessManager _brightnessManager;
    private readonly ScreenManager _screenManager;
    private readonly TaskLaunchCoordinator _launchCoordinator;
    private readonly WeComNotificationService _notificationService;
    private readonly TaskScreenshotCoordinator _screenshotCoordinator;
    private AppConfig _config = new();
    private AutomationTaskConfig? _selectedTask;
    private AutomationTaskConfig? _editingTask;
    private AutomationTaskConfig? _editingTaskTarget;
    private string? _taskEditSnapshot;
    private RuntimeSession? _currentSession;
    private string _statusText = "准备就绪";
    private string _elapsed = "00:00:00";
    private readonly DispatcherTimer _uiTimer;
    private bool _serviceConfiguredAtLoad;
    private string _appliedServiceSettings = "";
    private bool _appliedStartupEnabled;
    private string _autoSaveMessage = "";
    public string AutoSaveMessage { get => _autoSaveMessage; set => SetProperty(ref _autoSaveMessage, value); }

    public ObservableCollection<AutomationTaskConfig> Tasks => _config.Tasks;
    public ObservableCollection<AutomationTaskConfig> VisibleTasks { get; } = [];
    public IReadOnlyList<string> ToolTypes { get; } = ["BGI", "MAA", "ZOG", "MFA", "M7A", "自定义任务"];
    public ObservableCollection<AutomationTaskConfig> ContainerOf(AutomationTaskConfig task) =>
        Tasks.FirstOrDefault(root => root.IsGroup && root.Children.Contains(task))?.Children ?? Tasks;
    public void RefreshVisibleTasks()
    {
        var selected = SelectedTask;
        VisibleTasks.Clear();
        foreach (var root in Tasks)
        {
            root.IsGroupChild = false;
            VisibleTasks.Add(root);
            foreach (var child in root.Children)
            {
                child.IsGroupChild = true;
                child.GroupScheduledStartTime = root.ScheduledStartTime;
                child.RefreshNextExecutionText();
                if (root.IsExpanded) VisibleTasks.Add(child);
            }
        }
        SelectedTask = selected;
        OnPropertyChanged(nameof(SelectedTask));
    }
    public void ToggleGroup(AutomationTaskConfig group)
    {
        group.IsExpanded = !group.IsExpanded;
        RefreshVisibleTasks();
    }
    public void ApplyToolType(string type)
    {
        if (EditingTask is not { IsGroup: false } draft || draft.ToolType == type) return;
        if (type != "自定义任务")
        {
            var profile = _toolProfiles.CreateProfile(type);
            draft.ProcessRules.Clear();
            KnownToolProfileService.ApplyRecommendedSettings(draft, profile);
        }
        else
        {
            draft.CompletionMode = CompletionDetectionMode.MainProcessExit;
            draft.CompletionProcessName = "";
            draft.CompletionLogPath = "";
            draft.CompletionKeyword = "";
            draft.CompletionFailureKeyword = "";
            draft.RunAsAdministrator = false;
            draft.ProcessRules.Clear();
        }
        draft.ToolType = type;
    }
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public AutomationTaskConfig? SelectedTask { get => _selectedTask; set { if (SetProperty(ref _selectedTask, value)) RaiseCommandStates(); } }
    public AutomationTaskConfig? EditingTask { get => _editingTask; private set => SetProperty(ref _editingTask, value); }
    public AutomationTaskConfig? EditingTaskTarget => _editingTaskTarget;
    public bool HasTaskEdits => EditingTask is not null && JsonSerializer.Serialize(EditingTask) != _taskEditSnapshot;
    public RuntimeSession? CurrentSession { get => _currentSession; private set { if (SetProperty(ref _currentSession, value)) RaiseRuntimeProperties(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string NextRunText => _scheduler.NextRun is { } next ? next.ToString("yyyy/MM/dd HH:mm") : "未启用";
    public string NextTaskNames => _scheduler.NextRun is { } next
        ? string.Join("、", Tasks.Where(task => task.Enabled && SchedulerService.TryParseTime(task.ScheduledStartTime, out var time) && time == next.TimeOfDay).Select(task => task.Name))
        : "尚未设置定时任务，点击查看任务列表";
    public int TaskIntervalSeconds { get => _config.TaskIntervalSeconds; set { _config.TaskIntervalSeconds = Math.Max(0, value); OnPropertyChanged(); } }
    public FailurePolicy FailurePolicy { get => _config.FailurePolicy; set { _config.FailurePolicy = value; OnPropertyChanged(); } }
    public QueueExecutionMode ExecutionMode { get => _config.ExecutionMode; set { _config.ExecutionMode = value; OnPropertyChanged(); } }
    public string WeComWebhookUrl
    {
        get => _config.Notifications.WeComWebhookUrl;
        set
        {
            if (_config.Notifications.WeComWebhookUrl == value) return;
            _config.Notifications.WeComWebhookUrl = value ?? "";
            _config.Notifications.Enabled = !string.IsNullOrWhiteSpace(value);
            OnPropertyChanged();
        }
    }
    public bool NotifyOnStart { get => _config.Notifications.NotifyOnStart; set { _config.Notifications.NotifyOnStart = value; OnPropertyChanged(); } }
    public bool NotifyOnComplete { get => _config.Notifications.NotifyOnComplete; set { _config.Notifications.NotifyOnComplete = value; OnPropertyChanged(); } }
    public bool NotifyOnFailure
    {
        get => _config.Notifications.NotifyOnFailure;
        set
        {
            _config.Notifications.NotifyOnFailure = value;
            OnPropertyChanged();
        }
    }
    public bool NotifyOnForcedStop
    {
        get => _config.Notifications.NotifyOnForcedStop;
        set
        {
            _config.Notifications.NotifyOnForcedStop = value;
            _config.Notifications.NotifyOnTimeout = value;
            OnPropertyChanged();
        }
    }
    public bool CaptureTaskScreenshots
    {
        get => _config.Notifications.CaptureTaskScreenshots;
        set { _config.Notifications.CaptureTaskScreenshots = value; OnPropertyChanged(); }
    }
    public int RunningScreenshotDelaySeconds
    {
        get => _config.Notifications.RunningScreenshotDelaySeconds;
        set
        {
            var normalized = Math.Clamp(value, 1, 3600);
            if (_config.Notifications.RunningScreenshotDelaySeconds == normalized) return;
            _config.Notifications.RunningScreenshotDelaySeconds = normalized;
            OnPropertyChanged();
        }
    }
    public bool GenerateExecutionLog
    {
        get => _config.GenerateExecutionLog;
        set
        {
            _config.GenerateExecutionLog = value;
            _log.FileLoggingEnabled = value;
            OnPropertyChanged();
        }
    }
    public bool StartWithWindows
    {
        get => _config.StartWithWindows;
        set
        {
            if (_config.StartWithWindows == value) return;
            _config.StartWithWindows = value;
            if (value && _config.UseSystemService)
            {
                _config.UseSystemService = false;
                _config.LockScreenDisplayTimeoutEnabled = false;
                OnPropertyChanged(nameof(UseSystemService));
                OnPropertyChanged(nameof(LockScreenDisplayTimeoutEnabled));
            }
            OnPropertyChanged();
        }
    }
    public bool UseSystemService
    {
        get => _config.UseSystemService;
        set
        {
            if (_config.UseSystemService == value) return;
            _config.UseSystemService = value;
            if (!value && _config.LockScreenDisplayTimeoutEnabled)
            {
                _config.LockScreenDisplayTimeoutEnabled = false;
                OnPropertyChanged(nameof(LockScreenDisplayTimeoutEnabled));
            }
            if (value && _config.StartWithWindows) { _config.StartWithWindows = false; OnPropertyChanged(nameof(StartWithWindows)); }
            OnPropertyChanged();
        }
    }
    public bool LockScreenDisplayTimeoutEnabled { get => _config.LockScreenDisplayTimeoutEnabled; set { _config.LockScreenDisplayTimeoutEnabled = value; OnPropertyChanged(); } }
    public int LockScreenDisplayTimeoutAcSeconds { get => _config.LockScreenDisplayTimeoutAcSeconds; set { _config.LockScreenDisplayTimeoutAcSeconds = Math.Clamp(value, 10, 3600); OnPropertyChanged(); } }
    public int LockScreenDisplayTimeoutDcSeconds { get => _config.LockScreenDisplayTimeoutDcSeconds; set { _config.LockScreenDisplayTimeoutDcSeconds = Math.Clamp(value, 10, 3600); OnPropertyChanged(); } }
    public bool StartMinimizedToTray
    {
        get => _config.StartMinimizedToTray;
        set
        {
            if (_config.StartMinimizedToTray == value) return;
            _config.StartMinimizedToTray = value;
            OnPropertyChanged();
        }
    }
    public bool EnableScreenManager
    {
        get => _config.EnableScreenManager;
        set
        {
            if (_config.EnableScreenManager == value) return;
            _config.EnableScreenManager = value;
            _screenManager.Enabled = value;
            OnPropertyChanged();
        }
    }
    public int IdleTimeoutMinutes
    {
        get => _config.IdleTimeoutMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 1440);
            if (_config.IdleTimeoutMinutes == normalized) return;
            _config.IdleTimeoutMinutes = normalized;
            _screenManager.IdleTimeout = TimeSpan.FromMinutes(normalized);
            OnPropertyChanged();
        }
    }
    public int WakeBeforeTaskSeconds
    {
        get => _config.WakeBeforeTaskSeconds;
        set
        {
            var normalized = Math.Clamp(value, 0, 3600);
            if (_config.WakeBeforeTaskSeconds == normalized) return;
            _config.WakeBeforeTaskSeconds = normalized;
            OnPropertyChanged();
        }
    }
    public Array FailurePolicies => Enum.GetValues(typeof(FailurePolicy));
    public Array CompletionModes => Enum.GetValues(typeof(CompletionDetectionMode));
    public Array CompletionActions => Enum.GetValues(typeof(TaskCompletionAction));
    public bool HasActiveTask => CurrentSession?.Status is TaskRunStatus.Starting or TaskRunStatus.Running or TaskRunStatus.CompletionDetected or TaskRunStatus.Cleaning or TaskRunStatus.CleanupVerifying;
    public string CurrentTaskName => HasActiveTask ? CurrentSession!.TaskName : "—";
    public string CurrentStage => HasActiveTask ? CurrentSession!.Status.ToString().ToUpperInvariant() : "IDLE";
    public string RootPid => HasActiveTask && CurrentSession!.RootPid > 0 ? CurrentSession.RootPid.ToString() : "—";
    public int RelatedCount => HasActiveTask ? CurrentSession!.TrackedProcessCount : 0;
    public string CurrentStatusHeadline => HasActiveTask ? CurrentSession!.TaskName : "暂无任务运行";
    public string CurrentStatusHint => CurrentSession?.Status switch
    {
        TaskRunStatus.Starting => "正在启动任务",
        TaskRunStatus.Running => "任务正在运行",
        TaskRunStatus.CompletionDetected => "已检测完成，准备清理",
        TaskRunStatus.Cleaning => "正在清理关联进程",
        TaskRunStatus.CleanupVerifying => "正在确认清理结果",
        _ => "进入任务列表选择任务后点击「开始」，或等待定时执行"
    };
    public string RecentEvent => Logs.LastOrDefault()?.Message ?? "进入任务列表选择任务，或等待定时执行";

    public string ManualActionText => _launchCoordinator.IsBusy ? "■  结束" : "▶  开始";
    public bool IsExecutionBusy => _launchCoordinator.IsBusy;
    public ScreenManagerState ScreenState => _screenManager.State;
    public string ScreenStateText => ScreenState switch
    {
        ScreenManagerState.IdleMonitoring => "空闲监控",
        ScreenManagerState.Blackout => "假息屏中",
        ScreenManagerState.PreparingTask => "准备任务",
        ScreenManagerState.RunningTask => "执行任务",
        _ => ScreenState.ToString()
    };
    public ICommand ToggleExecutionCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand DuplicateTaskCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ResetTaskCommand { get; }
    public ICommand ImportToolsCommand { get; }
    public event Action<string>? ValidationFailed;
    public event Action<string>? NoticeRequested;
    public event Func<bool>? HideToTrayRequested;

    public MainViewModel()
    {
        var bus = new TaskEventBus();
        var monitor = new ProcessMonitorService();
        var cleanup = new ProcessCleanupService(monitor, _log);
        _notificationService = new WeComNotificationService(bus, () => _config.Notifications, _log);
        _screenshotCoordinator = new TaskScreenshotCoordinator(bus, new DesktopScreenshotService(), _notificationService,
            () => _config.Notifications, _log, PrepareForScreenshotAsync);
        var runner = new TaskRunnerService(monitor, cleanup, _log, bus, _screenshotCoordinator);
        _brightnessManager = new BrightnessManager(_log);
        _screenManager = new ScreenManager(_log, _brightnessManager);
        _queue = new TaskQueueService(runner, _log, bus);
        _launchCoordinator = new TaskLaunchCoordinator(_screenManager, _queue, _log);
        _scheduler.Error += ex => _ = _log.WriteAsync(LogLevel.Error, $"定时任务执行异常：{ex.Message}");
        runner.SessionChanged += session => Application.Current.Dispatcher.Invoke(() => CurrentSession = session);
        _log.EntryWritten += entry => Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); if (Logs.Count > 2000) Logs.RemoveAt(0); OnPropertyChanged(nameof(RecentEvent)); });
        _queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskQueueService.Status)) Application.Current.Dispatcher.Invoke(UpdateQueueStatus); };
        _screenManager.StateChanged += _ => Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ScreenState));
            OnPropertyChanged(nameof(ScreenStateText));
        });
        _launchCoordinator.BusyChanged += _ => Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ManualActionText));
            OnPropertyChanged(nameof(IsExecutionBusy));
            RaiseCommandStates();
        });
        ToggleExecutionCommand = new RelayCommand(ToggleSelectedTask, () => _launchCoordinator.IsBusy || SelectedTask is not null);
        DeleteTaskCommand = new RelayCommand(DeleteTask, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        DuplicateTaskCommand = new RelayCommand(DuplicateTask, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        AddRuleCommand = new RelayCommand(() => (EditingTask ?? SelectedTask)?.ProcessRules.Add(new ProcessRule { ProcessName = "Process.exe" }));
        DeleteRuleCommand = new RelayCommand(() => { var task = EditingTask ?? SelectedTask; if (task?.ProcessRules.Count > 0) task.ProcessRules.RemoveAt(task.ProcessRules.Count - 1); });
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ResetTaskCommand = new AsyncRelayCommand(ResetSelectedTaskAsync, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        ImportToolsCommand = new AsyncRelayCommand(ApplyKnownToolsAsync, () => !_launchCoordinator.IsBusy);
        _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickUi(), Application.Current.Dispatcher);
    }

    public async Task InitializeAsync()
    {
        _config = await _configService.LoadAsync();
        _serviceConfiguredAtLoad = _config.UseSystemService;
        _appliedServiceSettings = ServiceSettingsSignature();
        if (_config.UseSystemService && ServiceManagementClient.NeedsRepair()) _appliedServiceSettings = "";
        _config.Notifications ??= new NotificationConfig();
        _config.Notifications.Enabled = !string.IsNullOrWhiteSpace(_config.Notifications.WeComWebhookUrl);
        _config.Notifications.RunningScreenshotDelaySeconds = Math.Clamp(_config.Notifications.RunningScreenshotDelaySeconds, 1, 3600);
        _config.ExecutionMode = QueueExecutionMode.Sequential;
        _config.AutoBlackoutAfterTask = false;
        _config.IdleTimeoutMinutes = Math.Clamp(_config.IdleTimeoutMinutes, 1, 1440);
        _config.WakeBeforeTaskSeconds = Math.Clamp(_config.WakeBeforeTaskSeconds, 0, 3600);
        _screenManager.Enabled = _config.EnableScreenManager;
        _screenManager.IdleTimeout = TimeSpan.FromMinutes(_config.IdleTimeoutMinutes);
        _log.FileLoggingEnabled = _config.GenerateExecutionLog;
        var logCleanup = await _log.CleanupAsync();
        if (logCleanup.DeletedFiles > 0)
            await _log.WriteAsync(LogLevel.Info, $"日志自动清理完成：删除 {logCleanup.DeletedFiles} 个文件，释放 {logCleanup.FreedBytes / 1024d / 1024d:F1} MB");
        if (logCleanup.FailedFiles > 0)
            await _log.WriteAsync(LogLevel.Warning, $"日志自动清理有 {logCleanup.FailedFiles} 个文件无法删除");
        try
        {
            _startupService.Apply(_config.StartWithWindows && !_config.UseSystemService);
            _appliedStartupEnabled = _config.StartWithWindows && !_config.UseSystemService;
        }
        catch (Exception ex) { await _log.WriteAsync(LogLevel.Warning, $"同步开机启动项失败：{ex.Message}"); }
        await _screenManager.RecoverDisplayStateAsync();
        Tasks.CollectionChanged += (_, _) => RefreshTaskIndexes();
        RefreshTaskIndexes();
        OnPropertyChanged(nameof(Tasks)); OnPropertyChanged(nameof(TaskIntervalSeconds)); OnPropertyChanged(nameof(FailurePolicy)); OnPropertyChanged(nameof(GenerateExecutionLog)); OnPropertyChanged(nameof(StartWithWindows)); OnPropertyChanged(nameof(StartMinimizedToTray));
        OnPropertyChanged(nameof(EnableScreenManager)); OnPropertyChanged(nameof(IdleTimeoutMinutes)); OnPropertyChanged(nameof(WakeBeforeTaskSeconds));
        OnPropertyChanged(nameof(UseSystemService)); OnPropertyChanged(nameof(LockScreenDisplayTimeoutEnabled)); OnPropertyChanged(nameof(LockScreenDisplayTimeoutAcSeconds)); OnPropertyChanged(nameof(LockScreenDisplayTimeoutDcSeconds));
        OnPropertyChanged(nameof(WeComWebhookUrl)); OnPropertyChanged(nameof(NotifyOnStart)); OnPropertyChanged(nameof(NotifyOnComplete)); OnPropertyChanged(nameof(NotifyOnFailure)); OnPropertyChanged(nameof(NotifyOnForcedStop)); OnPropertyChanged(nameof(CaptureTaskScreenshots)); OnPropertyChanged(nameof(RunningScreenshotDelaySeconds));
        SelectedTask = Tasks.FirstOrDefault();
        _screenManager.StartMonitoring();
        _scheduler.Start(() => Tasks.ToList(), () => _config.WakeBeforeTaskSeconds, RunScheduledTasksAsync);
        OnPropertyChanged(nameof(NextRunText));
        await _log.WriteAsync(LogLevel.Info, $"程序启动，已加载任务队列（{Tasks.Count} 项）");
    }
    public async Task SaveSettingsAsync()
    {
        ValidateNotificationSettings();
        if (_config.LockScreenDisplayTimeoutEnabled && !_config.UseSystemService)
            throw new InvalidOperationException("启用“登录页和锁屏页自动息屏”前，请先启用 GameDayWork 系统服务。");
        var serviceSettings = ServiceSettingsSignature();
        if (serviceSettings != _appliedServiceSettings && (_config.UseSystemService || _serviceConfiguredAtLoad))
        {
            var result = await ServiceManagementClient.RunElevatedAsync(_config.UseSystemService,
                _config.LockScreenDisplayTimeoutEnabled, _config.LockScreenDisplayTimeoutAcSeconds, _config.LockScreenDisplayTimeoutDcSeconds);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            _serviceConfiguredAtLoad = _config.UseSystemService;
            _appliedServiceSettings = serviceSettings;
        }
        var startupEnabled = _config.StartWithWindows && !_config.UseSystemService;
        if (startupEnabled != _appliedStartupEnabled)
        {
            _startupService.Apply(startupEnabled);
            _appliedStartupEnabled = startupEnabled;
        }
        await _configService.SaveAsync(_config);
    }
    public async Task SaveAsync()
    {
        ValidateNotificationSettings();
        await _configService.SaveAsync(_config);
    }
    private void ValidateNotificationSettings()
    {
        if (!string.IsNullOrWhiteSpace(_config.Notifications.WeComWebhookUrl)
            && !WeComNotificationService.TryValidateWebhook(_config.Notifications.WeComWebhookUrl, out _, out var error))
            throw new InvalidOperationException(error);
    }
    public void BeginTaskEdit(AutomationTaskConfig task)
    {
        SelectedTask = task;
        _editingTaskTarget = task;
        _taskEditSnapshot = JsonSerializer.Serialize(task);
        EditingTask = JsonSerializer.Deserialize<AutomationTaskConfig>(_taskEditSnapshot)!;
        EditingTask.IsGroupChild = ContainerOf(task) != Tasks;
    }
    public void DiscardTaskEdit()
    {
        EditingTask = null;
        _editingTaskTarget = null;
        _taskEditSnapshot = null;
    }
    public async Task SaveTaskEditAsync(bool keepEditing = false)
    {
        if (EditingTask is null || _editingTaskTarget is null) return;
        var original = _editingTaskTarget;
        var previous = JsonSerializer.Deserialize<AutomationTaskConfig>(JsonSerializer.Serialize(original))!;
        // Capture this edit before awaiting I/O. Later keystrokes must remain pending.
        var savedSnapshot = JsonSerializer.Serialize(EditingTask);
        var savedDraft = JsonSerializer.Deserialize<AutomationTaskConfig>(savedSnapshot)!;
        ApplyTaskConfiguration(original, savedDraft);
        try { await SaveAsync(); }
        catch { ApplyTaskConfiguration(original, previous); throw; }
        if (keepEditing) _taskEditSnapshot = savedSnapshot;
        else DiscardTaskEdit();
        RefreshVisibleTasks();
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(NextTaskNames));
    }
    private static void ApplyTaskConfiguration(AutomationTaskConfig target, AutomationTaskConfig source)
    {
        // Keep the live task object and runtime state used by the scheduler and runner.
        target.Name = source.Name;
        target.ToolType = source.ToolType;
        target.GroupTaskIntervalSeconds = source.GroupTaskIntervalSeconds;
        if (target.IsGroup)
        {
            var children = source.Children.Select(child =>
            {
                var live = target.Children.FirstOrDefault(item => item.Id == child.Id);
                if (live is null) return child;
                ApplyTaskConfiguration(live, child);
                return live;
            }).ToList();
            target.Children.Clear();
            foreach (var child in children) target.Children.Add(child);
        }
        target.Enabled = source.Enabled;
        target.ProgramPath = source.ProgramPath;
        target.Arguments = source.Arguments;
        target.WorkingDirectory = source.WorkingDirectory;
        target.CompletionMode = source.CompletionMode;
        target.CompletionProcessName = source.CompletionProcessName;
        target.CompletionLogPath = source.CompletionLogPath;
        target.CompletionKeyword = source.CompletionKeyword;
        target.CompletionFailureKeyword = source.CompletionFailureKeyword;
        target.Description = source.Description;
        target.MaxRunMinutes = source.MaxRunMinutes;
        target.CleanupWaitSeconds = source.CleanupWaitSeconds;
        target.CleanupRetries = source.CleanupRetries;
        target.RunAsAdministrator = source.RunAsAdministrator;
        target.ScheduledStartTime = source.ScheduledStartTime;
        target.CompletionAction = source.CompletionAction;
        target.TrackChildren = source.TrackChildren;
        target.UseJobObject = source.UseJobObject;
        target.ProcessRules = source.ProcessRules;
    }
    private string ServiceSettingsSignature() => $"{_config.UseSystemService}:{_config.LockScreenDisplayTimeoutEnabled}:{_config.LockScreenDisplayTimeoutAcSeconds}:{_config.LockScreenDisplayTimeoutDcSeconds}";

    public void OpenWindowsPowerSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.PowerOptions") { UseShellExecute = true }); }
    }
    public Task<NotificationTestResult> TestWeComNotificationAsync() => _notificationService.SendTestAsync();
    public Task<bool> EnterBlackoutAsync() => _screenManager.EnterBlackoutAsync();
    public Task ExitBlackoutAsync() => _screenManager.ExitBlackoutAsync();
    public void Reorder(AutomationTaskConfig source, AutomationTaskConfig target)
    {
        if (_launchCoordinator.IsBusy || source == target) return;
        var container = ContainerOf(source);
        if (container != ContainerOf(target)) return;
        var oldIndex = container.IndexOf(source); var newIndex = container.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0) container.Move(oldIndex, newIndex);
        RefreshVisibleTasks();
    }
    public void EndCurrentTask()
    {
        if (_launchCoordinator.IsBusy) _launchCoordinator.Stop();
    }

    private async void ToggleSelectedTask()
    {
        if (_launchCoordinator.IsBusy)
        {
            EndCurrentTask();
            return;
        }

        try { await RunSingleTaskAsync(); }
        catch (OperationCanceledException)
        {
            await _log.WriteAsync(LogLevel.Warning, "任务启动已取消");
        }
        catch (Exception ex)
        {
            await _log.WriteAsync(LogLevel.Error, $"启动任务失败：{ex.Message}");
            ValidationFailed?.Invoke(ex.Message);
        }
    }
    private async Task RunSingleTaskAsync()
    {
        if (SelectedTask is null) { ValidationFailed?.Invoke("请先选择一个任务。"); return; }
        var plan = TaskPlanService.ManualPlan(Tasks, SelectedTask);
        if (!await ValidateBeforeRunAsync(plan)) return;
        await _launchCoordinator.RunAsync(plan, DateTime.Now, TaskIntervalSeconds, FailurePolicy);
        RaiseCommandStates();
    }
    private async Task RunScheduledTasksAsync(ScheduledLaunchBatch batch)
    {
        var scheduled = TaskPlanService.Expand(batch.Anchors.OrderBy(task => Tasks.IndexOf(task)));

        if (!await ValidateBeforeRunAsync(scheduled)) return;
        await _log.WriteAsync(LogLevel.Info, $"定时任务准备：{string.Join(" → ", scheduled.Select(task => task.Name))}；PrepareAt={batch.PrepareAt:HH:mm:ss}，LaunchAt={batch.ScheduledAt:HH:mm:ss}");
        await _launchCoordinator.RunAsync(scheduled, batch.ScheduledAt, TaskIntervalSeconds, FailurePolicy);
        RaiseCommandStates();
    }
    private async Task<bool> ValidateBeforeRunAsync(IReadOnlyList<AutomationTaskConfig> tasks)
    {
        if (tasks.Count == 0) { ValidationFailed?.Invoke("没有已启用的任务可执行。"); return false; }
        var issues = _validator.Validate(tasks);
        if (issues.Count == 0) { await SaveAsync(); return true; }
        var message = string.Join(Environment.NewLine, issues.Select(x => $"• {x.TaskName}：{x.Message}"));
        foreach (var issue in issues) await _log.WriteAsync(LogLevel.Error, $"配置校验失败 - {issue.TaskName}：{issue.Message}");
        ValidationFailed?.Invoke(message);
        return false;
    }
    private void UpdateQueueStatus()
    {
        StatusText = _queue.Status switch { QueueRunStatus.Running => "执行中", QueueRunStatus.Stopping => "正在停止", QueueRunStatus.Completed => "已完成", QueueRunStatus.Failed => "执行失败", _ => "准备就绪" };
        OnPropertyChanged(nameof(ManualActionText));
        RaiseCommandStates();
    }
    private void DeleteTask()
    {
        if (SelectedTask is null) return;
        var container = ContainerOf(SelectedTask);
        var index = container.IndexOf(SelectedTask);
        container.Remove(SelectedTask);
        SelectedTask = container.ElementAtOrDefault(Math.Min(index, container.Count - 1));
        RefreshVisibleTasks();
    }
    private void DuplicateTask()
    {
        if (SelectedTask is null) return;
        var copy = JsonSerializer.Deserialize<AutomationTaskConfig>(JsonSerializer.Serialize(SelectedTask))!;
        copy.Id = Guid.NewGuid();
        foreach (var child in copy.Children) child.Id = Guid.NewGuid();
        copy.Name += " 副本";
        var container = ContainerOf(SelectedTask);
        container.Insert(container.IndexOf(SelectedTask) + 1, copy); SelectedTask = copy;
        RefreshVisibleTasks();
    }
    private void MoveSelected(int offset) { if (SelectedTask is null) return; var container = ContainerOf(SelectedTask); var from = container.IndexOf(SelectedTask); var to = from + offset; if (to >= 0 && to < container.Count) container.Move(from, to); RefreshVisibleTasks(); }
    private async Task ResetSelectedTaskAsync()
    {
        var target = _editingTaskTarget ?? SelectedTask;
        if (target is null) return;
        var container = ContainerOf(target);
        var index = container.IndexOf(target);
        var savedRoots = (await _configService.LoadAsync()).Tasks;
        var saved = savedRoots.Concat(savedRoots.SelectMany(root => root.Children)).FirstOrDefault(t => t.Id == target.Id);
        if (saved is null) { ValidationFailed?.Invoke("该任务尚未保存，无法重置。"); return; }
        if (EditingTask is not null) { EditingTask = saved; EditingTask.IsGroupChild = container != Tasks; }
        else { container[index] = saved; SelectedTask = saved; RefreshVisibleTasks(); }
        await _log.WriteAsync(LogLevel.Info, $"已重置任务配置：{saved.Name}");
    }
    private async Task ApplyKnownToolsAsync()
    {
        var discovered = _toolProfiles.Discover();
        var added = 0;
        var updated = 0;
        foreach (var profile in discovered)
        {
            var aliases = profile.Name switch { "MAA" => new[] { "MAA", "MMA" }, "MFA" => new[] { "MFA", "MAN" }, _ => new[] { profile.Name } };
            var existing = Tasks.FirstOrDefault(task => aliases.Contains(task.Name, StringComparer.OrdinalIgnoreCase));
            if (existing is not null)
            {
                KnownToolProfileService.ApplyRecommendedSettings(existing, profile);
                updated++;
            }
            else
            {
                Tasks.Add(profile);
                added++;
            }
        }
        if (added + updated > 0)
        {
            await SaveAsync();
            await _log.WriteAsync(LogLevel.Success, $"五项目适配已应用：新增 {added} 项，更新 {updated} 项");
        }
        NoticeRequested?.Invoke(added + updated > 0
            ? $"已应用本机工具适配：新增 {added} 项，更新 {updated} 项。"
            : "未发现 BGI、MAA、ZOG、MFA 或 M7A。");
    }
    public IReadOnlyList<AutomationTaskConfig> DiscoverKnownTools() => _toolProfiles.Discover();
    public async Task<bool> AddKnownToolAsync(AutomationTaskConfig profile)
    {
        if (_launchCoordinator.IsBusy) return false;
        Tasks.Add(profile);
        SelectedTask = profile;
        await SaveAsync();
        await _log.WriteAsync(LogLevel.Success, $"已添加适配任务：{profile.Name}");
        return true;
    }
    private void RefreshTaskIndexes()
    {
        for (var index = 0; index < Tasks.Count; index++) Tasks[index].DisplayIndex = index + 1;
        RefreshVisibleTasks();
    }
    private void RaiseCommandStates()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(RaiseCommandStates);
            return;
        }
        ((RelayCommand)ToggleExecutionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeleteTaskCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DuplicateTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ResetTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportToolsCommand).RaiseCanExecuteChanged();
    }
    private void TickUi()
    {
        if (CurrentSession is { } session) Elapsed = ((session.EndTime ?? DateTimeOffset.Now) - session.StartTime).ToString(@"hh\:mm\:ss");
        foreach (var task in VisibleTasks) task.RefreshNextExecutionText();
        OnPropertyChanged(nameof(NextRunText)); OnPropertyChanged(nameof(NextTaskNames)); RaiseRuntimeProperties();
    }
    private void RaiseRuntimeProperties()
    {
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentStage));
        OnPropertyChanged(nameof(RootPid));
        OnPropertyChanged(nameof(RelatedCount));
        OnPropertyChanged(nameof(CurrentStatusHeadline));
        OnPropertyChanged(nameof(CurrentStatusHint));
    }
    private async Task PrepareForScreenshotAsync(CancellationToken token)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        var hidden = await dispatcher.InvokeAsync(() =>
        {
            var changed = false;
            var handlers = HideToTrayRequested;
            if (handlers is null) return false;
            foreach (Func<bool> handler in handlers.GetInvocationList()) changed |= handler();
            return changed;
        });
        if (hidden) await Task.Delay(TimeSpan.FromMilliseconds(250), token);
    }
    private void OpenLogs() { Directory.CreateDirectory(_log.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true }); }
    public void Dispose() { _scheduler.Dispose(); _uiTimer.Stop(); }
    public async Task ShutdownAsync()
    {
        Dispose();
        await _launchCoordinator.DisposeAsync();
        await _screenshotCoordinator.DisposeAsync();
        await _notificationService.DisposeAsync();
        await SaveAsync();
        await _screenManager.DisposeAsync();
    }
}
