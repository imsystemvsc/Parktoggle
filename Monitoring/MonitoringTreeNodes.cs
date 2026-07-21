using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace ParkToggleWpf.Monitoring;

public abstract class MonitoringTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    protected MonitoringTreeNode(string name)
    {
        Name = name;
        Children = new ObservableCollection<MonitoringTreeNode>();
        Children.CollectionChanged += (s, e) => SubscribeChildren();
    }

    public string Name { get; }

    public ObservableCollection<MonitoringTreeNode> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public virtual bool? IsSelectedState
    {
        get
        {
            if (Children.Count == 0) return false;

            int selectedCount = 0;
            int totalCount = 0;

            foreach (var child in GetLeafSensors())
            {
                totalCount++;
                if (child.IsSelected) selectedCount++;
            }

            if (totalCount == 0) return false;
            if (selectedCount == totalCount) return true;
            if (selectedCount == 0) return false;
            return null; // Indeterminate
        }
        set
        {
            if (!value.HasValue) return;
            bool target = value.Value;
            foreach (var leaf in GetLeafSensors())
            {
                leaf.IsSelected = target;
            }
            NotifySelectionChanged();
        }
    }

    public IEnumerable<SensorSelectionViewModel> GetLeafSensors()
    {
        if (this is MonitoringSensorLeafNode leaf)
        {
            yield return leaf.Sensor;
        }
        else
        {
            foreach (var child in Children)
            {
                foreach (var s in child.GetLeafSensors())
                {
                    yield return s;
                }
            }
        }
    }

    public void SubscribeChildren()
    {
        foreach (var child in Children)
        {
            child.PropertyChanged -= Child_PropertyChanged;
            child.PropertyChanged += Child_PropertyChanged;

            if (child is MonitoringSensorLeafNode leaf)
            {
                leaf.Sensor.PropertyChanged -= Sensor_PropertyChanged;
                leaf.Sensor.PropertyChanged += Sensor_PropertyChanged;
            }
        }
    }

    private void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsSelectedState))
        {
            OnPropertyChanged(nameof(IsSelectedState));
        }
    }

    private void Sensor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorSelectionViewModel.IsSelected))
        {
            NotifySelectionChanged();
        }
    }

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelectedState));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class MonitoringHardwareNode : MonitoringTreeNode
{
    public MonitoringHardwareNode(string hardwareId, string name, HardwareType hardwareType)
        : base(name)
    {
        HardwareId = hardwareId;
        HardwareType = hardwareType;
    }

    public string HardwareId { get; }

    public HardwareType HardwareType { get; }

    public string HardwareIcon => HardwareType switch
    {
        HardwareType.Cpu => "🖥",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "🎮",
        HardwareType.Memory => "🧠",
        HardwareType.Storage => "💾",
        HardwareType.Motherboard => "📟",
        HardwareType.Network => "🌐",
        _ => "⚡"
    };
}

public sealed class MonitoringSensorGroupNode : MonitoringTreeNode
{
    public MonitoringSensorGroupNode(string name, SensorType sensorType)
        : base(name)
    {
        SensorType = sensorType;
    }

    public SensorType SensorType { get; }
}

public sealed class MonitoringSensorLeafNode : MonitoringTreeNode
{
    public MonitoringSensorLeafNode(SensorSelectionViewModel sensor)
        : base(sensor.SensorDisplayName)
    {
        Sensor = sensor;
        Sensor.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SensorSelectionViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(IsSelectedState));
            }
        };
    }

    public SensorSelectionViewModel Sensor { get; }

    public override bool? IsSelectedState
    {
        get => Sensor.IsSelected;
        set => Sensor.IsSelected = value ?? false;
    }

    public string SensorIcon => Sensor.SensorType switch
    {
        SensorType.Temperature => "🌡",
        SensorType.Load or SensorType.Level or SensorType.Control => "📊",
        SensorType.Fan => "🌀",
        SensorType.Power => "⚡",
        SensorType.Voltage => "🔌",
        SensorType.Clock => "⏱",
        _ => "✦"
    };
}

internal static class MonitoringTreeBuilder
{
    public static IReadOnlyList<MonitoringTreeNode> Build(IEnumerable<SensorSelectionViewModel> sensors)
    {
        var result = new List<MonitoringTreeNode>();

        var hardwareGroups = sensors
            .GroupBy(sensor => sensor.HardwareId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetSafeKey(group.FirstOrDefault()?.HardwareDisplayName))
            .ThenBy(group => GetSafeKey(group.Key));

        foreach (var hardwareGroup in hardwareGroups)
        {
            var first = hardwareGroup.FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            var hardwareName = first.HardwareDisplayName;
            if (string.IsNullOrWhiteSpace(hardwareName))
            {
                hardwareName = first.HardwareName;
            }

            if (string.IsNullOrWhiteSpace(hardwareName))
            {
                hardwareName = first.HardwareType.ToString();
            }

            var hardwareNode = new MonitoringHardwareNode(first.HardwareId, hardwareName, first.HardwareType);

            var sensorGroups = hardwareGroup
                .GroupBy(sensor => sensor.SensorType)
                .OrderBy(group => GetSafeKey(SensorDisplayNameFormatter.GetGroupLabel(group.Key)))
                .ThenBy(group => group.Key.ToString());

            foreach (var group in sensorGroups)
            {
                var groupName = SensorDisplayNameFormatter.GetGroupLabel(group.Key);
                var groupNode = new MonitoringSensorGroupNode(groupName, group.Key);

                var orderedSensors = group
                    .OrderBy(sensor => GetSafeKey(sensor.SensorDisplayName))
                    .ThenBy(sensor => sensor.SensorId, StringComparer.OrdinalIgnoreCase);

                foreach (var sensor in orderedSensors)
                {
                    groupNode.Children.Add(new MonitoringSensorLeafNode(sensor));
                }

                groupNode.SubscribeChildren();
                hardwareNode.Children.Add(groupNode);
            }

            hardwareNode.SubscribeChildren();
            result.Add(hardwareNode);
        }

        return result;
    }

    private static string GetSafeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }
}

