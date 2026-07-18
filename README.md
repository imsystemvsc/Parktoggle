# Park Toggle

Park Toggle is a modern, lightweight Windows utility designed to dynamically manage CPU core parking, power plans, and system thermals based on the applications you are running. 

By running quietly in the system tray, Park Toggle seamlessly switches your PC between an energy-efficient **Cool & Idle** mode and a high-performance **Always On** mode when it detects heavy workloads or games.

## ✨ Key Features

- **Dynamic Power Plan Switching**: Automatically activates high-performance power profiles when specific processes (e.g., games) are launched, and reverts to power-saving profiles when they are closed.
- **Advanced Core Parking Control**: Actively unparks all logical cores during intensive tasks to eliminate micro-stutters, and aggressively parks them during idle time to reduce temperatures.
- **Live Hardware Telemetry**: Features a beautiful, dark-themed dashboard providing real-time data on CPU package temperatures, total power draw, and per-core parking status.
- **Customizable Sensor Monitoring**: Integrates with LibreHardwareMonitor to track and display precise readouts for various system sensors in a clean, symmetrical grid layout.
- **Unobtrusive System Tray Operation**: Designed to launch silently with Windows and reside in the system tray with custom status icons (❄️ for Cool & Idle, ⚡ for Always On). Includes a custom dark-themed context menu with quick access to common System Tools like Restart Explorer, Task Manager, Flush DNS, and more.
- **Modern UI**: A sleek, edge-to-edge landscape interface utilizing Windows 11 Mica backdrop and immersive dark mode title bars.

## 🚀 Getting Started

### Prerequisites
- Windows 10 (20H1 or newer) / Windows 11
- .NET 8.0 Desktop Runtime

### Building from Source
Park Toggle is built with WPF and .NET 8.0. To compile the application yourself:
1. Clone the repository.
2. Open `ParkToggleWpf.sln` in Visual Studio 2022 or use the .NET CLI.
3. Build the project in Release mode or run the following command to generate a self-contained, single-file executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## ⚙️ Usage

1. Launch **Park Toggle**.
2. From the main dashboard, switch to the **Settings** tab to define the `.exe` names of the processes (games or heavy applications) that should trigger performance mode.
3. Ensure the app is set to run at startup. It will sit in the tray and automatically manage your power states without any further interaction!
4. Use the global hotkey (`Ctrl + Shift + P`) to manually toggle the performance mode at any time.

## 🛠️ Built With
- **WPF & .NET 8.0**
- **CommunityToolkit.Mvvm** - For clean MVVM architecture
- **LibreHardwareMonitorLib** - For deep hardware telemetry
- **LiveChartsCore** - For fluid data visualization
- **H.NotifyIcon.Wpf** - For robust system tray integration
