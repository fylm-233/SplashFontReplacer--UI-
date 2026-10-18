using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MultiReplacer.App.Models;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.Storage.Pickers;

namespace MultiReplacer.App.Views
{
    /// <summary>
    /// Adobe 系列界面字体替换。
    /// 交互刻意与「达芬奇 UI 字体」页保持一致：目标选择 → 字体候选列表 → 右侧状态与日志 → 应用/恢复。
    /// </summary>
    public sealed partial class AdobePage : Page
    {
        private readonly ObservableCollection<FontRow> rows = new ObservableCollection<FontRow>();
        private List<FontRow> allRows = new List<FontRow>();
        private AdobeAppInfo current;
        private string selectedFont;
        private bool ready;

        public AdobePage()
        {
            InitializeComponent();
            LvFonts.ItemsSource = rows;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadFontList();
            selectedFont = App.Settings.AdobeFontName;
            ready = true;
            await BuildCandidatesAsync();
            await RediscoverAsync(false);
        }

        // ---------------- 字体候选 ----------------

        private void LoadFontList()
        {
            NumLarge.Value = App.Settings.AdobeLargeSize > 0 ? App.Settings.AdobeLargeSize : 16;
            NumMedium.Value = App.Settings.AdobeMediumSize > 0 ? App.Settings.AdobeMediumSize : 14;
            NumSmall.Value = App.Settings.AdobeSmallSize > 0 ? App.Settings.AdobeSmallSize : 9;
            ChkPluginFonts.IsChecked = App.Settings.AdobeReplacePluginFonts;
        }


        private AdobeFontOptions ReadOptions()
        {
            AdobeFontOptions options = new AdobeFontOptions();
            options.FontName = String.IsNullOrEmpty(selectedFont) ? "Microsoft YaHei UI" : selectedFont;
            options.LargeSize = ReadSize(NumLarge, 16);
            options.MediumSize = ReadSize(NumMedium, 14);
            options.SmallSize = ReadSize(NumSmall, 9);
            options.ReplacePluginFonts = ChkPluginFonts.IsChecked == true;
            return options;
        }

        private static int ReadSize(NumberBox box, int fallback)
        {
            if (box == null) return fallback;
            double value = box.Value;
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return fallback;
            int size = (int)Math.Round(value);
            if (size < 6) size = 6;
            if (size > 30) size = 30;
            return size;
        }

        private async Task BuildCandidatesAsync()
        {
            List<string> installed = null;
            await Task.Run(delegate { installed = FontInventory.ListInstalled(); });
            HashSet<string> installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);

            List<FontRow> list = new List<FontRow>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(list, seen, installedSet, "Microsoft YaHei UI", true, "推荐，界面清晰");
            AddCandidate(list, seen, installedSet, "Microsoft YaHei", false, "微软雅黑");
            AddCandidate(list, seen, installedSet, "SimHei", false, "黑体");
            AddCandidate(list, seen, installedSet, "DengXian", false, "等线");
            AddCandidate(list, seen, installedSet, "KaiTi", false, "楷体");
            AddCandidate(list, seen, installedSet, "FangSong", false, "仿宋");

            for (int i = 0; i < installed.Count; i++)
            {
                AddCandidate(list, seen, installedSet, installed[i], false, "已安装");
            }

