using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MultiReplacer.Core;

internal static class Program
{
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        List<AdobeAppInfo> apps = AdobeDictionary.Discover();
        Console.WriteLine("发现 " + apps.Count + " 个可处理应用");
        for (int i = 0; i < apps.Count; i++)
        {
            AdobeAppInfo app = apps[i];
            AdobeDictionary.Analyze(app);
            Console.WriteLine("---");
            Console.WriteLine("产品   : " + app.Product + " / " + app.Version);
            Console.WriteLine("语言包 : " + app.DatFile);
            Console.WriteLine("签名   : " + app.HasSignature + "  算法: " + app.DigestAlgorithm);
            Console.WriteLine("字体键 : " + app.StyleKeyCount + "  插件键: " + app.PluginKeyCount);
            Console.WriteLine("当前   : " + app.CurrentFont + "  " + app.CurrentLarge + "/" + app.CurrentMedium + "/" + app.CurrentSmall);
            Console.WriteLine("说明   : " + app.Message);
        }
        if (apps.Count == 0) return;

        AdobeAppInfo source = apps[0];

        // ---- 沙盒：把语言包目录复制到临时目录，全程不碰 Program Files ----
        string sandbox = Path.Combine(Path.GetTempPath(), "mr-sandbox-" + Guid.NewGuid().ToString("N"));
        AdobeDictionary.CopyDirectory(source.LocaleDir, sandbox);
        AdobeAppInfo app2 = AdobeDictionary.FromLocaleDirectory(sandbox);
        if (app2 == null) { Console.WriteLine("沙盒目录无法识别"); return; }
        AdobeDictionary.Analyze(app2);
        Console.WriteLine("=== 沙盒 ===");
        Console.WriteLine("识别为 : " + app2.DisplayName + "  键 " + app2.StyleKeyCount);
        Console.WriteLine("原始   : " + app2.CurrentFont + "  " + app2.CurrentLarge + "/" + app2.CurrentMedium + "/" + app2.CurrentSmall);

        AdobeFontOptions options = new AdobeFontOptions();
        options.FontName = "Microsoft YaHei UI";
        options.LargeSize = 16;
        options.MediumSize = 14;
        options.SmallSize = 9;
        options.ReplacePluginFonts = true;

        string backupRoot = Path.Combine(sandbox + "-backup");
        string error;
        AdobePatchSummary summary = AdobeDictionary.Prepare(app2, backupRoot, options, out error);
        if (summary == null) { Console.WriteLine("Prepare 失败: " + error); return; }

        Console.WriteLine("改写样式键 " + summary.StyleModified + "，插件键 " + summary.PluginModified +
                          "，摘要 " + summary.DigestAlgorithm);

        AdobeDictionary.Deploy(summary.StagingDirectory, app2);
        AdobeDictionary.TryDeleteDirectory(summary.StagingDirectory);
        AdobeDictionary.Analyze(app2);
        Console.WriteLine("应用后 : " + app2.CurrentFont + "  " + app2.CurrentLarge + "/" + app2.CurrentMedium + "/" + app2.CurrentSmall);

        string dat = Path.Combine(sandbox, app2.DatFileName);
        foreach (string line in File.ReadAllLines(dat, Encoding.UTF8))
        {
            if (line.IndexOf("/FontStyle/Large=", StringComparison.Ordinal) >= 0)
            {
                Console.WriteLine("Large  : " + line);
                break;
            }
        }

        string sig = Path.Combine(sandbox, "META-INF", "signatures.xml");
        if (File.Exists(sig))
        {
            string xml = AdobeDictionary.ReadText(sig);
            Console.WriteLine("摘要已写入签名文件: " + (xml.IndexOf(summary.NewDigest, StringComparison.Ordinal) >= 0));
        }

        // ---- 还原 ----
        string backupDir = AdobeDictionary.GetBackupDir(app2, backupRoot);
        AdobeDictionary.Restore(backupDir, app2);
        AdobeDictionary.Analyze(app2);
        Console.WriteLine("还原后 : " + app2.CurrentFont + "  " + app2.CurrentLarge + "/" + app2.CurrentMedium + "/" + app2.CurrentSmall);

        AdobeDictionary.TryDeleteDirectory(sandbox);
        AdobeDictionary.TryDeleteDirectory(backupRoot);
        Console.WriteLine("沙盒已清理");
    }
}
