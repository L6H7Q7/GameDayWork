using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using GameOrchestrator.Models;
using GameOrchestrator.ViewModels;
using Forms = System.Windows.Forms;
using GameOrchestrator.Services;

namespace GameOrchestrator.Views;

public partial class MainWindow : Window
{
    private const int BlackoutHotKeyId = 0x4744;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmHotKey = 0x0312;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkZ = 0x5A;
    private readonly MainViewModel _viewModel = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayStatusItem;
    private readonly Forms.ToolStripMenuItem _trayEndTaskItem;
    private readonly System.Drawing.Icon _idleTrayIcon;
    private readonly System.Drawing.Icon _runningTrayIcon;
    private Point _dragStart;
    private bool _draggedTask;
    private enum MainPage { Home, TaskEdit, Settings }
    private AutomationTaskConfig? _selectionBeforePointer;
    private MainPage _page;
    private bool _navigationPending;
    private bool _closingPending;
    private bool _exitRequested;
    private bool _initialized;
    private nint _windowHandle;
    private HwndSource? _windowSource;
    private int _blackoutHotKeyPending;
    private bool _logScrollPending;
    private readonly bool _serviceManaged;
    public MainWindow(bool serviceManaged = false)
    {
        _serviceManaged = serviceManaged;
        InitializeComponent(); DataContext = _viewModel;
        _viewModel.Logs.CollectionChanged += LogsChanged;
        InitializeAutoSave();
        _viewModel.ToggleExecutionCommand.CanExecuteChanged += (_, _) => UpdateExecutionButton();
        _viewModel.DeleteTaskCommand.CanExecuteChanged += (_, _) => UpdateExecutionButton();
        _viewModel.DuplicateTaskCommand.CanExecuteChanged += (_, _) => UpdateExecutionButton();
        UpdateExecutionButton();
        _idleTrayIcon = LoadTrayIcon("Assets/GameDayWork-Idle.ico");
        _runningTrayIcon = LoadTrayIcon("Assets/GameDayWork-Running.ico");
        Opacity = 0;
        ShowActivated = false;
        ShowInTaskbar = false;
        _viewModel.HideToTrayRequested += HideToTray;
        _trayStatusItem = new Forms.ToolStripMenuItem("状态：空闲监控") { Enabled = false };
        _trayEndTaskItem = new Forms.ToolStripMenuItem("结束任务") { Visible = false };
        _trayEndTaskItem.Click += (_, _) => _viewModel.EndCurrentTask();
        _trayIcon = CreateTrayIcon();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ScreenStateText) or nameof(MainViewModel.HasActiveTask) or nameof(MainViewModel.IsExecutionBusy)) UpdateTrayStatus();
        };
        Closing += MainWindow_Closing;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // Create the native handle needed by the global hotkey without making the
        // WPF window visible. The configuration decides whether Show is ever called.
        _ = new WindowInteropHelper(this).EnsureHandle();
        await _viewModel.InitializeAsync();
        EnableAutoSave();
        _viewModel.ValidationFailed += ShowValidationErrors;
        _viewModel.NoticeRequested += ShowNotice;
        WirePlaceholderControls();
        UpdateTrayStatus();

        if (!_viewModel.StartMinimizedToTray) ShowMainWindow();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        if (!RegisterHotKey(_windowHandle, BlackoutHotKeyId, ModControl | ModAlt | ModNoRepeat, VkZ))
        {
            MessageBox.Show(
                "无法注册全局快捷键 Ctrl + Alt + Z，可能已被其他程序占用。",
                "快捷键注册失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAutoSave();
        _viewModel.Logs.CollectionChanged -= LogsChanged;
        if (_windowHandle != nint.Zero) UnregisterHotKey(_windowHandle, BlackoutHotKeyId);
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            ConstrainMaximizedBounds(hwnd, lParam);
            handled = true;
            return nint.Zero;
        }

        if (message != WmHotKey || wParam.ToInt32() != BlackoutHotKeyId) return nint.Zero;
        handled = true;
        _ = EnterBlackoutFromHotKeyAsync();
        return nint.Zero;
    }

    private static void ConstrainMaximizedBounds(nint windowHandle, nint minMaxInfoPointer)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == nint.Zero) return;

        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    private async Task EnterBlackoutFromHotKeyAsync()
    {
        if (Interlocked.Exchange(ref _blackoutHotKeyPending, 1) != 0) return;
        try
        {
            // WM_HOTKEY is raised while the keys are still held. Wait for their release so
            // ScreenManager does not interpret the key-up events as a request to wake again.
            while (IsHotKeyPressed())
                await Task.Delay(25);

            await _viewModel.EnterBlackoutAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "息屏管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _blackoutHotKeyPending, 0);
        }
    }

    private static bool IsHotKeyPressed() =>
        IsKeyDown(VkControl) || IsKeyDown(VkMenu) || IsKeyDown(VkZ);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        if (_closingPending || _navigationPending) return;
        _closingPending = true;
        try
        {
            if (!await TryLeavePageAsync()) return;
            ShowPage(MainPage.Home);
            HideToTray();
            await _viewModel.SaveAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"隐藏前保存配置失败：{ex}"); }
        finally { _closingPending = false; }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add(_trayEndTaskItem);
        menu.Items.Add("进入黑屏", null, async (_, _) => await RunTrayActionAsync(_viewModel.EnterBlackoutAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, async (_, _) => await ExitApplicationAsync());
        var icon = new Forms.NotifyIcon
        {
            Text = "GameDayWork - 空闲监控",
            Icon = _idleTrayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowMainWindow();
        };
        return icon;
    }

    private void ShowMainWindow()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private bool HideToTray()
    {
        if (!IsVisible) return false;
        Hide();
        return true;
    }

    internal void PrepareForFatalShutdown()
    {
        // Do not let the normal close-to-tray behavior cancel a fatal application shutdown.
        _exitRequested = true;
        _trayIcon.Visible = false;
    }

    private void UpdateTrayStatus()
    {
        _trayStatusItem.Text = $"状态：{_viewModel.ScreenStateText}";
        _trayEndTaskItem.Visible = _viewModel.IsExecutionBusy;
        _trayIcon.Text = $"GameDayWork - {_viewModel.ScreenStateText}";
        _trayIcon.Icon = _viewModel.HasActiveTask ? _runningTrayIcon : _idleTrayIcon;
    }

    private static System.Drawing.Icon LoadTrayIcon(string relativePath)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/GameDayWork;component/{relativePath}", UriKind.Absolute))
            ?? throw new InvalidOperationException($"找不到托盘图标资源：{relativePath}");
        using (resource.Stream)
        using (var icon = new System.Drawing.Icon(resource.Stream))
            return (System.Drawing.Icon)icon.Clone();
    }

    private async Task RunTrayActionAsync(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "息屏管理器", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitRequested) return;
        if (_navigationPending || GlobalSettingsPage.IsSaving || !await TryLeavePageAsync()) return;
        _exitRequested = true;
        try
        {
            if (_serviceManaged || _viewModel.UseSystemService)
                await ServiceManagementClient.NotifyIntentionalExitAsync(System.Diagnostics.Process.GetCurrentProcess().SessionId);
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"退出程序时清理失败：{ex}"); }
        finally
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _idleTrayIcon.Dispose();
            _runningTrayIcon.Dispose();
            Application.Current.Shutdown(_serviceManaged ? 77 : 0);
        }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true && (_viewModel.EditingTask ?? _viewModel.SelectedTask) is { } task) task.ProgramPath = dialog.FileName;
    }
    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _viewModel.Logs.Clear();
    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        var url = (_viewModel.EditingTask ?? _viewModel.SelectedTask)?.RepositoryUrl;
        if (!string.IsNullOrWhiteSpace(url))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
    private void ShowValidationErrors(string message) => MessageBox.Show(this, message, "无法开始执行", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void ShowNotice(string message) => MessageBox.Show(this, message, "本机工具识别", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void Settings_Click(object sender, RoutedEventArgs e) => await NavigateAsync(MainPage.Settings);
    private async void Home_Click(object sender, RoutedEventArgs e) => await NavigateAsync(MainPage.Home);
    private async void Back_Click(object sender, RoutedEventArgs e) => await NavigateAsync(MainPage.Home);

    private void UpdateExecutionButton()
    {
        if (ExecutionButton is null || DeleteTaskButton is null) return;
        ExecutionButton.IsEnabled = _viewModel.ToggleExecutionCommand.CanExecute(null);
        DeleteTaskButton.IsEnabled = _viewModel.DeleteTaskCommand.CanExecute(null);
        DuplicateTaskButton.IsEnabled = _viewModel.DuplicateTaskCommand.CanExecute(null);
        AddTaskButton.IsEnabled = !_viewModel.IsExecutionBusy;
        var container = _viewModel.SelectedTask is { } selected ? _viewModel.ContainerOf(selected) : _viewModel.Tasks;
        var index = _viewModel.SelectedTask is { } task ? container.IndexOf(task) : -1;
        MoveUpButton.IsEnabled = !_viewModel.IsExecutionBusy && index > 0;
        MoveDownButton.IsEnabled = !_viewModel.IsExecutionBusy && index >= 0 && index < container.Count - 1;
        GroupSettingsPanel.IsEnabled = !_viewModel.IsExecutionBusy;
        TaskSettingsPanel.IsEnabled = !_viewModel.IsExecutionBusy;
    }

    private void SetNavigationPending(bool pending)
    {
        _navigationPending = pending;
        TasksPage.IsEnabled = !pending;
    }

    private async void Execution_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationPending || _closingPending) return;
        // A stop must stay available even when an editor has unsaved changes.
        if (!_viewModel.IsExecutionBusy && !await NavigateAsync(MainPage.Home)) return;
        if (_viewModel.ToggleExecutionCommand.CanExecute(null)) _viewModel.ToggleExecutionCommand.Execute(null);
    }

    private void ShowPage(MainPage page)
    {
        _page = page;
        HomePage.Visibility = page == MainPage.Home ? Visibility.Visible : Visibility.Collapsed;
        TasksPage.Visibility = Visibility.Visible;
        TaskEditPage.Visibility = page == MainPage.TaskEdit ? Visibility.Visible : Visibility.Collapsed;
        GlobalSettingsPage.Visibility = page == MainPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        var isGroup = _viewModel.EditingTask?.IsGroup == true;
        var isChild = _viewModel.EditingTask?.IsGroupChild == true;
        TaskSettingsPanel.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        GroupSettingsPanel.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        TaskScheduleFields.Visibility = isChild ? Visibility.Collapsed : Visibility.Visible;
        GroupScheduleHint.Visibility = isChild ? Visibility.Visible : Visibility.Collapsed;
        UpdateExecutionButton();
        if (page == MainPage.Home) LogsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private async Task<bool> NavigateAsync(MainPage page, AutomationTaskConfig? task = null)
    {
        if (_navigationPending || _closingPending) return false;
        if (_page == page && (page != MainPage.TaskEdit || task == _viewModel.EditingTaskTarget)) return true;
        var previousSelection = _viewModel.EditingTaskTarget ?? (task is not null ? _selectionBeforePointer : null) ?? _viewModel.SelectedTask;
        SetNavigationPending(true);
        try
        {
            if (!await TryLeavePageAsync())
            {
                _viewModel.SelectedTask = previousSelection;
                return false;
            }
            if (page == MainPage.Settings) GlobalSettingsPage.BeginEdit(_viewModel);
            if (page == MainPage.TaskEdit && task is not null)
            {
                _viewModel.BeginTaskEdit(task);
            }
            ShowPage(page);
            return true;
        }
        finally { SetNavigationPending(false); }
    }

    private async Task<bool> TryLeavePageAsync()
    {
        if (!await FlushAutoSaveAsync(showErrors: true)) return false;
        _viewModel.DiscardTaskEdit();
        return true;
    }

    internal static void UpdateInputBindings(DependencyObject root)
    {
        foreach (var grid in InputElements(root).OfType<DataGrid>())
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        foreach (var input in InputElements(root).OfType<TextBox>()) input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    internal static bool HasInputErrors(DependencyObject root) => Validation.GetHasError(root)
        || InputElements(root).Any(Validation.GetHasError);

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.DeleteTaskCommand.CanExecute(null) && await NavigateAsync(MainPage.Home)) _viewModel.DeleteTaskCommand.Execute(null);
    }
    private async void DuplicateTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.DuplicateTaskCommand.CanExecute(null) && await NavigateAsync(MainPage.Home)) _viewModel.DuplicateTaskCommand.Execute(null);
    }
    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsExecutionBusy && await NavigateAsync(MainPage.Home)) { _viewModel.MoveUpCommand.Execute(null); UpdateExecutionButton(); }
    }
    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsExecutionBusy && await NavigateAsync(MainPage.Home)) { _viewModel.MoveDownCommand.Execute(null); UpdateExecutionButton(); }
    }

    private void WirePlaceholderControls()
    {
        foreach (var level in new[] { "INFO", "SUCCESS", "WARNING", "ERROR" }) LogFilter.Items.Add(new ComboBoxItem { Content = level });
        LogFilter.SelectionChanged += LogFilter_SelectionChanged;
    }
    private void LogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        CollectionViewSource.GetDefaultView(_viewModel.Logs).Filter = item => selected == "全部" || item is LogEntry entry && entry.LevelText == selected;
        RequestLogScroll();
    }
    private static IEnumerable<FrameworkElement> InputElements(DependencyObject root)
    {
        // Inactive tab contents leave the visual tree but still hold bindings and errors.
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node)) continue;
            if (node is FrameworkElement element) yield return element;
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) pending.Push(VisualTreeHelper.GetChild(node, index));
            if (node is FrameworkElement or FrameworkContentElement)
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                    if (child is DependencyObject dependency) pending.Push(dependency);
        }
    }
    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = $"GameDayWork-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllLines(dialog.FileName, _viewModel.Logs.Select(x => $"{x.Time:O} [{x.LevelText}] {x.Message}"));
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void LogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RequestLogScroll();

    private void LogList_Loaded(object sender, RoutedEventArgs e) => RequestLogScroll();

    private void LogList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) RequestLogScroll();
    }

    private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Virtualized rows can change the extent after a scroll request. Follow the
        // final layout, including wrapped messages and window size changes.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
            RequestLogScroll();
    }

    private void RequestLogScroll()
    {
        if (LogList is null || LogList.Items.Count == 0 || _logScrollPending) return;

        // CollectionChanged is raised while WPF is still updating the ItemsControl. Calling
        // ScrollIntoView synchronously here can re-enter its item generator and leave it out
        // of sync with the source. Coalesce bursts of log entries and scroll after layout has
        // had a chance to process the collection changes.
        _logScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _logScrollPending = false;
            if (LogList.Items.Count == 0 || _exitRequested) return;

            try
            {
                // Use the filtered view's last row and align its bottom, even when
                // a single message is taller than the viewport.
                InputElements(LogList).OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
            }
            catch (InvalidOperationException ex)
            {
                // Auto-scroll is optional. If WPF is still regenerating containers, skip this
                // request instead of allowing a transient view inconsistency to stop the task.
                System.Diagnostics.Debug.WriteLine($"日志列表自动滚动已跳过：{ex}");
            }
        });
    }
    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsExecutionBusy || !await NavigateAsync(MainPage.Home)) return;
        var menu = new ContextMenu
        {
            PlacementTarget = sender as Button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 220
        };
        foreach (var isGroup in new[] { false, true })
        {
            var item = new MenuItem { Header = isGroup ? "任务组" : "独立任务" };
            item.Click += async (_, _) =>
            {
                if (_viewModel.IsExecutionBusy) return;
                var task = new AutomationTaskConfig { IsGroup = isGroup, Name = isGroup ? "新任务组" : "新任务", IsExpanded = isGroup };
                _viewModel.Tasks.Add(task);
                _viewModel.RefreshVisibleTasks();
                await NavigateAsync(MainPage.TaskEdit, task);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void ToolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if ((sender as ComboBox)?.SelectedItem is string type) _viewModel.ApplyToolType(type);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "选择任务类型失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task EditGroupMembersAsync(bool add, int? offset = null)
    {
        if (_navigationPending || _viewModel.IsExecutionBusy || _viewModel.EditingTaskTarget is not { IsGroup: true } group) return;
        var memberId = (GroupMembersList.SelectedItem as AutomationTaskConfig)?.Id;
        if (!add && memberId is null) return;
        SetNavigationPending(true);
        try
        {
            if (!await FlushAutoSaveAsync(showErrors: true)) return;
            _viewModel.DiscardTaskEdit();
            if (add)
            {
                group.Children.Add(new AutomationTaskConfig { IsGroupChild = true, CompletionAction = TaskCompletionAction.RunNext });
                group.IsExpanded = true;
            }
            else if (group.Children.FirstOrDefault(child => child.Id == memberId) is { } child)
            {
                if (offset is null) group.Children.Remove(child);
                else
                {
                    var from = group.Children.IndexOf(child); var to = from + offset.Value;
                    if (to >= 0 && to < group.Children.Count) group.Children.Move(from, to);
                }
            }
            await _viewModel.SaveAsync();
            _viewModel.RefreshVisibleTasks();
            if (add && group.Children.LastOrDefault() is { } newTask)
            {
                TaskList.UpdateLayout();
                TaskList.ScrollIntoView(newTask);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "修改组内任务失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            // Membership buttons save the group settings and keep the group editor open.
            if (_viewModel.EditingTask is null) _viewModel.BeginTaskEdit(group);
            ShowPage(MainPage.TaskEdit);
            if (add) GroupMembersList.SelectedIndex = group.Children.Count - 1;
            else GroupMembersList.SelectedItem = _viewModel.EditingTask?.Children.FirstOrDefault(child => child.Id == memberId);
            SetNavigationPending(false);
        }
    }
    private async void AddGroupTask_Click(object sender, RoutedEventArgs e) => await EditGroupMembersAsync(true);
    private async void RemoveGroupTask_Click(object sender, RoutedEventArgs e) => await EditGroupMembersAsync(false);
    private async void MoveGroupTaskUp_Click(object sender, RoutedEventArgs e) => await EditGroupMembersAsync(false, -1);
    private async void MoveGroupTaskDown_Click(object sender, RoutedEventArgs e) => await EditGroupMembersAsync(false, 1);
    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExecutionButton();
    }
    private void TaskList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _selectionBeforePointer = _viewModel.SelectedTask;
        _dragStart = e.GetPosition(TaskList);
        _draggedTask = false;
    }
    private async void TaskList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedTask || IsInteractiveControl(e.OriginalSource as DependencyObject))
        {
            _selectionBeforePointer = null;
            return;
        }
        if (FindItem(e.OriginalSource as DependencyObject) is { } task)
        {
            e.Handled = true;
            if (!await NavigateAsync(MainPage.TaskEdit, task))
                _viewModel.SelectedTask = _viewModel.EditingTaskTarget ?? _selectionBeforePointer ?? _viewModel.SelectedTask;
            else if (task.IsGroup) _viewModel.ToggleGroup(task);
        }
        _selectionBeforePointer = null;
    }
    private async void TaskList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel.SelectedTask is not { } task) return;
        _selectionBeforePointer = null;
        e.Handled = true;
        if (await NavigateAsync(MainPage.TaskEdit, task) && task.IsGroup) _viewModel.ToggleGroup(task);
    }
    private static bool IsInteractiveControl(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar) return true;
            source = ParentOf(source);
        }
        return false;
    }
    private void TaskList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsExecutionBusy || e.LeftButton != MouseButtonState.Pressed || IsInteractiveControl(e.OriginalSource as DependencyObject)) return;
        var delta = e.GetPosition(TaskList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (FindItem(e.OriginalSource as DependencyObject) is { } item)
        {
            _draggedTask = true;
            DragDrop.DoDragDrop(TaskList, item, DragDropEffects.Move);
        }
    }
    private void TaskList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(AutomationTaskConfig)) is AutomationTaskConfig source && FindItem(e.OriginalSource as DependencyObject) is { } target) _viewModel.Reorder(source, target);
        UpdateExecutionButton();
    }
    private static AutomationTaskConfig? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem) source = ParentOf(source);
        return (source as ListBoxItem)?.DataContext as AutomationTaskConfig;
    }
    private static DependencyObject? ParentOf(DependencyObject source) => source is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
