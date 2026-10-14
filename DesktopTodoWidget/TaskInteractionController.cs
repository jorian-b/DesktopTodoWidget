using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopTodoWidget
{
    internal sealed class TaskInteractionController
    {
        private readonly TaskManager _taskManager;
        private readonly ListBox _taskList;
        private readonly Window _owner;
        private readonly Action _saveTasks;
        private TaskItem? _dragCandidate;
        private Point _dragStartPosition;
        private bool _isReminderDialogOpen;

        public TaskInteractionController(
            TaskManager taskManager,
            ListBox taskList,
            Window owner,
            Action saveTasks)
        {
            _taskManager = taskManager;
            _taskList = taskList;
            _owner = owner;
            _saveTasks = saveTasks;
        }

        public void OpenReminder(object? sender)
        {
            if (_isReminderDialogOpen ||
                (sender as FrameworkElement)?.DataContext is not TaskItem item)
            {
                return;
            }

            _isReminderDialogOpen = true;
            try
            {
                var dialog = new ReminderDialog(item) { Owner = _owner };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                _taskManager.SetReminder(
                    item,
                    dialog.DueAt,
                    dialog.ReminderMinutesBefore,
                    dialog.Recurrence,
                    dialog.ShowDaysBefore,
                    dialog.RecurrenceDays);
                _saveTasks();
            }
            finally
            {
                _isReminderDialogOpen = false;
            }
        }

        public void CheckChanged(object? sender)
        {
            if ((sender as CheckBox)?.DataContext is TaskItem item)
            {
                _taskManager.SetTaskChecked(item, DateTime.Now);
            }

            _saveTasks();
        }

        public void Delete(object? sender)
        {
            if ((sender as Button)?.DataContext is TaskItem item)
            {
                _taskManager.DeleteTask(item, DateTime.Now);
                _saveTasks();
            }
        }

        public void Restore(object? sender)
        {
            if ((sender as FrameworkElement)?.DataContext is not TaskItem item ||
                !_taskManager.RestoreTask(item))
            {
                return;
            }

            _saveTasks();
        }

        public void ClearAlarm(object? sender)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem item)
            {
                item.ReminderMinutesBefore = null;
                item.ReminderTriggered = false;
                _saveTasks();
                _taskList.Items.Refresh();
            }
        }

        public void UrgentChanged(object? sender)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskItem)
            {
                _saveTasks();
                _taskList.Items.Refresh();
            }
        }

        public void BeginReorder(MouseButtonEventArgs e)
        {
            _dragCandidate = null;
            if (e.OriginalSource is not DependencyObject source ||
                IsInteractiveElement(source))
            {
                return;
            }

            DependencyObject? container = ItemsControl.ContainerFromElement(_taskList, source);
            if (container is not ListBoxItem { DataContext: TaskItem item })
            {
                return;
            }

            _dragCandidate = item;
            _dragStartPosition = e.GetPosition(_taskList);
        }

        public void EndReorder() => _dragCandidate = null;

        public void BeginReorder(MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed ||
                _dragCandidate is not TaskItem item)
            {
                return;
            }

            Point currentPosition = e.GetPosition(_taskList);
            if (Math.Abs(currentPosition.X - _dragStartPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _dragStartPosition.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragCandidate = null;
            DragDrop.DoDragDrop(_taskList, item, DragDropEffects.Move);
        }

        public void Drop(DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TaskItem)) is not TaskItem dropped ||
                e.OriginalSource is not FrameworkElement source ||
                source.DataContext is not TaskItem target)
            {
                return;
            }

            if (_taskManager.MoveTask(dropped, target))
            {
                _saveTasks();
            }
        }

        private bool IsInteractiveElement(DependencyObject element)
        {
            DependencyObject? container = ItemsControl.ContainerFromElement(_taskList, element);
            for (DependencyObject? current = element;
                 current != null && current != container;
                 current = GetParent(current))
            {
                if (current is ButtonBase or TextBoxBase or Selector)
                {
                    return true;
                }
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject element) =>
            element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : element is FrameworkContentElement contentElement
                    ? contentElement.Parent
                    : null;
    }
}
