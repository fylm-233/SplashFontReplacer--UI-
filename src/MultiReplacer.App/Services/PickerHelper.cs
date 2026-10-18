using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using MultiReplacer.Core;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MultiReplacer.App.Services
{
    /// <summary>
    /// 统一的文件/目录选择入口。
    ///
    /// 背景：WinRT 的 FileOpenPicker 在提权（管理员）进程里会失败——选择器由宿主进程
    /// 代理，高完整性级别的调用方拿不到结果，表现为"点按钮没反应"。所以这里做了两件事：
    ///   1. 提权时直接走 Win32 公共对话框（进程内，提权可用）；
    ///   2. 非提权时仍用 WinRT 选择器（界面更现代），一旦抛异常也自动回退到 Win32，
    ///      这样即使权限判定不准，用户也不会被卡住。
    /// 所有页面的选择按钮都走这里，避免每个页面各写一份。
    /// </summary>
    public static class PickerHelper
    {
        /// <summary>最近一次失败原因，供页面写日志。</summary>
        public static string LastError { get; private set; }

        /// <summary>当前是否应当直接使用原生对话框。</summary>
        public static bool PreferNative
        {
            get { return ElevationService.IsElevated; }
        }

        public static string ModeText
        {
            get { return PreferNative ? "Win32 公共对话框（管理员模式）" : "系统选择器"; }
        }

        private static IntPtr Owner
        {
            get
            {
                try
                {
                    if (MainWindow.Instance == null) return IntPtr.Zero;
                    return WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }
        }

        public static async Task<StorageFile> PickOpenFileAsync(string title, string description,
                                                               string[] extensions, string initialDirectory)
        {
            LastError = null;

            if (!PreferNative)
            {
                try
                {
                    FileOpenPicker picker = new FileOpenPicker();
                    picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                    if (!String.IsNullOrEmpty(initialDirectory))
                        picker.SuggestedStartLocation = PickerLocationId.Desktop;
                    for (int i = 0; i < extensions.Length; i++) picker.FileTypeFilter.Add(Normalize(extensions[i]));
                    Initialize(picker);
                    StorageFile picked = await picker.PickSingleFileAsync();
                    if (picked != null) return picked;
                    // 用户取消：不再回退，否则会连弹两次
                    return null;
                }
                catch (Exception ex)
                {
                    LastError = "系统选择器不可用（" + ex.Message + "），已改用原生对话框。";
                }
            }

            string error;
            string path = NativeFileDialog.OpenFile(Owner, title, description, extensions, initialDirectory, out error);
            if (path == null)
            {
                if (!String.IsNullOrEmpty(error)) LastError = error;
                return null;
            }
            return await AsStorageFile(path);
        }

        public static async Task<StorageFile> PickSaveFileAsync(string title, string description,
                                                                string[] extensions, string suggestedName)
        {
            LastError = null;

            if (!PreferNative)
            {
                try
                {
                    FileSavePicker picker = new FileSavePicker();
                    picker.SuggestedFileName = suggestedName;
                    picker.FileTypeChoices.Add(String.IsNullOrEmpty(description) ? "文件" : description,
                                               new List<string>(NormalizeAll(extensions)));
                    Initialize(picker);
                    StorageFile picked = await picker.PickSaveFileAsync();
                    if (picked != null) return picked;
                    return null;
                }
                catch (Exception ex)
                {
                    LastError = "系统选择器不可用（" + ex.Message + "），已改用原生对话框。";
                }
            }

            string error;
            string path = NativeFileDialog.SaveFile(Owner, title, description, extensions, null, suggestedName, out error);
            if (path == null)
            {
                if (!String.IsNullOrEmpty(error)) LastError = error;
                return null;
            }
            return await AsStorageFile(path);
        }

        /// <summary>选择目录，返回绝对路径；取消返回 null。</summary>
        public static async Task<string> PickFolderAsync(string title)
        {
            LastError = null;

            if (!PreferNative)
            {
                try
                {
                    FolderPicker picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    Initialize(picker);
                    StorageFolder folder = await picker.PickSingleFolderAsync();
                    if (folder != null) return folder.Path;
                    return null;
                }
                catch (Exception ex)
                {
                    LastError = "系统选择器不可用（" + ex.Message + "），已改用原生对话框。";
                }
            }

            string error;
            string path = NativeFileDialog.PickFolder(Owner, title, out error);
            if (path == null && !String.IsNullOrEmpty(error)) LastError = error;
            return path;
        }

        private static void Initialize(object picker)
        {
            IntPtr hwnd = Owner;
            if (hwnd == IntPtr.Zero) return;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        private static string Normalize(string extension)
        {
            if (String.IsNullOrEmpty(extension)) return "*";
            return extension[0] == '.' ? extension : "." + extension;
        }

        private static string[] NormalizeAll(string[] extensions)
        {
            List<string> list = new List<string>();
            for (int i = 0; i < extensions.Length; i++) list.Add(Normalize(extensions[i]));
            if (list.Count == 0) list.Add("*");
            return list.ToArray();
        }

        /// <summary>
        /// 原生对话框只给路径，这里换回 StorageFile 以便复用既有的 WinRT 解码流程。
        /// 极少数路径（如网络位置）可能取不到，此时返回 null 并记录原因。
        /// </summary>
        private static async Task<StorageFile> AsStorageFile(string path)
        {
            try
            {
                return await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception ex)
            {
                LastError = "无法打开所选文件：" + ex.Message;
                return null;
            }
        }
    }
}
