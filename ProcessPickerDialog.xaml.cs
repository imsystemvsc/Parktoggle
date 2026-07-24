using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace ParkToggleWpf;

public class ProcessInfo
{
    public string ProcessName { get; set; } = "";
    public string MainWindowTitle { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public System.Windows.Media.ImageSource? Icon { get; set; }
}

public partial class ProcessPickerDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public string? SelectedExecutable { get; private set; }
    public ObservableCollection<ProcessInfo> RunningProcesses { get; set; } = new();

    public ProcessPickerDialog()
    {
        InitializeComponent();
        
        LoadProcesses();
        ProcessList.ItemsSource = RunningProcesses;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
    }

    private void LoadProcesses()
    {
        var processes = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .OrderBy(p => p.ProcessName)
            .ToList();

        foreach (var p in processes)
        {
            try
            {
                // We just need the process name to add it to the settings list.
                // Our AutomationService uses "proc.ProcessName" which excludes ".exe" anyway.
                // But the user's settings use "target.ToLowerInvariant().Replace(".exe", "")"
                // so we can just provide ProcessName + ".exe".
                
                var info = new ProcessInfo
                {
                    ProcessName = p.ProcessName,
                    MainWindowTitle = p.MainWindowTitle
                };

                try
                {
                    var filePath = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        info.ExecutableName = filePath;
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                        if (icon != null)
                        {
                            info.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle,
                                Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        }
                    }
                    else
                    {
                        info.ExecutableName = p.ProcessName + ".exe";
                    }
                }
                catch
                {
                    info.ExecutableName = p.ProcessName + ".exe";
                }

                RunningProcesses.Add(info);
            }
            catch
            {
                // Ignore processes we can't access
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private void ProcessList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Select_Click(sender, e);
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessInfo info)
        {
            SelectedExecutable = info.ExecutableName;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
