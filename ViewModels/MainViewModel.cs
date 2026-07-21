using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Microsoft.Win32;
using ParkToggleWpf.Monitoring;
using ParkToggleWpf;

namespace ParkToggleWpf.ViewModels;

public class TargetExecutableViewModel
{
    public string DisplayName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public System.Windows.Media.ImageSource? Icon { get; set; }
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly PowerPlanService _powerPlanService;
    private readonly CoreParkingService _coreParkingService;
    private CpuTemperatureService? _cpuTemperatureService;
    private readonly DispatcherTimer _cpuTimer;
    private readonly AutomationService _automationService;
    private readonly ObservableCollection<ObservableValue> _temperatureValues = new();
    private readonly ObservableValue _cpuLoadValue = new(0);
    private readonly ObservableValue _cpuLoadRemaining = new(100);
    private readonly ObservableValue _gpuLoadValue = new(0);
    private readonly ObservableValue _gpuLoadRemaining = new(100);

    public AutomationOptions AutomationSettings { get; private set; } = new AutomationOptions();
    
    public ObservableCollection<TargetExecutableViewModel> TargetExecutableViewModels { get; } = new();
    
    public ObservableCollection<LogicalCoreViewModel> Cores { get; } = new();

    [ObservableProperty]
    private ISeries[] _temperatureSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private ISeries[] _cpuLoadSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private ISeries[] _gpuLoadSeries = Array.Empty<ISeries>();

    public Axis[] EmptyAxes { get; set; } = new Axis[] 
    { 
        new Axis 
        { 
            IsVisible = true,
            ShowSeparatorLines = false,
            LabelsPaint = new SolidColorPaint(new SKColor(136, 136, 136)),
            TextSize = 12,
            Labeler = value => $"{((int)value - 60) * 2}s"
        } 
    };

    public Axis[] TempYAxes { get; set; } = new Axis[] 
    { 
        new Axis 
        { 
            IsVisible = true, 
            MinLimit = 30, 
            MaxLimit = 105,
            ShowSeparatorLines = true,
            SeparatorsPaint = new SolidColorPaint(new SKColor(51, 51, 51)),
            LabelsPaint = new SolidColorPaint(new SKColor(136, 136, 136)),
            Labeler = value => $"{value} °C",
            TextSize = 12
        } 
    };

    [ObservableProperty]
    private ObservableCollection<PowerPlan> _plans = new();

    [ObservableProperty]
    private PowerPlan? _selectedPlan;

    [ObservableProperty]
    private ParkMode _currentMode;

    [ObservableProperty]
    private string _trayIconSource = "pack://application:,,,/Resources/Icons/main.ico";

    partial void OnCurrentModeChanged(ParkMode value)
    {
        TrayIconSource = value switch
        {
            ParkMode.AlwaysOn => "pack://application:,,,/Resources/Icons/lightningbolt.ico",
            ParkMode.CoolIdle => "pack://application:,,,/Resources/Icons/snowflake.ico",
            _ => "pack://application:,,,/Resources/Icons/main.ico"
        };
    }

    [ObservableProperty]
    private string _modeValueText = "--";

    [ObservableProperty]
    private string _packageTempText = "--";

    [ObservableProperty]
    private string _gpuTempText = "--";

    [ObservableProperty]
    private string _automationStatusText = "Automation Status: Idle";

    [ObservableProperty]
    private string _coreLabel = "Core Parking Min Cores (AC/DC): --";

    [ObservableProperty]
    private int _coreColumns = 4;

    [ObservableProperty]
    private string _idleLabel = "Idle State Max (AC/DC): --";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _taskbarProgressValue;

    [ObservableProperty]
    private System.Windows.Shell.TaskbarItemProgressState _taskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.None;

    public string LogPath => _powerPlanService.LogPath;

    private const string AppName = "ParkToggle";
    private string _cpuLoadText = "0%";
    public string CpuLoadText
    {
        get => _cpuLoadText;
        set { _cpuLoadText = value; OnPropertyChanged(nameof(CpuLoadText)); }
    }

