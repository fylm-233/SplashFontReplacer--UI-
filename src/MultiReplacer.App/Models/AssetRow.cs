using System.Globalization;
using MultiReplacer.Core;

namespace MultiReplacer.App.Models
{
    public sealed class AssetRow
    {
        public PngAsset Asset { get; set; }
        public int Index { get; set; }
        public string Dimensions { get; set; }
        public string Bytes { get; set; }
        public string Offset { get; set; }
        public string Color { get; set; }
        public string Judgement { get; set; }
        public string Sha256 { get; set; }

        public static AssetRow From(PngAsset asset, bool splash, bool logo)
        {
            AssetRow row = new AssetRow();
            row.Asset = asset;
            row.Index = asset.Index;
            row.Dimensions = asset.Dimensions;
            row.Bytes = asset.Length.ToString("N0", CultureInfo.InvariantCulture);
            row.Offset = asset.OffsetHex;
            row.Color = asset.ColorTypeText;
            if (splash) row.Judgement = "启动图候选";
            else if (logo) row.Judgement = "Logo 候选";
            else row.Judgement = "其他 PNG";
            return row;
        }
    }

    public sealed class FontRow
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Bytes { get; set; }
        public string Note { get; set; }
        public bool Installed { get; set; }
        public bool Recommended { get; set; }
    }

    public sealed class AdobeAppRow
    {
        public AdobeAppInfo App { get; set; }
        public string Display { get; set; }
    }

    /// <summary>启动图候选行（Adobe 启动图页面）。</summary>
    public sealed class SplashRow
    {
        public SplashCandidate Candidate { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public string Dimensions { get; set; }
        public string Bytes { get; set; }
        public string Judgement { get; set; }

        public static SplashRow From(SplashCandidate candidate)
        {
            SplashRow row = new SplashRow();
            row.Candidate = candidate;
            row.Name = candidate.NameName;
            // 位置列同时体现存储形态，用户一眼能看出是资源、独立文件、容器还是归档
            row.Host = candidate.KindText + " · " + candidate.RelativeFile;
            row.Dimensions = candidate.Width.ToString(CultureInfo.InvariantCulture)
                             + " × " + candidate.Height.ToString(CultureInfo.InvariantCulture);
            row.Bytes = candidate.Size.ToString("N0", CultureInfo.InvariantCulture);
            row.Judgement = (candidate.LikelySplash ? "疑似启动图" : "其他图片")
                            + " · " + ImageProbe.FormatText(candidate.Format);
            return row;
        }
    }
}
