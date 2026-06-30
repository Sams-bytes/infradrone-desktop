using System;
using System.Collections.Generic;

namespace InfraDroneDesktop.Services;

public class CameraProfile
{
    public string Name { get; set; } = "";
    public double SensorWidthMm { get; set; }
    public double SensorHeightMm { get; set; }
    public double FocalLengthMm { get; set; }
    public int ImageWidthPx { get; set; }
    public int ImageHeightPx { get; set; }

    public static readonly CameraProfile DjiMavic3E = new()
    {
        Name = "DJI Mavic 3 Enterprise", SensorWidthMm = 17.3, SensorHeightMm = 13.0,
        FocalLengthMm = 12.29, ImageWidthPx = 5280, ImageHeightPx = 3956
    };
    public static readonly CameraProfile ParrotSequoia = new()
    {
        Name = "Parrot Sequoia Multispectral", SensorWidthMm = 4.8, SensorHeightMm = 3.6,
        FocalLengthMm = 4.0, ImageWidthPx = 1280, ImageHeightPx = 960
    };
    public static readonly CameraProfile Generic20MP = new()
    {
        Name = "Generic 20MP (1-inch sensor)", SensorWidthMm = 13.2, SensorHeightMm = 8.8,
        FocalLengthMm = 8.8, ImageWidthPx = 5472, ImageHeightPx = 3648
    };
}

public class SurveyGridResult
{
    public List<(double Lat, double Lon)> Waypoints { get; set; } = new();
    public double GsdCm { get; set; }
    public double FootprintWidthM { get; set; }
    public double FootprintHeightM { get; set; }
    public double LineSpacingM { get; set; }
    public double ShotIntervalM { get; set; }
    public int EstimatedPhotos { get; set; }
    public double EstimatedFlightTimeMin { get; set; }
    public double AreaHa { get; set; }
}

public class SurveyGridService
{
    // Generate a lawnmower grid pattern over a polygon area
    public static SurveyGridResult GenerateGrid(
        List<(double Lat, double Lon)> polygon,
        double altitudeM,
        CameraProfile camera,
        double frontOverlapPct = 80,
        double sideOverlapPct = 70,
        double speedMs = 8,
        double cameraAngleDeg = 90) // 90 = nadir, lower = oblique
    {
        var result = new SurveyGridResult();

        // Ground Sample Distance (GSD) calculation
        // GSD (cm/px) = (sensor width mm * altitude m * 100) / (focal length mm * image width px)
        var gsdCm = (camera.SensorWidthMm * altitudeM * 100) / (camera.FocalLengthMm * camera.ImageWidthPx);
        result.GsdCm = gsdCm;

        // Ground footprint of one photo
        var footprintW = (camera.ImageWidthPx * gsdCm) / 100.0; // metres
        var footprintH = (camera.ImageHeightPx * gsdCm) / 100.0;
        result.FootprintWidthM = footprintW;
        result.FootprintHeightM = footprintH;

        // Spacing between flight lines (side overlap)
        var lineSpacing = footprintW * (1 - sideOverlapPct / 100.0);
        result.LineSpacingM = lineSpacing;

        // Spacing between shots along a line (front overlap)
        var shotInterval = footprintH * (1 - frontOverlapPct / 100.0);
        result.ShotIntervalM = shotInterval;

        // Bounding box of polygon
        var minLat = double.MaxValue; var maxLat = double.MinValue;
        var minLon = double.MaxValue; var maxLon = double.MinValue;
        foreach (var (lat, lon) in polygon)
        {
            minLat = Math.Min(minLat, lat); maxLat = Math.Max(maxLat, lat);
            minLon = Math.Min(minLon, lon); maxLon = Math.Max(maxLon, lon);
        }

        // Convert to metres using simple equirectangular approximation
        var centerLat = (minLat + maxLat) / 2;
        var metersPerDegLat = 111320.0;
        var metersPerDegLon = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);

        var widthM = (maxLon - minLon) * metersPerDegLon;
        var heightM = (maxLat - minLat) * metersPerDegLat;
        result.AreaHa = (widthM * heightM) / 10000.0;

        // Generate lawnmower lines (north-south lines, sweeping east-west)
        var waypoints = new List<(double Lat, double Lon)>();
        int numLines = Math.Max(1, (int)Math.Ceiling(widthM / lineSpacing));
        bool goingNorth = true;

        for (int i = 0; i <= numLines; i++)
        {
            var lon = minLon + (i * lineSpacing / metersPerDegLon);
            if (lon > maxLon) lon = maxLon;

            if (goingNorth)
            {
                waypoints.Add((minLat, lon));
                waypoints.Add((maxLat, lon));
            }
            else
            {
                waypoints.Add((maxLat, lon));
                waypoints.Add((minLat, lon));
            }
            goingNorth = !goingNorth;
        }

        result.Waypoints = waypoints;

        // Estimate number of photos along the lines
        var totalLineLength = numLines * heightM;
        result.EstimatedPhotos = (int)(totalLineLength / shotInterval);

        // Estimate flight time (distance / speed + turns)
        var totalDistance = totalLineLength + (numLines * lineSpacing); // lines + turn segments
        result.EstimatedFlightTimeMin = (totalDistance / speedMs) / 60.0;

        return result;
    }
}
