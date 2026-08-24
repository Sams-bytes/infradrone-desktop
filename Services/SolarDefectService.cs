using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InfraDroneDesktop.Services;

public class SolarClassification
{
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public Dictionary<string, float> AllScores { get; set; } = new();
}

// Real trained classifier (not a placeholder) -- CNN trained on RaptorMaps'
// public InfraredSolarModules dataset (20,000 real thermal images, 12 real
// defect classes). 50.17% validation accuracy on this first training run --
// a real, working starting point, not yet production-grade. Follows the
// exact same ONNX Runtime pattern as DefectDetectionService, adapted for
// classification (single label per image, softmax + argmax) rather than
// object detection (no NMS needed).
public class SolarDefectService
{
    private InferenceSession? _session;
    private const int InputSize = 40;
    private string[] _classes = Array.Empty<string>();

    public bool IsLoaded => _session != null;

    public bool LoadModel(string modelPath, string labelsPath)
    {
        try
        {
            _session = new InferenceSession(modelPath);
            _classes = JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(labelsPath)) ?? Array.Empty<string>();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Solar] Failed to load model: " + ex.Message);
            return false;
        }
    }

    public SolarClassification? Classify(string imagePath)
    {
        if (_session == null) return null;

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null) return null;

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

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", input) };
        using var results = _session.Run(inputs);
        var logits = results.First().AsEnumerable<float>().ToArray();

        // Softmax over the 12 real classes
        var maxLogit = logits.Max();
        var exps = logits.Select(l => (float)Math.Exp(l - maxLogit)).ToArray();
        var sumExp = exps.Sum();
        var probs = exps.Select(e => e / sumExp).ToArray();

        var allScores = new Dictionary<string, float>();
        for (int i = 0; i < _classes.Length && i < probs.Length; i++)
            allScores[_classes[i]] = probs[i];

        var bestIdx = Array.IndexOf(probs, probs.Max());
        return new SolarClassification
        {
            Label = bestIdx >= 0 && bestIdx < _classes.Length ? _classes[bestIdx] : "unknown",
            Confidence = probs.Max(),
            AllScores = allScores
        };
    }
}