            list.Sort(delegate(FontRow a, FontRow b)
            {
                if (a.Recommended != b.Recommended) return a.Recommended ? -1 : 1;
                if (a.Installed != b.Installed) return a.Installed ? -1 : 1;
                return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            allRows = list;
            ApplyFontFilter();
        }

        private void AddCandidate(List<FontRow> list, HashSet<string> seen, HashSet<string> installed,
                                  string name, bool recommended, string note)
        {
            if (String.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (!seen.Add(name)) return;

            FontRow row = new FontRow();
            row.Name = name;
            row.Installed = installed.Contains(name);
            row.Recommended = recommended;
            row.Status = row.Installed ? "已安装" : "未安装";
            row.Bytes = Encoding.UTF8.GetByteCount(name).ToString(CultureInfo.InvariantCulture);
            row.Note = note;
            list.Add(row);
        }

        private void OnFontFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFontFilter();
        }

        private void ApplyFontFilter()
        {
            if (!ready || TxtFontFilter == null || LvFonts == null) return;
            string filter = (TxtFontFilter.Text ?? String.Empty).Trim();
            rows.Clear();
            FontRow select = null;
            for (int i = 0; i < allRows.Count; i++)
            {
                FontRow row = allRows[i];
                if (filter.Length > 0 && row.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(row);
                if (select == null && String.Equals(row.Name, selectedFont, StringComparison.OrdinalIgnoreCase)) select = row;
            }
            if (select != null)
            {
                LvFonts.SelectedItem = select;
            }
            else if (rows.Count > 0 && LvFonts.SelectedItem == null)
            {
                LvFonts.SelectedIndex = 0;
            }
            UpdateButtons();
        }

        private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FontRow row = LvFonts.SelectedItem as FontRow;
            if (row == null) return;
            selectedFont = row.Name;
            App.Settings.AdobeFontName = row.Name;
            UpdateButtons();
        }

        // ---------------- 应用发现 ----------------

        private async Task RediscoverAsync(bool verbose)
        {
            MainWindow.Instance.Status("正在扫描 Adobe 应用…");
            List<AdobeAppInfo> found = null;
            await Task.Run(delegate { found = AdobeDictionary.Discover(); });

            string keep = App.Settings.AdobeLocaleDir;
            ObservableCollection<AdobeAppRow> items = new ObservableCollection<AdobeAppRow>();
            for (int i = 0; i < found.Count; i++)
            {
                AdobeAppRow row = new AdobeAppRow();
                row.App = found[i];
                row.Display = found[i].DisplayName + "  ·  " + found[i].Locale;
                items.Add(row);
            }

            cboRows = items;
            CboApp.ItemsSource = items;

            if (items.Count == 0)
            {
                current = null;
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "未检测到可替换字体的 Adobe 应用";
                StatusBar.Message = "未在注册表与 Program Files\\Adobe 下找到带 Dictionaries 语言包的应用。" +
                                    "可点击[手动选择…]指定 Dictionaries 目录。";
                TxtDetail.Text = "尚未检测";
                TxtBackup.Text = "备份路径：-";
                TxtKeyInfo.Text = "";
                UpdateButtons();
                MainWindow.Instance.Status("就绪");
                return;
            }

            AdobeAppRow select = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (!String.IsNullOrEmpty(keep) &&
                    String.Equals(items[i].App.LocaleDir, keep, StringComparison.OrdinalIgnoreCase))
                {
                    select = items[i];
                    break;
                }
            }
            CboApp.SelectedItem = select ?? items[0];
            if (verbose) Log("检测到 " + items.Count + " 个可处理的 Adobe 应用。");
            MainWindow.Instance.Status("就绪");
        }

        private ObservableCollection<AdobeAppRow> cboRows;

        private async void OnRediscover(object sender, RoutedEventArgs e)
        {
            await RediscoverAsync(true);
        }

        private async void OnBrowseLocale(object sender, RoutedEventArgs e)
        {
            string dir = await PickerHelper.PickFolderAsync("选择 Adobe 语言包目录");
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (String.IsNullOrEmpty(dir)) return;

            AdobeAppInfo info = null;
            await Task.Run(delegate { info = AdobeDictionary.FromLocaleDirectory(dir); });
            if (info == null)
            {
                await ShowMessage("路径无效",
                    "该目录下未找到可用的 Adobe 语言包（*.dat）。\r\n\r\n" +
                    "请选择类似下面的目录：\r\n" +
                    "C:\\Program Files\\Adobe\\Adobe After Effects 2026\\Support Files\\Dictionaries\\zh_CN\r\n" +
                    "或直接选择 Dictionaries 目录。");
                return;
            }

            AdobeDictionary.Analyze(info);
            AdobeAppRow row = new AdobeAppRow();
            row.App = info;
            row.Display = info.DisplayName + "  ·  " + info.Locale;

            if (cboRows == null) cboRows = new ObservableCollection<AdobeAppRow>();
            cboRows.Insert(0, row);
            CboApp.ItemsSource = cboRows;
            CboApp.SelectedItem = row;
            Log("已添加自定义语言包：" + info.LocaleDir);
        }

