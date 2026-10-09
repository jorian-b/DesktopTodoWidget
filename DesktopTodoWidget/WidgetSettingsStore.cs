using System.IO;
using System.Text.Json;

namespace DesktopTodoWidget
{
    internal sealed class WidgetSettingsStore
    {
        private readonly string _path;

        public WidgetSettingsStore(string path)
        {
            _path = path;
        }

        public WidgetSettings? Load()
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(_path));
        }

        public void Save(WidgetSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings));
        }
    }
}
