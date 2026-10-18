using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MultiReplacer.App.Models;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.Storage.Pickers;

namespace MultiReplacer.App.Views
{
    public sealed partial class FontPage : Page
    {
        private readonly ObservableCollection<FontRow> rows = new ObservableCollection<FontRow>();
        private List<FontRow> allRows = new List<FontRow>();
        private QtFontSlot slot;
        private string selectedFont;
        private bool ready;

        public FontPage()
        {
            InitializeComponent();
            LvFonts.ItemsSource = rows;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            string dll = App.Settings.QWindowsPath;
            if (String.IsNullOrEmpty(dll))
            {
                string resolve = App.Settings.ResolveExePath;
                if (String.IsNullOrEmpty(resolve)) resolve = ResolveLocator.Find();
                if (!String.IsNullOrEmpty(resolve)) dll = ResolveLocator.GetQWindowsPath(resolve);
            }
            TxtDll.Text = dll ?? String.Empty;
            selectedFont = App.Settings.ResolveFontName;
            ready = true;
            await RefreshAsync(false);
        }

        private string DllPath
        {
            get { return (TxtDll.Text ?? String.Empty).Trim().Trim('"'); }
        }

        private string BackupPath
        {
            get
            {
                string name = String.IsNullOrEmpty(DllPath) ? "qwindows.dll" : Path.GetFileName(DllPath);
                return Path.Combine(AppPaths.ResolveBackupDir, name + ".font.original.bak");
            }
        }

        private async void OnBrowse(object sender, RoutedEventArgs e)
        {
            Windows.Storage.StorageFile file = await PickerHelper.PickOpenFileAsync(
                "选择 qwindows.dll", "DLL 文件", new string[] { ".dll" }, null);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            TxtDll.Text = file.Path;
            App.Settings.QWindowsPath = file.Path;
            App.Settings.Save(out string _);
            await RefreshAsync(true);
        }

        private async void OnDetect(object sender, RoutedEventArgs e)
        {
            await RefreshAsync(true);
        }

        private async Task RefreshAsync(bool verbose)
        {
            string path = DllPath;
            slot = null;
            try
            {
                slot = await Task.Run(delegate { return QtFontTable.Analyze(path); });
            }
            catch (Exception ex)
            {
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Title = "检测失败";
                StatusBar.Message = ex.Message;
                Log("检测失败：" + ex.Message);
                return;
            }

            StatusBar.IsOpen = true;
            if (!slot.FileExists)
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "未找到 qwindows.dll";
                StatusBar.Message = slot.Message;
            }
            else if (!slot.TableFound)
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "字体表无法识别";
                StatusBar.Message = slot.Message;
            }
            else
            {
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Title = "当前字体：" + slot.CurrentFont;
                StatusBar.Message = slot.Message;
            }

            if (verbose) Log(slot.Message);

            TxtSlotInfo.Text = slot.SlotOffset >= 0
                ? "槽位 " + slot.SlotOffsetHex + " · " + slot.SlotLength + " 字节"
                : "槽位未知";

            TxtDetail.Text =
                "目标文件：" + (String.IsNullOrEmpty(path) ? "-" : path) + "\r\n" +
                "当前字体：" + (slot.CurrentFont ?? "-") + "\r\n" +
                "槽位偏移：" + slot.SlotOffsetHex + "\r\n" +
                "可用长度：" + slot.SlotLength + " 字节（字体名最多 " + slot.MaxFontBytes + " 字节）\r\n" +
                "文件 SHA-256：" + ShortHash(slot.CurrentSha256);

            TxtBackup.Text = "备份路径：" + BackupPath + (File.Exists(BackupPath) ? "（已存在）" : "（尚未创建）");

