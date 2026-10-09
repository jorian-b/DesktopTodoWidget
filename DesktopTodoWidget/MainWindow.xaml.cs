using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopTodoWidget
{
    public partial class MainWindow : Window
    {
        private readonly TaskManager _taskManager = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTodoWidget",
            "tasks.json"));
        private readonly WidgetSettingsStore _settingsStore = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTodoWidget",
            "settings.json"));
        private TaskGroupSelectorController _taskGroupSelector = null!;
        private TaskInteractionController _taskInteractions = null!;
        private TaskAlarmPresenter _alarmPresenter = null!;
        private bool _isLocked = false;
        private bool _keepBackgroundVisible;
        private bool _isWidgetHovered;
        private System.Windows.Threading.DispatcherTimer? _reminderTimer;
        private System.Windows.Threading.DispatcherTimer? _settingsSaveTimer;

        public MainWindow()
        {
            LogDiagnostic("MainWindow Constructor Started");
            try
            {
                InitializeComponent();
                _taskGroupSelector = new TaskGroupSelectorController(
                    TaskSetSelector,
                    DeleteTaskGroupButton,
                    _taskManager,
                    this,
                    SaveWidgetSettings);
                _taskInteractions = new TaskInteractionController(
                    _taskManager,
                    TaskList,
                    this,
                    SaveTasks);
                _alarmPresenter = new TaskAlarmPresenter(
                    _taskManager,
                    SelectTaskGroup,
                    AlarmOverlay,
                    AlarmTaskText,
                    AlarmDetailText,
                    RemoveTimerAfterDismiss);
                LogDiagnostic("InitializeComponent Done");

                _settingsSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _settingsSaveTimer.Tick += (_, _) =>
                {
                    _settingsSaveTimer.Stop();
                    SaveWidgetSettings();
                };
                LoadWidgetSettings();
                UpdateHoverVisibilityToggle();
                _taskGroupSelector.Refresh();
                LocationChanged += (_, _) => ScheduleWidgetSettingsSave();
                SizeChanged += (_, _) => ScheduleWidgetSettingsSave();
                Closed += (_, _) =>
                {
                    _settingsSaveTimer.Stop();
                    SaveWidgetSettings();
                };

                this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;

                this.StateChanged += MainWindow_StateChanged;

                TaskList.ItemsSource = _taskManager.TaskView;
                LoadTasks();
                StartReminderTimer();
                LogDiagnostic("MainWindow Constructor Finished");
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Error in constructor: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Startup Error: {ex.Message}");
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
        }

        private void MainWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isLocked || IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
            {
                return;
            }

            try
            {
                DragMove();
                SnapToNearestWorkAreaEdge();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static bool IsWithinInteractiveControl(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is ButtonBase or TextBoxBase or CheckBox)
                {
                    return true;
                }

                element = element is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }

            return false;
        }

        private void SnapToNearestWorkAreaEdge()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr monitor = WinAPI.MonitorFromWindow(hwnd, WinAPI.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new WinAPI.MonitorInfo { Size = Marshal.SizeOf<WinAPI.MonitorInfo>() };
            if (monitor == IntPtr.Zero || !WinAPI.GetMonitorInfo(monitor, ref monitorInfo))
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY;
            var workArea = monitorInfo.WorkArea;
            double leftEdge = workArea.Left / scaleX;
            double rightEdge = workArea.Right / scaleX;
            double topEdge = workArea.Top / scaleY;
            double bottomEdge = workArea.Bottom / scaleY;
            const double snapDistance = 32;

            if (Math.Abs(Left - leftEdge) <= snapDistance)
            {
                Left = leftEdge;
            }
            else if (Math.Abs(rightEdge - (Left + Width)) <= snapDistance)
            {
                Left = rightEdge - Width;
            }

            if (Math.Abs(Top - topEdge) <= snapDistance)
            {
                Top = topEdge;
            }
            else if (Math.Abs(bottomEdge - (Top + Height)) <= snapDistance)
            {
                Top = bottomEdge - Height;
            }
        }

        protected override async void OnSourceInitialized(EventArgs e)
        {
            LogDiagnostic("OnSourceInitialized Triggered");
            base.OnSourceInitialized(e);
            
            var hwnd = new WindowInteropHelper(this).Handle;
            LogDiagnostic($"My HWND: {hwnd}");

            // Hook into WndProc to block minimization more robustly
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source.AddHook(WndProc);

            // Wait for the desktop shell (Progman/WorkerW) to be fully ready.
            // On Windows 11 startup this can take several seconds -- we poll instead
            // of using a fixed delay so we don't wait longer than necessary.
            LogDiagnostic("Waiting for desktop shell to be ready...");
            await WaitForDesktopShellAsync();

            MakeDesktopWidget();
        }

        private async System.Threading.Tasks.Task WaitForDesktopShellAsync()
        {
            const int maxWaitMs = 30000; // Wait up to 30 seconds
            const int pollMs = 500;      // Check every 500ms
            int elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                IntPtr progman = WinAPI.FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    // Progman is ready; give WorkerW a moment to spawn too
                    await System.Threading.Tasks.Task.Delay(500);
                    LogDiagnostic($"Desktop shell ready after ~{elapsed}ms");
                    return;
                }
                await System.Threading.Tasks.Task.Delay(pollMs);
                elapsed += pollMs;
            }
            LogDiagnostic("Timed out waiting for desktop shell -- proceeding anyway");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MINIMIZE = 0xF020;
            const int WM_WINDOWPOSCHANGING = 0x0046;

            if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MINIMIZE)
            {
                handled = true;
            }

            // ROOT FIX: Prevent Windows/WPF from hiding our embedded window.
            // When the window is parented to the desktop (WorkerW/Progman), WPF
            // receives a WM_ACTIVATE(0) and tries to hide the window by setting
            // SWP_HIDEWINDOW in WINDOWPOS. We intercept and clear that flag here.
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                const uint SWP_HIDEWINDOW = 0x0080;
                if ((wp.flags & SWP_HIDEWINDOW) != 0)
                {
                    wp.flags &= ~SWP_HIDEWINDOW; // Remove the hide flag
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, true);
                    LogDiagnostic("WM_WINDOWPOSCHANGING: Blocked SWP_HIDEWINDOW");
                }
            }

            return IntPtr.Zero;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [Conditional("DEBUG")]
        private void LogDiagnostic(string message)
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "DesktopTodoWidget_DEBUG.log");
                File.AppendAllText(logPath, $"{DateTime.Now}: {message}{Environment.NewLine}");
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void AddTask_Click(object sender, RoutedEventArgs e)
        {
            if (_taskManager.SelectedMode is TaskListMode.Inactive or TaskListMode.Trash)
            {
                SelectTaskGroup("Daily");
            }

            TaskInput.Visibility = Visibility.Visible;
            UpdateTaskListMaxHeight();
            // Delay focus until after the layout pass so the TextBox is
            // fully rendered and can accept keyboard input.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                TaskInput.Focus();
                System.Windows.Input.Keyboard.Focus(TaskInput);
            }));
        }

        private void LockToggle_Click(object sender, RoutedEventArgs e)
        {
            if (LockToggle == null || LockIcon == null) return;
            _isLocked = LockToggle.IsChecked == true;
            LockIcon.Text = _isLocked ? "\uE72E" : "\uE785";
            CloseButton.Visibility = _isLocked ? Visibility.Collapsed : Visibility.Visible;
            ResizeMode = _isLocked ? System.Windows.ResizeMode.NoResize : System.Windows.ResizeMode.CanResizeWithGrip;
        }

        private void PinToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = PinToggle.IsChecked == true;
            PinIcon.Text = "\uE718";
            PinToggle.ToolTip = Topmost ? "Unpin from top" : "Keep on top";
        }

        private void HoverVisibilityToggle_Click(object sender, RoutedEventArgs e)
        {
            _keepBackgroundVisible = HoverVisibilityToggle.IsChecked == true;
            UpdateHoverVisibilityToggle();
            ScheduleWidgetSettingsSave();
        }

        private void UpdateHoverVisibilityToggle()
        {
            HoverVisibilitySlash.Visibility = _keepBackgroundVisible
                ? Visibility.Collapsed
                : Visibility.Visible;
            HoverVisibilityToggle.ToolTip = _keepBackgroundVisible
                ? "Background stays visible when not hovered"
                : "Background hides when not hovered";
            BackgroundSurface.Opacity = _isWidgetHovered || _keepBackgroundVisible ? 1 : 0;
        }

        private void MainRoot_MouseEnter(object sender, MouseEventArgs e)
        {
            _isWidgetHovered = true;
            BackgroundSurface.Opacity = 1;
            MainFrame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
            TaskCardVisuals.Refresh(TaskList, true);
            LockToggle.Visibility = Visibility.Visible;
            PinToggle.Visibility = Visibility.Visible;
            HoverVisibilityToggle.Visibility = Visibility.Visible;
            AddButton.Visibility = Visibility.Visible;
            UpdateTimerButtonVisibility();
            TaskSetBar.Visibility = Visibility.Visible;
            UpdateTaskListMaxHeight();
        }

        private void MainRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            if (TaskSetSelector.IsDropDownOpen || TimerPopup.IsOpen)
            {
                return;
            }

            HideHoverControls();
        }

        private void TaskSetSelector_DropDownClosed(object sender, EventArgs e)
        {
            if (!MainRoot.IsMouseOver)
            {
                HideHoverControls();
            }
        }

        private void HideHoverControls()
        {
            _isWidgetHovered = false;
            TimerPopup.IsOpen = false;
            BackgroundSurface.Opacity = _keepBackgroundVisible ? 1 : 0;
            MainFrame.BorderBrush = Brushes.Transparent;
            TaskCardVisuals.Refresh(TaskList, false);
            LockToggle.Visibility = Visibility.Collapsed;
            PinToggle.Visibility = Visibility.Collapsed;
            HoverVisibilityToggle.Visibility = Visibility.Collapsed;
            AddButton.Visibility = Visibility.Collapsed;
            TimerButton.Visibility = Visibility.Collapsed;
            TaskSetBar.Visibility = Visibility.Collapsed;
            UpdateTaskListMaxHeight();
        }

        private void TaskCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border taskCard)
            {
                TaskCardVisuals.Update(taskCard, _isWidgetHovered);
            }
        }

        private void TaskCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is Border { DataContext: TaskItem { IsTimer: true } })
            {
                e.Handled = true;
            }
        }

        private void TaskSetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_taskGroupSelector.HandleSelectionChanged())
            {
                UpdateTimerButtonVisibility();
                UpdateTaskListMaxHeight();
            }
        }

        private void AddTaskGroup_Click(object sender, RoutedEventArgs e) =>
            _taskGroupSelector.AddCustomGroup();

        private void DeleteTaskGroup_Click(object sender, RoutedEventArgs e) =>
            _taskGroupSelector.DeleteSelectedGroup();

        private void SelectTaskGroup(string groupName)
        {
            _taskGroupSelector.SelectTaskGroup(groupName);
            UpdateTimerButtonVisibility();
        }

        private void UpdateTimerButtonVisibility() =>
            TimerButton.Visibility = _isWidgetHovered &&
                                     _taskManager.SelectedMode == TaskListMode.Daily
                ? Visibility.Visible
                : Visibility.Collapsed;

        private void TimerButton_Click(object sender, RoutedEventArgs e)
        {
            TimerValidationText.Visibility = Visibility.Collapsed;
            TimerMinutesInput.Text = string.Empty;
            TimerPopup.IsOpen = !TimerPopup.IsOpen;
            if (TimerPopup.IsOpen)
            {
                TimerMinutesInput.Focus();
            }
        }

        private void TimerPreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string minutes)
            {
                TimerMinutesInput.Text = minutes;
                TimerValidationText.Visibility = Visibility.Collapsed;
            }
        }

        private void StartTimer_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TimerMinutesInput.Text, out int minutes) || minutes <= 0)
            {
                TimerValidationText.Text = "Enter a duration greater than zero minutes.";
                TimerValidationText.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                _taskManager.AddTimer(TimeSpan.FromMinutes(minutes), DateTime.Now);
            }
            catch (ArgumentOutOfRangeException)
            {
                TimerValidationText.Text = "That duration is too long.";
                TimerValidationText.Visibility = Visibility.Visible;
                return;
            }

            SaveTasks();
            TimerPopup.IsOpen = false;
            UpdateTaskListMaxHeight();
        }

        private void CancelTimer_Click(object sender, RoutedEventArgs e) =>
            TimerPopup.IsOpen = false;

        private void TimerPopup_Closed(object? sender, EventArgs e)
        {
            if (!MainRoot.IsMouseOver)
            {
                HideHoverControls();
            }
        }

        private void RemoveTimerAfterDismiss(Guid timerId)
        {
            if (_taskManager.RemoveTimer(timerId))
            {
                SaveTasks();
            }
        }

        private void Reminder_Click(object sender, RoutedEventArgs e) =>
            _taskInteractions.OpenReminder(sender);

        private void TestAlarm_Click(object sender, RoutedEventArgs e) =>
            _alarmPresenter.Test(sender);

        private void DismissAlarm_Click(object sender, RoutedEventArgs e) =>
            _alarmPresenter.Dismiss();

        private void StartReminderTimer()
        {
            _reminderTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _reminderTimer.Tick += ReminderTimer_Tick;
            _reminderTimer.Start();
            Closed += (_, _) => _reminderTimer.Stop();
        }

        private void ReminderTimer_Tick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            _taskManager.RefreshTimerDisplays(now);
            TaskProcessingResult result = _taskManager.ProcessScheduledTasks(now);
            if (result.Changed)
            {
                SaveTasks();
            }

            foreach (TaskAlarm alarm in result.Alarms)
            {
                _alarmPresenter.Show(alarm.Task, alarm.Detail, alarm.Group, timerId: alarm.TimerId);
            }
        }

        private void TaskInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (!string.IsNullOrWhiteSpace(TaskInput.Text))
                {
                    _taskManager.AddTask(TaskInput.Text);
                    SaveTasks();
                }
                TaskInput.Text = "";
                TaskInput.Visibility = Visibility.Collapsed;
                UpdateTaskListMaxHeight();
            }
            else if (e.Key == Key.Escape)
            {
                // Allow Escape to dismiss the input without adding a task
                TaskInput.Text = "";
                TaskInput.Visibility = Visibility.Collapsed;
                UpdateTaskListMaxHeight();
            }
        }

        private void TaskInput_LostFocus(object sender, RoutedEventArgs e)
        {
            // Collapse the input if user clicks away without committing
            if (TaskInput.Visibility == Visibility.Visible)
            {
                TaskInput.Text = "";
                TaskInput.Visibility = Visibility.Collapsed;
                UpdateTaskListMaxHeight();
            }
        }

        private void TaskContentGrid_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTaskListMaxHeight();
        }

        private void TaskContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTaskListMaxHeight();
        }

        private void UpdateTaskListMaxHeight()
        {
            if (TaskContentGrid.ActualHeight <= 0)
            {
                return;
            }

            double inputHeight = TaskInput.Visibility == Visibility.Visible
                ? TaskInput.Height + TaskInput.Margin.Top + TaskInput.Margin.Bottom
                : 0;
            double addButtonHeight = AddButton.Height + AddButton.Margin.Top + AddButton.Margin.Bottom;
            double availableHeight = TaskContentGrid.ActualHeight
                - HeaderPanel.ActualHeight
                - HeaderPanel.Margin.Top
                - HeaderPanel.Margin.Bottom
                - inputHeight
                - (AddButton.Visibility == Visibility.Visible ? addButtonHeight : 0)
                - (TaskSetBar.Visibility == Visibility.Visible
                    ? TaskSetBar.ActualHeight + TaskSetBar.Margin.Top + TaskSetBar.Margin.Bottom
                    : 0);

            TaskList.MaxHeight = Math.Max(0, availableHeight);
        }

        private void Checkbox_Changed(object sender, RoutedEventArgs e) =>
            _taskInteractions.CheckChanged(sender);

        private void Delete_Click(object sender, RoutedEventArgs e) =>
            _taskInteractions.Delete(sender);

        private void RestoreTask_Click(object sender, RoutedEventArgs e) =>
            _taskInteractions.Restore(sender);

        private void ClearAlarm_Click(object sender, RoutedEventArgs e) =>
            _taskInteractions.ClearAlarm(sender);

        private void UrgentToggle_Click(object sender, RoutedEventArgs e) =>
            _taskInteractions.UrgentChanged(sender);

        private void TaskList_PreviewMouseMove(object sender, MouseEventArgs e) =>
            _taskInteractions.BeginReorder(e);

        private void TaskList_Drop(object sender, DragEventArgs e) =>
            _taskInteractions.Drop(e);

        private void LoadTasks()
        {
            try
            {
                _taskManager.Load(_taskManager.CustomTaskGroups.ToArray());
            }
            catch (Exception ex)
            {
                LogDiagnostic($"LoadTasks Error: {ex.Message}");
            }
            _taskGroupSelector.Refresh();
        }

        private void SaveTasks()
        {
            try
            {
                _taskManager.Save();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"SaveTasks Error: {ex.Message}");
            }
        }

        private void LoadWidgetSettings()
        {
            try
            {
                var settings = _settingsStore.Load();
                if (settings == null)
                {
                    return;
                }

                _taskManager.ResetCustomTaskGroups(settings.CustomTaskGroups ?? new List<string>());

                LockToggle.IsChecked = settings.IsLocked;
                PinToggle.IsChecked = settings.IsPinned;
                _keepBackgroundVisible = settings.KeepBackgroundVisible;
                HoverVisibilityToggle.IsChecked = _keepBackgroundVisible;

                if (!double.IsFinite(settings.Left) || !double.IsFinite(settings.Top) ||
                    !double.IsFinite(settings.Width) || !double.IsFinite(settings.Height) ||
                    settings.Width < MinWidth || settings.Height < MinHeight)
                {
                    return;
                }

                Width = settings.Width;
                Height = settings.Height;
                double minLeft = SystemParameters.VirtualScreenLeft;
                double minTop = SystemParameters.VirtualScreenTop;
                double maxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - Width);
                double maxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - Height);
                Left = Math.Clamp(settings.Left, minLeft, maxLeft);
                Top = Math.Clamp(settings.Top, minTop, maxTop);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"LoadWidgetSettings Error: {ex.Message}");
            }
        }

        private void ScheduleWidgetSettingsSave()
        {
            if (_settingsSaveTimer == null)
            {
                return;
            }

            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        private void SaveWidgetSettings()
        {
            try
            {
                var settings = new WidgetSettings
                {
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    IsLocked = _isLocked,
                    IsPinned = Topmost,
                    KeepBackgroundVisible = _keepBackgroundVisible,
                    CustomTaskGroups = _taskManager.CustomTaskGroups.ToList()
                };
                _settingsStore.Save(settings);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"SaveWidgetSettings Error: {ex.Message}");
            }
        }

        private void MakeDesktopWidget()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                LogDiagnostic("MakeDesktopWidget Started");

                // Step 1: Send magic message to Progman to spawn WorkerW
                IntPtr progman = WinAPI.FindWindow("Progman", null);
                LogDiagnostic($"Progman HWND: {progman}");
                WinAPI.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero,
                    WinAPI.SendMessageTimeoutFlags.SMTO_NORMAL, 1000, out _);

                // Step 2: Find the WorkerW that sits BEHIND desktop icons
                IntPtr workerw = IntPtr.Zero;
                WinAPI.EnumWindows((tophandle, lParam) =>
                {
                    IntPtr shellView = WinAPI.FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (shellView != IntPtr.Zero)
                    {
                        // The WorkerW we want is the SIBLING that appears AFTER this one
                        workerw = WinAPI.FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
                        return false; // Stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);

                LogDiagnostic($"WorkerW: {workerw}");

                // Step 3: Parent our window to WorkerW (or Progman as fallback)
                IntPtr targetParent = (workerw != IntPtr.Zero) ? workerw : progman;
                WinAPI.SetParent(hwnd, targetParent);
                LogDiagnostic($"SetParent to: {targetParent}");

                // Step 4: Set extended style to hide from Alt+Tab.
                // NOTE: We do NOT add WS_CHILD here. Adding WS_CHILD to a WPF window
                // that has been re-parented to the desktop (WorkerW) strips it of the
                // ability to receive keyboard focus, which breaks text input entirely.
                // WS_EX_TOOLWINDOW alone is sufficient to hide it from Alt+Tab.
                const int GWL_STYLE = -16;
                const int WS_POPUP  = unchecked((int)0x80000000);
                int style = WinAPI.GetWindowLong(hwnd, GWL_STYLE);
                // Ensure the window has WS_POPUP (not WS_CHILD) so focus still works
                WinAPI.SetWindowLong(hwnd, GWL_STYLE, (style & ~0x40000000) | WS_POPUP);

                int exStyle = WinAPI.GetWindowLong(hwnd, WinAPI.GWL_EXSTYLE);
                WinAPI.SetWindowLong(hwnd, WinAPI.GWL_EXSTYLE, exStyle | WinAPI.WS_EX_TOOLWINDOW);

                // Step 6: Position window and force it visible
                WinAPI.SetWindowPos(hwnd, WinAPI.HWND_TOP,
                    (int)this.Left, (int)this.Top, (int)this.Width, (int)this.Height,
                    WinAPI.SWP_NOACTIVATE | WinAPI.SWP_SHOWWINDOW);

                // Step 7: ShowWindow ensures it's rendered after the parent change
                WinAPI.ShowWindow(hwnd, WinAPI.SW_SHOWNA);

                LogDiagnostic("MakeDesktopWidget Finished Successfully");

                StartSafetyTimer();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"MakeDesktopWidget Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void StartSafetyTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                bool isVisible = WinAPI.IsWindowVisible(hwnd);
                LogDiagnostic($"Safety Check: IsVisible={isVisible}, State={this.WindowState}, HWND={hwnd}");

                // Re-surface if something hid us
                if (!isVisible && hwnd != IntPtr.Zero)
                {
                    LogDiagnostic("Safety Timer: Window was hidden! Re-showing...");
                    WinAPI.ShowWindow(hwnd, WinAPI.SW_SHOWNA);
                    WinAPI.SetWindowPos(hwnd, WinAPI.HWND_TOP,
                        (int)this.Left, (int)this.Top, (int)this.Width, (int)this.Height,
                        WinAPI.SWP_NOACTIVATE | WinAPI.SWP_SHOWWINDOW);
                }
            };
            timer.Start();
            LogDiagnostic("Safety Timer Started (5s interval)");
        }

    }
}
