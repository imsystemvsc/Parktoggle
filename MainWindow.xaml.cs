using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ParkToggleWpf.Monitoring;
using ParkToggleWpf.ViewModels;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NHotkey;
using NHotkey.Wpf;

namespace ParkToggleWpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private bool _isExitRequested;
    private bool _hasShownTrayTip;

    private MonitoringOptions? _monitoringOptions;
    private MonitoringRepository? _monitoringRepository;
    private HardwareMonitorService? _hardwareMonitorService;
    private MonitoringManager? _monitoringManager;
    private readonly ObservableCollection<SensorSelectionViewModel> _monitoringSensors = new();
    private ICollectionView? _monitoringSensorsView;
    private readonly Dictionary<string, SensorSelectionViewModel> _sensorLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SensorPreferenceState> _storedSensorPreferences = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Point? _dragStartPoint;
    private SensorSelectionViewModel? _draggedSensor;
    private bool _suppressPreferencePersistence;
    private bool _monitoringInitialized;

    private static readonly TimeSpan BringToFrontDelay = TimeSpan.FromMilliseconds(120);

    public MainViewModel ViewModel { get; }

    public ObservableCollection<SensorSelectionViewModel> MonitoringSensors => _monitoringSensors;

    public ICollectionView MonitoringSensorsView => _monitoringSensorsView ??= CreateMonitoringSensorsView();


    public event PropertyChangedEventHandler? PropertyChanged;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = this;
        _monitoringSensors.CollectionChanged += MonitoringSensorsOnCollectionChanged;

        if (Environment.GetCommandLineArgs().Contains("--hidden"))
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            _hasShownTrayTip = true; // prevent balloon tip on auto-start
        }

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;

        try
        {
            HotkeyManager.Current.AddOrReplace("ToggleMode", Key.P, ModifierKeys.Control | ModifierKeys.Shift, OnToggleModeHotkey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to register global hotkey: {ex.Message}");
        }
    }

    private async void OnToggleModeHotkey(object? sender, HotkeyEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            await ViewModel.ToggleModeAsync();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeMonitoringAsync().ConfigureAwait(false);
        await BringToFrontAsync().ConfigureAwait(false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyMicaBackdrop();
    }

    private async Task BringToFrontAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            Topmost = true;
            Activate();
            Focus();
        }, DispatcherPriority.Loaded);

        await Task.Delay(BringToFrontDelay).ConfigureAwait(false);

        await Dispatcher.InvokeAsync(() => Topmost = false, DispatcherPriority.Loaded);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_isExitRequested)
        {
            Hide();
            ShowInTaskbar = false;

            if (!_hasShownTrayTip)
            {
                _hasShownTrayTip = true;
            }
        }
        else if (WindowState == WindowState.Normal)
        {
            ShowInTaskbar = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExitRequested)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.Dispose();

        if (_monitoringManager is not null)
        {
            _monitoringManager.SampleCaptured -= OnMonitoringSampleCaptured;
            _monitoringManager.Dispose();
            _monitoringManager = null;
        }

        _hardwareMonitorService = null;
        _monitoringRepository = null;
        _monitoringOptions = null;
        _monitoringInitialized = false;
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitApplication()
    {
        _isExitRequested = true;
        
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150); // Allow the context menu time to close
            TrayIcon.Visibility = Visibility.Collapsed;
            TrayIcon.Dispose();
            Environment.Exit(0);
        }, DispatcherPriority.Background);
    }

    private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Clear-RecycleBin -Force\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Stop-Process -Name explorer -Force\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private void OpenTaskManager_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "taskmgr.exe",
            UseShellExecute = true
        });
    }

    private void OpenPowerOptions_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "control.exe",
            Arguments = "powercfg.cpl",
            UseShellExecute = true
        });
    }

    private void FlushDNS_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ipconfig.exe",
            Arguments = "/flushdns",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private async Task InitializeMonitoringAsync()
    {
        if (_monitoringInitialized)
        {
            return;
        }

        try
        {
            _monitoringOptions = MonitoringOptions.CreateDefault();
            _monitoringRepository = new MonitoringRepository(_monitoringOptions);
            await LoadSensorSelectionPreferencesAsync().ConfigureAwait(false);
            _hardwareMonitorService = new HardwareMonitorService();
            _monitoringManager = new MonitoringManager(_hardwareMonitorService, _monitoringRepository, _monitoringOptions);
            _monitoringManager.SampleCaptured += OnMonitoringSampleCaptured;
            _monitoringManager.Start();
            _monitoringInitialized = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Monitoring initialization failed: {ex}");
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(this, "Monitoring is unavailable. Please check hardware monitor permissions.", "Monitoring", MessageBoxButton.OK, MessageBoxImage.Warning);
            }, DispatcherPriority.Background);
        }
    }

    private void OnMonitoringSampleCaptured(object? sender, MonitoringSample sample)
    {
        _ = Dispatcher.InvokeAsync(() => HandleMonitoringSample(sample), DispatcherPriority.Background);
    }

    private void HandleMonitoringSample(MonitoringSample sample)
    {
        if (!_monitoringInitialized)
        {
            return;
        }

        foreach (var sensor in sample.Samples)
        {
            var selection = EnsureSensorSelection(sensor);
            selection.UpdateReading(sensor.Value);
        }
    }

    private void PopulateSensorsSnapshot()
    {
        if (_hardwareMonitorService is null)
        {
            return;
        }

        try
        {
            var samples = _hardwareMonitorService.GetSamples();
            Trace.WriteLine($"Settings snapshot found {samples.Count} sensors.");
            foreach (var sample in samples)
            {
                EnsureSensorSelection(sample);
            }

            RefreshMonitoringSensorsView();
            Trace.WriteLine($"Monitoring sensor count: {_monitoringSensors.Count}.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to populate sensors snapshot: {ex}");
        }
    }

    private void ApplyStoredSelections()
    {
        if (_monitoringSensors.Count == 0)
        {
            return;
        }

        _suppressPreferencePersistence = true;
        try
        {
            foreach (var sensor in _monitoringSensors)
            {
                if (_storedSensorPreferences.TryGetValue(sensor.SensorId, out var preference))
                {
                    sensor.SetSortOrder(preference.SortOrder);
                    sensor.IsSelected = preference.IsSelected;
                }
                else
                {
                    sensor.IsSelected = false;
                }
            }
        }
        finally
        {
            _suppressPreferencePersistence = false;
        }

        RefreshMonitoringSensorsView();
    }

    private async Task LoadSensorSelectionPreferencesAsync()
    {
        _storedSensorPreferences.Clear();

        if (_monitoringRepository is null)
        {
            return;
        }

        try
        {
            var preferences = await _monitoringRepository
                .GetSensorPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var preference in preferences)
            {
                var category = string.IsNullOrWhiteSpace(preference.Category)
                    ? string.Empty
                    : preference.Category;

                _storedSensorPreferences[preference.SensorId] = new SensorPreferenceState(
                    preference.SensorId,
                    preference.IsSelected,
                    category,
                    preference.SortOrder);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load sensor preferences: {ex}");
        }
    }
    private SensorSelectionViewModel EnsureSensorSelection(SensorSample sample)
    {
        if (_sensorLookup.TryGetValue(sample.SensorId, out var existing))
        {
            return existing;
        }

        var displayName = BuildDisplayName(sample);
        var segments = SensorDisplayNameFormatter.BuildSegments(sample);
        var categoryInfo = SensorDisplayNameFormatter.GetHardwareCategoryInfo(sample.HardwareType);

        _storedSensorPreferences.TryGetValue(sample.SensorId, out var storedPreference);

        var viewModel = new SensorSelectionViewModel(
            sample.SensorId,
            displayName,
            sample.Unit,
            categoryInfo.Category,
            categoryInfo.Order,
            sample.HardwareId,
            sample.HardwareName ?? string.Empty,
            sample.HardwareType,
            sample.SensorType,
            segments.Hardware,
            segments.Group,
            segments.Sensor);

        if (storedPreference is not null)
        {
            viewModel.SetSortOrder(storedPreference.SortOrder);
            if (storedPreference.IsSelected)
            {
                viewModel.IsSelected = true;
            }
        }
        else
        {
            viewModel.SetSortOrder(GetNextSortOrder(categoryInfo.Category));
        }

        viewModel.PropertyChanged += SensorSelectionOnPropertyChanged;
        _sensorLookup[sample.SensorId] = viewModel;
        _monitoringSensors.Add(viewModel);

        RefreshMonitoringSensorsView();

        return viewModel;
    }

    private int GetNextSortOrder(string category)
    {
        var maxOrder = _monitoringSensors
            .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SortOrder)
            .Where(order => order != SensorSelectionViewModel.UnsetSortOrder)
            .DefaultIfEmpty(-1)
            .Max();

        return maxOrder + 1;
    }

    private List<SensorPreferenceState> BuildCurrentPreferenceSnapshot()
    {
        var snapshot = new List<SensorPreferenceState>();

        foreach (var group in _monitoringSensors
            .Where(s => s.IsSelected)
            .GroupBy(s => s.Category, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderBy(s => s.SortOrder == SensorSelectionViewModel.UnsetSortOrder ? int.MaxValue : s.SortOrder)
                .ThenBy(s => s.SensorDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var sensor = ordered[i];
                if (sensor.SortOrder != i)
                {
                    sensor.SetSortOrder(i);
                }

                snapshot.Add(new SensorPreferenceState(sensor.SensorId, true, sensor.Category, sensor.SortOrder));
            }
        }

        return snapshot;
    }

    private async Task PersistSensorPreferencesAsync()
    {
        var snapshot = BuildCurrentPreferenceSnapshot();

        _storedSensorPreferences.Clear();
        foreach (var preference in snapshot)
        {
            _storedSensorPreferences[preference.SensorId] = preference;
        }

        if (_monitoringRepository is null)
        {
            return;
        }

        try
        {
            await _monitoringRepository
                .SaveSensorPreferencesAsync(
                    snapshot.Select(p => (p.SensorId, p.IsSelected, p.Category, p.SortOrder)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save sensor preferences: {ex}");
        }
    }

    private sealed record SensorPreferenceState(string SensorId, bool IsSelected, string Category, int SortOrder);

    private static string BuildDisplayName(SensorSample sample)
    {
        return SensorDisplayNameFormatter.BuildDisplayName(sample);
    }

    private async void OpenMonitoringSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_monitoringInitialized)
        {
            await InitializeMonitoringAsync();
        }

        PopulateSensorsSnapshot();
        ApplyStoredSelections();

        var window = new MonitoringSettingsWindow(_monitoringSensors)
        {
            Owner = this
        };

        window.ShowDialog();

        await PersistSensorPreferencesAsync().ConfigureAwait(false);
        RefreshMonitoringSensorsView();
    }

    private void MonitoringSensorsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(RefreshMonitoringSensorsView, DispatcherPriority.Background);
    }

    private ICollectionView CreateMonitoringSensorsView()
    {
        var view = new ListCollectionView(_monitoringSensors);
        view.Filter = static item => item is SensorSelectionViewModel vm && vm.IsSelected;
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorSelectionViewModel.Category)));
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(SensorSelectionViewModel.CategoryOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(SensorSelectionViewModel.Category), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(SensorSelectionViewModel.SortOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(SensorSelectionViewModel.SensorDisplayName), ListSortDirection.Ascending));
        return view;
    }

    private void RefreshMonitoringSensorsView()
    {
        if (_monitoringSensorsView is null)
        {
            return;
        }

        _monitoringSensorsView.Refresh();
        OnPropertyChanged(nameof(MonitoringSensorsView));
    }

    private void ActiveSensorsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            _dragStartPoint = null;
            _draggedSensor = null;
            return;
        }

        var container = FindAncestor<System.Windows.Controls.ListViewItem>(source);
        if (container?.DataContext is SensorSelectionViewModel sensor)
        {
            _dragStartPoint = e.GetPosition(listView);
            _draggedSensor = sensor;
        }
        else
        {
            _dragStartPoint = null;
            _draggedSensor = null;
        }
    }

    private void ActiveSensorsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStartPoint is null || _draggedSensor is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStartPoint = null;
            _draggedSensor = null;
            return;
        }

        if (sender is not System.Windows.Controls.ListView listView)
        {
            return;
        }

        var currentPosition = e.GetPosition(listView);
        if (Math.Abs(currentPosition.X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new System.Windows.DataObject(typeof(SensorSelectionViewModel), _draggedSensor);
        System.Windows.DragDrop.DoDragDrop(listView, data, System.Windows.DragDropEffects.Move);

        _dragStartPoint = null;
        _draggedSensor = null;
    }

    private void ActiveSensorsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(SensorSelectionViewModel)))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (sender is not System.Windows.Controls.ListView || e.OriginalSource is not DependencyObject source)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var container = FindAncestor<System.Windows.Controls.ListViewItem>(source);
        if (container?.DataContext is not SensorSelectionViewModel target)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragged = (SensorSelectionViewModel)e.Data.GetData(typeof(SensorSelectionViewModel))!;
        if (ReferenceEquals(dragged, target) ||
            !string.Equals(dragged.Category, target.Category, StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void ActiveSensorsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(SensorSelectionViewModel)))
        {
            return;
        }

        if (sender is not System.Windows.Controls.ListView || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var container = FindAncestor<System.Windows.Controls.ListViewItem>(source);
        if (container?.DataContext is not SensorSelectionViewModel target)
        {
            return;
        }

        var dragged = (SensorSelectionViewModel)e.Data.GetData(typeof(SensorSelectionViewModel))!;
        if (ReferenceEquals(dragged, target) ||
            !string.Equals(dragged.Category, target.Category, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dropPosition = e.GetPosition(container);
        var insertAfter = dropPosition.Y > container.ActualHeight / 2;

        ReorderSensorsWithinCategory(dragged, target, insertAfter);

        _dragStartPoint = null;
        _draggedSensor = null;
        e.Handled = true;
    }

    private void ReorderSensorsWithinCategory(SensorSelectionViewModel source, SensorSelectionViewModel target, bool insertAfter)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        var category = source.Category;
        var categoryItems = _monitoringSensors
            .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.SensorDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!categoryItems.Remove(source))
        {
            return;
        }

        var targetIndex = categoryItems.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (insertAfter)
        {
            targetIndex += 1;
        }

        categoryItems.Insert(targetIndex, source);

        for (var i = 0; i < categoryItems.Count; i++)
        {
            categoryItems[i].SetSortOrder(i);
        }

        RefreshMonitoringSensorsView();
        _ = PersistSensorPreferencesAsync();
    }

    private void ActiveSensorsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var listScrollViewer = FindDescendant<ScrollViewer>(source);
        if (listScrollViewer is null)
        {
            return;
        }

        var offset = listScrollViewer.VerticalOffset;
        var atTop = offset <= 0;
        var atBottom = offset >= listScrollViewer.ScrollableHeight;
        var scrollUp = e.Delta > 0;

        if ((scrollUp && !atTop) || (!scrollUp && !atBottom))
        {
            e.Handled = true;
            listScrollViewer.ScrollToVerticalOffset(offset - e.Delta);
            return;
        }

        var parentScrollViewer = FindAncestor<ScrollViewer>(source);
        if (parentScrollViewer is null || ReferenceEquals(parentScrollViewer, listScrollViewer))
        {
            return;
        }

        e.Handled = true;
        parentScrollViewer.ScrollToVerticalOffset(parentScrollViewer.VerticalOffset - e.Delta);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent is null)
            {
                return null;
            }

            if (parent is T typed)
            {
                return typed;
            }

            current = parent;
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject current) where T : DependencyObject
    {
        if (current is null)
        {
            return null;
        }

        var queue = new Queue<DependencyObject>();
        queue.Enqueue(current);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            var childCount = VisualTreeHelper.GetChildrenCount(item);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(item, i);
                if (child is T typed)
                {
                    return typed;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }

    private void SensorSelectionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SensorSelectionViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (sender is SensorSelectionViewModel viewModel)
        {
            if (viewModel.IsSelected)
            {
                _storedSensorPreferences[viewModel.SensorId] = new SensorPreferenceState(
                    viewModel.SensorId,
                    true,
                    viewModel.Category,
                    viewModel.SortOrder);
            }
            else
            {
                _storedSensorPreferences.Remove(viewModel.SensorId);
            }

            if (!_suppressPreferencePersistence)
            {
                _ = PersistSensorPreferencesAsync();
            }
        }

        Dispatcher.InvokeAsync(RefreshMonitoringSensorsView, DispatcherPriority.Background);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ApplyMicaBackdrop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));

        int corner = (int)DwmWindowCornerPreference.Round;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        int backdrop = (int)DwmSystemBackdropType.Mica;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    private enum DwmSystemBackdropType
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}















