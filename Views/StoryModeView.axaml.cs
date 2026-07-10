using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraDroneDesktop.Views
{
    public partial class StoryModeView : UserControl
    {
        private const string Base = "/home/sam/opendd_dataset/";
        private int _currentStep = 0;

        private readonly (string Path, string Narrative)[] _steps = new[]
        {
            (Base + "analytics_traffic_volume.png",
             "STEP 1 -- THE SETTING.  Each bar is one 10-second window of the recording. The colors stack by road-user type: red = pedestrians, teal/green = vehicles (cars, vans, trucks all grouped together). The bars are different heights because real traffic naturally varies minute to minute -- look at the labeled peak (30 road users, busiest moment) versus the labeled dip (10 road users, quietest moment). This is one real roundabout, 9.3 minutes, fully drone-tracked -- nothing here is simulated."),

            (Base + "summary_chart.png",
             "STEP 2 -- THE HEADLINE FINDING.  LEFT chart: each bar is one pedestrian; red bars had at least one near-miss. Count them: 6 of the 15 pedestrians -- 40% -- experienced a real near-miss in this single short recording. RIGHT chart: when a near-miss happened, who reacted? The pedestrian adjusted 8 times. The vehicle yielded only 3 times. In plain terms: pedestrians are doing almost all of the safety work themselves."),

            (Base + "analytics_speed_heatmap.png",
             "STEP 3 -- WHERE IS IT RISKIEST?  Red dots = higher vehicle speed, green = slower. This map is built automatically from every vehicle position in the recording. Notice the red zone concentrated in one specific area -- that's a real, precise location worth a physical safety review, not a vague impression."),

            (Base + "analytics_near_miss_hotspot.png",
             "STEP 4 -- ALL 10 NEAR-MISSES, MAPPED.  Each red circle is one automatically-detected near-miss; bigger circle = more severe (lower reaction time). This is Time-To-Collision analysis: real math, run on real trajectories, with zero human watching the footage to spot it."),

            (Base + "trajectory_map.png",
             "STEP 5 -- ZOOMING INTO ONE CASE.  We're now following the single most severe event: pedestrian 910 (red path) and vehicle 942 (teal path), labeled directly on the map. Minimum distance between them: 4.7 meters. Time-To-Collision: 0.54 seconds -- under half a second of reaction time. This is a textbook-severe near-miss by published traffic-safety standards."),

            (Base + "pedestrian_behavior_profile.png",
             "STEP 6 -- DID THE PEDESTRIAN NOTICE?  This is ONE pedestrian's continuous 65-second walk -- not multiple people. Their speed naturally rises and falls a bit throughout, which is normal. IGNORE the earlier dips in the middle of the chart -- those are just ordinary pace variation, not flagged events. Look ONLY at the dark red-shaded band on the far right: their speed hits the LOWEST point of the entire walk right there, then acceleration spikes to its HIGHEST point immediately after. That specific moment, and only that one, is tied to the confirmed near-miss."),

            (Base + "analytics_pedestrian_acceleration_spikes.png",
             "STEP 7 -- INDEPENDENT CONFIRMATION.  This chart ranks all 15 pedestrians by their single strongest reaction moment, using a completely different calculation from Step 6 -- this one measures acceleration only, not speed. Find the '910' bar: the small label next to it reads 't=233s' -- the exact same moment as the red zone in Step 6. Two unrelated methods, agreeing on the same real event."),

            (Base + "analytics_yielding_behavior.png",
             "STEP 8 -- DID THE DRIVER NOTICE?  Each dot is one real pedestrian-vehicle interaction. The circled dot, labeled directly on the chart, is pedestrian 910 and vehicle 942 specifically. It sits in the grey 'no clear reaction' zone -- meaning the driver's acceleration barely changed at all. The pedestrian reacted. The driver didn't."),

            (Base + "safety_score.png",
             "STEP 9 -- WHY THIS MATTERS BEYOND ONE PERSON.  This isn't just about pedestrian 910. LEFT chart: four real, independently-measured risk factors for this whole location. RIGHT: they combine into one transparent score -- every input and its weight stated on the chart, nothing hidden. This is the number a Province could track over time, or compare across different crossings."),

            (Base + "trajectory_map.png",
             "STEP 10 -- BACK WHERE WE STARTED, THE PROPOSAL.  Same map as Step 5 -- pedestrian 910 and vehicle 942, deliberately shown again. Everything since Step 5 -- the reaction, the driver's non-response, the wider risk score -- all traces back to this one real, automatically-detected moment. In the deployed system: this exact moment triggers a live alert to a ground interview team, so they can ask this specific person what happened while it's still fresh. The data finds the risk. The conversation finds out why."),
        };

        public StoryModeView()
        {
            InitializeComponent();
            ShowStep(0);
        }

        private void ShowStep(int index)
        {
            if (index < 0 || index >= _steps.Length) return;
            _currentStep = index;
            var (path, narrative) = _steps[index];

            StepCounter.Text = $"Step {index + 1} of {_steps.Length}";
            NarrativeText.Text = narrative;

            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                StoryImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
            }
        }

        private void OnPrevious(object? sender, RoutedEventArgs e) => ShowStep(_currentStep - 1);
        private void OnNext(object? sender, RoutedEventArgs e) => ShowStep(_currentStep + 1);
    }
}
