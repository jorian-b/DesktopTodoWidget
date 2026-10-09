using System.Windows;
using System.Windows.Controls;

namespace DesktopTodoWidget
{
    internal sealed class TaskAlarmPresenter
    {
        private readonly TaskManager _taskManager;
        private readonly Action<string> _selectTaskGroup;
        private readonly Panel _overlay;
        private readonly TextBlock _taskText;
        private readonly TextBlock _detailText;
        private readonly Action<Guid> _removeTimer;
        private readonly Queue<(string Task, string Detail, string Group, Guid? TimerId)> _pendingAlarms = new();
        private Guid? _currentTimerId;

        public TaskAlarmPresenter(
            TaskManager taskManager,
            Action<string> selectTaskGroup,
            Panel overlay,
            TextBlock taskText,
            TextBlock detailText,
            Action<Guid> removeTimer)
        {
            _taskManager = taskManager;
            _selectTaskGroup = selectTaskGroup;
            _overlay = overlay;
            _taskText = taskText;
            _detailText = detailText;
            _removeTimer = removeTimer;
        }

        public void Test(object? sender)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem item)
            {
                Show(item.Text, "Test alarm", _taskManager.GetTaskSetName(item));
            }
        }

        public void Dismiss()
        {
            if (_currentTimerId is Guid timerId)
            {
                _removeTimer(timerId);
                _currentTimerId = null;
            }

            if (_pendingAlarms.Count > 0)
            {
                var nextAlarm = _pendingAlarms.Dequeue();
                Show(
                    nextAlarm.Task,
                    nextAlarm.Detail,
                    nextAlarm.Group,
                    enqueueWhenVisible: false,
                    timerId: nextAlarm.TimerId);
                return;
            }

            _overlay.Visibility = Visibility.Collapsed;
        }

        public void Show(
            string taskText,
            string detail,
            string? groupName = null,
            bool enqueueWhenVisible = true,
            Guid? timerId = null)
        {
            if (_overlay.Visibility == Visibility.Visible && enqueueWhenVisible)
            {
                _pendingAlarms.Enqueue((taskText, detail, groupName ?? "Daily", timerId));
                return;
            }

            _currentTimerId = timerId;
            _selectTaskGroup(groupName ?? "Daily");
            _taskText.Text = taskText;
            _detailText.Text = detail;
            _overlay.Visibility = Visibility.Visible;
            System.Media.SystemSounds.Exclamation.Play();
        }
    }
}
