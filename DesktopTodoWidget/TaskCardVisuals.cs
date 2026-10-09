using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopTodoWidget
{
    internal static class TaskCardVisuals
    {
        public static void Refresh(ItemsControl taskList, bool isHovered)
        {
            for (int index = 0; index < taskList.Items.Count; index++)
            {
                if (taskList.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                {
                    UpdateTree(container, isHovered);
                }
            }
        }

        public static void Update(Border taskCard, bool isHovered)
        {
            taskCard.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
            taskCard.BorderBrush = isHovered
                ? new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        }

        private static void UpdateTree(DependencyObject element, bool isHovered)
        {
            if (element is Border border && Equals(border.Tag, "TaskCard"))
            {
                Update(border, isHovered);
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                UpdateTree(VisualTreeHelper.GetChild(element, index), isHovered);
            }
        }
    }
}
