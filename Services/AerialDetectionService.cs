using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Services;

public class AerialDetection
{
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public class AerialDetectionService
{
    private InferenceSession? _session;
    private const int InputSize = 640;
    private float _confThreshold = 0.35f;

    // Trained on VisDrone2019-DET (10 real categories, permission granted directly by rights holder)
    private static readonly string[] ClassNames = {
        "pedestrian", "people", "bicycle", "car", "van",
        "truck", "tricycle", "awning-tricycle", "bus", "motor"
    };

    private static readonly Dictionary<string, SKColor> ClassColors = new()
    {
        ["pedestrian"] = new SKColor(220, 38, 38),       // red
        ["people"] = new SKColor(220, 38, 38),           // red
        ["bicycle"] = new SKColor(234, 179, 8),          // yellow
        ["motor"] = new SKColor(234, 179, 8),            // yellow
        ["car"] = new SKColor(62, 142, 126),             // teal
        ["van"] = new SKColor(62, 142, 126),              // teal
        ["truck"] = new SKColor(62, 142, 126),            // teal
        ["tricycle"] = new SKColor(148, 163, 184),       // grey
        ["awning-tricycle"] = new SKColor(148, 163, 184),// grey
        ["bus"] = new SKColor(139, 92, 246),             // purple
    };

    public bool IsLoaded => _session != null;
    public string ModelName { get; private set; } = "";

    public bool LoadModel(string path)
    {
        try
        {
            var options = new SessionOptions();
            try
            {
                options.AppendExecutionProvider_CUDA(0);
                Console.WriteLine("[AerialDetection] CUDA execution provider enabled.");
            }
            catch (Exception gpuEx)
            {
                Console.WriteLine("[AerialDetection] CUDA unavailable, falling back to CPU: " + gpuEx.Message);
            }
            _session = new InferenceSession(path, options);
            ModelName = System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AerialDetection] Failed to load model: " + ex.Message);
            return false;
        }
    }

    public List<AerialDetection> Detect(string imagePath)
    {
        if (_session == null) return new List<AerialDetection>();
        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null) return new List<AerialDetection>();

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

        // YOLOv8 output shape for our 10-class model: [1, 14, 8400] -> 4 box coords + 10 class scores per anchor
        int numClasses = ClassNames.Length;
        int numAnchors = 8400;
        var detections = new List<AerialDetection>();

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

            detections.Add(new AerialDetection
            {
                Label = maxClass >= 0 && maxClass < ClassNames.Length ? ClassNames[maxClass] : "unknown",
                Confidence = maxScore,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h
            });
        }

        return NonMaxSuppression(detections, 0.45f);
    }

    private List<AerialDetection> NonMaxSuppression(List<AerialDetection> dets, float iouThreshold)
    {
        var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<AerialDetection>();
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(d => IoU(best, d) > iouThreshold && d.Label == best.Label);
        }
        return keep;
    }

    private float IoU(AerialDetection a, AerialDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0;
    }

    public SKBitmap DrawDetections(string imagePath, List<AerialDetection> detections)
    {
        var bitmap = SKBitmap.Decode(imagePath);
        using var canvas = new SKCanvas(bitmap);
        using var textPaint = new SKPaint { TextSize = 24, IsAntialias = true };
        using var bgPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill };

        foreach (var d in detections)
        {
            var color = ClassColors.TryGetValue(d.Label, out var c) ? c : SKColors.White;
            using var boxPaint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            textPaint.Color = color;

            canvas.DrawRect(d.X, d.Y, d.Width, d.Height, boxPaint);
            var label = $"{d.Label} {d.Confidence:P0}";
            var textWidth = textPaint.MeasureText(label);
            canvas.DrawRect(d.X, d.Y - 28, textWidth + 8, 28, bgPaint);
            canvas.DrawText(label, d.X + 4, d.Y - 8, textPaint);
        }
        return bitmap;
    }
}
