using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Services;

public class WildlifeDetection
{
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

// Mirrors AerialDetectionService.cs exactly -- same YOLOv8 ONNX output layout,
// just numClasses=1 instead of 10. Trained on the Roboflow "Roe deer" thermal
// dataset (CC BY 4.0, thermal-drone-imaging-for-wildlife-monitoring workspace),
// fine-tuned 2026-08-30: mAP50=0.952, mAP50-95=0.543, precision=0.975, recall=0.911
// on the validation split (31 images, 45 instances).
//
// IMPORTANT — demo/dataset limitation, not yet field-validated:
// This model was trained on third-party thermal footage, not footage captured by
// DAMbv's own hardware. It proves the detection pipeline works on real thermal
// roe-deer imagery, but has not been validated against footage from an actual
// DAMbv survey flight. Treat any map/GPS output built on this as illustrative
// until validated against real field footage.
public class WildlifeDetectionService
{
    private InferenceSession? _session;
    private const int InputSize = 640;
    private float _confThreshold = 0.4f; // matches the conf used during validation/inference testing

    private static readonly string[] ClassNames = { "roe-deer" };

    private static readonly Dictionary<string, SKColor> ClassColors = new()
    {
        ["roe-deer"] = new SKColor(220, 38, 38), // bold red -- high contrast against thermal palettes
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
                Console.WriteLine("[WildlifeDetection] CUDA execution provider enabled.");
            }
            catch (Exception gpuEx)
            {
                Console.WriteLine("[WildlifeDetection] CUDA unavailable, falling back to CPU: " + gpuEx.Message);
            }
            _session = new InferenceSession(path, options);
            ModelName = System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WildlifeDetection] Failed to load model: " + ex.Message);
            return false;
        }
    }

    public List<WildlifeDetection> Detect(string imagePath)
    {
        if (_session == null) return new List<WildlifeDetection>();
        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null) return new List<WildlifeDetection>();

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

        // Output shape confirmed via export 2026-08-30: [1, 5, 8400] -> 4 box coords
        // + 1 class score (roe-deer only) per anchor. Same layout as the 10-class
        // AerialDetectionService, just numClasses=1.
        int numClasses = ClassNames.Length;
        int numAnchors = 8400;
        var detections = new List<WildlifeDetection>();

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

            detections.Add(new WildlifeDetection
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

    private List<WildlifeDetection> NonMaxSuppression(List<WildlifeDetection> dets, float iouThreshold)
    {
        var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<WildlifeDetection>();
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(d => IoU(best, d) > iouThreshold && d.Label == best.Label);
        }
        return keep;
    }

    private float IoU(WildlifeDetection a, WildlifeDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0;
    }

    public SKBitmap DrawDetections(string imagePath, List<WildlifeDetection> detections)
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
