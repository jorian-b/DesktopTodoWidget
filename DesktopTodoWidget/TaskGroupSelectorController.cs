using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopTodoWidget
{
    internal sealed class TaskGroupSelectorController
    {
        private readonly ComboBox _selector;
        private readonly Button _deleteGroupButton;
        private readonly TaskManager _taskManager;
        private readonly Window _owner;
        private readonly Action _saveSettings;

        public bool IsUpdatingSelection { get; private set; }

        public TaskGroupSelectorController(
            ComboBox selector,
            Button deleteGroupButton,
            TaskManager taskManager,
            Window owner,
            Action saveSettings)
        {
            _selector = selector;
            _deleteGroupButton = deleteGroupButton;
            _taskManager = taskManager;
            _owner = owner;
            _saveSettings = saveSettings;
        }

        public bool HandleSelectionChanged()
        {
            if (IsUpdatingSelection ||
                _selector.SelectedItem is not ComboBoxItem selectedItem ||
                selectedItem.Tag is not string selection ||
                !_taskManager.SelectTaskSet(selection))
            {
                return false;
            }

            UpdateDeleteButton();
            return true;
        }

        public void Refresh(string? selectedGroup = null)
        {
            IsUpdatingSelection = true;
            try
            {
                _selector.Items.Clear();
                _selector.Items.Add(new ComboBoxItem { Content = "Daily", Tag = "Daily" });
                foreach (string group in _taskManager.CustomTaskGroups)
                {
                    _selector.Items.Add(new ComboBoxItem { Content = group, Tag = $"Group:{group}" });
                }

                _selector.Items.Add(new ComboBoxItem
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
                _selector.Items.Add(new ComboBoxItem { Content = "Inactive", Tag = "Inactive" });
                _selector.Items.Add(new ComboBoxItem { Content = "Trash", Tag = "Trash" });

                string selection = selectedGroup == null
                    ? _taskManager.SelectedMode switch
                    {
                        TaskListMode.Daily => "Daily",
                        TaskListMode.Inactive => "Inactive",
                        TaskListMode.Trash => "Trash",
                        _ => $"Group:{_taskManager.SelectedCustomGroup}"
                    }
                    : $"Group:{selectedGroup}";
                _selector.SelectedItem = _selector.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => Equals(item.Tag, selection))
                    ?? _selector.Items[0];
            }
            finally
            {
                IsUpdatingSelection = false;
            }

            UpdateDeleteButton();
        }

        public void AddCustomGroup()
        {
            var nameInput = CreateTextInput(string.Empty);
            var dialog = new Window
            {
                Title = "New task group",
                Width = 300,
                Height = 150,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = _owner,
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

                if (!_taskManager.TryAddCustomGroup(name))
                {
                    MessageBox.Show(dialog, "That group name is already in use.", "Invalid group name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Refresh(name);
                _saveSettings();
                dialog.DialogResult = true;
            };
            actions.Children.Add(cancelButton);
            actions.Children.Add(createButton);
            content.Children.Add(actions);
            dialog.Content = content;
            dialog.ShowDialog();
        }

        public void DeleteSelectedGroup()
        {
            if (_taskManager.SelectedMode != TaskListMode.Custom ||
                _taskManager.SelectedCustomGroup is not string groupName)
            {
                return;
            }

            if (_taskManager.CustomGroupHasTasks(groupName))
            {
                MessageBox.Show(
                    _owner,
                    "This group still contains tasks. Move or delete its tasks before deleting the group.",
                    "Group is not empty",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                _owner,
                $"Delete the empty group \"{groupName}\"?",
                "Delete task group",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            _taskManager.RemoveEmptyCustomGroup(groupName);
            Refresh();
            _saveSettings();
        }

        public void SelectTaskGroup(string groupName)
        {
            _taskManager.SelectTaskGroup(groupName);
            Refresh();
        }

        private void UpdateDeleteButton() =>
            _deleteGroupButton.Visibility = _taskManager.SelectedMode == TaskListMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

        private static TextBox CreateTextInput(string text) =>
            new()
            {
                Text = text,
                Height = 30,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 12),
                VerticalContentAlignment = VerticalAlignment.Center
            };
    }
}
