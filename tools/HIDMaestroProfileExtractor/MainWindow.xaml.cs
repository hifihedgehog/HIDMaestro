using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using HIDMaestro;

namespace HIDMaestro.ProfileExtractor;

public partial class MainWindow : Window
{
    private List<DeviceRow> _devices = new();
    private HMProfile? _lastExtracted;
    private string _lastJson = "";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PopulateDevices();
    }

    private sealed class DeviceRow
    {
        public required HMHidDeviceInfo Info { get; init; }
        public string DisplayLabel => BuildLabel(Info);

        private static string BuildLabel(HMHidDeviceInfo d)
        {
            string product = d.ProductString ?? "(unknown)";
            string mfg = string.IsNullOrEmpty(d.ManufacturerString) ? "" : $"  [{d.ManufacturerString}]";
            string usage = UsageLabel(d.TopLevelUsagePage, d.TopLevelUsage);
            return $"VID_{d.VendorId:X4}:PID_{d.ProductId:X4}  {usage}   {product}{mfg}";
        }

        private static string UsageLabel(ushort page, ushort usage)
        {
            if (page == 0x01)
            {
                return usage switch
                {
                    0x02 => "(Mouse)",
                    0x04 => "(Joystick)",
                    0x05 => "(Gamepad)",
                    0x06 => "(Keyboard)",
                    0x08 => "(Multi-axis)",
                    _    => $"(0x01:0x{usage:X2})",
                };
            }
            return $"(0x{page:X2}:0x{usage:X2})";
        }
    }

    private void PopulateDevices()
    {
        try
        {
            var devices = HMDeviceExtractor.ListDevices();
            _devices = devices
                .OrderBy(d => d.VendorId)
                .ThenBy(d => d.ProductId)
                .ThenBy(d => d.TopLevelUsage)
                .Select(d => new DeviceRow { Info = d })
                .ToList();
            DeviceCombo.ItemsSource = _devices;
            if (_devices.Count > 0)
            {
                // Default to the first HID gamepad/joystick if present.
                var preferred = _devices.FirstOrDefault(d =>
                    d.Info.TopLevelUsagePage == 0x01 &&
                    (d.Info.TopLevelUsage == 0x04 || d.Info.TopLevelUsage == 0x05));
                DeviceCombo.SelectedItem = preferred ?? _devices[0];
            }
            StatusText.Text = $"Found {_devices.Count} HID interface(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Enumerate failed: {ex.Message}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateDevices();
        ExtractButton.IsEnabled = DeviceCombo.SelectedItem is not null;
    }

    private void DeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ExtractButton.IsEnabled = DeviceCombo.SelectedItem is not null;
        SaveButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        JsonPreview.Clear();
        LayoutPreview.Clear();
        HexPreview.Clear();
    }

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceRow row) return;

        try
        {
            _lastExtracted = HMDeviceExtractor.Extract(row.Info);
            _lastJson = HMDeviceExtractor.ToJson(_lastExtracted);

            JsonPreview.Text = _lastJson;
            LayoutPreview.Text = BuildLayoutView(_lastExtracted);
            HexPreview.Text = FormatHex(_lastExtracted.DescriptorHex);

            SaveButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
            int descByteCount = _lastExtracted.GetDescriptorBytes()?.Length ?? 0;
            StatusText.Text = $"Extracted {row.Info.ProductString ?? "device"} — {descByteCount}-byte descriptor.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Extract failed: {ex.Message}";
            _lastExtracted = null;
            _lastJson = "";
            SaveButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExtracted is null || string.IsNullOrEmpty(_lastJson)) return;

        var dlg = new SaveFileDialog
        {
            Title = "Save HIDMaestro profile",
            FileName = _lastExtracted.Id + ".json",
            Filter = "HIDMaestro profile (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, _lastJson);
                StatusText.Text = $"Saved to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Save failed: {ex.Message}";
            }
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastJson)) return;
        try
        {
            Clipboard.SetText(_lastJson);
            StatusText.Text = "Profile JSON copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private static string BuildLayoutView(HMProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Profile: {profile.Name}");
        sb.AppendLine($"  Id:                 {profile.Id}");
        sb.AppendLine($"  Vendor:             {profile.Vendor}");
        sb.AppendLine($"  VID:PID:            0x{profile.VendorId:X4}:0x{profile.ProductId:X4}");
        sb.AppendLine($"  Product String:     {profile.ProductString}");
        sb.AppendLine($"  Manufacturer:       {profile.ManufacturerString}");
        sb.AppendLine($"  Type:               {profile.Type}");
        sb.AppendLine($"  Connection:         {profile.Connection}");
        sb.AppendLine($"  Declared inputSize: {profile.InputReportSize} byte(s)");
        sb.AppendLine();
        sb.AppendLine($"Descriptor summary (via HMProfile public API):");
        sb.AppendLine($"  Buttons:    {profile.ButtonCount}");
        sb.AppendLine($"  Axes:       {profile.AxisCount}");
        sb.AppendLine($"  Hat:        {(profile.HasHat ? "yes" : "no")}");
        sb.AppendLine($"  Stick bits: {profile.StickBits}");
        sb.AppendLine($"  Trigger bits:{profile.TriggerBits}");
        sb.AppendLine($"  Deployable: {profile.IsDeployable}");
        return sb.ToString();
    }

    private static string FormatHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return "(empty)";
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 2)
        {
            sb.Append(hex, i, Math.Min(2, hex.Length - i));
            sb.Append(' ');
            if ((i / 2 + 1) % 16 == 0) sb.AppendLine();
        }
        return sb.ToString();
    }
}
