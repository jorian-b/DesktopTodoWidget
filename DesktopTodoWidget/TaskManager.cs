using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;

namespace DesktopTodoWidget
{
    internal sealed class TaskManager
    {
        private static readonly string[] ReservedGroupNames = ["Daily", "Inactive", "Trash"];
        private readonly string _savePath;

        public ObservableCollection<TaskItem> Tasks { get; } = new();
        public ObservableCollection<string> CustomTaskGroups { get; } = new();
        public ICollectionView TaskView { get; }
        public TaskListMode SelectedMode { get; private set; } = TaskListMode.Daily;
        public string? SelectedCustomGroup { get; private set; }

        public TaskManager(string savePath)
        {
            _savePath = savePath;
            TaskView = CollectionViewSource.GetDefaultView(Tasks);
            TaskView.Filter = IsVisibleInSelectedTaskSet;
        }

        public void Load(IEnumerable<string>? configuredGroups = null)
        {
            if (configuredGroups != null)
            {
                foreach (string group in configuredGroups)
                {
                    AddGroupIfValid(group);
                }
            }

            Tasks.Clear();
            try
            {
                if (File.Exists(_savePath))
                {
                    string json = File.ReadAllText(_savePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loadedTasks = JsonSerializer.Deserialize<List<TaskItem>>(json);
                        if (loadedTasks != null)
                        {
                            Tasks.Clear();
                            foreach (TaskItem item in loadedTasks)
                            {
                                Tasks.Add(item);
                                AddGroupIfValid(item.GroupName);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Tasks.Clear();
                System.Diagnostics.Debug.WriteLine($"Task load failed: {ex}");
                throw;
            }

            TaskView.Refresh();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
                File.WriteAllText(_savePath, JsonSerializer.Serialize(Tasks));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Task save failed: {ex}");
                throw;
            }
        }

        public bool SelectTaskSet(string selection)
        {
            SelectedCustomGroup = null;
            switch (selection)
            {
                case "Daily":
                    SelectedMode = TaskListMode.Daily;
                    break;
                case "Inactive":
                    SelectedMode = TaskListMode.Inactive;
                    break;
                case "Trash":
                    SelectedMode = TaskListMode.Trash;
                    break;
                default:
                    if (!selection.StartsWith("Group:", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    string group = selection["Group:".Length..];
                    if (!CustomTaskGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    SelectedMode = TaskListMode.Custom;
                    SelectedCustomGroup = group;
                    break;
            }

            TaskView.Refresh();
            return true;
        }

        public void SelectTaskGroup(string groupName)
        {
            if (IsReservedGroup(groupName))
            {
                SelectTaskSet(groupName);
            }
            else if (CustomTaskGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                SelectTaskSet($"Group:{groupName}");
            }
            else
            {
                SelectTaskSet("Daily");
            }
        }

        public bool TryAddCustomGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || IsReservedGroup(name) ||
                CustomTaskGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            CustomTaskGroups.Add(name);
            SelectTaskSet($"Group:{name}");
            return true;
        }

        public void ResetCustomTaskGroups(IEnumerable<string> groups)
        {
            CustomTaskGroups.Clear();
            foreach (string group in groups)
            {
                AddGroupIfValid(group);
            }
        }

        public bool CustomGroupHasTasks(string groupName) =>
            Tasks.Any(task => string.Equals(task.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

        public bool RemoveEmptyCustomGroup(string groupName)
        {
            if (CustomGroupHasTasks(groupName) ||
                !CustomTaskGroups.Remove(groupName))
            {
                return false;
            }

            SelectTaskSet("Daily");
            return true;
        }

        public TaskItem AddTask(string text)
        {
            var item = new TaskItem
            {
                Text = text,
                GroupName = SelectedMode == TaskListMode.Custom
                    ? SelectedCustomGroup ?? "Daily"
                    : "Daily"
            };
            Tasks.Insert(0, item);
            TaskView.Refresh();
            return item;
        }

        public bool DeleteTask(TaskItem item, DateTime now)
        {
            if (SelectedMode == TaskListMode.Trash)
            {
                return Tasks.Remove(item);
            }

            if (SelectedMode == TaskListMode.Inactive)
            {
                item.IsActive = false;
                item.DeletedAt = now;
            }
            else if (!string.IsNullOrWhiteSpace(item.Recurrence) && item.DueAt is DateTime scheduledAt)
            {
                item.DueAt = GetNextOccurrence(scheduledAt, item.Recurrence, now);
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
                item.DeletedAt = now;
            }

            TaskView.Refresh();
            return true;
        }

        public bool RestoreTask(TaskItem item)
        {
            if (item.DeletedAt == null)
            {
                return false;
            }

            item.DeletedAt = null;
            if (string.IsNullOrWhiteSpace(item.Recurrence))
            {
                item.IsActive = true;
                item.IsChecked = false;
                item.CompletedAt = null;
            }

            item.ReminderTriggered = false;
            TaskView.Refresh();
            return true;
        }

        public void SetTaskChecked(TaskItem item, DateTime now)
        {
            if (item.IsChecked && item.IsActive &&
                !string.IsNullOrWhiteSpace(item.Recurrence) &&
                item.DueAt is DateTime scheduledAt)
            {
                item.DueAt = GetNextOccurrence(scheduledAt, item.Recurrence, now);
                item.IsActive = false;
                item.ReminderTriggered = false;
                item.CompletedAt = null;
                TaskView.Refresh();
            }
            else if (!item.IsChecked && !item.IsActive)
            {
                item.IsActive = true;
                item.ReminderTriggered = false;
                item.CompletedAt = null;
                TaskView.Refresh();
            }
            else
            {
                item.CompletedAt = item.IsChecked ? now : null;
            }
        }

        public void SetReminder(TaskItem item, DateTime dueAt, int? minutesBefore, string? recurrence)
        {
            bool scheduleChanged = item.DueAt != dueAt ||
                                   item.ReminderMinutesBefore != minutesBefore;
            item.DueAt = dueAt;
            item.ReminderMinutesBefore = minutesBefore;
            item.Recurrence = recurrence;

            if (string.IsNullOrWhiteSpace(recurrence) && !item.IsActive)
            {
                item.IsActive = true;
                item.IsChecked = false;
            }

            if (scheduleChanged)
            {
                item.ReminderTriggered = false;
            }

            TaskView.Refresh();
        }

        public bool MoveTask(TaskItem task, TaskItem target)
        {
            int oldIndex = Tasks.IndexOf(task);
            int newIndex = Tasks.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            {
                return false;
            }

            Tasks.Move(oldIndex, newIndex);
            return true;
        }

        public string GetTaskSetName(TaskItem item)
        {
            if (item.DeletedAt != null)
            {
                return "Trash";
            }

            return !item.IsActive && !string.IsNullOrWhiteSpace(item.Recurrence)
                ? "Inactive"
                : item.GroupName;
        }

        public TaskProcessingResult ProcessScheduledTasks(DateTime now)
        {
            bool changed = false;
            var alarms = new List<TaskAlarm>();
            var expiredTasks = new List<TaskItem>();
            DateTime recurringTaskStartTime = now.Date.AddHours(2);

            foreach (TaskItem item in Tasks)
            {
                if (item.DeletedAt is DateTime deletedAt && now - deletedAt >= TimeSpan.FromDays(1))
                {
                    expiredTasks.Add(item);
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
                    changed = true;
                }

                if (item.DeletedAt != null ||
                    (item.IsChecked && (item.IsActive || string.IsNullOrWhiteSpace(item.Recurrence))) ||
                    item.ReminderTriggered ||
                    item.DueAt is not DateTime dueAt ||
                    item.ReminderMinutesBefore is not int minutesBefore ||
                    now < dueAt.AddMinutes(-minutesBefore))
                {
                    continue;
                }

                item.ReminderTriggered = true;
                DateTime alarmAt = dueAt.AddMinutes(-minutesBefore);
                string detail = minutesBefore == 0
                    ? $"Alarm at {alarmAt:MMM d, HH:mm}"
                    : $"Alarm {minutesBefore} minutes before at {alarmAt:MMM d, HH:mm}";
                alarms.Add(new TaskAlarm(item.Text, detail, GetTaskSetName(item)));
                changed = true;
            }

            foreach (TaskItem expiredTask in expiredTasks)
            {
                Tasks.Remove(expiredTask);
                changed = true;
            }

            if (changed)
            {
                TaskView.Refresh();
            }

            return new TaskProcessingResult(alarms, changed);
        }

        public static DateTime GetNextOccurrence(DateTime scheduledAt, string recurrence, DateTime now)
        {
            DateTime nextOccurrence = AdvanceOccurrence(scheduledAt, recurrence);
            while (nextOccurrence <= now)
            {
                nextOccurrence = AdvanceOccurrence(nextOccurrence, recurrence);
            }

            return nextOccurrence;
        }

        private bool IsVisibleInSelectedTaskSet(object candidate)
        {
            if (candidate is not TaskItem item)
            {
                return false;
            }

            return SelectedMode switch
            {
                TaskListMode.Daily => item.IsActive && item.DeletedAt == null &&
                                      string.Equals(item.GroupName, "Daily", StringComparison.OrdinalIgnoreCase),
                TaskListMode.Inactive => !item.IsActive && item.DeletedAt == null &&
                                         !string.IsNullOrWhiteSpace(item.Recurrence),
                TaskListMode.Trash => item.DeletedAt != null,
                TaskListMode.Custom => item.IsActive && item.DeletedAt == null &&
                                       string.Equals(item.GroupName, SelectedCustomGroup, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private void AddGroupIfValid(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !IsReservedGroup(name) &&
                !CustomTaskGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                CustomTaskGroups.Add(name);
            }
        }

        private static bool IsReservedGroup(string name) =>
            ReservedGroupNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        private static DateTime AdvanceOccurrence(DateTime occurrence, string recurrence) =>
            recurrence switch
            {
                "Biweekly" => occurrence.AddDays(14),
                "Weekly" => occurrence.AddDays(7),
                "Monthly" => occurrence.AddMonths(1),
                "Yearly" => occurrence.AddYears(1),
                _ => occurrence.AddDays(1)
            };
    }

    internal enum TaskListMode
    {
        Daily,
        Custom,
        Inactive,
        Trash
    }

    internal sealed record TaskAlarm(string Task, string Detail, string Group);

    internal sealed record TaskProcessingResult(IReadOnlyList<TaskAlarm> Alarms, bool Changed);
}
