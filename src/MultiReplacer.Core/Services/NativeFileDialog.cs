using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiReplacer.Core
{
    /// <summary>
    /// Win32 公共文件对话框（comdlg32 / shell32）。
    ///
    /// 为什么需要它：WinRT 的 FileOpenPicker 在**提权进程**里会以 E_ACCESSDENIED 失败，
    /// 这是 Windows 的既定行为（选择器宿主进程不接受高完整性级别调用），
    /// 表现为点了"选择图片…"什么都不弹。Win32 的 GetOpenFileName 是进程内对话框，
    /// 提权与否都能弹，因此作为管理员模式下的通道。
    ///
    /// 刻意不用 COM 的 IFileOpenDialog：vtable 顺序一旦写错就是静默崩溃，
    /// 而这里只需要"能选文件/能选目录"，GetOpenFileName + SHBrowseForFolder 足够。
    /// </summary>
    public static class NativeFileDialog
    {
        private const int OFN_READONLY = 0x00000001;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_HIDEREADONLY = 0x00000004;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_EXPLORER = 0x00080000;

        private const int BIF_RETURNONLYFSDIRS = 0x00000001;
        private const int BIF_NEWDIALOGSTYLE = 0x00000040;
        private const int BIF_EDITBOX = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BrowseInfo
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo bi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        /// <summary>
        /// 把 ".png|.jpg" 这样的扩展名列表拼成 comdlg32 需要的过滤器串
        /// （双 \0 分隔、双 \0 结尾）。单独抽出来便于验证。
        /// </summary>
        public static string BuildFilter(string description, string[] extensions)
        {
            StringBuilder sb = new StringBuilder();
            StringBuilder patterns = new StringBuilder();
            if (extensions != null)
            {
                for (int i = 0; i < extensions.Length; i++)
                {
                    string ext = extensions[i];
                    if (String.IsNullOrEmpty(ext)) continue;
                    if (ext[0] != '.') ext = "." + ext;
                    if (patterns.Length > 0) patterns.Append(';');
                    patterns.Append('*').Append(ext);
                }
            }
            if (patterns.Length == 0) patterns.Append("*.*");

            string label = String.IsNullOrEmpty(description) ? "文件" : description;
            sb.Append(label).Append(" (").Append(patterns).Append(')').Append('\0');
            sb.Append(patterns).Append('\0');
            sb.Append("所有文件 (*.*)").Append('\0').Append("*.*").Append('\0');
            sb.Append('\0');
            return sb.ToString();
        }

        /// <summary>弹出打开文件对话框。返回 null 表示用户取消或失败（error 里有原因）。</summary>
        public static string OpenFile(IntPtr owner, string title, string description, string[] extensions,
                                      string initialDirectory, out string error)
        {
            return Show(owner, title, description, extensions, initialDirectory, false, out error);
        }

        /// <summary>弹出保存文件对话框。</summary>
        public static string SaveFile(IntPtr owner, string title, string description, string[] extensions,
                                      string initialDirectory, string suggestedName, out string error)
        {
            return Show(owner, title, description, extensions, initialDirectory, true, out error, suggestedName);
        }

        private static string Show(IntPtr owner, string title, string description, string[] extensions,
                                   string initialDirectory, bool save, out string error, string suggestedName = null)
        {
            error = null;
            IntPtr buffer = IntPtr.Zero;
            IntPtr titleBuffer = IntPtr.Zero;
            try
            {
                const int capacity = 4096;
                buffer = Marshal.AllocHGlobal(capacity * 2);
                for (int i = 0; i < capacity * 2; i++) Marshal.WriteByte(buffer, i, 0);
                if (!String.IsNullOrEmpty(suggestedName))
                {
                    byte[] seed = Encoding.Unicode.GetBytes(suggestedName);
                    int copy = Math.Min(seed.Length, capacity * 2 - 2);
                    Marshal.Copy(seed, 0, buffer, copy);
                }

                OpenFileName ofn = new OpenFileName();
                ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
                ofn.hwndOwner = owner;
                ofn.lpstrFilter = BuildFilter(description, extensions);
                ofn.nFilterIndex = 1;
                ofn.lpstrFile = buffer;
                ofn.nMaxFile = capacity;
                ofn.lpstrInitialDir = String.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory)
                    ? null : initialDirectory;
                ofn.lpstrTitle = title;
                ofn.Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY;
                if (save) ofn.Flags |= OFN_OVERWRITEPROMPT;
                else ofn.Flags |= OFN_FILEMUSTEXIST;

                if (extensions != null && extensions.Length > 0)
                {
                    string first = extensions[0];
                    if (!String.IsNullOrEmpty(first))
                    {
                        ofn.lpstrDefExt = first[0] == '.' ? first.Substring(1) : first;
                    }
                }

                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                if (!ok)
                {
                    int code = Marshal.GetLastWin32Error();
                    // 0x7177 = FNERR_INVALIDFILENAME；用户取消时 code 通常为 0
                    if (code != 0) error = "文件对话框返回错误码 " + code;
                    return null;
                }

                string path = Marshal.PtrToStringUni(buffer);
                if (String.IsNullOrEmpty(path)) return null;
                if (!File.Exists(path) && !save)
                {
                    error = "选择的文件不存在：" + path;
                    return null;
                }
                return path;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (titleBuffer != IntPtr.Zero) Marshal.FreeHGlobal(titleBuffer);
            }
        }

        /// <summary>弹出"选择文件夹"对话框（老式树形选择器，但提权下同样可用）。</summary>
        public static string PickFolder(IntPtr owner, string title, out string error)
        {
            error = null;
            IntPtr pidl = IntPtr.Zero;
            IntPtr display = IntPtr.Zero;
            try
            {
                const int displayChars = 512;
                display = Marshal.AllocHGlobal(displayChars * 2);
                for (int i = 0; i < displayChars * 2; i++) Marshal.WriteByte(display, i, 0);

                BrowseInfo bi = new BrowseInfo();
                bi.hwndOwner = owner;
                bi.pszDisplayName = display;
                bi.lpszTitle = String.IsNullOrEmpty(title) ? "选择目录" : title;
                bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

                pidl = SHBrowseForFolderW(ref bi);
                if (pidl == IntPtr.Zero) return null;

                StringBuilder path = new StringBuilder(1024);
                if (!SHGetPathFromIDListW(pidl, path))
                {
                    error = "无法把所选项目解析为文件系统路径。";
                    return null;
                }
                string result = path.ToString();
                return String.IsNullOrEmpty(result) ? null : result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
                if (display != IntPtr.Zero) Marshal.FreeHGlobal(display);
            }
        }
    }
}