        private async void OnAppChanged(object sender, SelectionChangedEventArgs e)
        {
            AdobeAppRow row = CboApp.SelectedItem as AdobeAppRow;
            if (row == null)
            {
                current = null;
                UpdateButtons();
                return;
            }

            current = row.App;
            App.Settings.AdobeLocaleDir = current.LocaleDir;
            App.Settings.Save(out string _);

            StatusBar.IsOpen = true;
            if (current.DictionaryFound)
            {
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Title = current.DisplayName + "（" + current.Locale + "）";
                StatusBar.Message = current.Message;
            }
            else
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = current.DisplayName + "：不支持或无法识别";
                StatusBar.Message = current.Message;
            }

            TxtKeyInfo.Text = current.StyleKeyCount > 0
                ? current.StyleKeyCount + " 个字体键"
                : "无字体键";

            StringBuilder sb = new StringBuilder();
            sb.Append("目标文件：").Append(current.DatFile).Append("\r\n");
            sb.Append("当前字体：").Append(String.IsNullOrEmpty(current.CurrentFont) ? "-" : current.CurrentFont).Append("\r\n");
            sb.Append("当前字号：大 ").Append(current.CurrentLarge > 0 ? current.CurrentLarge.ToString() : "-")
              .Append(" / 中 ").Append(current.CurrentMedium > 0 ? current.CurrentMedium.ToString() : "-")
              .Append(" / 小 ").Append(current.CurrentSmall > 0 ? current.CurrentSmall.ToString() : "-").Append("\r\n");
            sb.Append("字体键：").Append(current.StyleKeyCount).Append(" 条");
            if (current.PluginKeyCount > 0) sb.Append("，插件字体键 ").Append(current.PluginKeyCount).Append(" 条");
            sb.Append("\r\n");
            sb.Append("签名：").Append(current.HasSignature
                ? "有（摘要 " + (String.IsNullOrEmpty(current.DigestAlgorithm) ? "未知" : current.DigestAlgorithm.ToUpperInvariant()) + "）"
                : "无");
            sb.Append("\r\n");
            sb.Append("格式：").Append(current.SupportsPlatformSeparator ? "双平台（{{*MSWindows*}}）" : "单平台");
            TxtDetail.Text = sb.ToString();

            string backupDir = AdobeDictionary.GetBackupDir(current, AppPaths.BackupRoot);
            bool backupExists = File.Exists(Path.Combine(backupDir, current.DatFileName));
            TxtBackup.Text = "备份路径：" + backupDir + (backupExists ? "（已存在）" : "（首次应用时创建）");

            UpdateButtons();
            await BuildCandidatesAsync();
            await RefreshDiffAsync(false);
        }

        private async Task RefreshDiffAsync(bool verbose)
        {
            if (current == null) return;
            AdobeAppInfo snapshot = current;
            AdobeDiffResult diff = null;
            await Task.Run(delegate { diff = AdobeDictionary.Diff(snapshot, AppPaths.BackupRoot); });

            if (diff == null || (diff.CurrentCount == 0 && diff.BackupCount == 0))
            {
                if (verbose) Log("尚无备份基线，首次应用时会把当前语言包保存为基线。");
                return;
            }
            string text = diff.Identical
                ? "与备份基线一致（" + diff.CurrentCount + " 条键值，未打过补丁）。"
                : "相对基线：修改 " + diff.Modified + " 条，新增 " + diff.Added + " 条，缺失 " + diff.Deleted + " 条。";
            if (verbose) Log(text);
        }

        // ---------------- 应用 / 恢复 ----------------

