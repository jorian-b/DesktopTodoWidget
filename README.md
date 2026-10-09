# 🗂️ Desktop Todo Widget

A lightweight, always-on-desktop **Todo Widget** for Windows built with **WPF (.NET 8)**. It lives on your desktop — behind all open windows but always visible — with a sleek glassmorphism UI.

---

## ✨ Features

- 📌 **Embedded on desktop** — sits on the desktop wallpaper layer (behind all windows), never in Alt+Tab
- 🌑 **Glassmorphism UI** — semi-transparent dark frosted-glass design with rounded corners
- ✅ **Checkbox support** — mark tasks as done; state is saved and restored on restart
- ➕ **Add tasks** — click **+ Add Task**, type, and press `Enter` to add
- ✏️ **Inline editing** — click any task text to edit it directly
- 🗑️ **Delete tasks** — remove individual tasks with the ✕ button
- 🔄 **Drag & drop reordering** — drag tasks to reorder them
- 🔒 **Lock/Unlock** — toggle the 🔓 lock icon to prevent accidental dragging
- 📐 **Resizable widget** — resize the widget and its position and size are restored on restart
- 💾 **Persistent storage** — tasks saved as JSON in `%AppData%\DesktopTodoWidget\tasks.json`
- 🚀 **Startup-ready** — waits for the Windows desktop shell (Progman/WorkerW) to be ready before embedding, making it safe for Windows startup auto-run

---

## 📸 Preview

> _A 400×600 semi-transparent widget card that floats on your desktop wallpaper._

---

## 🛠️ Tech Stack

| Layer             | Technology                                     |
| ----------------- | ---------------------------------------------- |
| Language          | C# 12                                          |
| Framework         | .NET 8 (WPF)                                   |
| UI                | XAML with `WindowChrome`, `AllowsTransparency` |
| Desktop Embedding | Win32 API (`Progman` → `WorkerW` trick)        |
| Persistence       | `System.Text.Json`                             |
| Distribution      | Single-file self-contained `.exe` (win-x64)    |

---

## 🚀 Getting Started

### Prerequisites

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (for building from source)

### Run from Source

```bash
git clone https://github.com/YOUR_USERNAME/DesktopTodoWidget.git
cd DesktopTodoWidget/DesktopTodoWidget
dotnet run
```

### Build a Standalone `.exe`

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The output `.exe` will be in:

```
bin/Release/net8.0-windows/win-x64/publish/
```

---

## 📁 Project Structure

```
DesktopTodoWidget-v1/
├── DesktopTodoWidget/
│   ├── App.xaml               # Application entry point
│   ├── App.xaml.cs
│   ├── MainWindow.xaml        # UI layout (XAML)
│   ├── MainWindow.xaml.cs     # Logic: tasks, drag-drop, desktop embedding
│   ├── WinAPI.cs              # P/Invoke Win32 API declarations
│   └── DesktopTodoWidget.csproj
├── DesktopTodoWidget.sln
└── README.md
```

---

## ⚙️ How Desktop Embedding Works

The widget uses a well-known Windows shell trick to embed itself into the desktop wallpaper layer:

1. Sends `0x052C` message to `Progman` to spawn a `WorkerW` window behind desktop icons.
2. Finds the correct `WorkerW` handle via `EnumWindows`.
3. Calls `SetParent` to re-parent the WPF window into `WorkerW`.
4. Applies `WS_EX_TOOLWINDOW` to hide it from Alt+Tab.
5. Keeps `WS_POPUP` (not `WS_CHILD`) so keyboard focus and text input continue to work.
6. A `WM_WINDOWPOSCHANGING` hook blocks any `SWP_HIDEWINDOW` flags that WPF would otherwise apply when the window loses activation.
7. A safety timer re-surfaces the window every 5 seconds if something hides it.

---

## 💾 Data Storage

Tasks are stored as a JSON array at:

```
%AppData%\DesktopTodoWidget\tasks.json
```

Example format:

```json
[
  { "Text": "Buy groceries", "IsChecked": false },
  { "Text": "Read a book", "IsChecked": true }
]
```

---

## 🖱️ Usage

| Action             | How                                       |
| ------------------ | ----------------------------------------- |
| **Move widget**    | Drag the header area (when unlocked 🔓)   |
| **Lock position**  | Click 🔓 to lock → 🔒                     |
| **Add a task**     | Click **+ Add Task**, type, press `Enter` |
| **Cancel add**     | Press `Escape` or click away              |
| **Check/uncheck**  | Click the checkbox next to a task         |
| **Edit task text** | Click on the task text and type           |
| **Delete task**    | Click the **✕** on the right of a task    |
| **Reorder tasks**  | Drag a task to a new position             |
| **Close widget**   | Click **✕** in the top-right corner       |

---

## 🪵 Debug Log

A diagnostic log is written to the system temp folder:

```
%TEMP%\DesktopTodoWidget_DEBUG.log
```

Useful for troubleshooting startup or embedding issues.

---

## 📋 Auto-Start on Windows Boot (Optional)

To have the widget start automatically with Windows:

1. Press `Win + R`, type `shell:startup`, and press Enter.
2. Place a shortcut to `DesktopTodoWidget.exe` in that folder.

---

## 📄 License

MIT License — free to use, modify, and distribute.

---

## 🙏 Acknowledgements

- The `WorkerW` / `Progman` desktop embedding technique is a community-discovered Windows shell trick.
- Built with ❤️ using WPF and .NET 8.
"# ToDo-Deskstop-Widgets" 
"# ToDo-Deskstop-Widgets" 
"# ToDo-Deskstop-Widgets" 
"# ToDo-Deskstop-Widgets" 
"# ToDo-Deskstop-Widgets" 
