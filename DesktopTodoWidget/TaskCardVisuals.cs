using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopTodoWidget
{
    internal static class TaskCardVisuals
    {
        private static readonly SolidColorBrush BackgroundBrush = CreateBrush(0x80, 0, 0, 0);
        private static readonly SolidColorBrush HoverBorderBrush = CreateBrush(0x66, 255, 255, 255);
        private static readonly SolidColorBrush BorderBrush = CreateBrush(0x38, 255, 255, 255);
        private static readonly DropShadowEffect OverdueGlow = CreateGlow(14, 0.65);
        private static readonly DropShadowEffect UrgentOverdueGlow = CreateGlow(22, 0.95);

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
            if (taskCard.DataContext is TaskItem { IsOverdue: true } item)
            {
                taskCard.Background = BackgroundBrush;
                taskCard.BorderBrush = isHovered ? HoverBorderBrush : BorderBrush;
                taskCard.BorderThickness = new Thickness(1);
                taskCard.Effect = item.IsUrgent ? UrgentOverdueGlow : OverdueGlow;
                return;
            }

            taskCard.Background = BackgroundBrush;
            taskCard.BorderBrush = isHovered ? HoverBorderBrush : BorderBrush;
            taskCard.BorderThickness = new Thickness(1);
            taskCard.Effect = null;
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

        private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static DropShadowEffect CreateGlow(double blurRadius, double opacity)
        {
            var effect = new DropShadowEffect
            {
                Color = Color.FromRgb(255, 55, 48),
                BlurRadius = blurRadius,
                ShadowDepth = 0,
                Opacity = opacity
            };
            effect.Freeze();
            return effect;
        }
    }
}
