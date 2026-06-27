using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraDroneDesktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnFlightView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load flight view
    }

    private void OnMissionView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load mission view
    }

    private void OnPreflightView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load preflight view
    }

    private void OnWeatherView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load weather view
    }

    private void OnParamsView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load params view
    }

    private void OnAuditView(object? sender, RoutedEventArgs e)
    {
        // TODO: Load audit view
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        // TODO: Connect to MAVLink
    }
}