        private async void OnApplyFont(object sender, RoutedEventArgs e)
        {
            if (current == null)
            {
                await ShowMessage("未选择应用", "请先选择一个 Adobe 应用或语言包目录。");
                return;
            }
            if (!current.DictionaryFound)
            {
                await ShowMessage("不支持替换",
                    "该语言包里没有 FontStyle 字体键，无法替换字体。\r\n\r\n" + (current.Message ?? ""));
                return;
            }
            FontRow row = LvFonts.SelectedItem as FontRow;
            if (row == null)
            {
                await ShowMessage("未选择字体", "请先在列表中选择一个字体族名。");
                return;
            }
            if (!row.Installed)
            {
                if (!await Confirm("字体未安装",
                    "字体「" + row.Name + "」似乎未安装到系统。应用后 Adobe 可能回退到默认字体。仍要继续吗？"))
                {
                    return;
                }
            }
            if (IsAdobeRunning())
            {
                await ShowMessage("Adobe 应用正在运行", "请先完全退出该 Adobe 应用后再替换字体。");
                return;
            }

            AdobeFontOptions options = ReadOptions();
            options.FontName = row.Name;
            string backupDir = AdobeDictionary.GetBackupDir(current, AppPaths.BackupRoot);

            if (!await Confirm("确认替换字体",
                "将重建并写入：\r\n" + current.DatFile + "\r\n\r\n" +
                "字体：" + options.FontName + "\r\n" +
                "字号：大 " + options.LargeSize + " / 中 " + options.MediumSize + " / 小 " + options.SmallSize + "\r\n" +
                "插件对话框字体：" + (options.ReplacePluginFonts ? "同时替换" : "不替换") + "\r\n" +
                "基线备份：" + backupDir + "\r\n\r\n" +
                (current.HasSignature ? "会同步 META-INF\\signatures.xml 的摘要。继续吗？" : "继续吗？")))
            {
                return;
            }

            try
            {
                MainWindow.Instance.Status("正在重建 Adobe 语言包…");
                AdobeAppInfo snapshot = current;
                string error = null;
                AdobePatchSummary summary = null;
                await Task.Run(delegate { summary = AdobeDictionary.Prepare(snapshot, AppPaths.BackupRoot, options, out error); });
                if (summary == null) throw new Exception(error);

                Log("暂存目录：" + summary.StagingDirectory);
                Log("字体样式键 " + summary.StyleModified + " 条，插件字体键 " + summary.PluginModified + " 条。");
                if (!String.IsNullOrEmpty(summary.NewDigest))
                {
                    Log("摘要已同步（" + (String.IsNullOrEmpty(summary.DigestAlgorithm) ? "sha256" : summary.DigestAlgorithm) + "）。");
                }
                Log("新 SHA-256：" + summary.DatSha256);

                string deployError = await DeployAsync(summary.StagingDirectory, snapshot);
                if (deployError != null) throw new Exception(deployError);
                AdobeDictionary.TryDeleteDirectory(summary.StagingDirectory);

                App.Settings.AdobeFontName = options.FontName;
                App.Settings.AdobeLargeSize = options.LargeSize;
                App.Settings.AdobeMediumSize = options.MediumSize;
                App.Settings.AdobeSmallSize = options.SmallSize;
                App.Settings.AdobeReplacePluginFonts = options.ReplacePluginFonts;
                App.Settings.Save(out string _);

                Log("已写入：" + snapshot.LocaleDir);
                AdobeDictionary.Analyze(snapshot);
                await RefreshDiffAsync(false);
                await ShowMessage("替换完成",
                    "已把「" + snapshot.DisplayName + "」的界面字体改为 " + options.FontName +
                    "（" + options.LargeSize + "/" + options.MediumSize + "/" + options.SmallSize + "）。\r\n\r\n" +
                    "重启该 Adobe 应用后生效。");
            }
            catch (Exception ex)
            {
                Log("替换失败：" + ex.Message);
                await ShowMessage("替换失败", ex.Message);
            }
            finally
            {
                MainWindow.Instance.Status("就绪");
                UpdateButtons();
            }
        }