            await BuildCandidatesAsync();
        }

        private static string ShortHash(string hash)
        {
            if (String.IsNullOrEmpty(hash)) return "-";
            return hash.Substring(0, Math.Min(16, hash.Length)) + "…";
        }

        private async Task BuildCandidatesAsync()
        {
            int maxBytes = slot != null && slot.MaxFontBytes > 0 ? slot.MaxFontBytes : 23;
            List<string> installed = null;
            await Task.Run(delegate { installed = FontInventory.ListInstalled(); });
            HashSet<string> installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);

            List<FontRow> list = new List<FontRow>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(list, seen, installedSet, QtFontTable.DefaultFont, true, "推荐，界面清晰", maxBytes);
            AddCandidate(list, seen, installedSet, "Microsoft YaHei", false, "微软雅黑", maxBytes);
            AddCandidate(list, seen, installedSet, "SimHei", false, "黑体", maxBytes);
            AddCandidate(list, seen, installedSet, "DengXian", false, "等线", maxBytes);
            AddCandidate(list, seen, installedSet, "KaiTi", false, "楷体", maxBytes);
            AddCandidate(list, seen, installedSet, "FangSong", false, "仿宋", maxBytes);

            for (int i = 0; i < installed.Count; i++)
            {
                AddCandidate(list, seen, installedSet, installed[i], false, "已安装", maxBytes);
            }

            list.Sort(delegate(FontRow a, FontRow b)
            {
                if (a.Recommended != b.Recommended) return a.Recommended ? -1 : 1;
                if (a.Installed != b.Installed) return a.Installed ? -1 : 1;
                return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            allRows = list;
            ApplyFontFilter();
            await Task.CompletedTask;
        }

        private void AddCandidate(List<FontRow> list, HashSet<string> seen, HashSet<string> installed, string name, bool recommended, string note, int maxBytes)
        {
            if (String.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (!seen.Add(name)) return;
            if (!QtFontTable.IsAsciiFontName(name)) return;
            int bytes = System.Text.Encoding.ASCII.GetByteCount(name);
            if (bytes > maxBytes) return;

            FontRow row = new FontRow();
            row.Name = name;
            row.Installed = installed.Contains(name);
            row.Recommended = recommended;
            row.Status = row.Installed ? "已安装" : "未安装";
            row.Bytes = bytes.ToString(CultureInfo.InvariantCulture);
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
            App.Settings.ResolveFontName = row.Name;
            UpdateButtons();
        }

        private async void OnApplyFont(object sender, RoutedEventArgs e)
        {
            FontRow row = LvFonts.SelectedItem as FontRow;
            if (row == null)
            {
                await ShowMessage("未选择字体", "请先在列表中选择一个字体族名。");
                return;
            }
            if (!row.Installed)
            {
                if (!await Confirm("字体未安装",
                    "字体「" + row.Name + "」似乎未安装到系统。应用后 Resolve 可能回退到系统默认字体。仍要继续吗？"))
                {
                    return;
                }
            }
            if (Patcher.IsProcessRunning("Resolve"))
            {
                await ShowMessage("Resolve 正在运行", "请完全退出 DaVinci Resolve 后再替换字体。");
                return;
            }
            if (!await Confirm("确认替换字体",
                "将修改：\r\n" + DllPath + "\r\n\r\n字体：" + row.Name +
                "\r\n备份：" + BackupPath + "\r\n\r\n继续吗？"))
            {
                return;
            }

            try
            {
                MainWindow.Instance.Status("正在替换 UI 字体…");
                string path = DllPath;
                string backup = BackupPath;
                string font = row.Name;
                QtFontApplyResult result = null;
                string error = null;
                await Task.Run(delegate { result = QtFontTable.Apply(path, backup, font, out error); });
                if (result == null) throw new Exception(error);

                Log(result.Message);
                Log("备份：" + result.BackupPath + (result.BackupCreated ? "（新建）" : "（已存在）"));
                Log("SHA-256：" + result.BeforeSha256 + " → " + result.AfterSha256);

                await RefreshAsync(false);
                await ShowMessage("字体替换完成", result.Message + "\r\n\r\n重启 DaVinci Resolve 后生效。");
            }
            catch (Exception ex)
            {
                Log("字体替换失败：" + ex.Message);
                await ShowMessage("字体替换失败", ex.Message);
            }
            finally
            {
                MainWindow.Instance.Status("就绪");
                UpdateButtons();
            }
        }

        private async void OnRestoreFont(object sender, RoutedEventArgs e)
        {
            string backup = BackupPath;
            if (!File.Exists(backup))
            {
                await ShowMessage("无法恢复", "找不到字体备份：\r\n" + backup);
                return;
            }
            if (Patcher.IsProcessRunning("Resolve"))
            {
                await ShowMessage("Resolve 正在运行", "请先完全退出 DaVinci Resolve。");
                return;
            }
            if (!await Confirm("确认恢复字体", "将用字体备份覆盖当前 qwindows.dll。\r\n\r\n备份：" + backup + "\r\n\r\n继续吗？")) return;

            try
            {
                MainWindow.Instance.Status("正在恢复字体备份…");
                string path = DllPath;
                string error = null;
                bool ok = false;
                await Task.Run(delegate { ok = QtFontTable.Restore(path, backup, out error); });
                if (!ok)
                {
                    // 备份恢复同样可能需要管理员权限
                    string elevateError = null;
                    ok = await Task.Run(delegate
                    {
                        string e;
                        bool r = ElevationService.TryElevate("copyFile", backup, path, out e);
                        elevateError = e;
                        return r;
                    });
                    if (!ok) throw new Exception(String.IsNullOrEmpty(elevateError) ? error : elevateError);
                }
                Log("已恢复字体备份：" + backup);
                await RefreshAsync(false);
                await ShowMessage("恢复完成", "已恢复 qwindows.dll 字体备份。");
            }
            catch (Exception ex)
            {
                Log("恢复字体失败：" + ex.Message);
                await ShowMessage("恢复字体失败", ex.Message);
            }
            finally
            {
                MainWindow.Instance.Status("就绪");
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            if (!ready) return;
            bool hasFont = LvFonts.SelectedItem is FontRow;
            BtnApplyFont.IsEnabled = hasFont && slot != null && slot.FileExists && slot.TableFound;
            BtnRestoreFont.IsEnabled = File.Exists(BackupPath);
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
