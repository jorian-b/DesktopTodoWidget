namespace DesktopTodoWidget
{
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
}
