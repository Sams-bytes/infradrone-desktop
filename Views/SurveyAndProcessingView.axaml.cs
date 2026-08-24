using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
namespace InfraDroneDesktop.Views;

public partial class SurveyAndProcessingView : UserControl
{
    private SurveyGridView? _surveyGridView;
    private ProcessingView? _processingView;

    // Re-exposed so MainWindow's existing "Send to Mission" wiring keeps
    // working unchanged -- this consolidated view just hosts the two real,
    // already-working views, it doesn't duplicate their internal logic.
    public event System.Action? SendToMissionRequested;

    public SurveyAndProcessingView()
    {
        InitializeComponent();
        _surveyGridView = new SurveyGridView();
        _surveyGridView.SendToMissionRequested += () => SendToMissionRequested?.Invoke();
        _processingView = new ProcessingView();
        ContentHost.Children.Add(_surveyGridView);
    }

    public System.Collections.Generic.List<(double Lat, double Lon, double AltM)>? GetGeneratedWaypoints()
        => _surveyGridView?.GetGeneratedWaypoints();

    public void SetFlightView(FlightView fv) => _processingView?.SetFlightView(fv);

    private void OnSubNav(object? s, RoutedEventArgs e)
    {
        var btn = s as Button;
        ContentHost.Children.Clear();
        if (btn == BtnSubPlan && _surveyGridView != null) ContentHost.Children.Add(_surveyGridView);
        if (btn == BtnSubProcess && _processingView != null) ContentHost.Children.Add(_processingView);

        foreach (var b in new[] { BtnSubPlan, BtnSubProcess })
        {
            bool active = b == btn;
            b.Background = new SolidColorBrush(Color.Parse(active ? "#0d3d2e" : "#1a2637"));
            b.Foreground = new SolidColorBrush(Color.Parse(active ? "#0d9e75" : "#94a3b8"));
        }
    }
}
