using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopTodoWidget
{
    internal sealed class TaskInteractionController
    {
        private readonly TaskManager _taskManager;
        private readonly ListBox _taskList;
        private readonly Window _owner;
        private readonly Action _saveTasks;

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
            if ((sender as FrameworkElement)?.DataContext is not TaskItem item)
            {
                return;
            }

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
                dialog.RecurrenceDays);
            _saveTasks();
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

        public void BeginReorder(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _taskList.SelectedItem is { } item)
            {
                DragDrop.DoDragDrop(_taskList, item, DragDropEffects.Move);
            }
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
    }
}
