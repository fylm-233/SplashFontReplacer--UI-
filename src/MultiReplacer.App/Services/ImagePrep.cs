using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MultiReplacer.App.Services
{
    public enum FitMode
    {
        /// <summary>等比缩放后居中裁切，铺满目标尺寸（不拉伸变形）。</summary>
        Cover,
        /// <summary>直接拉伸到目标尺寸。</summary>
        Stretch
    }

    /// <summary>输出格式，必须与目标资源的原始格式一致（BMP 资源写 PNG 会直接崩）。</summary>
    public enum OutputImageFormat
    {
        Png,
        Bmp,
        Jpeg
    }

    /// <summary>
    /// 把用户选的图片渲染成指定尺寸的目标格式。
    /// 放在 App 层是为了用 WinRT 的解码/缩放/编码器，Core 因此不需要引入图像库。
    /// </summary>
    public static class ImagePrep
    {
        public sealed class Info
        {
            public int Width;
            public int Height;
        }

        public static async Task<Info> ProbeAsync(StorageFile file)
        {
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                Info info = new Info();
                info.Width = (int)decoder.PixelWidth;
                info.Height = (int)decoder.PixelHeight;
                return info;
            }
        }

        public static Task<byte[]> RenderAsync(StorageFile file, int targetWidth, int targetHeight, FitMode mode)
        {
            return RenderAsync(file, targetWidth, targetHeight, mode, OutputImageFormat.Png);
        }

        public static async Task<byte[]> RenderAsync(StorageFile file, int targetWidth, int targetHeight,
                                                    FitMode mode, OutputImageFormat format)
        {
            return await RenderAsync(file, targetWidth, targetHeight, mode, format, false);
        }

        /// <summary>
        /// forceOpaque=true 时丢掉 alpha 通道重新编码。
        /// 容器路线要求"等长覆盖"，丢掉 alpha 通常能把 PNG 压小 20%~30%，
        /// 是在不改变尺寸前提下最省事的体积优化手段。
        /// </summary>
        public static async Task<byte[]> RenderAsync(StorageFile file, int targetWidth, int targetHeight,
                                                    FitMode mode, OutputImageFormat format, bool forceOpaque)
        {
            if (targetWidth <= 0 || targetHeight <= 0) return null;

            using (IRandomAccessStream input = await file.OpenAsync(FileAccessMode.Read))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

                BitmapTransform transform = new BitmapTransform();
                transform.InterpolationMode = BitmapInterpolationMode.Fant;

                // 源尺寸与目标尺寸完全一致时不要走缩放/裁切：
                // 哪怕 scale=1，WIC 也会跑一遍重采样，像素会有 ±1 的偏差，
                // 导致"原图导出再导回"拿不到逐字节一致的画面。
                bool identical = decoder.PixelWidth == (uint)targetWidth
                                 && decoder.PixelHeight == (uint)targetHeight;
                if (identical)
                {
                    transform.ScaledWidth = decoder.PixelWidth;
                    transform.ScaledHeight = decoder.PixelHeight;
                }
                else if (mode == FitMode.Cover)
                {
                    double scale = Math.Max((double)targetWidth / decoder.PixelWidth,
                                            (double)targetHeight / decoder.PixelHeight);
                    double cropWidth = targetWidth / scale;
                    double cropHeight = targetHeight / scale;
                    if (cropWidth > decoder.PixelWidth) cropWidth = decoder.PixelWidth;
                    if (cropHeight > decoder.PixelHeight) cropHeight = decoder.PixelHeight;

                    BitmapBounds bounds = new BitmapBounds();
                    bounds.X = (uint)Math.Max(0, (decoder.PixelWidth - cropWidth) / 2);
                    bounds.Y = (uint)Math.Max(0, (decoder.PixelHeight - cropHeight) / 2);
                    bounds.Width = (uint)Math.Max(1, Math.Round(cropWidth));
                    bounds.Height = (uint)Math.Max(1, Math.Round(cropHeight));
                    transform.Bounds = bounds;

                    transform.ScaledWidth = (uint)targetWidth;
                    transform.ScaledHeight = (uint)targetHeight;
                }
                else
                {
                    transform.ScaledWidth = (uint)targetWidth;
                    transform.ScaledHeight = (uint)targetHeight;
                }

                // BMP 编码器不支持预乘 alpha，必须换回直通 alpha，否则颜色会发暗。
                // PNG/JPEG 同样用直通 alpha：预乘会先乘后除，半透明像素每轮往返都会丢精度，
                // 表现就是"原图导出再导回"画面有细微偏差。
                BitmapAlphaMode alpha = (format == OutputImageFormat.Bmp || forceOpaque)
                    ? BitmapAlphaMode.Ignore
                    : BitmapAlphaMode.Straight;

                using (SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, alpha, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage))
                using (InMemoryRandomAccessStream output = new InMemoryRandomAccessStream())
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(EncoderId(format), output);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();

                    output.Seek(0);
                    uint size = (uint)output.Size;
                    byte[] bytes = new byte[size];
                    using (DataReader reader = new DataReader(output.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync(size);
                        reader.ReadBytes(bytes);
                    }
                    return bytes;
                }
            }
        }

        private static Guid EncoderId(OutputImageFormat format)
        {
            switch (format)
            {
                case OutputImageFormat.Bmp: return BitmapEncoder.BmpEncoderId;
                case OutputImageFormat.Jpeg: return BitmapEncoder.JpegEncoderId;
                default: return BitmapEncoder.PngEncoderId;
            }
        }
    }
}
