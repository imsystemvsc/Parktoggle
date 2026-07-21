# Park Toggle

Park Toggle is a modern, lightweight Windows utility designed to dynamically manage CPU core parking, power plans, and system thermals based on the applications you are running. 

By running quietly in the system tray, Park Toggle seamlessly switches your PC between an energy-efficient **Cool & Idle** mode and a high-performance **Always On** mode when it detects heavy workloads or games.

## ✨ Key Features

- **Dynamic Power Plan Switching**: Automatically activates high-performance power profiles when specific processes (e.g., games) are launched, and reverts to power-saving profiles when they are closed.
- **Advanced Core Parking Control**: Actively unparks all logical cores during intensive tasks to eliminate micro-stutters, and aggressively parks them during idle time to reduce temperatures.
- **Live Hardware Telemetry Dashboard**: Features a beautiful, symmetrical dark-themed dashboard providing real-time data on CPU and GPU temperatures alongside interactive load gauges.
- **Historical Trend Charting**: Displays a live rolling history of CPU temperatures over time via an interactive, gradient-filled chart.
- **Unobtrusive System Tray Operation**: Designed to launch silently with Windows via scheduled tasks (avoiding UAC prompts) and reside in the system tray with custom status icons (❄️ for Cool & Idle, ⚡ for Always On). 
- **Quick Action Context Menu**: Includes a custom dark-themed tray context menu with quick access to common System Tools like Restart Explorer, Task Manager, Flush DNS, and more.
- **Modern WPF UI**: A sleek interface utilizing customized tabs with active state contrast, a warm `#E8E6E3` text palette for eye-strain reduction, and immersive dark mode borders.

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
2. From the main dashboard, switch to the **Automation** tab (or **Monitoring**) to manage the `.exe` names of the processes (games or heavy applications) that should trigger performance mode.
3. Ensure **Start with Windows (Minimized)** is checked so it can automatically manage your power states in the background.
4. Use the custom Quick Action buttons on the dashboard to trigger manual overrides, or use the tray icon for quick access to system tools.

## 🛠️ Built With
- **WPF & .NET 8.0**
- **CommunityToolkit.Mvvm** - For clean MVVM architecture
- **LibreHardwareMonitorLib** - For deep hardware telemetry
- **LiveChartsCore (SkiaSharp)** - For fluid, gradient-filled data visualization and gauges
- **H.NotifyIcon.Wpf** - For robust system tray integration
