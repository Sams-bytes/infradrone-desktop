using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InfraDroneDesktop.Services;
using System;
using System.IO;
using System.Text.Json;

namespace InfraDroneDesktop.Views;

public partial class LicenseView : UserControl
{
    private readonly EncryptionService _enc = new EncryptionService();

    public LicenseView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        DeviceId.Text = EncryptionService.GetDeviceId();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var loaded = _enc.LoadLicense();
        if (loaded && _enc.License != null)
        {
            LicenseDot.Fill = new SolidColorBrush(Color.Parse("#0d9e75"));
            LicenseStatus.Text = "License valid";
            LicensedTo.Text = _enc.License.IssuedTo;
            LicenseOperator.Text = _enc.License.Operator;
            LicenseExpiry.Text = _enc.License.ExpiryDate.ToString("dd MMMM yyyy");
            EncryptDot.Fill = new SolidColorBrush(Color.Parse("#0d9e75"));
            EncryptStatus.Text = "AES-256-GCM encryption active";
        }
        else
        {
            LicenseDot.Fill = new SolidColorBrush(Color.Parse("#ef4444"));
            LicenseStatus.Text = _enc.Status;
            EncryptDot.Fill = new SolidColorBrush(Color.Parse("#ef4444"));
            EncryptStatus.Text = "Encryption disabled — install valid license";
        }
    }

    private void OnIssue(object? sender, RoutedEventArgs e)
    {
        var op = OperatorInput.Text?.Trim();
        var to = IssuedToInput.Text?.Trim();
        if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(to))
        {
            IssueStatus.Text = "Please fill in operator name and issued to.";
            IssueStatus.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
            return;
        }
        var days = (int)(ValidDaysInput.Value ?? 365);
        // Use target device ID if provided, otherwise use this device
        var targetDeviceId = TargetDeviceId.Text?.Trim();
        var license = EncryptionService.IssueLicense(op, to, days);
        if (!string.IsNullOrEmpty(targetDeviceId))
            license.DeviceId = targetDeviceId;

        // Save locally if for this device
        if (string.IsNullOrEmpty(targetDeviceId))
        {
            _enc.SaveLicense(license);
            IssueStatus.Text = $"License issued for {days} days — saved to this device";
        }
        else
        {
            // Export license file for remote device
            var json = JsonSerializer.Serialize(license, new JsonSerializerOptions { WriteIndented = true });
            var exportPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"InfraDrone_License_{to.Replace(" ","_")}.json");
            File.WriteAllText(exportPath, json);
            IssueStatus.Text = $"License exported to Desktop: {System.IO.Path.GetFileName(exportPath)}";
        }
        IssueStatus.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
        RefreshStatus();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import InfraDrone License",
            FileTypeFilter = new[] { new FilePickerFileType("License") { Patterns = new[] { "*.json" } } }
        });
        if (files.Count == 0) return;
        var json = File.ReadAllText(files[0].Path.LocalPath);
        var license = JsonSerializer.Deserialize<DeviceLicense>(json);
        if (license == null)
        {
            IssueStatus.Text = "Invalid license file.";
            IssueStatus.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
            return;
        }
        if (!_enc.VerifyLicense(license))
        {
            IssueStatus.Text = "License not valid for this device.";
            IssueStatus.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
            return;
        }
        _enc.SaveLicense(license);
        IssueStatus.Text = "License imported successfully.";
        IssueStatus.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
        RefreshStatus();
    }
}
