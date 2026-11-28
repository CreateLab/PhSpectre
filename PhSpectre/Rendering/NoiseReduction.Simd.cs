using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PhSpectre.Rendering;

public static partial class NoiseReduction
{
    // Tests run the transpose that machines without SSE use.
    internal static bool PortableTranspose;

    // 8x8 DCT on four columns at once; block[2r] and block[2r+1] are the halves of row r. Pass, transpose, pass
    // leaves transposed coefficients, which thresholding ignores.
    private static class SimdDct
    {
        private static readonly Vector128<float> A = Vector128.Create(0.5f * MathF.Cos(MathF.PI / 4));
        private static readonly Vector128<float> B = Vector128.Create(0.5f * MathF.Cos(MathF.PI / 8));
        private static readonly Vector128<float> C = Vector128.Create(0.5f * MathF.Cos(3 * MathF.PI / 8));
        private static readonly Vector128<float> D = Vector128.Create(0.5f * MathF.Cos(MathF.PI / 16));
        private static readonly Vector128<float> E = Vector128.Create(0.5f * MathF.Cos(3 * MathF.PI / 16));
        private static readonly Vector128<float> F = Vector128.Create(0.5f * MathF.Cos(5 * MathF.PI / 16));
        private static readonly Vector128<float> G = Vector128.Create(0.5f * MathF.Cos(7 * MathF.PI / 16));

        public static bool IsSupported => Vector128.IsHardwareAccelerated;

        public static void Forward(ref Vector128<float> v)
        {
            ForwardColumns(ref v);
            Transpose(ref v);
            ForwardColumns(ref v);
        }

        public static void Inverse(ref Vector128<float> v)
        {
            InverseColumns(ref v);
            Transpose(ref v);
            InverseColumns(ref v);
        }

        private static void ForwardColumns(ref Vector128<float> v)
        {
            for (int h = 0; h < 2; h++)
            {
                ref var r = ref Unsafe.Add(ref v, h);
                var x0 = r; var x1 = Unsafe.Add(ref r, 2); var x2 = Unsafe.Add(ref r, 4); var x3 = Unsafe.Add(ref r, 6);
                var x4 = Unsafe.Add(ref r, 8); var x5 = Unsafe.Add(ref r, 10); var x6 = Unsafe.Add(ref r, 12); var x7 = Unsafe.Add(ref r, 14);
                var s0 = x0 + x7; var s1 = x1 + x6; var s2 = x2 + x5; var s3 = x3 + x4;
                var d0 = x0 - x7; var d1 = x1 - x6; var d2 = x2 - x5; var d3 = x3 - x4;
                var p = s0 + s3; var q = s1 + s2; var m = s0 - s3; var n = s1 - s2;
                r = A * (p + q);
                Unsafe.Add(ref r, 8) = A * (p - q);
                Unsafe.Add(ref r, 4) = B * m + C * n;
                Unsafe.Add(ref r, 12) = C * m - B * n;
                Unsafe.Add(ref r, 2) = D * d0 + E * d1 + F * d2 + G * d3;
                Unsafe.Add(ref r, 6) = E * d0 - G * d1 - D * d2 - F * d3;
                Unsafe.Add(ref r, 10) = F * d0 - D * d1 + G * d2 + E * d3;
                Unsafe.Add(ref r, 14) = G * d0 - F * d1 + E * d2 - D * d3;
            }
        }

        private static void InverseColumns(ref Vector128<float> v)
        {
            for (int h = 0; h < 2; h++)
            {
                ref var r = ref Unsafe.Add(ref v, h);
                var c0 = r; var c1 = Unsafe.Add(ref r, 2); var c2 = Unsafe.Add(ref r, 4); var c3 = Unsafe.Add(ref r, 6);
                var c4 = Unsafe.Add(ref r, 8); var c5 = Unsafe.Add(ref r, 10); var c6 = Unsafe.Add(ref r, 12); var c7 = Unsafe.Add(ref r, 14);
                var p = A * (c0 + c4); var q = A * (c0 - c4); var m = B * c2 + C * c6; var n = C * c2 - B * c6;
                var s0 = p + m; var s1 = q + n; var s2 = q - n; var s3 = p - m;
                var d0 = D * c1 + E * c3 + F * c5 + G * c7;
                var d1 = E * c1 - G * c3 - D * c5 - F * c7;
                var d2 = F * c1 - D * c3 + G * c5 + E * c7;
                var d3 = G * c1 - F * c3 + E * c5 - D * c7;
                r = s0 + d0; Unsafe.Add(ref r, 14) = s0 - d0;
                Unsafe.Add(ref r, 2) = s1 + d1; Unsafe.Add(ref r, 12) = s1 - d1;
                Unsafe.Add(ref r, 4) = s2 + d2; Unsafe.Add(ref r, 10) = s2 - d2;
                Unsafe.Add(ref r, 6) = s3 + d3; Unsafe.Add(ref r, 8) = s3 - d3;
            }
        }

