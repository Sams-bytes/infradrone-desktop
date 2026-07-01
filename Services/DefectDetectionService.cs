using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Services;

public class Detection
{
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public class DefectDetectionService
{
    private InferenceSession? _session;
    private const int InputSize = 640;
    private float _confThreshold = 0.35f;

    // COCO class names (placeholder until custom infrastructure-defect model is trained)
    private static readonly string[] CocoClasses = {
        "pothole"
    };

    public bool IsLoaded => _session != null;
    public string ModelName { get; private set; } = "";

    public bool LoadModel(string path)
    {
        try
        {
            _session = new InferenceSession(path);
            ModelName = System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AI] Failed to load model: " + ex.Message);
            return false;
        }
    }

    public List<Detection> Detect(string imagePath)
    {
        if (_session == null) return new List<Detection>();

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null) return new List<Detection>();

        var scaleX = (float)bitmap.Width / InputSize;
        var scaleY = (float)bitmap.Height / InputSize;

        using var resized = bitmap.Resize(new SKImageInfo(InputSize, InputSize), SKFilterQuality.Medium);
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });

        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                var px = resized.GetPixel(x, y);
                input[0, 0, y, x] = px.Red / 255f;
                input[0, 1, y, x] = px.Green / 255f;
                input[0, 2, y, x] = px.Blue / 255f;
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", input) };
        using var results = _session.Run(inputs);
        var output = results.First().AsEnumerable<float>().ToArray();

        // YOLOv8 output shape: [1, 5, 8400] -> 4 box coords + 1 class score (pothole) per anchor
        int numClasses = 1;
        int numAnchors = 8400;
        var detections = new List<Detection>();

        for (int i = 0; i < numAnchors; i++)
        {
            float maxScore = 0;
            int maxClass = -1;
            for (int c = 0; c < numClasses; c++)
            {
                var score = output[(4 + c) * numAnchors + i];
                if (score > maxScore) { maxScore = score; maxClass = c; }
            }
            if (maxScore < _confThreshold) continue;

            var cx = output[0 * numAnchors + i] * scaleX;
            var cy = output[1 * numAnchors + i] * scaleY;
            var w = output[2 * numAnchors + i] * scaleX;
            var h = output[3 * numAnchors + i] * scaleY;

            detections.Add(new Detection
            {
                Label = maxClass >= 0 && maxClass < CocoClasses.Length ? CocoClasses[maxClass] : "unknown",
                Confidence = maxScore,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h
            });
        }

        return NonMaxSuppression(detections, 0.45f);
    }

    private List<Detection> NonMaxSuppression(List<Detection> dets, float iouThreshold)
    {
        var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<Detection>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(d => IoU(best, d) > iouThreshold && d.Label == best.Label);
        }
        return keep;
    }

    private float IoU(Detection a, Detection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0;
    }

    public SKBitmap DrawDetections(string imagePath, List<Detection> detections)
    {
        var bitmap = SKBitmap.Decode(imagePath);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
        using var textPaint = new SKPaint { Color = SKColors.Lime, TextSize = 24, IsAntialias = true };
        using var bgPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill };

        foreach (var d in detections)
        {
            canvas.DrawRect(d.X, d.Y, d.Width, d.Height, paint);
            var label = $"{d.Label} {d.Confidence:P0}";
            var textWidth = textPaint.MeasureText(label);
            canvas.DrawRect(d.X, d.Y - 28, textWidth + 8, 28, bgPaint);
            canvas.DrawText(label, d.X + 4, d.Y - 8, textPaint);
        }
        return bitmap;
    }
}
