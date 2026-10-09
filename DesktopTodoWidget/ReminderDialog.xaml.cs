using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DesktopTodoWidget
{
    public partial class ReminderDialog : Window
    {
        public DateTime DueAt { get; private set; }
        public int? ReminderMinutesBefore { get; private set; }
        public string? Recurrence { get; private set; }
        public IReadOnlyList<DayOfWeek> RecurrenceDays { get; private set; } = Array.Empty<DayOfWeek>();

        public ReminderDialog(TaskItem item)
        {
            InitializeComponent();
            TaskName.Text = item.Text;
            DueDatePicker.SelectedDate = item.DueAt?.Date ?? DateTime.Today;
            TimeInput.Text = item.DueAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";

            AlertChoice.Items.Add(new ComboBoxItem { Content = "No alert", Tag = "none" });
            AlertChoice.Items.Add(new ComboBoxItem { Content = "At the scheduled time", Tag = "0" });
            AlertChoice.Items.Add(new ComboBoxItem { Content = "5 minutes before", Tag = "5" });
            AlertChoice.Items.Add(new ComboBoxItem { Content = "15 minutes before", Tag = "15" });
            AlertChoice.Items.Add(new ComboBoxItem { Content = "1 hour before", Tag = "60" });
            AlertChoice.SelectedValue = item.ReminderMinutesBefore?.ToString(CultureInfo.InvariantCulture) ?? "none";

            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Does not repeat", Tag = "none" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Daily", Tag = "Daily" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Weekly", Tag = "Weekly" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Every 2 weeks", Tag = "Biweekly" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Monthly", Tag = "Monthly" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Yearly", Tag = "Yearly" });
            RecurrenceChoice.Items.Add(new ComboBoxItem { Content = "Selected weekdays", Tag = TaskManager.SelectedWeekdaysRecurrence });
            RecurrenceChoice.SelectedValue = item.Recurrence ?? "none";

            foreach (CheckBox weekdayCheckBox in GetWeekdayCheckBoxes())
            {
                if (Enum.TryParse(weekdayCheckBox.Tag?.ToString(), out DayOfWeek weekday))
                {
                    weekdayCheckBox.IsChecked = item.RecurrenceDays?.Contains(weekday) == true;
                }
            }
        }

        private void RecurrenceChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool usesSelectedWeekdays =
                Equals(RecurrenceChoice.SelectedValue, TaskManager.SelectedWeekdaysRecurrence);
            WeekdaysPanel.Visibility = usesSelectedWeekdays ? Visibility.Visible : Visibility.Collapsed;
            RecurrenceHelp.Text = usesSelectedWeekdays
                ? "Repeats every week on the selected days. The date above is the first eligible date."
                : "Recurring tasks return to the active list at 2:00 AM on their next scheduled day.";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DueDatePicker.SelectedDate is not DateTime selectedDate ||
                !TimeSpan.TryParseExact(TimeInput.Text, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan selectedTime) ||
                selectedTime >= TimeSpan.FromDays(1))
            {
                ShowValidationError("Choose a date and enter a valid 24-hour time, such as 09:30.");
                return;
            }

            string selectedRecurrence = RecurrenceChoice.SelectedValue?.ToString() ?? "none";
            DayOfWeek[] selectedWeekdays = GetWeekdayCheckBoxes()
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => Enum.Parse<DayOfWeek>(checkBox.Tag!.ToString()!))
                .Order()
                .ToArray();

            if (selectedRecurrence == TaskManager.SelectedWeekdaysRecurrence && selectedWeekdays.Length == 0)
            {
                ShowValidationError("Select at least one day of the week.");
                return;
            }

            string selectedAlert = AlertChoice.SelectedValue?.ToString() ?? "none";
            int? selectedMinutesBefore = null;
            if (selectedAlert != "none")
            {
                if (!int.TryParse(selectedAlert, NumberStyles.None, CultureInfo.InvariantCulture, out int minutesBefore))
                {
                    ShowValidationError("Choose a valid alert option.");
                    return;
                }

                selectedMinutesBefore = minutesBefore;
            }

            DueAt = selectedDate.Date.Add(selectedTime);
            ReminderMinutesBefore = selectedMinutesBefore;
            Recurrence = selectedRecurrence == "none" ? null : selectedRecurrence;
            RecurrenceDays = selectedRecurrence == TaskManager.SelectedWeekdaysRecurrence
                ? selectedWeekdays
                : Array.Empty<DayOfWeek>();
            DialogResult = true;
        }

        private IEnumerable<CheckBox> GetWeekdayCheckBoxes() =>
            WeekdaysPanel.Children
                .OfType<UniformGrid>()
                .SelectMany(grid => grid.Children.OfType<CheckBox>());

        private void ShowValidationError(string message)
        {
            ValidationMessage.Text = message;
            ValidationMessage.Visibility = Visibility.Visible;
        }
    }
}