    private string _gpuLoadText = "0%";
    public string GpuLoadText
    {
        get => _gpuLoadText;
        set { _gpuLoadText = value; OnPropertyChanged(nameof(GpuLoadText)); }
    }

    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public bool StartWithWindows
    {
        get
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/query /tn \"{AppName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                // Clean up old registry key just in case
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
                {
                    if (key?.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }

                if (value)
                {
                    var exePath = Environment.ProcessPath;
                    if (exePath != null && exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        exePath = exePath.Substring(0, exePath.Length - 4) + ".exe";
                    }

                    var arguments = $"/create /tn \"{AppName}\" /tr \"\\\"{exePath}\\\" --hidden\" /sc onlogon /rl highest /f";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit();
                }
                else
                {
                    var arguments = $"/delete /tn \"{AppName}\" /f";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit();
                }
                
                OnPropertyChanged(nameof(StartWithWindows));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to set StartWithWindows: {ex.Message}");
            }
        }
    }

    public MainViewModel()
    {
        _powerPlanService = new PowerPlanService();
        _coreParkingService = new CoreParkingService();
        
        try
        {
            _cpuTemperatureService = new CpuTemperatureService();
        }
        catch
        {
            _cpuTemperatureService = null;
            PackageTempText = "Unavailable";
            GpuTempText = "Unavailable";
        }

        _cpuTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };
        _cpuTimer.Tick += (_, _) => UpdateCpuTemperatures();

        _temperatureSeries = new ISeries[]
        {
            new LineSeries<ObservableValue>
            {
                Values = _temperatureValues,
                Fill = new LinearGradientPaint(new[] { new SKColor(0, 175, 239, 150), new SKColor(247, 178, 103, 150) }, new SKPoint(0, 0), new SKPoint(1, 0)),
                Stroke = new LinearGradientPaint(new[] { new SKColor(0, 175, 239), new SKColor(247, 178, 103) }, new SKPoint(0, 0), new SKPoint(1, 0)) { StrokeThickness = 3 },
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0.8
            }
        };

        CpuLoadSeries = new ISeries[]
        {
            new PieSeries<ObservableValue>
            {
                Values = new[] { _cpuLoadValue },
                InnerRadius = 22,
                MaxRadialColumnWidth = 3,
                Fill = new SolidColorPaint(new SKColor(167, 196, 126)), // Greenish
                DataLabelsPaint = null
            },
            new PieSeries<ObservableValue>
            {
                Values = new[] { _cpuLoadRemaining },
                InnerRadius = 22,
                MaxRadialColumnWidth = 3,
                Fill = new SolidColorPaint(new SKColor(0, 0, 0, 0)),
                DataLabelsPaint = null,
                Stroke = null
            }
        };

        GpuLoadSeries = new ISeries[]
        {
            new PieSeries<ObservableValue>
            {
                Values = new[] { _gpuLoadValue },
                InnerRadius = 22,
                MaxRadialColumnWidth = 3,
                Fill = new SolidColorPaint(new SKColor(167, 196, 126)),
                DataLabelsPaint = null
            },
            new PieSeries<ObservableValue>
            {
                Values = new[] { _gpuLoadRemaining },
                InnerRadius = 22,
                MaxRadialColumnWidth = 3,
                Fill = new SolidColorPaint(new SKColor(0, 0, 0, 0)),
                DataLabelsPaint = null,
                Stroke = null
            }
        };

        // Load settings before starting automation
        AutomationSettings = AutomationSettingsManager.Load();

        _automationService = new AutomationService(_powerPlanService, AutomationSettings);
        _automationService.AutomationTriggered += (s, msg) => 
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
            {
                AutomationStatusText = $"Automation: {msg}";
                _ = RefreshAsync();
            });
        };
        _automationService.Start();

        SyncTargetExecutables();

        // Start initial refresh
        _ = RefreshAsync();
        _cpuTimer.Start();
    }

    private void SyncTargetExecutables()
    {
        TargetExecutableViewModels.Clear();
        foreach (var path in AutomationSettings.TargetExecutables)
        {
            var vm = new TargetExecutableViewModel
            {
                FullPath = path,
                DisplayName = System.IO.Path.GetFileName(path)
            };

            try
            {
                if (System.IO.File.Exists(path))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        vm.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch { }

            TargetExecutableViewModels.Add(vm);
        }
    }

    [RelayCommand]
    public void AddGameExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title = "Select Game Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            string fileName = dialog.FileName; // Full path
            if (!AutomationSettings.TargetExecutables.Contains(fileName))
            {
                var newSettings = new AutomationOptions
                {
                    SmartBatteryEnabled = AutomationSettings.SmartBatteryEnabled,
                    TargetExecutables = AutomationSettings.TargetExecutables.ToList()
                };
                newSettings.TargetExecutables.Add(fileName);
                AutomationSettings = newSettings;
                AutomationSettingsManager.Save(AutomationSettings);
                OnPropertyChanged(nameof(AutomationSettings));
                SyncTargetExecutables();
            }
        }
    }

    [RelayCommand]
    public void AddRunningGame()
    {
        var dialog = new ProcessPickerDialog();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedExecutable))
        {
            if (!AutomationSettings.TargetExecutables.Contains(dialog.SelectedExecutable))
            {
                var newSettings = new AutomationOptions
                {
                    SmartBatteryEnabled = AutomationSettings.SmartBatteryEnabled,
                    TargetExecutables = AutomationSettings.TargetExecutables.ToList()
                };
                newSettings.TargetExecutables.Add(dialog.SelectedExecutable);
                AutomationSettings = newSettings;
                AutomationSettingsManager.Save(AutomationSettings);
                OnPropertyChanged(nameof(AutomationSettings));
                SyncTargetExecutables();
            }
        }
    }

    [RelayCommand]
    public void RemoveGameExecutable(TargetExecutableViewModel vm)
    {
        if (vm == null) return;
        string fileName = vm.FullPath;
        if (AutomationSettings.TargetExecutables.Contains(fileName))
        {
            var newSettings = new AutomationOptions
            {
                SmartBatteryEnabled = AutomationSettings.SmartBatteryEnabled,
                TargetExecutables = AutomationSettings.TargetExecutables.ToList()
            };
            newSettings.TargetExecutables.Remove(fileName);
            AutomationSettings = newSettings;
            AutomationSettingsManager.Save(AutomationSettings);
            OnPropertyChanged(nameof(AutomationSettings));
            SyncTargetExecutables();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var plans = await _powerPlanService.GetPlansAsync();
            Plans = new ObservableCollection<PowerPlan>(plans);
            
            var active = plans.FirstOrDefault(p => p.IsActive) ?? await _powerPlanService.GetActivePlanAsync();
            var matchedPlan = plans.FirstOrDefault(p => p.Guid == active.Guid);
            
            // Bypass the setter logic to avoid setting active plan when we just refreshed it
            SelectedPlan = matchedPlan;

            if (SelectedPlan != null)
            {
                var snapshot = await _powerPlanService.GetModeSnapshotAsync(SelectedPlan.Guid);
                UpdateModeDetails(snapshot);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Refresh failed: {ex.Message}", "Park Toggle", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleModeAsync()
    {
        if (SelectedPlan == null || IsBusy) return;
        IsBusy = true;
        try
        {
            await _powerPlanService.ToggleModeAsync(SelectedPlan.Guid, SelectedPlan.Name);
            var snapshot = await _powerPlanService.GetModeSnapshotAsync(SelectedPlan.Guid);
            UpdateModeDetails(snapshot);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Toggle failed: {ex.Message}", "Park Toggle", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedPlanChanged(PowerPlan? value)
    {
        if (value != null && !IsBusy)
        {
            _ = SetActivePlanAsync(value);
        }
    }

    private async Task SetActivePlanAsync(PowerPlan plan)
    {
        IsBusy = true;
        try
        {
            await _powerPlanService.SetActivePlanAsync(plan.Guid, plan.Name);
            var snapshot = await _powerPlanService.GetModeSnapshotAsync(plan.Guid);
            UpdateModeDetails(snapshot);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to set plan: {ex.Message}", "Park Toggle", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateModeDetails(ModeSnapshot snapshot)
    {
        CurrentMode = snapshot.Mode;
        ModeValueText = PowerPlanService.ModeToDisplay(snapshot.Mode);
        CoreLabel = $"Core Parking Min Cores (AC/DC): {snapshot.Core.Ac}/{snapshot.Core.Dc}";
        IdleLabel = $"Idle State Max (AC/DC): {snapshot.Idle.Ac}/{snapshot.Idle.Dc}";
    }

    private void UpdateCpuTemperatures()
    {
        if (_cpuTemperatureService == null) return;
        try
        {
            var snapshot = _cpuTemperatureService.GetSnapshot();
            PackageTempText = snapshot.PackageCelsius.HasValue ? $"{snapshot.PackageCelsius.Value:F1} \u00B0C" : "N/A";
            GpuTempText = snapshot.GpuCelsius.HasValue ? $"{snapshot.GpuCelsius.Value:F1} \u00B0C" : "N/A";

            if (snapshot.CpuLoad.HasValue)
            {
                _cpuLoadValue.Value = snapshot.CpuLoad.Value;
                _cpuLoadRemaining.Value = Math.Max(0, 100 - snapshot.CpuLoad.Value);
                CpuLoadText = $"{snapshot.CpuLoad.Value:F0}%";
                if (CpuLoadSeries[0] is PieSeries<ObservableValue> cpuPie)
                {
                    cpuPie.Fill = new SolidColorPaint(GetLoadColor(snapshot.CpuLoad.Value));
                }
            }

            if (snapshot.GpuLoad.HasValue)
            {
                _gpuLoadValue.Value = snapshot.GpuLoad.Value;
                _gpuLoadRemaining.Value = Math.Max(0, 100 - snapshot.GpuLoad.Value);
                GpuLoadText = $"{snapshot.GpuLoad.Value:F0}%";
                if (GpuLoadSeries[0] is PieSeries<ObservableValue> gpuPie)
                {
                    gpuPie.Fill = new SolidColorPaint(GetLoadColor(snapshot.GpuLoad.Value));
                }
            }

            if (snapshot.PackageCelsius.HasValue)
            {
                _temperatureValues.Add(new ObservableValue(snapshot.PackageCelsius.Value));
                if (_temperatureValues.Count > 60)
                {
                    _temperatureValues.RemoveAt(0);
                }

                TaskbarProgressValue = Math.Min(1.0, Math.Max(0.0, snapshot.PackageCelsius.Value / 100.0));
                if (snapshot.PackageCelsius.Value >= 85.0)
                {
                    TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
                }
                else if (snapshot.PackageCelsius.Value >= 70.0)
                {
                    TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.Paused;
                }
                else
                {
                    TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
                }
            }
            else
            {
                TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            }
        }
        catch
        {
            PackageTempText = "N/A";
            GpuTempText = "N/A";
            TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }

        UpdateCores();
    }

    private void UpdateCores()
    {
        var states = _coreParkingService.GetParkedStates();
        foreach (var kvp in states)
        {
            var coreName = kvp.Key;
            var isParked = kvp.Value;
            var existingCore = Cores.FirstOrDefault(c => c.CoreName == coreName);
            if (existingCore != null)
            {
                existingCore.IsParked = isParked;
            }
            else
            {
                Cores.Add(new LogicalCoreViewModel(coreName, isParked));
            }
        }
        
        int count = Cores.Count;
        if (count > 0)
        {
            if (count <= 8) CoreColumns = count / 2;
            else if (count % 8 == 0) CoreColumns = 8;
            else if (count % 6 == 0) CoreColumns = 6;
            else if (count % 4 == 0) CoreColumns = 4;
            else if (count % 5 == 0) CoreColumns = 5;
            else CoreColumns = (int)Math.Ceiling(Math.Sqrt(count));
            
            if (CoreColumns == 0) CoreColumns = 4;
        }
    }

    [RelayCommand]
    public void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Unable to open log: {ex.Message}", "Park Toggle", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Dispose()
    {
        _cpuTimer.Stop();
        _automationService.Dispose();
        _cpuTemperatureService?.Dispose();
        _coreParkingService?.Dispose();
    }

    private SKColor GetLoadColor(double load)
    {
        if (load < 40) return new SKColor(167, 196, 126); // Greenish
        if (load < 75) return new SKColor(240, 220, 100); // Yellowish
        return new SKColor(240, 100, 100); // Reddish
    }
}
