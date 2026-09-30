using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace InfraDroneDesktop.Views;

/// <summary>🏛 Asset Monitor: one sidebar tab with five sub-tabs (same pattern as Traffic Intelligence).</summary>
public partial class AssetMonitorView : UserControl
{
    private readonly Dictionary<Button, Func<Control>> _factories = new();
    private readonly Dictionary<Button, Control> _cache = new();

    public AssetMonitorView()
    {
        InitializeComponent();
        _factories[BtnSubBriefing] = () => new AmBriefingPanel();
        _factories[BtnSubRegister] = () => new AmRegisterPanel();
        _factories[BtnSubMap] = () => new AmMapPanel();
        _factories[BtnSubSatellite] = () => new AmSatellitePanel();
        _factories[BtnSubBridgeCheck] = () => new AmBridgeCheckPanel();
        _factories[BtnSubRoadCheck] = () => new AmRoadCheckPanel();
        _factories[BtnSubTasks] = () => new AmTasksPanel();
        _factories[BtnSubCondition] = () => new AmConditionPanel();
        _factories[BtnSubValue] = () => new AmValueAuditPanel();

        // Labels are TextBlocks with their own colour, so the button template can never
        // hide the text (the text was only visible on mouse-hover before).
        foreach (var b in _factories.Keys)
            b.Content = new TextBlock { Text = b.Content as string ?? "", FontSize = 13 };

        var first = _factories[BtnSubBriefing]();
        _cache[BtnSubBriefing] = first;
        ContentHost.Children.Add(first);
        ApplyNavStyle(BtnSubBriefing);

        // "📍 Show on map" from any sub-tab -> switch to the Map sub-tab and fly to the bridge
        AmNav.ShowOnMapRequested += (id, lat, lon) =>
        {
            OnSubNav(BtnSubMap, new RoutedEventArgs());
            if (_cache.TryGetValue(BtnSubMap, out var v) && v is AmMapPanel map) map.FocusOn(id, lat, lon);
        };
    }

    private void ApplyNavStyle(Button activeBtn)
    {
        foreach (var b in _factories.Keys)
        {
            bool active = b == activeBtn;
            if (b.Content is TextBlock tb)
                tb.Dyn(TextBlock.ForegroundProperty, active ? "AppAccentFg" : "AppTextPrimary");
            b.Dyn(Button.BackgroundProperty, active ? "AppAccentBg" : "AppPanelBg");
            b.Dyn(Button.BorderBrushProperty, active ? "AppAccentFg" : "AppPanelBorder");
            tb_Bold(b, active);
        }
    }

    private static void tb_Bold(Button b, bool active)
    {
        if (b.Content is TextBlock tb) tb.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnSubNav(object? s, RoutedEventArgs e)
    {
        if (s is not Button btn || !_factories.ContainsKey(btn)) return;
        if (!_cache.TryGetValue(btn, out var view))
        {
            view = _factories[btn]();
            _cache[btn] = view;
        }
        ContentHost.Children.Clear();
        ContentHost.Children.Add(view);
        ApplyNavStyle(btn);   // theme-aware, follows light/dark mode switches
    }
}