        private async Task<string> DeployAsync(string staging, AdobeAppInfo app)
        {
            string direct = null;
            bool ok = false;
            await Task.Run(delegate
            {
                try
                {
                    AdobeDictionary.Deploy(staging, app);
                    ok = true;
                }
                catch (Exception ex)
                {
                    direct = ex.Message;
                }
            });
            if (ok) return null;

            MainWindow.Instance.Status("正在请求管理员权限…");
            string elevateError = null;
            ok = await Task.Run(delegate
            {
                string e;
                bool r = ElevationService.TryElevate("copyTree", staging, app.LocaleDir, out e);
                elevateError = e;
                return r;
            });
            if (ok) return null;

            string error = String.IsNullOrEmpty(elevateError) ? direct : elevateError;
            if (String.IsNullOrEmpty(error)) error = "写入失败，且提权未完成。";
            return error;
        }

        private async void OnRestoreFont(object sender, RoutedEventArgs e)
        {
            if (current == null)
            {
                await ShowMessage("未选择应用", "请先选择一个 Adobe 应用或语言包目录。");
                return;
            }

            string backupDir = AdobeDictionary.GetBackupDir(current, AppPaths.BackupRoot);
            if (!File.Exists(Path.Combine(backupDir, current.DatFileName)))
            {
                await ShowMessage("无法恢复", "尚未创建备份基线：\r\n" + backupDir);
                return;
            }
            if (IsAdobeRunning())
            {
                await ShowMessage("Adobe 应用正在运行", "请先完全退出该 Adobe 应用。");
                return;
            }
            if (!await Confirm("确认恢复官方语言包",
                "将用备份基线覆盖当前语言包目录：\r\n\r\n备份：" + backupDir +
                "\r\n目标：" + current.LocaleDir + "\r\n\r\n继续吗？"))
            {
                return;
            }

            try
            {
                MainWindow.Instance.Status("正在恢复 Adobe 语言包…");
                AdobeAppInfo snapshot = current;
                string direct = null;
                bool ok = false;
                await Task.Run(delegate
                {
                    try
                    {
                        AdobeDictionary.Restore(backupDir, snapshot);
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        direct = ex.Message;
                    }
                });

                if (!ok)
                {
                    MainWindow.Instance.Status("正在请求管理员权限…");
                    string elevateError = null;
                    ok = await Task.Run(delegate
                    {
                        string e;
                        bool r = ElevationService.TryElevate("copyTree", backupDir, snapshot.LocaleDir, out e);
                        elevateError = e;
                        return r;
                    });
                    if (!ok) throw new Exception(String.IsNullOrEmpty(elevateError) ? direct : elevateError);
                }

                Log("已从备份恢复：" + backupDir);
                AdobeDictionary.Analyze(snapshot);
                await RefreshDiffAsync(false);
                await ShowMessage("恢复完成", "已恢复语言包目录。重启该 Adobe 应用后生效。");
            }
            catch (Exception ex)
            {
                Log("恢复失败：" + ex.Message);
                await ShowMessage("恢复失败", ex.Message);
            }
            finally
            {
                MainWindow.Instance.Status("就绪");
                UpdateButtons();
            }
        }

        /// <summary>
        /// 常见 Adobe 主程序进程名；命中任一就提示用户先退出。
        /// </summary>
        private static bool IsAdobeRunning()
        {
            string[] names = new string[] { "AfterFX", "Adobe Premiere Pro", "Adobe Media Encoder", "Audition", "Prelude" };
            for (int i = 0; i < names.Length; i++)
            {
                if (Patcher.IsProcessRunning(names[i])) return true;
            }
            return false;
        }

        private void UpdateButtons()
        {
            if (!ready) return;
            bool hasFont = LvFonts.SelectedItem is FontRow;
            bool usable = current != null && current.DictionaryFound;
            BtnApplyFont.IsEnabled = hasFont && usable;
            BtnRestoreFont.IsEnabled = current != null &&
                File.Exists(Path.Combine(AdobeDictionary.GetBackupDir(current, AppPaths.BackupRoot), current.DatFileName));
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + "\r\n";
            TxtLog.Text = (TxtLog.Text ?? String.Empty) + line;
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
