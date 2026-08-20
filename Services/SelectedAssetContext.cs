using System;
using System.Collections.Generic;
namespace InfraDroneDesktop.Services;

// Lightweight shared state letting Flight View hand off "the asset the user
// just clicked on the map" to the Asset Intelligence tab, without the two
// views needing a direct reference to each other. Flight View sets these and
// raises NavigateToHealthPassportRequested; MainWindow subscribes once at
// startup to actually switch tabs, and Asset Intelligence reads the fields
// back out once it becomes visible.
public static class SelectedAssetContext
{
    public static Dictionary<string, string>? CurrentFields { get; private set; }
    public static string CurrentLayerName { get; private set; } = "";

    public static event Action? NavigateToHealthPassportRequested;

    public static void SetAndNavigate(Dictionary<string, string> fields, string layerName)
    {
        CurrentFields = fields;
        CurrentLayerName = layerName;
        NavigateToHealthPassportRequested?.Invoke();
    }
}
