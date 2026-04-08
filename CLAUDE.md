# Meridian 59 TUI Client

## Build & Run
To build the TUI client, use the provided Visual Studio solution or build via CLI:
```bash
dotnet build Meridian59.sln --configuration Debug --arch x64
```
Run the executable:
```bash
./Meridian59.TuiClient/bin/x64/Debug/Meridian59.TuiClient.exe
```
(Note: Replace paths with your actual build output)

## Usage
- **Movement**: Use `W`, `A`, `S`, `D` keys to move.
- **Recording**: Press `T` to toggle path recording. Records are saved as `path_YYYYMMDD_HHMMSS.json`.
- **ASCII Art**: The right side of the screen shows a top-down view of the room and objects centered on your avatar.
    - `@`: Your Avatar
    - `P`: Other Players
    - `M`: Monsters/Attackables
    - `i`: Items

## Replaying Paths
To replay a path, you can modify `Program.cs` or `TuiClient.cs` to use `PathReplayer` to load and execute a recorded JSON file.

## Project Structure
- `TuiClient.cs`: Main client logic, UI layout, and input handling.
- `AsciiRenderer.cs`: Converts room object data into ASCII characters for display.
- `PathRecorder.cs`: Captures player movements, actions, and chat messages with timestamps.
- `PathReplayer.cs`: Reads recorded JSON files and executes the actions at the appropriate time.
