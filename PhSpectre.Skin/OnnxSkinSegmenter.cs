using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Skin;

// Skin segmentation with MediaPipe's selfie model on ONNX Runtime. The model is loaded on first use.
public sealed class OnnxSkinSegmenter : ISkinSegmenter
{
    private const int Size = 256, Classes = 6, BodySkin = 2, FaceSkin = 3;

    private readonly Lazy<InferenceSession?> _session;
    private readonly Action<Exception>? _onError;

    public OnnxSkinSegmenter(Action<Exception>? onError = null)
    {
        _onError = onError;
        _session = new Lazy<InferenceSession?>(Load);
    }

    public static void Register(Action<Exception>? onError = null) => SkinRetouch.Segmenter = new OnnxSkinSegmenter(onError);

    private InferenceSession? Load()
    {
        try
        {
            using var stream = typeof(OnnxSkinSegmenter).Assembly.GetManifestResourceStream("selfie_multiclass.onnx")
                               ?? throw new InvalidOperationException("The skin model is missing from the program.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return Create(bytes.ToArray());
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            return null;
        }
    }

    // XNNPACK is the fastest on phones; where it is not available the ordinary CPU path does the work.
    private static InferenceSession Create(byte[] model)
    {
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        try
        {
            using var options = new SessionOptions { IntraOpNumThreads = 1 };
            options.AppendExecutionProvider("XNNPACK",
                new Dictionary<string, string> { ["intra_op_num_threads"] = threads.ToString() });
            return new InferenceSession(model, options);
        }
        catch (OnnxRuntimeException)
        {
            return new InferenceSession(model);
        }
    }

    // Averages up to 4 x 4 samples per cell: a full resize of a large photo takes seconds on a phone.
    private static void Shrink(Image<Rgb24> photo, Span<float> into)
    {
        const int samples = 4;
        var columns = new int[Size * samples];
        for (int i = 0; i < columns.Length; i++) columns[i] = (int)((i + 0.5) * photo.Width / columns.Length);
        var sum = new float[Size * 3];
        for (int y = 0; y < Size; y++)
        {
            Array.Clear(sum);
            for (int sy = 0; sy < samples; sy++)
            {
                int py = (int)((y * samples + sy + 0.5) * photo.Height / (Size * samples));
                var row = photo.Frames.RootFrame.DangerousGetPixelRowMemory(py).Span;
                for (int i = 0; i < columns.Length; i++)
                {
                    var p = row[columns[i]];
                    int o = i / samples * 3;
                    sum[o] += p.R; sum[o + 1] += p.G; sum[o + 2] += p.B;
                }
            }
            const float unit = 1f / (255f * samples * samples);
            for (int x = 0; x < Size * 3; x++) into[y * Size * 3 + x] = sum[x] * unit;
        }
    }

    public SkinMask? Segment(Image<Rgb24> photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var session = _session.Value;
        if (session == null) return null;
        try
        {
            var input = new DenseTensor<float>(new[] { 1, Size, Size, 3 });
            Shrink(photo, input.Buffer.Span);

            string name = session.InputMetadata.Keys.First();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
            var logits = results.First().AsTensor<float>().ToArray();

            var skin = new float[Size * Size];
            Span<float> score = stackalloc float[Classes];
            for (int n = 0; n < skin.Length; n++)
            {
                float max = float.MinValue, sum = 0f;
                for (int c = 0; c < Classes; c++) max = Math.Max(max, logits[n * Classes + c]);
                for (int c = 0; c < Classes; c++) sum += score[c] = MathF.Exp(logits[n * Classes + c] - max);
                skin[n] = (score[BodySkin] + score[FaceSkin]) / sum;
            }
            return new SkinMask(skin, Size, Size);
        }
        catch (OnnxRuntimeException ex)
        {
            _onError?.Invoke(ex);
            return null;
        }
    }
}
