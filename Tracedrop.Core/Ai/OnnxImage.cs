using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Tracedrop.Core.Ai;

/// <summary>Moves pixels between OpenCV mats and NCHW float tensors.</summary>
internal static class OnnxImage
{
    /// <summary>Planar NCHW tensor from a 3-channel CV_32F mat (channel order as stored in the mat).</summary>
    public static DenseTensor<float> ToTensor(Mat floatImage3)
    {
        var h = floatImage3.Rows;
        var w = floatImage3.Cols;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        var buffer = tensor.Buffer.Span;
        var planes = Cv2.Split(floatImage3);
        try
        {
            var plane = new float[h * w];
            for (var c = 0; c < 3; c++)
            {
                using var contiguous = planes[c].Clone(); // Split planes are continuous; Clone guarantees it.
                Marshal.Copy(contiguous.Data, plane, 0, plane.Length);
                plane.AsSpan().CopyTo(buffer.Slice(c * h * w, h * w));
            }
        }
        finally
        {
            foreach (var p in planes) p.Dispose();
        }
        return tensor;
    }

    /// <summary>One H×W plane of an output tensor (shape [1,C,H,W] or [1,H,W] or [H,W]) as a CV_32F mat.</summary>
    public static Mat PlaneToMat(Tensor<float> tensor, int channel = 0)
    {
        var dims = tensor.Dimensions.ToArray();
        int h, w;
        if (dims.Length == 4) { h = dims[2]; w = dims[3]; }
        else if (dims.Length == 3) { h = dims[1]; w = dims[2]; }
        else if (dims.Length == 2) { h = dims[0]; w = dims[1]; }
        else throw new InvalidOperationException("Unexpected model output shape " + string.Join("x", dims));

        var mat = new Mat(h, w, MatType.CV_32FC1);
        var plane = new float[h * w];
        var offset = channel * h * w;
        if (tensor is DenseTensor<float> dense)
        {
            dense.Buffer.Span.Slice(offset, h * w).CopyTo(plane);
        }
        else
        {
            var all = tensor.ToArray();
            Array.Copy(all, offset, plane, 0, plane.Length);
        }
        Marshal.Copy(plane, 0, mat.Data, plane.Length);
        return mat;
    }

    public static int Channels(Tensor<float> tensor) => tensor.Dimensions.Length == 4 ? tensor.Dimensions[1] : 1;

    /// <summary>Fixed input size from the model's metadata, or null for a dynamic dimension.</summary>
    public static (int? H, int? W) InputSize(InferenceSession session)
    {
        var meta = session.InputMetadata.First().Value;
        var dims = meta.Dimensions;
        if (dims.Length < 4) return (null, null);
        int? h = dims[2] > 0 ? dims[2] : null;
        int? w = dims[3] > 0 ? dims[3] : null;
        return (h, w);
    }

    public static NamedOnnxValue Input(InferenceSession session, DenseTensor<float> tensor) =>
        NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor);

    /// <summary>Applies the logistic function in place when a map holds logits rather than probabilities.</summary>
    public static void SigmoidIfLogits(Mat map)
    {
        Cv2.MinMaxLoc(map, out double min, out double max);
        if (min >= -0.01 && max <= 1.01) return;
        using var neg = new Mat();
        Cv2.Multiply(map, Scalar.All(-1), neg);
        Cv2.Exp(neg, neg);
        Cv2.Add(neg, Scalar.All(1), neg);
        Cv2.Divide(1.0, neg, map);
    }
}