        // Android's runtime has no ARM shuffles and Vector128.Shuffle is slower than scalar there, so without SSE
        // floats are swapped one by one: still about 3x faster than scalar.
        private static void Transpose(ref Vector128<float> v)
        {
            if (!Sse.IsSupported || PortableTranspose) { TransposeSwapping(ref Unsafe.As<Vector128<float>, float>(ref v)); return; }
            Transpose4(ref v, ref Unsafe.Add(ref v, 2), ref Unsafe.Add(ref v, 4), ref Unsafe.Add(ref v, 6));
            Transpose4(ref Unsafe.Add(ref v, 9), ref Unsafe.Add(ref v, 11), ref Unsafe.Add(ref v, 13), ref Unsafe.Add(ref v, 15));
            Transpose4(ref Unsafe.Add(ref v, 1), ref Unsafe.Add(ref v, 3), ref Unsafe.Add(ref v, 5), ref Unsafe.Add(ref v, 7));
            Transpose4(ref Unsafe.Add(ref v, 8), ref Unsafe.Add(ref v, 10), ref Unsafe.Add(ref v, 12), ref Unsafe.Add(ref v, 14));
            for (int r = 0; r < 4; r++)
            {
                ref var upper = ref Unsafe.Add(ref v, 2 * r + 1);
                ref var lower = ref Unsafe.Add(ref v, 2 * r + 8);
                (upper, lower) = (lower, upper);
            }
        }

        private static void TransposeSwapping(ref float f)
        {
            for (int r = 0; r < Block - 1; r++)
                for (int c = r + 1; c < Block; c++)
                {
                    ref float a = ref Unsafe.Add(ref f, r * Block + c);
                    ref float b = ref Unsafe.Add(ref f, c * Block + r);
                    (a, b) = (b, a);
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose4(ref Vector128<float> r0, ref Vector128<float> r1, ref Vector128<float> r2, ref Vector128<float> r3)
        {
            var t0 = Sse.UnpackLow(r0, r1); var t1 = Sse.UnpackHigh(r0, r1);
            var t2 = Sse.UnpackLow(r2, r3); var t3 = Sse.UnpackHigh(r2, r3);
            r0 = Sse.MoveLowToHigh(t0, t2); r1 = Sse.MoveHighToLow(t2, t0);
            r2 = Sse.MoveLowToHigh(t1, t3); r3 = Sse.MoveHighToLow(t3, t1);
        }
    }

    private sealed partial class Workspace
    {
        // The same work as DenoiseLumaScalar, on four pixels at a time.
        private void DenoiseLumaSimd(NoiseProfile profile, float k)
        {
            int w = _w, h = _h, n = w * h;
            // Every offset below stays inside the picture: blocks never start past (w - 8, h - 8).
            ref float src = ref MemoryMarshal.GetArrayDataReference(Y);
            ref float sum = ref MemoryMarshal.GetArrayDataReference(_sum);
            ref float total = ref MemoryMarshal.GetArrayDataReference(_weight);
            Span<Vector128<float>> block = stackalloc Vector128<float>[2 * Block];
            ref var v = ref MemoryMarshal.GetReference(block);
            var one = Vector128.Create(1f);
            for (int by = 0; ; by = Math.Min(by + Step, h - Block))
            {
                for (int bx = 0; ; bx = Math.Min(bx + Step, w - Block))
                {
                    int origin = by * w + bx;
                    for (int r = 0, o = origin; r < Block; r++, o += w)
                    {
                        Unsafe.Add(ref v, 2 * r) = Vector128.LoadUnsafe(ref src, (nuint)o);
                        Unsafe.Add(ref v, 2 * r + 1) = Vector128.LoadUnsafe(ref src, (nuint)(o + 4));
                    }
                    SimdDct.Forward(ref v);
                    float dc = v.GetElement(0);
                    var t = Vector128.Create(k * profile.LumaSigma(dc / Block));
                    var count = Vector128<float>.Zero;
                    for (int i = 0; i < 2 * Block; i++)
                    {
                        ref var c = ref Unsafe.Add(ref v, i);
                        var keep = Vector128.GreaterThanOrEqual(Vector128.Abs(c), t);
                        c &= keep;
                        count += one & keep;
                    }
                    // The mean is always kept and counted once.
                    float kept = Vector128.Sum(count) + (MathF.Abs(dc) >= t.ToScalar() ? 0f : 1f);
                    v = v.WithElement(0, dc);
                    SimdDct.Inverse(ref v);
                    var weight = Vector128.Create(1f / kept);
                    for (int r = 0, o = origin; r < Block; r++, o += w)
                    {
                        var left = Unsafe.Add(ref v, 2 * r) * weight;
                        var right = Unsafe.Add(ref v, 2 * r + 1) * weight;
                        (Vector128.LoadUnsafe(ref sum, (nuint)o) + left).StoreUnsafe(ref sum, (nuint)o);
                        (Vector128.LoadUnsafe(ref sum, (nuint)(o + 4)) + right).StoreUnsafe(ref sum, (nuint)(o + 4));
                        (Vector128.LoadUnsafe(ref total, (nuint)o) + weight).StoreUnsafe(ref total, (nuint)o);
                        (Vector128.LoadUnsafe(ref total, (nuint)(o + 4)) + weight).StoreUnsafe(ref total, (nuint)(o + 4));
                    }
                    if (bx == w - Block) break;
                }
                if (by == h - Block) break;
            }
            int x = 0;
            for (; x <= n - 4; x += 4)
                (Vector128.LoadUnsafe(ref sum, (nuint)x) / Vector128.LoadUnsafe(ref total, (nuint)x)).StoreUnsafe(ref sum, (nuint)x);
            for (; x < n; x++) _sum[x] /= _weight[x];
        }
    }
}
