using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Services;

public class BirdDetection
{
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

// Mirrors WildlifeDetectionService.cs exactly -- same single-class YOLOv8
// output layout, but with two values that MUST match this specific model's
// actual export, confirmed 2026-08-31, not copy-pasted from the roe-deer model:
//   - InputSize = 800 (not 640) -- this model was trained AND exported at
//     imgsz=800, matching the Big Bird dataset's native 800x800 images.
//   - numAnchors = 13125 (not 8400) -- confirmed from the ONNX export's
//     printed output shape (1, 5, 13125). Anchor count scales with input
//     resolution, so this is a direct consequence of the 800 InputSize,
//     not an independent guess.
// Getting either of these wrong would NOT cause a compile or runtime error --
// it would silently produce wrong/garbage detections, since the tensor math
// would just misalign. Verified against the actual export log before writing.
//
// Trained on Big Bird (Wilson et al. 2026, Remote Sensing in Ecology and
// Conservation, DOI 10.1002/rse2.70059), University of Queensland, bird-only
// (single class) detector: mAP50=0.960, mAP50-95=0.636, precision=0.955,
// recall=0.915 on the paper's own held-out test split (5,152 images, 6,815
// bird instances, 101+ species, global coverage, 21 camera systems).
// Exceeds the paper's own published bird-only benchmark of 93.7% AP.
//
// IMPORTANT — same caveat as WildlifeDetectionService: trained on third-party
// research imagery, not DAMbv's own captured footage. Proves the pipeline
// works on real, diverse aerial bird imagery; not yet validated against a
// real DAMbv survey flight.
public class BigBirdDetectionService
{
    private InferenceSession? _session;
    private const int InputSize = 800;
    private float _confThreshold = 0.4f;

    private static readonly string[] ClassNames = { "bird" };

    private static readonly Dictionary<string, SKColor> ClassColors = new()
    {
        ["bird"] = new SKColor(34, 197, 94), // green -- distinct from roe-deer's red, avoids confusion if both are ever shown together
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
                Console.WriteLine("[BigBirdDetection] CUDA execution provider enabled.");
            }
            catch (Exception gpuEx)
            {
                Console.WriteLine("[BigBirdDetection] CUDA unavailable, falling back to CPU: " + gpuEx.Message);
            }
            _session = new InferenceSession(path, options);
            ModelName = System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[BigBirdDetection] Failed to load model: " + ex.Message);
            return false;
        }
    }

    public List<BirdDetection> Detect(string imagePath)
    {
        if (_session == null) return new List<BirdDetection>();
        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null) return new List<BirdDetection>();

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

        // Output shape confirmed via export 2026-08-31: [1, 5, 13125] -> 4 box
        // coords + 1 class score (bird only) per anchor.
        int numClasses = ClassNames.Length;
        int numAnchors = 13125;
        var detections = new List<BirdDetection>();

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

            detections.Add(new BirdDetection
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

    private List<BirdDetection> NonMaxSuppression(List<BirdDetection> dets, float iouThreshold)
    {
        var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<BirdDetection>();
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(d => IoU(best, d) > iouThreshold && d.Label == best.Label);
        }
        return keep;
    }

    private float IoU(BirdDetection a, BirdDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0;
    }

    public SKBitmap DrawDetections(string imagePath, List<BirdDetection> detections)
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
