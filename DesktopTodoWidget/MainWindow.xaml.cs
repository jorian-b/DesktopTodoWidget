using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopTodoWidget
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<TaskItem> Tasks = new();
        private string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTodoWidget",
            "tasks.json");
        private string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTodoWidget",
            "settings.json");
        private bool _isLocked = false;
        private bool _keepBackgroundVisible;
        private TaskListMode _taskListMode = TaskListMode.Daily;
        private string? _selectedCustomTaskGroup;
        private bool _isUpdatingTaskSetSelector;
        private readonly ObservableCollection<string> _customTaskGroups = new();
        private bool _isWidgetHovered;
        private ICollectionView? _taskView;
        private System.Windows.Threading.DispatcherTimer? _reminderTimer;
        private System.Windows.Threading.DispatcherTimer? _settingsSaveTimer;
        private readonly Queue<(string Task, string Detail, string Group)> _pendingAlarms = new();

        public MainWindow()
        {
            LogDiagnostic("MainWindow Constructor Started");
            try
            {
                InitializeComponent();
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
                RefreshTaskSetSelector();
                LocationChanged += (_, _) => ScheduleWidgetSettingsSave();
                SizeChanged += (_, _) => ScheduleWidgetSettingsSave();
                Closed += (_, _) =>
                {
                    _settingsSaveTimer.Stop();
                    SaveWidgetSettings();
                };

                this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;

                this.StateChanged += MainWindow_StateChanged;

                TaskList.ItemsSource = Tasks;
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
            if (_taskListMode is TaskListMode.Inactive or TaskListMode.Trash)
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
            SetTaskBubbleSurfaces(true);
            LockToggle.Visibility = Visibility.Visible;
            PinToggle.Visibility = Visibility.Visible;
            HoverVisibilityToggle.Visibility = Visibility.Visible;
            AddButton.Visibility = Visibility.Visible;
            TaskSetBar.Visibility = Visibility.Visible;
            UpdateTaskListMaxHeight();
        }

        private void MainRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            if (TaskSetSelector.IsDropDownOpen)
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
            BackgroundSurface.Opacity = _keepBackgroundVisible ? 1 : 0;
            MainFrame.BorderBrush = Brushes.Transparent;
            SetTaskBubbleSurfaces(false);
            LockToggle.Visibility = Visibility.Collapsed;
            PinToggle.Visibility = Visibility.Collapsed;
            HoverVisibilityToggle.Visibility = Visibility.Collapsed;
            AddButton.Visibility = Visibility.Collapsed;
            TaskSetBar.Visibility = Visibility.Collapsed;
            UpdateTaskListMaxHeight();
        }

        private void TaskCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border taskCard)
            {
                SetTaskCardSurface(taskCard, _isWidgetHovered);
            }
        }

        private void SetTaskBubbleSurfaces(bool isHovered)
        {
            for (int index = 0; index < TaskList.Items.Count; index++)
            {
                if (TaskList.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                {
                    UpdateTaskCardSurface(container, isHovered);
                }
            }
        }

        private static void UpdateTaskCardSurface(DependencyObject element, bool isHovered)
        {
            if (element is Border border && Equals(border.Tag, "TaskCard"))
            {
                SetTaskCardSurface(border, isHovered);
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                UpdateTaskCardSurface(VisualTreeHelper.GetChild(element, index), isHovered);
            }
        }

        private static void SetTaskCardSurface(Border taskCard, bool isHovered)
        {
            taskCard.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
            taskCard.BorderBrush = isHovered
                ? new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        }

        private void TaskSetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingTaskSetSelector ||
                TaskSetSelector.SelectedItem is not ComboBoxItem selectedItem ||
                selectedItem.Tag is not string selection)
            {
                return;
            }

            _selectedCustomTaskGroup = null;
            switch (selection)
            {
                case "Daily":
                    _taskListMode = TaskListMode.Daily;
                    break;
                case "Inactive":
                    _taskListMode = TaskListMode.Inactive;
                    break;
                case "Trash":
                    _taskListMode = TaskListMode.Trash;
                    break;
                default:
                    if (!selection.StartsWith("Group:", StringComparison.Ordinal))
                    {
                        return;
                    }
                    _taskListMode = TaskListMode.Custom;
                    _selectedCustomTaskGroup = selection["Group:".Length..];
                    break;
            }

            if (_taskView != null)
            {
                _taskView.Refresh();
                UpdateTaskListMaxHeight();
            }
            UpdateDeleteTaskGroupButton();
        }

        private void AddTaskGroup_Click(object sender, RoutedEventArgs e)
        {
            var nameInput = CreateDialogTextInput(string.Empty);
            var dialog = new Window
            {
                Title = "New task group",
                Width = 300,
                Height = 150,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };

            var content = new StackPanel { Margin = new Thickness(18) };
            content.Children.Add(new TextBlock { Text = "Group name" });
            content.Children.Add(nameInput);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 72,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var createButton = new Button { Content = "Create", MinWidth = 72, IsDefault = true };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;
            createButton.Click += (_, _) =>
            {
                string name = nameInput.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show(dialog, "Enter a group name.", "Invalid group name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (new[] { "Daily", "Inactive", "Trash" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    _customTaskGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    MessageBox.Show(dialog, "That group name is already in use.", "Duplicate group name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _customTaskGroups.Add(name);
                RefreshTaskSetSelector(name);
                SaveWidgetSettings();
                dialog.DialogResult = true;
            };
            actions.Children.Add(cancelButton);
            actions.Children.Add(createButton);
            content.Children.Add(actions);
            dialog.Content = content;
            dialog.ShowDialog();
        }

        private void RefreshTaskSetSelector(string? selectedGroup = null)
        {
            _isUpdatingTaskSetSelector = true;
            TaskSetSelector.Items.Clear();
            TaskSetSelector.Items.Add(new ComboBoxItem { Content = "Daily", Tag = "Daily" });
            foreach (string group in _customTaskGroups)
            {
                TaskSetSelector.Items.Add(new ComboBoxItem { Content = group, Tag = $"Group:{group}" });
            }

            TaskSetSelector.Items.Add(new ComboBoxItem
            {
                Content = new Border
                {
                    Height = 1,
                    Margin = new Thickness(10, 3, 10, 3),
                    Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255))
                },
                IsEnabled = false,
                Focusable = false
            });
            TaskSetSelector.Items.Add(new ComboBoxItem { Content = "Inactive", Tag = "Inactive" });
            TaskSetSelector.Items.Add(new ComboBoxItem { Content = "Trash", Tag = "Trash" });

            string selection = selectedGroup == null
                ? _taskListMode switch
                {
                    TaskListMode.Daily => "Daily",
                    TaskListMode.Inactive => "Inactive",
                    TaskListMode.Trash => "Trash",
                    _ => $"Group:{_selectedCustomTaskGroup}"
                }
                : $"Group:{selectedGroup}";
            TaskSetSelector.SelectedItem = TaskSetSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, selection))
                ?? TaskSetSelector.Items[0];
            _isUpdatingTaskSetSelector = false;
            if (selectedGroup != null)
            {
                _taskListMode = TaskListMode.Custom;
                _selectedCustomTaskGroup = selectedGroup;
                _taskView?.Refresh();
            }

            UpdateDeleteTaskGroupButton();
        }

        private void UpdateDeleteTaskGroupButton()
        {
            DeleteTaskGroupButton.Visibility = _taskListMode == TaskListMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void DeleteTaskGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_taskListMode != TaskListMode.Custom || _selectedCustomTaskGroup is not string groupName)
            {
                return;
            }

            if (Tasks.Any(task => string.Equals(task.GroupName, groupName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    this,
                    "This group still contains tasks. Move or delete its tasks before deleting the group.",
                    "Group is not empty",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                this,
                $"Delete the empty group \"{groupName}\"?",
                "Delete task group",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            _customTaskGroups.Remove(groupName);
            _taskListMode = TaskListMode.Daily;
            _selectedCustomTaskGroup = null;
            RefreshTaskSetSelector();
            _taskView?.Refresh();
            SaveWidgetSettings();
        }

        private void SelectTaskGroup(string groupName)
        {
            if (string.Equals(groupName, "Daily", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(groupName, "Inactive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(groupName, "Trash", StringComparison.OrdinalIgnoreCase))
            {
                _taskListMode = string.Equals(groupName, "Inactive", StringComparison.OrdinalIgnoreCase)
                    ? TaskListMode.Inactive
                    : string.Equals(groupName, "Trash", StringComparison.OrdinalIgnoreCase)
                        ? TaskListMode.Trash
                        : TaskListMode.Daily;
                _selectedCustomTaskGroup = null;
                RefreshTaskSetSelector();
                _taskView?.Refresh();
                return;
            }

            if (_customTaskGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                RefreshTaskSetSelector(groupName);
                return;
            }

            _taskListMode = TaskListMode.Daily;
            _selectedCustomTaskGroup = null;
            RefreshTaskSetSelector();
            _taskView?.Refresh();
        }

        private static string GetTaskSetName(TaskItem item)
        {
            if (item.DeletedAt != null)
            {
                return "Trash";
            }

            return !item.IsActive && !string.IsNullOrWhiteSpace(item.Recurrence)
                ? "Inactive"
                : item.GroupName;
        }

        private void Reminder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TaskItem item)
            {
                return;
            }

            DateTime? originalDueAt = item.DueAt;
            int? originalReminderMinutesBefore = item.ReminderMinutesBefore;
            var datePicker = new DatePicker
            {
                SelectedDate = item.DueAt?.Date ?? DateTime.Today,
                Margin = new Thickness(0, 4, 0, 12)
            };
            var timeInput = CreateDialogTextInput(item.DueAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00");
            var reminderChoice = CreateDialogChoice();
            reminderChoice.SelectedValuePath = "Tag";
            reminderChoice.Items.Add(new ComboBoxItem { Content = "No alert", Tag = "none" });
            reminderChoice.Items.Add(new ComboBoxItem { Content = "At the scheduled time", Tag = "0" });
            reminderChoice.Items.Add(new ComboBoxItem { Content = "5 minutes before", Tag = "5" });
            reminderChoice.Items.Add(new ComboBoxItem { Content = "15 minutes before", Tag = "15" });
            reminderChoice.Items.Add(new ComboBoxItem { Content = "1 hour before", Tag = "60" });
            reminderChoice.SelectedValue = item.ReminderMinutesBefore?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var recurrenceChoice = CreateDialogChoice();
            recurrenceChoice.SelectedValuePath = "Tag";
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Does not repeat", Tag = "none" });
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Daily", Tag = "Daily" });
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Weekly", Tag = "Weekly" });
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Every 2 weeks", Tag = "Biweekly" });
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Monthly", Tag = "Monthly" });
            recurrenceChoice.Items.Add(new ComboBoxItem { Content = "Yearly", Tag = "Yearly" });
            recurrenceChoice.SelectedValue = item.Recurrence ?? "none";

            var dialog = new Window
            {
                Title = "Task date and reminder",
                Width = 310,
                Height = 400,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.Black,
                Content = null
            };
            var content = new StackPanel { Margin = new Thickness(18) };
            content.Children.Add(new TextBlock
            {
                Text = item.Text,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(new TextBlock { Text = "Date" });
            content.Children.Add(datePicker);
            content.Children.Add(new TextBlock { Text = "Time (24-hour, HH:mm)" });
            content.Children.Add(timeInput);
            content.Children.Add(new TextBlock { Text = "Alert" });
            content.Children.Add(reminderChoice);
            content.Children.Add(new TextBlock { Text = "Repeat" });
            content.Children.Add(recurrenceChoice);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelButton = new Button { Content = "Cancel", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var saveButton = new Button { Content = "Save", MinWidth = 72, IsDefault = true };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;
            saveButton.Click += (_, _) =>
            {
                if (datePicker.SelectedDate is not DateTime selectedDate ||
                    !TimeSpan.TryParseExact(timeInput.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var selectedTime) ||
                    selectedTime >= TimeSpan.FromDays(1))
                {
                    MessageBox.Show(dialog, "Select a date and enter a valid time in HH:mm format.", "Invalid date or time", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                item.DueAt = selectedDate.Date.Add(selectedTime);
                var selectedReminder = reminderChoice.SelectedValue?.ToString() ?? "none";
                item.ReminderMinutesBefore = selectedReminder == "none"
                    ? null
                    : int.Parse(selectedReminder, CultureInfo.InvariantCulture);
                var selectedRecurrence = recurrenceChoice.SelectedValue?.ToString() ?? "none";
                item.Recurrence = selectedRecurrence == "none" ? null : selectedRecurrence;
                if (selectedRecurrence == "none" && !item.IsActive)
                {
                    item.IsActive = true;
                    item.IsChecked = false;
                }
                if (item.DueAt != originalDueAt || item.ReminderMinutesBefore != originalReminderMinutesBefore)
                {
                    item.ReminderTriggered = false;
                }
                SaveTasks();
                _taskView?.Refresh();
                dialog.DialogResult = true;
            };
            actions.Children.Add(cancelButton);
            actions.Children.Add(saveButton);
            content.Children.Add(actions);
            dialog.Content = content;
            dialog.ShowDialog();
        }

        private void TestAlarm_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem item)
            {
                ShowAlarmOverlay(item.Text, "Test alarm", GetTaskSetName(item));
            }
        }

        private void DismissAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingAlarms.Count > 0)
            {
                var nextAlarm = _pendingAlarms.Dequeue();
                ShowAlarmOverlay(nextAlarm.Task, nextAlarm.Detail, nextAlarm.Group, enqueueWhenVisible: false);
                return;
            }

            AlarmOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShowAlarmOverlay(string taskText, string detail, string? groupName = null, bool enqueueWhenVisible = true)
        {
            if (AlarmOverlay.Visibility == Visibility.Visible && enqueueWhenVisible)
            {
                _pendingAlarms.Enqueue((taskText, detail, groupName ?? "Daily"));
                return;
            }

            SelectTaskGroup(groupName ?? "Daily");
            AlarmTaskText.Text = taskText;
            AlarmDetailText.Text = detail;
            AlarmOverlay.Visibility = Visibility.Visible;
            System.Media.SystemSounds.Exclamation.Play();
        }

        private static ComboBox CreateDialogChoice()
        {
            var choice = new ComboBox
            {
                Margin = new Thickness(0, 4, 0, 16)
            };
            return choice;
        }

        private static TextBox CreateDialogTextInput(string text)
        {
            return new TextBox
            {
                Text = text,
                Height = 30,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 12),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private void StartReminderTimer()
        {
            _reminderTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _reminderTimer.Tick += ReminderTimer_Tick;
            _reminderTimer.Start();
            Closed += (_, _) => _reminderTimer.Stop();
        }

        private void ReminderTimer_Tick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            DateTime recurringTaskStartTime = now.Date.AddHours(2);
            bool taskListChanged = false;
            var tasksToRemove = new List<TaskItem>();
            foreach (var item in Tasks.ToList())
            {
                if (item.DeletedAt is DateTime deletedAt && now - deletedAt >= TimeSpan.FromDays(1))
                {
                    tasksToRemove.Add(item);
                    continue;
                }

                if (!item.IsActive && !string.IsNullOrWhiteSpace(item.Recurrence) &&
                    item.DeletedAt == null &&
                    item.DueAt is DateTime nextOccurrence &&
                    nextOccurrence.Date <= now.Date &&
                    now >= recurringTaskStartTime)
                {
                    item.IsActive = true;
                    item.IsChecked = false;
                    if (nextOccurrence <= now)
                    {
                        item.ReminderTriggered = true;
                    }
                    taskListChanged = true;
                }

                if (item.DeletedAt != null ||
                    (item.IsChecked && (item.IsActive || string.IsNullOrWhiteSpace(item.Recurrence))) ||
                    item.ReminderTriggered || item.DueAt is not DateTime dueAt ||
                    item.ReminderMinutesBefore is not int minutesBefore ||
                    now < dueAt.AddMinutes(-minutesBefore))
                {
                    continue;
                }

                item.ReminderTriggered = true;
                SaveTasks();
                DateTime alarmAt = dueAt.AddMinutes(-minutesBefore);
                string alarmDetail = minutesBefore == 0
                    ? $"Alarm at {alarmAt:MMM d, HH:mm}"
                    : $"Alarm {minutesBefore} minutes before at {alarmAt:MMM d, HH:mm}";
                ShowAlarmOverlay(item.Text, alarmDetail, GetTaskSetName(item));
            }

            foreach (var item in tasksToRemove)
            {
                Tasks.Remove(item);
                taskListChanged = true;
            }

            if (taskListChanged)
            {
                SaveTasks();
                _taskView?.Refresh();
            }
        }

        private void TaskInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (!string.IsNullOrWhiteSpace(TaskInput.Text))
                {
                    Tasks.Insert(0, new TaskItem
                    {
                        Text = TaskInput.Text,
                        GroupName = _taskListMode == TaskListMode.Custom
                            ? _selectedCustomTaskGroup ?? "Daily"
                            : "Daily"
                    });
                    SaveTasks();
                    _taskView?.Refresh();
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

        private void Checkbox_Changed(object sender, RoutedEventArgs e)
        {
            if ((sender as CheckBox)?.DataContext is TaskItem item)
            {
                if (item.IsChecked && item.IsActive && !string.IsNullOrWhiteSpace(item.Recurrence) && item.DueAt is DateTime scheduledAt)
                {
                    item.DueAt = GetNextOccurrence(scheduledAt, item.Recurrence);
                    item.IsActive = false;
                    item.ReminderTriggered = false;
                    item.CompletedAt = null;
                    _taskView?.Refresh();
                }
                else if (!item.IsChecked && !item.IsActive)
                {
                    item.IsActive = true;
                    item.ReminderTriggered = false;
                    item.CompletedAt = null;
                    _taskView?.Refresh();
                }
                else
                {
                    item.CompletedAt = item.IsChecked ? DateTime.Now : null;
                }
            }

            SaveTasks();
        }

        private static DateTime GetNextOccurrence(DateTime scheduledAt, string recurrence)
        {
            DateTime nextOccurrence = AdvanceOccurrence(scheduledAt, recurrence);
            while (nextOccurrence <= DateTime.Now)
            {
                nextOccurrence = AdvanceOccurrence(nextOccurrence, recurrence);
            }
            return nextOccurrence;
        }

        private static DateTime AdvanceOccurrence(DateTime occurrence, string recurrence)
        {
            return recurrence switch
            {
                "Biweekly" => occurrence.AddDays(14),
                "Weekly" => occurrence.AddDays(7),
                "Monthly" => occurrence.AddMonths(1),
                "Yearly" => occurrence.AddYears(1),
                _ => occurrence.AddDays(1)
            };
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TaskItem item)
            {
                if (_taskListMode == TaskListMode.Trash)
                {
                    Tasks.Remove(item);
                }
                else if (_taskListMode == TaskListMode.Inactive)
                {
                    item.IsActive = false;
                    item.DeletedAt = DateTime.Now;
                }
                else if (!string.IsNullOrWhiteSpace(item.Recurrence) && item.DueAt is DateTime scheduledAt)
                {
                    item.DueAt = GetNextOccurrence(scheduledAt, item.Recurrence);
                    item.IsActive = false;
                    item.IsChecked = false;
                    item.CompletedAt = null;
                    item.ReminderTriggered = false;
                }
                else
                {
                    item.IsActive = false;
                    item.IsChecked = false;
                    item.CompletedAt = null;
                    item.DeletedAt = DateTime.Now;
                }
                _taskView?.Refresh();
                SaveTasks();
            }
        }

        private void RestoreTask_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TaskItem item ||
                item.DeletedAt == null)
            {
                return;
            }

            item.DeletedAt = null;
            if (string.IsNullOrWhiteSpace(item.Recurrence))
            {
                item.IsActive = true;
                item.IsChecked = false;
                item.CompletedAt = null;
            }
            item.ReminderTriggered = false;
            _taskView?.Refresh();
            SaveTasks();
        }

        private void ClearAlarm_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem item)
            {
                item.ReminderMinutesBefore = null;
                item.ReminderTriggered = false;
                SaveTasks();
                TaskList.Items.Refresh();
            }
        }

        private void UrgentToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem)
            {
                SaveTasks();
                TaskList.Items.Refresh();
            }
        }

        private void TaskList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var item = TaskList.SelectedItem;
                if (item != null)
                {
                    DragDrop.DoDragDrop(TaskList, item, DragDropEffects.Move);
                }
            }
        }

        private void TaskList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TaskItem)) is TaskItem dropped)
            {
                var target = ((FrameworkElement)e.OriginalSource).DataContext as TaskItem;
                if (target == null || dropped == null) return;

                int oldIndex = Tasks.IndexOf(dropped);
                int newIndex = Tasks.IndexOf(target);

                Tasks.Move(oldIndex, newIndex);
                SaveTasks();
            }
        }

        private void LoadTasks()
        {
            try
            {
                string fullPath = SavePath;
                LogDiagnostic($"Loading tasks from: {fullPath}");

                if (File.Exists(fullPath))
                {
                    string json = File.ReadAllText(fullPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var data = JsonSerializer.Deserialize<ObservableCollection<TaskItem>>(json);
                        if (data != null)
                        {
                            Tasks = data;
                            foreach (string group in Tasks
                                         .Select(task => task.GroupName)
                                         .Where(group => !string.IsNullOrWhiteSpace(group) &&
                                                         !string.Equals(group, "Daily", StringComparison.OrdinalIgnoreCase) &&
                                                         !new[] { "Inactive", "Trash" }.Contains(group, StringComparer.OrdinalIgnoreCase))
                                         .Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (!_customTaskGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                                {
                                    _customTaskGroups.Add(group);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"LoadTasks Error: {ex.Message}");
                Tasks = new();
            }
            finally
            {
                RefreshTaskSetSelector();
                _taskView = CollectionViewSource.GetDefaultView(Tasks);
                _taskView.Filter = task => task is TaskItem item && _taskListMode switch
                {
                    TaskListMode.Daily => item.IsActive && item.DeletedAt == null &&
                                          string.Equals(item.GroupName, "Daily", StringComparison.OrdinalIgnoreCase),
                    TaskListMode.Inactive => !item.IsActive && item.DeletedAt == null &&
                                             !string.IsNullOrWhiteSpace(item.Recurrence),
                    TaskListMode.Trash => item.DeletedAt != null,
                    TaskListMode.Custom => item.IsActive && item.DeletedAt == null &&
                                           string.Equals(item.GroupName, _selectedCustomTaskGroup, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                TaskList.ItemsSource = _taskView;
            }
        }

        private void SaveTasks()
        {
            try
            {
                string fullPath = SavePath;
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, JsonSerializer.Serialize(Tasks));
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
                if (!File.Exists(SettingsPath))
                {
                    return;
                }

                var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath));
                if (settings == null)
                {
                    return;
                }

                _customTaskGroups.Clear();
                foreach (string group in settings.CustomTaskGroups ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(group) &&
                        !new[] { "Daily", "Inactive", "Trash" }.Contains(group, StringComparer.OrdinalIgnoreCase) &&
                        !_customTaskGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                    {
                        _customTaskGroups.Add(group);
                    }
                }

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
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var settings = new WidgetSettings
                {
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    IsLocked = _isLocked,
                    IsPinned = Topmost,
                    KeepBackgroundVisible = _keepBackgroundVisible,
                    CustomTaskGroups = _customTaskGroups.ToList()
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
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

        private enum TaskListMode
        {
            Daily,
            Custom,
            Inactive,
            Trash
        }

    }

    internal sealed class WidgetSettings
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPinned { get; set; }
        public bool KeepBackgroundVisible { get; set; }
        public List<string> CustomTaskGroups { get; set; } = new();
    }

    public class TaskItem
    {
        public bool IsChecked { get; set; }
        public string Text { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public int? ReminderMinutesBefore { get; set; }
        public bool ReminderTriggered { get; set; }
        public string? Recurrence { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsUrgent { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string GroupName { get; set; } = "Daily";

        [JsonIgnore]
        public bool HasReminder => DueAt != null && ReminderMinutesBefore != null;

        [JsonIgnore]
        public DateTime? DiscardAt => DeletedAt?.AddDays(1);

        [JsonIgnore]
        public bool HasPendingRemoval => DeletedAt != null;

        [JsonIgnore]
        public string DiscardToolTip => DiscardAt is DateTime discardAt
            ? $"Will be discarded on {discardAt:MMM d, yyyy 'at' HH:mm}"
            : string.Empty;

        [JsonIgnore]
        public string ReminderToolTip
        {
            get
            {
                if (DueAt is not DateTime dueAt || ReminderMinutesBefore is not int minutesBefore)
                {
                    return "No alarm set";
                }

                DateTime alarmAt = dueAt.AddMinutes(-minutesBefore);
                return minutesBefore == 0
                    ? $"Alarm at {alarmAt:MMM d, HH:mm}"
                    : $"Alarm {minutesBefore} minutes before at {alarmAt:MMM d, HH:mm}";
            }
        }

        [JsonIgnore]
        public string DeleteToolTip => DeletedAt != null
            ? "Delete permanently"
            : !IsActive
                ? "Move to Trash; permanently remove after one day"
            : !string.IsNullOrWhiteSpace(Recurrence)
                ? "Skip this occurrence; task returns at its next occurrence"
                : "Move to Trash; permanently remove after one day";
    }
}
