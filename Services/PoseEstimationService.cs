using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace InfraDroneDesktop.Services;

public class PoseResult
{
    public float BoxX, BoxY, BoxW, BoxH;
    public SKPoint[] Keypoints = new SKPoint[17];
    public float[] Scores = new float[17];
}

public class PoseEstimationService
{
    private InferenceSession? _session;
    private const int InputW = 192;
    private const int InputH = 256;
    private const float Padding = 1.1f;
    private static readonly float[] Mean = { 123.675f, 116.28f, 103.53f };
    private static readonly float[] Std = { 58.395f, 57.12f, 57.375f };
    public const float KeypointThreshold = 0.2f;

    // Standard COCO 17-keypoint skeleton, ported directly from FlyPose's
    // real visualization.py (verified, not guessed).
    public static readonly (int, int)[] Skeleton = {
        (0,1),(0,2),(1,3),(2,4),(3,5),(4,6),(5,6),(5,7),(7,9),
        (6,8),(8,10),(5,11),(6,12),(11,12),(11,13),(13,15),(12,14),(14,16)
    };

    public bool IsLoaded => _session != null;

    public bool LoadModel(string path)
    {
        try
        {
            _session = new InferenceSession(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PoseEstimation] Failed to load model: {ex.Message}");
            return false;
        }
    }

    // Ported from FlyPose's preprocess_pose_crop + pose_affine (real Python
    // reference). Uses a simple scale+translate affine (no rotation), so
    // the inverse is computed directly rather than a general matrix invert.
    public PoseResult? EstimatePose(SKBitmap frame, float boxX, float boxY, float boxW, float boxH)
    {
        if (_session == null) return null;

        float cx = boxX + boxW * 0.5f;
        float cy = boxY + boxH * 0.5f;
        float w = Math.Max(boxW * Padding, 1.0f);
        float h = Math.Max(boxH * Padding, 1.0f);

        float scale = Math.Min(InputW / w, InputH / h);
        float tx = InputW * 0.5f - scale * cx;
        float ty = InputH * 0.5f - scale * cy;

        var matrix = new SKMatrix
        {
            ScaleX = scale, SkewY = 0, TransX = tx,
            SkewX = 0, ScaleY = scale, TransY = ty,
            Persp0 = 0, Persp1 = 0, Persp2 = 1
        };

        using var warped = new SKBitmap(InputW, InputH);
        using (var canvas = new SKCanvas(warped))
        {
            canvas.Clear(SKColors.Black);
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(frame, 0, 0);
        }

        var input = new DenseTensor<float>(new[] { 1, 3, InputH, InputW });
        for (int y = 0; y < InputH; y++)
        {
            for (int x = 0; x < InputW; x++)
            {
                var px = warped.GetPixel(x, y);
                // to_rgb=True in FlyPose's config -- SkiaSharp GetPixel already
                // gives RGBA order, so px.Red/Green/Blue map directly.
                input[0, 0, y, x] = (px.Red - Mean[0]) / Std[0];
                input[0, 1, y, x] = (px.Green - Mean[1]) / Std[1];
                input[0, 2, y, x] = (px.Blue - Mean[2]) / Std[2];
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", input) };
        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        int hmH = output.Dimensions[2]; // 64
        int hmW = output.Dimensions[3]; // 48
        float scaleX = (float)InputW / hmW;
        float scaleY = (float)InputH / hmH;

        var result = new PoseResult { BoxX = boxX, BoxY = boxY, BoxW = boxW, BoxH = boxH };

        for (int k = 0; k < 17; k++)
        {
            float maxVal = float.MinValue;
            int maxX = 0, maxY = 0;
            for (int yy = 0; yy < hmH; yy++)
            {
                for (int xx = 0; xx < hmW; xx++)
                {
                    var v = output[0, k, yy, xx];
                    if (v > maxVal) { maxVal = v; maxX = xx; maxY = yy; }
                }
            }

            float fx = maxX, fy = maxY;
            // Sub-pixel refinement, ported directly from refine_peak()
            if (maxX > 0 && maxX < hmW - 1 && maxY > 0 && maxY < hmH - 1)
            {
                var right = output[0, k, maxY, maxX + 1];
                var left = output[0, k, maxY, maxX - 1];
                var down = output[0, k, maxY + 1, maxX];
                var up = output[0, k, maxY - 1, maxX];
                fx += 0.25f * Math.Sign(right - left);
                fy += 0.25f * Math.Sign(down - up);
            }

            float px = fx * scaleX;
            float py = fy * scaleY;

            // Inverse of our simple scale+translate affine: x_orig = (x - tx)/scale
            float origX = (px - tx) / scale;
            float origY = (py - ty) / scale;

            result.Keypoints[k] = new SKPoint(origX, origY);
            result.Scores[k] = maxVal;
        }

        return result;
    }
}
