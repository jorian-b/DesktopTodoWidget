using System.Text.Json.Serialization;

namespace DesktopTodoWidget
{
    public class TaskItem
    {
        public bool IsChecked { get; set; }
        public string Text { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public int? ReminderMinutesBefore { get; set; }
        public bool ReminderTriggered { get; set; }
        public string? Recurrence { get; set; }
        public List<DayOfWeek> RecurrenceDays { get; set; } = new();
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
