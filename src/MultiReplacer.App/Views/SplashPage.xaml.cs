using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MultiReplacer.App.Models;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MultiReplacer.App.Views
{
    public sealed partial class SplashPage : Page
    {
        private readonly ObservableCollection<AssetRow> rows = new ObservableCollection<AssetRow>();
        private List<PngAsset> allAssets = new List<PngAsset>();
        private List<PngAsset> selectedAssets = new List<PngAsset>();
        private PngAsset selectedAsset;
        private byte[] selectedOriginalBytes;
        private byte[] replacementRaw;
        private Dictionary<int, byte[]> replacementByLength = new Dictionary<int, byte[]>();
        private string replacementPath;
        private PeInfo currentPe;
        private bool busy;
        private bool ready;

        public SplashPage()
        {
            InitializeComponent();
            LvAssets.ItemsSource = rows;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppSettings s = App.Settings;
            TxtExe.Text = s.ResolveExePath;
            NumMin.Value = s.MinSide;
            ChkSplashOnly.IsOn = !s.IncludeLogoAssets;
            ChkCopies.IsChecked = s.PatchIdenticalCopies;
            ChkClearSig.IsChecked = s.ClearAuthenticode;

            if (String.IsNullOrEmpty(TxtExe.Text))
            {
                string found = ResolveLocator.Find();
                if (!String.IsNullOrEmpty(found)) TxtExe.Text = found;
            }

            // XAML 解析阶段控件的属性赋值会触发 ValueChanged/Toggled，
            // 此时其他控件尚未构造完成，因此统一用 ready 做闸门。
            ready = true;
            ApplyFilter();
            UpdateButtons();
            Log("就绪。选择 Resolve.exe 后点击[扫描]。");
        }

        private string ResolvePath
        {
            get { return (TxtExe.Text ?? String.Empty).Trim().Trim('"'); }
        }

        // ---------------- 基础交互 ----------------

        private async void OnBrowse(object sender, RoutedEventArgs e)
        {
            Windows.Storage.StorageFile file = await PickerHelper.PickOpenFileAsync(
                "选择 Resolve.exe", "程序", new string[] { ".exe" }, ResolvePath);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            TxtExe.Text = file.Path;
            App.Settings.ResolveExePath = file.Path;
            App.Settings.Save(out string _);
            allAssets.Clear();
            rows.Clear();
            currentPe = null;
            ClearSelection();
            HeaderInfo.IsOpen = false;
            UpdateButtons();
            Log("已选择文件：" + file.Path);
        }

        private async void OnScan(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            string path = ResolvePath;
            if (!File.Exists(path))
            {
                await ShowMessage("路径错误", "找不到 Resolve.exe，请先选择正确路径。");
                return;
            }

            SetBusy(true, "正在扫描嵌入 PNG…");
            Log("开始扫描：" + path);
            try
            {
                List<PngAsset> assets = null;
                PeInfo pe = null;
                string error = null;
                await Task.Run(delegate
                {
                    assets = PngScanner.Scan(path, 32, out pe, out error);
                });

                if (assets == null)
                {
                    throw new Exception(error ?? "扫描失败。");
                }

                assets.Sort(delegate(PngAsset a, PngAsset b)
                {
                    int area = b.Area.CompareTo(a.Area);
                    return area != 0 ? area : a.Offset.CompareTo(b.Offset);
                });
                for (int i = 0; i < assets.Count; i++) assets[i].Index = i;

                allAssets = assets;
                currentPe = pe;
                ClearSelection();
                ApplyFilter();

                HeaderInfo.Severity = InfoBarSeverity.Success;
                HeaderInfo.Title = String.Format(CultureInfo.InvariantCulture,
                    "文件 {0:N0} 字节 · PE {1} · 嵌入 PNG {2} 个 · Checksum 0x{3:X8} · Authenticode {4}",
                    pe.FileSize, pe.OptionalMagic == 0x20B ? "64-bit" : "32-bit", assets.Count, pe.StoredChecksum,
                    pe.HasSecurityDirectory ? "存在（修改后失效）" : "无");
                HeaderInfo.Message = "";
                HeaderInfo.IsOpen = true;
                Log("扫描完成，找到 " + assets.Count + " 个 PNG 资源。");
            }
            catch (Exception ex)
            {
                HeaderInfo.Severity = InfoBarSeverity.Error;
                HeaderInfo.Title = "扫描失败";
                HeaderInfo.Message = ex.Message;
                HeaderInfo.IsOpen = true;
                Log("扫描失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
                UpdateButtons();
            }
        }

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnFilterNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            ApplyFilter();
        }

        private void OnFilterToggled(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            try
            {
                ToggleSwitch toggle = sender as ToggleSwitch;
                if (toggle == null) return;
                if (toggle == ChkSplashOnly && ChkSplashOnly.IsOn && ChkLogoOnly != null) ChkLogoOnly.IsOn = false;
                if (toggle == ChkLogoOnly && ChkLogoOnly.IsOn && ChkSplashOnly != null) ChkSplashOnly.IsOn = false;
                ApplyFilter();
            }
            catch (Exception ex)
            {
                AppLog.Error("OnFilterToggled 异常", ex);
            }
        }

        private bool IsSuspectedSplash(PngAsset a)
        {
            if (a.Width <= 0 || a.Height <= 0) return false;
            double ratio = (double)a.Width / (double)a.Height;
            return ratio >= 2.15 && ratio <= 2.42;
        }

        private bool IsSuspectedLogo(PngAsset a)
        {
            if (a.Width < 100 || a.Height < 20) return false;
            double ratio = (double)a.Width / (double)a.Height;
            return ratio >= 3.8 && ratio <= 4.3;
        }

        private void ApplyFilter()
        {
            if (!ready || TxtFilter == null || NumMin == null || ChkSplashOnly == null ||
                ChkLogoOnly == null || ListHint == null) return;
            string filter = (TxtFilter.Text ?? String.Empty).Trim().ToLowerInvariant();
            int min = NumMin.Value is double d && !Double.IsNaN(d) ? (int)d : 0;
            bool splashOnly = ChkSplashOnly.IsOn;
            bool logoOnly = ChkLogoOnly.IsOn;

            rows.Clear();
            for (int i = 0; i < allAssets.Count; i++)
            {
                PngAsset a = allAssets[i];
                if (Math.Max(a.Width, a.Height) < min) continue;
                bool splash = IsSuspectedSplash(a);
                bool logo = IsSuspectedLogo(a);
                if (splashOnly && !splash) continue;
                if (logoOnly && !logo) continue;
                if (filter.Length > 0)
                {
                    string haystack = (a.Index + " " + a.Dimensions + " " + a.Length + " " + a.OffsetHex + " " + a.ColorTypeText).ToLowerInvariant();
                    if (haystack.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                }
                rows.Add(AssetRow.From(a, splash, logo));
            }

            ListHint.Text = "显示 " + rows.Count + " / " + allAssets.Count + " 个资源 · 支持 Shift / Ctrl 多选";
            ClearSelection();
            UpdateButtons();
        }

        private async void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            List<PngAsset> picked = new List<PngAsset>();
            for (int i = 0; i < LvAssets.SelectedItems.Count; i++)
            {
                AssetRow row = LvAssets.SelectedItems[i] as AssetRow;
                if (row != null && row.Asset != null) picked.Add(row.Asset);
            }
            selectedAssets = picked;

            if (picked.Count == 0)
            {
                selectedAsset = null;
                selectedOriginalBytes = null;
                ImgOriginal.Source = null;
                TxtOriginalInfo.Text = "未选择资源";
                TxtSelection.Text = "";
                UpdateButtons();
                return;
            }

            selectedAsset = picked[0];
            try
            {
                string path = ResolvePath;
                PngAsset asset = selectedAsset;
                byte[] bytes = null;
                await Task.Run(delegate { bytes = PngCodec.ExtractAsset(path, asset); });
                selectedOriginalBytes = bytes;
                asset.Sha256 = HashTools.Sha256Bytes(bytes);
                ImgOriginal.Source = await ToBitmapAsync(bytes);

                if (picked.Count == 1)
                {
                    TxtOriginalInfo.Text = String.Format(CultureInfo.InvariantCulture,
                        "编号 {0} · {1} · {2:N0} 字节 · {3} · {4} · SHA-256 {5}…",
                        asset.Index, asset.Dimensions, asset.Length, asset.OffsetHex, asset.ColorTypeText,
                        asset.Sha256.Substring(0, 16));
                }
                else if (SelectionIsSameSize())
                {
                    long total = 0, minLen = Int64.MaxValue, maxLen = 0;
                    for (int i = 0; i < picked.Count; i++)
                    {
                        total += picked[i].Length;
                        if (picked[i].Length < minLen) minLen = picked[i].Length;
                        if (picked[i].Length > maxLen) maxLen = picked[i].Length;
                    }
                    TxtOriginalInfo.Text = String.Format(CultureInfo.InvariantCulture,
                        "已选 {0} 张同尺寸启动图 · {1} · 合计 {2:N0} 字节 · 单张 {3:N0}..{4:N0} 字节",
                        picked.Count, asset.Dimensions, total, minLen, maxLen);
                }
                else
                {
                    TxtOriginalInfo.Text = String.Format(CultureInfo.InvariantCulture,
                        "已选 {0} 项，但尺寸不一致（{1} / {2}）。批量替换已禁用。",
                        picked.Count, picked[0].Dimensions, picked[1].Dimensions);
                }

                TxtSelection.Text = picked.Count == 1
                    ? "#" + asset.Index + " " + asset.Dimensions
                    : "已选 " + picked.Count + " 项";
            }
            catch (Exception ex)
            {
                selectedOriginalBytes = null;
                Log("读取资源失败：" + ex.Message);
            }
            UpdateButtons();
        }

        private bool SelectionIsSameSize()
        {
            if (selectedAssets.Count == 0) return false;
            int w = selectedAssets[0].Width, h = selectedAssets[0].Height;
            for (int i = 1; i < selectedAssets.Count; i++)
            {
                if (selectedAssets[i].Width != w || selectedAssets[i].Height != h) return false;
            }
            return true;
        }

        private void OnSelectSameSize(object sender, RoutedEventArgs e)
        {
            if (selectedAsset == null) return;
            int w = selectedAsset.Width, h = selectedAsset.Height;
            LvAssets.SelectedItems.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Asset.Width == w && rows[i].Asset.Height == h)
                {
                    LvAssets.SelectedItems.Add(rows[i]);
                }
            }
            Log("已选中所有 " + w + " x " + h + " 的资源。");
        }

        private void ClearSelection()
        {
            selectedAsset = null;
            selectedOriginalBytes = null;
            selectedAssets = new List<PngAsset>();
            replacementRaw = null;
            replacementByLength = new Dictionary<int, byte[]>();
            replacementPath = null;
            ImgOriginal.Source = null;
            ImgNew.Source = null;
            TxtOriginalInfo.Text = "未选择资源";
            TxtNewInfo.Text = "尚未选择替换图";
            TxtSelection.Text = "";
        }

        // ---------------- 替换图 ----------------

        private async void OnChooseReplacement(object sender, RoutedEventArgs e)
        {
            if (selectedAssets.Count == 0 || selectedOriginalBytes == null)
            {
                await ShowMessage("未选择资源", "请先从列表中选择一个或多个原始 PNG 资源。");
                return;
            }
            if (!SelectionIsSameSize())
            {
                await ShowMessage("尺寸不一致", "批量替换只支持尺寸完全一致的资源。");
                return;
            }

            Windows.Storage.StorageFile file = await PickerHelper.PickOpenFileAsync(
                "选择新的 PNG", "PNG 图片", new string[] { ".png" }, null);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            try
            {
                SetBusy(true, "正在适配新图片…");
                PngAsset first = selectedAssets[0];
                byte[] raw = File.ReadAllBytes(file.Path);
                string error;
                PngPacket packet = PngCodec.Parse(raw, true, out error);
                if (packet == null) throw new Exception(error);
                if (packet.Width != first.Width || packet.Height != first.Height)
                {
                    throw new Exception(String.Format(CultureInfo.InvariantCulture,
                        "新图尺寸必须与原图完全一致。原图 {0}x{1}，当前文件 {2}x{3}。",
                        first.Width, first.Height, packet.Width, packet.Height));
                }

                List<PngAsset> targets = selectedAssets;
                Dictionary<int, byte[]> fitted = new Dictionary<int, byte[]>();
                List<string> errors = new List<string>();
                await Task.Run(delegate
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        PngAsset asset = targets[i];
                        if (fitted.ContainsKey(asset.Length)) continue;
                        string fitError;
                        byte[] data = PngCodec.FitToExactLength(raw, asset.Length, out fitError);
                        if (data == null) errors.Add("#" + asset.Index + "（" + asset.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节）：" + fitError);
                        else fitted[asset.Length] = data;
                    }
                });

                if (errors.Count > 0)
                {
                    throw new Exception("有资源无法精确适配，批量操作已取消：\r\n\r\n" + String.Join("\r\n", errors.ToArray()));
                }

                replacementRaw = raw;
                replacementPath = file.Path;
                replacementByLength = fitted;
                ImgNew.Source = await ToBitmapAsync(raw);

                long minLen = Int64.MaxValue, maxLen = 0;
                for (int i = 0; i < selectedAssets.Count; i++)
                {
                    if (selectedAssets[i].Length < minLen) minLen = selectedAssets[i].Length;
                    if (selectedAssets[i].Length > maxLen) maxLen = selectedAssets[i].Length;
                }
                TxtNewInfo.Text = String.Format(CultureInfo.InvariantCulture,
                    "{0} · {1:N0} 字节 · {2}x{3}\r\n将替换 {4} 张，覆盖 {5} 种原始长度（{6:N0}..{7:N0} 字节）",
                    Path.GetFileName(file.Path), raw.Length, packet.Width, packet.Height,
                    selectedAssets.Count, fitted.Count, minLen, maxLen);
                Log("已选择替换图：" + file.Path + "，将替换 " + selectedAssets.Count + " 张。");
            }
            catch (Exception ex)
            {
                replacementRaw = null;
                replacementByLength = new Dictionary<int, byte[]>();
                ImgNew.Source = null;
                TxtNewInfo.Text = "替换图不可用";
                await ShowMessage("替换图错误", ex.Message);
                Log("替换图错误：" + ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
                UpdateButtons();
            }
        }

        private async void OnExportOriginal(object sender, RoutedEventArgs e)
        {
            if (selectedAsset == null || selectedOriginalBytes == null) return;
            Windows.Storage.StorageFile file = await PickerHelper.PickSaveFileAsync(
                "导出原启动图", "PNG 图片", new string[] { ".png" },
                "export_" + selectedAsset.Index + "_" + selectedAsset.Width + "x" + selectedAsset.Height);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;
            await File.WriteAllBytesAsync(file.Path, selectedOriginalBytes);
            Log("已导出原图：" + file.Path);
        }

        // ---------------- 补丁 ----------------

        private List<PatchItem> BuildPatchItems(out string error)
        {
            error = null;
            if (selectedAssets.Count == 0 || replacementRaw == null || replacementByLength.Count == 0)
            {
                error = "请先选择资源并指定新的 PNG。";
                return null;
            }
            if (!SelectionIsSameSize())
            {
                error = "所选资源尺寸不一致，无法批量替换。";
                return null;
            }

            List<PatchItem> items = new List<PatchItem>();
            HashSet<long> added = new HashSet<long>();
            Dictionary<long, string> originalHashes = new Dictionary<long, string>();
            Dictionary<int, string> replacementHashes = new Dictionary<int, string>();

            for (int i = 0; i < selectedAssets.Count; i++)
            {
                PngAsset asset = selectedAssets[i];
                byte[] replacement;
                if (!replacementByLength.TryGetValue(asset.Length, out replacement))
                {
                    error = "资源 #" + asset.Index + " 缺少目标长度的适配结果。";
                    return null;
                }
                string replacementHash;
                if (!replacementHashes.TryGetValue(asset.Length, out replacementHash))
                {
                    replacementHash = HashTools.Sha256Bytes(replacement);
                    replacementHashes[asset.Length] = replacementHash;
                }
                string originalHash = GetAssetHash(asset, originalHashes);
                AddItem(items, added, asset, replacement, replacementHash, originalHash);
            }

            if (ChkCopies.IsChecked == true)
            {
                for (int i = 0; i < selectedAssets.Count; i++)
                {
                    PngAsset source = selectedAssets[i];
                    string sourceHash = originalHashes[source.Offset];
                    byte[] replacement = replacementByLength[source.Length];
                    string replacementHash = replacementHashes[source.Length];
                    for (int j = 0; j < allAssets.Count; j++)
                    {
                        PngAsset candidate = allAssets[j];
                        if (candidate.Width != source.Width || candidate.Height != source.Height) continue;
                        if (candidate.Length != source.Length) continue;
                        if (added.Contains(candidate.Offset)) continue;
                        try
                        {
                            string hash = GetAssetHash(candidate, originalHashes);
                            if (!String.Equals(hash, sourceHash, StringComparison.OrdinalIgnoreCase)) continue;
                            AddItem(items, added, candidate, replacement, replacementHash, hash);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            return items;
        }

        private void AddItem(List<PatchItem> items, HashSet<long> added, PngAsset asset, byte[] replacement, string replacementHash, string originalHash)
        {
            if (added.Contains(asset.Offset)) return;
            PatchItem item = new PatchItem();
            item.Original = asset;
            item.OriginalSha256 = originalHash;
            item.Replacement = replacement;
            item.ReplacementSha256 = replacementHash;
            item.ReplacementPath = replacementPath;
            items.Add(item);
            added.Add(asset.Offset);
        }

        private string GetAssetHash(PngAsset asset, Dictionary<long, string> cache)
        {
            string hash;
            if (cache.TryGetValue(asset.Offset, out hash)) return hash;
            byte[] bytes = PngCodec.ExtractAsset(ResolvePath, asset);
            hash = HashTools.Sha256Bytes(bytes);
            asset.Sha256 = hash;
            cache[asset.Offset] = hash;
            return hash;
        }

        private async void OnCreatePatch(object sender, RoutedEventArgs e)
        {
            string error;
            List<PatchItem> items = BuildPatchItems(out error);
            if (items == null)
            {
                await ShowMessage("无法生成补丁", error);
                return;
            }

            string source = ResolvePath;
            Windows.Storage.StorageFile file = await PickerHelper.PickSaveFileAsync(
                "保存补丁副本", "可执行文件", new string[] { ".exe" }, "Resolve.patched");
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            SetBusy(true, "正在生成补丁副本…");
            try
            {
                string output = file.Path;
                bool clear = ChkClearSig.IsChecked == true;
                PatchResult result = null;
                string err = null;
                await Task.Run(delegate { result = Patcher.CreatePatchedCopy(source, output, items, clear, out err); });
                if (result == null) throw new Exception(err);

                Log("补丁生成成功：" + result.OutputPath);
                for (int i = 0; i < result.VerificationMessages.Count; i++) Log("  " + result.VerificationMessages[i]);
                Log("  SHA-256：" + result.OutputSha256);
                Log("  清单：" + result.ManifestPath);
                await ShowMessage("生成完成",
                    "已生成补丁副本。\r\n\r\n替换资源：" + result.ReplacementCount +
                    "\r\n文件长度：" + result.OutputSize.ToString("N0", CultureInfo.InvariantCulture) +
                    "\r\nPE Checksum：0x" + result.OriginalPeChecksum.ToString("X8") + " → 0x" + result.PatchedPeChecksum.ToString("X8") +
                    "\r\n\r\n输出：\r\n" + result.OutputPath);
            }
            catch (Exception ex)
            {
                Log("生成补丁失败：" + ex.Message);
                await ShowMessage("生成失败", ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
                UpdateButtons();
            }
        }

        private async void OnApplyPatch(object sender, RoutedEventArgs e)
        {
            if (replacementRaw == null || selectedAssets.Count == 0) return;
            string source = ResolvePath;
            if (Patcher.IsProcessRunning("Resolve"))
            {
                await ShowMessage("Resolve 正在运行", "DaVinci Resolve 正在运行，请完全退出后再应用补丁。");
                return;
            }
            if (!await Confirm("确认应用到 Resolve.exe",
                "将直接修改：\r\n" + source +
                "\r\n\r\n首次应用会创建备份到：\r\n" + GetBackupPath() +
                "\r\n\r\n程序目录通常需要管理员权限，若失败会请求提权。继续吗？"))
            {
                return;
            }

            string error;
            List<PatchItem> items = BuildPatchItems(out error);
            if (items == null)
            {
                await ShowMessage("无法应用补丁", error);
                return;
            }

            string temp = source + ".patch.tmp";
            SetBusy(true, "正在生成并验证补丁…");
            try
            {
                string backup = GetBackupPath();
                if (!File.Exists(backup))
                {
                    File.Copy(source, backup, false);
                    Log("已创建原始备份：" + backup);
                }

                bool clear = ChkClearSig.IsChecked == true;
                PatchResult result = null;
                string err = null;
                await Task.Run(delegate { result = Patcher.CreatePatchedCopy(source, temp, items, clear, out err); });
                if (result == null) throw new Exception(err);

                await DeployFileAsync(temp, source);

                Log("已应用到 Resolve.exe。");
                for (int i = 0; i < result.VerificationMessages.Count; i++) Log("  " + result.VerificationMessages[i]);
                Log("  PE Checksum：0x" + result.OriginalPeChecksum.ToString("X8") + " → 0x" + result.PatchedPeChecksum.ToString("X8"));

                await ShowMessage("应用完成", "替换已应用。\r\n\r\n请完全退出 Resolve 后重新启动。\r\n如需还原，请点击[恢复原始备份]。");
                ClearSelection();
                UpdateButtons();
            }
            catch (Exception ex)
            {
                Log("应用失败：" + ex.Message);
                await ShowMessage("应用失败", ex.Message);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                SetBusy(false, "就绪");
                UpdateButtons();
            }
        }

        private async void OnRestore(object sender, RoutedEventArgs e)
        {
            string source = ResolvePath;
            string backup = GetBackupPath();
            if (!File.Exists(backup))
            {
                await ShowMessage("无法恢复", "找不到原始备份：\r\n" + backup);
                return;
            }
            if (Patcher.IsProcessRunning("Resolve"))
            {
                await ShowMessage("Resolve 正在运行", "请先完全退出 DaVinci Resolve。");
                return;
            }
            if (!await Confirm("确认恢复", "将用原始备份覆盖当前 Resolve.exe。\r\n\r\n继续吗？")) return;

            SetBusy(true, "正在恢复…");
            try
            {
                await DeployFileAsync(backup, source);
                Log("已从备份恢复 Resolve.exe：" + backup);
                await ShowMessage("恢复完成", "已恢复原始 Resolve.exe。");
                allAssets.Clear();
                rows.Clear();
                HeaderInfo.IsOpen = false;
                ClearSelection();
            }
            catch (Exception ex)
            {
                Log("恢复失败：" + ex.Message);
                await ShowMessage("恢复失败", ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
                UpdateButtons();
            }
        }

        /// <summary>
        /// 直接覆盖目标文件；若因权限失败，自动以管理员身份完成同一操作。
        /// </summary>
        private async Task DeployFileAsync(string sourceFile, string destination)
        {
            try
            {
                string temp = destination + ".deploy.tmp";
                File.Copy(sourceFile, temp, true);
                File.Move(temp, destination, true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                Log("需要管理员权限，正在请求提权…");
            }
            catch (IOException)
            {
                Log("目标文件被占用或不可写，尝试提权…");
            }

            string elevateError = null;
            bool ok = await Task.Run(delegate
            {
                string e;
                bool r = ElevationService.TryElevate("copyFile", sourceFile, destination, out e);
                elevateError = e;
                return r;
            });

            if (!ok)
            {
                throw new Exception(String.IsNullOrEmpty(elevateError)
                    ? "写入失败。请以管理员身份运行本工具后重试。"
                    : elevateError);
            }
        }

        // ---------------- 辅助 ----------------

        private string GetBackupPath()
        {
            string name = String.IsNullOrEmpty(ResolvePath) ? "Resolve.exe" : Path.GetFileName(ResolvePath);
            return Path.Combine(AppPaths.ResolveBackupDir, name + ".original.bak");
        }

        private void SetBusy(bool value, string status)
        {
            busy = value;
            Busy.IsActive = value;
            if (MainWindow.Instance != null) MainWindow.Instance.Status(status);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (!ready) return;
            bool hasAsset = selectedAssets.Count > 0 && selectedOriginalBytes != null && SelectionIsSameSize();
            bool hasReplacement = replacementByLength.Count > 0 && replacementRaw != null;
            BtnScan.IsEnabled = !busy;
            BtnBrowse.IsEnabled = !busy;
            BtnChoosePng.IsEnabled = !busy && hasAsset;
            BtnExportOriginal.IsEnabled = !busy && selectedAsset != null && selectedOriginalBytes != null;
            BtnCreatePatch.IsEnabled = !busy && hasReplacement;
            BtnApply.IsEnabled = !busy && hasReplacement;
            BtnRestore.IsEnabled = !busy && !String.IsNullOrEmpty(ResolvePath) && File.Exists(GetBackupPath());
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + "\r\n";
            TxtLog.Text = (TxtLog.Text ?? String.Empty) + line;
        }

        private static async Task<BitmapImage> ToBitmapAsync(byte[] bytes)
        {
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                BitmapImage image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
        }

        private async Task<bool> Confirm(string title, string message)
        {
            ContentDialog dialog = new ContentDialog();
            dialog.XamlRoot = XamlRoot;
            dialog.Title = title;
            dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            dialog.PrimaryButtonText = "继续";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Close;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task ShowMessage(string title, string message)
        {
            ContentDialog dialog = new ContentDialog();
            dialog.XamlRoot = XamlRoot;
            dialog.Title = title;
            dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            dialog.CloseButtonText = "确定";
            await dialog.ShowAsync();
        }
    }
}
