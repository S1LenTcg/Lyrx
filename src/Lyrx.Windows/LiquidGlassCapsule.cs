using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Musebase.Windows;

/// <summary>
/// 预烘焙高分辨率苹果 Liquid Glass（液态玻璃）胶囊贴图：
/// 内核高度通透（无乳白塑料色块），边缘通过解析胶囊距离场呈现顺滑的高光折射轮廓与柔和投影。
/// </summary>
internal static class LiquidGlassCapsule
{
    internal const double CapsuleWidth = 72;
    internal const double CapsuleHeight = 34;
    internal const double CapsuleRadius = 17;

    internal const double PadLeft = 14;
    internal const double PadTop = 10;
    internal const double PadRight = 14;
    internal const double PadBottom = 18;

    internal const double TextureDipWidth = CapsuleWidth + PadLeft + PadRight;   // 100
    internal const double TextureDipHeight = CapsuleHeight + PadTop + PadBottom; // 62

    private static readonly Lazy<BitmapSource> CachedTexture = new(RenderTexture);

    public static BitmapSource Texture => CachedTexture.Value;

    private static BitmapSource RenderTexture()
    {
        const int scale = 4;
        const int width = (int)(TextureDipWidth * scale);   // 400
        const int height = (int)(TextureDipHeight * scale); // 248

        const double cx = PadLeft + CapsuleWidth * 0.5;
        const double cy = PadTop + CapsuleHeight * 0.5;
        const double halfW = CapsuleWidth * 0.5;
        const double r = CapsuleRadius;
        const double segHalf = halfW - r;

        // 对角双光源方向（主光源左上 10:30，次级环境反射右下 4:30）
        const double l1x = -0.66;
        const double l1y = -0.751;
        const double l2x = 0.68;
        const double l2y = 0.733;

        var pixels = new byte[width * height * 4];

        for (var py = 0; py < height; py++)
        {
            var y = (py + 0.5) / scale;
            for (var px = 0; px < width; px++)
            {
                var x = (px + 0.5) / scale;

                // 1. 胶囊本体距离场与外法线
                var clampedX = Math.Clamp(x - cx, -segHalf, segHalf);
                var dx = (x - cx) - clampedX;
                var dy = y - cy;
                var len = Math.Sqrt(dx * dx + dy * dy);
                var dist = len - r;

                double nx, ny;
                if (len > 1e-6)
                {
                    nx = dx / len;
                    ny = dy / len;
                }
                else
                {
                    nx = 0;
                    ny = -1;
                }

                // 2. 极轻贴合接触影（避免厚重悬浮立体感，自然融入背景）
                var sx = x - cx;
                var sy = (y - 1.2) - cy;
                var sClampedX = Math.Clamp(sx, -segHalf, segHalf);
                var sdx = sx - sClampedX;
                var sLen = Math.Sqrt(sdx * sdx + sy * sy);
                var sDist = sLen - r;
                var shadowA = sDist > -0.5
                    ? 0.055 * Math.Exp(-Math.Max(0, sDist) * Math.Max(0, sDist) / (3.8 * 3.8))
                    : 0.0;

                // 抗锯齿边缘覆盖度（0.55 DIP 过渡）
                var cov = Math.Clamp(0.5 - dist / 0.55, 0.0, 1.0);

                // 3. 玻璃内部着色（极低雾度，贴合融入背景）
                double outR = 0, outG = 0, outB = 0, outA = 0;

                // 先合成极淡接触影（仅在胶囊外侧露出）
                if (shadowA > 0.0005 && cov < 0.999)
                {
                    var s = shadowA * (1.0 - cov);
                    outA = s;
                }

                if (cov > 0.001)
                {
                    var inDist = Math.Max(0.0, -dist);

                    // 极清透平铺微光，中心约 88% 透明透出背景色
                    var u = Math.Clamp((x - PadLeft) / CapsuleWidth, 0.0, 1.0);
                    var v = Math.Clamp((y - PadTop) / CapsuleHeight, 0.0, 1.0);
                    var diag = 1.0 - (u * 0.45 + v * 0.55);

                    const double baseDarkA = 0.095;
                    var coreWhiteA = 0.025 + 0.028 * diag;

                    var glassA = baseDarkA + (1.0 - baseDarkA) * coreWhiteA;
                    var glassR = (10 * baseDarkA * (1.0 - coreWhiteA) + 255 * coreWhiteA) / Math.Max(1e-6, glassA);
                    var glassG = (14 * baseDarkA * (1.0 - coreWhiteA) + 255 * coreWhiteA) / Math.Max(1e-6, glassA);
                    var glassB = (18 * baseDarkA * (1.0 - coreWhiteA) + 255 * coreWhiteA) / Math.Max(1e-6, glassA);

                    // 4. 克制的细发丝级 Liquid Glass 边缘反光（无凸出厚描边）
                    var dot1 = Math.Max(0.0, nx * l1x + ny * l1y);
                    var dot2 = Math.Max(0.0, nx * l2x + ny * l2y);
                    var k1 = Math.Pow(dot1, 2.2);
                    var k2 = Math.Pow(dot2, 2.6);

                    var rimFactor = Math.Exp(-Math.Pow(Math.Max(0.0, inDist - 0.1) / 0.50, 2.0));
                    var rimA = Math.Clamp(rimFactor * (0.09 + 0.23 * k1 + 0.11 * k2), 0.0, 0.40);

                    var causticFactor = Math.Exp(-Math.Pow((inDist - 1.1) / 0.85, 2.0));
                    var causticA = Math.Clamp(causticFactor * (0.008 + 0.038 * k1 + 0.018 * k2), 0.0, 0.12);

                    var tintR = Math.Clamp(252 + 3 * k2 - 4 * k1, 0, 255);
                    var tintG = Math.Clamp(253 + 2 * k1, 0, 255);
                    var tintB = Math.Clamp(255 - 3 * k2, 0, 255);

                    var specA = Math.Clamp(rimA + (1.0 - rimA) * causticA, 0.0, 0.44);

                    var bodyA = glassA + (1.0 - glassA) * specA;
                    var premulR = glassR * glassA * (1.0 - specA) + tintR * specA;
                    var premulG = glassG * glassA * (1.0 - specA) + tintG * specA;
                    var premulB = glassB * glassA * (1.0 - specA) + tintB * specA;

                    var srcA = bodyA * cov;
                    outR = premulR * cov + outR * (1.0 - srcA);
                    outG = premulG * cov + outG * (1.0 - srcA);
                    outB = premulB * cov + outB * (1.0 - srcA);
                    outA = srcA + outA * (1.0 - srcA);
                }

                var idx = (py * width + px) * 4;
                pixels[idx] = (byte)Math.Clamp(Math.Round(outB), 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp(Math.Round(outG), 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp(Math.Round(outR), 0, 255);
                pixels[idx + 3] = (byte)Math.Clamp(Math.Round(outA * 255.0), 0, 255);
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
