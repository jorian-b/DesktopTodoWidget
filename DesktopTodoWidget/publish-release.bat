@echo off
setlocal

set "PROJECT=%~dp0DesktopTodoWidget.csproj"
set "PUBLISH_DIR=%~dp0release"

dotnet publish "%PROJECT%" --configuration Release "-p:PublishDir=%PUBLISH_DIR%"
if errorlevel 1 (
    echo Release publish failed.
    exit /b 1
)

echo Release executable: "%PUBLISH_DIR%\DesktopTodoWidget.exe"

pause