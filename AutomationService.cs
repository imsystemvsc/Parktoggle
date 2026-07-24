using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ParkToggleWpf;

public class AutomationOptions
{
    public bool SmartBatteryEnabled { get; set; } = true;
    public List<string> TargetExecutables { get; set; } = new();
    public CoolIdleTier SelectedCoolIdleTier { get; set; } = CoolIdleTier.Balanced;
}

public class AutomationService : IAsyncDisposable, IDisposable
{
    private readonly PowerPlanService _powerPlanService;
    private AutomationOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private bool _isCurrentlyOnBattery;
    private bool _wasRunningTargetProcess;
    
    public string? ActiveTargetName { get; private set; }
    
    // Store the guid of the plan that we should fallback to when automation switches off.
    public string? BasePlanGuid { get; set; }

    public event EventHandler<string>? AutomationTriggered;

    public AutomationService(PowerPlanService powerPlanService, AutomationOptions options)
    {
        _powerPlanService = powerPlanService;
        _options = options;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        CheckBatteryStatus();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        CheckBatteryStatus();
    }

    private void CheckBatteryStatus()
    {
        _isCurrentlyOnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
    }

    public void Start()
    {
        if (_loopTask is not null)
            return;

        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EvaluateConditionsAsync(token);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Automation error: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token); // Check every 3 seconds
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void UpdateOptions(AutomationOptions options)
    {
        _options = options;
        _ = TriggerEvaluationAsync();
    }

    public async Task TriggerEvaluationAsync()
    {
        try
        {
            await EvaluateConditionsAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Automation evaluation error: {ex}");
        }
    }

    private async Task EvaluateConditionsAsync(CancellationToken token)
    {
        if (BasePlanGuid is null)
        {
            // We need a known base plan to operate.
            var active = await _powerPlanService.GetActivePlanAsync(token);
            BasePlanGuid = active.Guid;
        }

        // Priority 1: Smart Battery (Forces Cool Idle)
        if (_options.SmartBatteryEnabled && _isCurrentlyOnBattery)
        {
            var snapshot = await _powerPlanService.GetModeSnapshotAsync(BasePlanGuid, token);
            if (snapshot.Mode != ParkMode.CoolIdle)
            {
                await _powerPlanService.SetModeAsync(BasePlanGuid, "Battery Auto-Switch", ParkMode.CoolIdle, _options.SelectedCoolIdleTier, token);
                ActiveTargetName = "Battery Saver";
                AutomationTriggered?.Invoke(this, $"Switched to Cool Idle [{PowerPlanService.TierToDisplay(_options.SelectedCoolIdleTier)}] (Battery Detected)");
                _powerPlanService.Log($"Automation triggered: Switched to Cool Idle [{PowerPlanService.TierToDisplay(_options.SelectedCoolIdleTier)}] (Battery Detected)");
                _wasRunningTargetProcess = false; // Reset state
            }
            return;
        }

        // Priority 2: Target Processes (Forces AlwaysOn)
        bool isTargetRunning = IsAnyTargetProcessRunning();
        
        if (isTargetRunning)
        {
            if (!_wasRunningTargetProcess || ActiveTargetName == null)
            {
                var snapshot = await _powerPlanService.GetModeSnapshotAsync(BasePlanGuid, token);
                if (snapshot.Mode != ParkMode.AlwaysOn)
                {
                    await _powerPlanService.SetModeAsync(BasePlanGuid, "Game Auto-Switch", ParkMode.AlwaysOn, _options.SelectedCoolIdleTier, token);
                    AutomationTriggered?.Invoke(this, $"Switched to Always-On (Detected: {ActiveTargetName})");
                    _powerPlanService.Log($"Automation triggered: Switched to Always-On (Detected: {ActiveTargetName})");
                }
                _wasRunningTargetProcess = true;
            }
        }
        else
        {
            if (_wasRunningTargetProcess || ActiveTargetName != null)
            {
                var snapshot = await _powerPlanService.GetModeSnapshotAsync(BasePlanGuid, token);
                if (snapshot.Mode != ParkMode.CoolIdle)
                {
                    await _powerPlanService.SetModeAsync(BasePlanGuid, "Game Auto-Switch", ParkMode.CoolIdle, _options.SelectedCoolIdleTier, token);
                    AutomationTriggered?.Invoke(this, $"Switched to Cool Idle [{PowerPlanService.TierToDisplay(_options.SelectedCoolIdleTier)}] (Game Closed/Removed)");
                    _powerPlanService.Log($"Automation triggered: Switched to Cool Idle [{PowerPlanService.TierToDisplay(_options.SelectedCoolIdleTier)}] (Game Closed/Removed)");
                }
                ActiveTargetName = null;
                _wasRunningTargetProcess = false;
            }
        }
    }

    private bool IsAnyTargetProcessRunning()
    {
        if (_options.TargetExecutables.Count == 0)
            return false;

        var runningProcesses = Process.GetProcesses();
        
        foreach (var proc in runningProcesses)
        {
            try
            {
                string procName = proc.ProcessName; // Does not include .exe
                
                foreach (var target in _options.TargetExecutables)
                {
                    string cleanTarget = System.IO.Path.GetFileNameWithoutExtension(target.Trim('"', ' ')).ToLowerInvariant();
                    if (string.Equals(procName, cleanTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        ActiveTargetName = System.IO.Path.GetFileName(target);
                        return true;
                    }
                }
            }
            catch
            {
                // Access denied or process exited.
            }
            finally
            {
                proc.Dispose(); // Keep things clean
            }
        }
        return false;
    }

    public async Task StopAsync()
    {
        if (_loopTask is null)
            return;

        _cts.Cancel();
        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loopTask = null;
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        await StopAsync();
        _cts.Dispose();
    }
}
