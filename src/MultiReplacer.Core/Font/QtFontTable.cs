using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MultiReplacer.Core
{
    /// <summary>
    /// Qt 平台插件 qwindows.dll 内字体回退表的分析结果。
    /// 表结构形如：Arial \0 MS UI Gothic \0 Gulim \0 SimSun \0 PMingLiU \0 Arial Unicode MS \0
    /// </summary>
    public sealed class QtFontSlot
    {
        public string DllPath;
        public bool FileExists;
        public bool TableFound;
        public bool Writable;
        public int SlotOffset = -1;
        public int SlotLength;
        public int AnchorOffset = -1;
        public string CurrentFont;
        public string OriginalFont;
        public string Message;
        public string CurrentSha256;
        public List<string> NearbyFonts = new List<string>();

        public string SlotOffsetHex { get { return SlotOffset < 0 ? "-" : "0x" + SlotOffset.ToString("X", CultureInfo.InvariantCulture); } }
        public int MaxFontBytes { get { return SlotLength > 1 ? SlotLength - 1 : 0; } }
    }

    public sealed class QtFontApplyResult
    {
        public string DllPath;
        public string BackupPath;
        public string FontName;
        public int SlotOffset;
        public int SlotLength;
        public string BeforeSha256;
        public string AfterSha256;
        public bool BackupCreated;
        public string Message;
    }

    /// <summary>
    /// 达芬奇 UI 字体替换：原地覆盖 qwindows.dll 中 Qt 字体回退表的固定槽位。
    /// 关键约束：写入长度必须小于等于槽位长度，文件总大小与 PE 结构保持不变。
    /// </summary>
    public static class QtFontTable
    {
        public const string DefaultFont = "Microsoft YaHei UI";
        public const string LegacyFont = "SimSun";
        public const string AnchorFont = "Arial Unicode MS";

        // 原版表中 SimSun 之后紧跟 PMingLiU，这段空隙是安全可写的。
        private const string SecondaryFont = "PMingLiU";
        private const int MaxSearchBeforeAnchor = 96;

        public static bool IsAsciiFontName(string fontName)
        {
            if (String.IsNullOrEmpty(fontName)) return false;
            for (int i = 0; i < fontName.Length; i++)
            {
                char c = fontName[i];
                if (c < 32 || c > 126) return false;
            }
            return true;
        }

        public static QtFontSlot Analyze(string dllPath)
        {
            QtFontSlot slot = new QtFontSlot();
            slot.DllPath = dllPath;
            slot.OriginalFont = LegacyFont;

            if (String.IsNullOrEmpty(dllPath))
            {
                slot.Message = "未指定 qwindows.dll 路径。";
                return slot;
            }
            if (!File.Exists(dllPath))
            {
                slot.Message = "未找到 qwindows.dll，请先确认 DaVinci Resolve 安装路径。";
                return slot;
            }

            slot.FileExists = true;
            byte[] data = File.ReadAllBytes(dllPath);
            slot.CurrentSha256 = HashTools.Sha256Bytes(data);

            int anchor = IndexOf(data, Encoding.ASCII.GetBytes(AnchorFont), 0);
            if (anchor < 0)
            {
                slot.Message = "未找到 Qt 字体回退表锚点（Arial Unicode MS），该文件可能不是受支持的 qwindows.dll。";
                return slot;
            }

            slot.TableFound = true;
            slot.AnchorOffset = anchor;
            int searchStart = Math.Max(0, anchor - MaxSearchBeforeAnchor);

            slot.NearbyFonts = ReadFontsBefore(data, searchStart, anchor);

            // 原版表中 SimSun 与锚点之间还有 PMingLiU 和若干 null 填充，
            // 因此先按已知的原始字体定位，再退回到"锚点前最后一段 ASCII"。
            int start = LastIndexOf(data, Encoding.ASCII.GetBytes(LegacyFont), searchStart, anchor - searchStart);
            string current;
            if (start >= 0)
            {
                current = LegacyFont;
            }
            else
            {
                current = ReadLastAsciiRunBefore(data, searchStart, anchor, out start);
            }

            if (String.IsNullOrEmpty(current) || start < 0)
            {
                slot.Message = "字体回退表结构无法识别，已停止以避免破坏文件。";
                return slot;
            }

            slot.SlotOffset = start;
            slot.SlotLength = anchor - start;
            slot.CurrentFont = current;

            bool known = String.Equals(current, LegacyFont, StringComparison.OrdinalIgnoreCase)
                || String.Equals(current, SecondaryFont, StringComparison.OrdinalIgnoreCase);
            slot.Writable = slot.MaxFontBytes >= Encoding.ASCII.GetByteCount(DefaultFont);

            if (String.Equals(current, DefaultFont, StringComparison.OrdinalIgnoreCase))
            {
                slot.Message = "当前已是 Microsoft YaHei UI，无需重复替换。";
            }
            else if (known)
            {
                slot.Message = slot.Writable
                    ? "检测到原始字体槽位（" + current + "），可安全原地替换。"
                    : "字体槽位空间不足，无法在不改变文件大小的前提下替换。";
            }
            else
            {
                slot.Message = "检测到已有自定义字体：" + current + "，可覆盖为其他字体。";
            }
            return slot;
        }

        public static QtFontApplyResult Apply(string dllPath, string backupPath, string fontName, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(fontName)) fontName = DefaultFont;
            fontName = fontName.Trim();

            if (!IsAsciiFontName(fontName))
            {
                error = "字体名必须是 ASCII 字符。Qt 二进制表中保存的是英文字体族名，例如 Microsoft YaHei UI。";
                return null;
            }
            if (Patcher.IsProcessRunning("Resolve"))
            {
                error = "DaVinci Resolve 正在运行，请完全退出后再替换字体。";
                return null;
            }

            QtFontSlot slot = Analyze(dllPath);
            if (!slot.FileExists)
            {
                error = slot.Message;
                return null;
            }
            if (!slot.TableFound || slot.SlotOffset < 0)
            {
                error = slot.Message;
                return null;
            }

            byte[] fontBytes = Encoding.ASCII.GetBytes(fontName);
            if (fontBytes.Length + 1 > slot.SlotLength)
            {
                error = "字体名过长。当前槽位可用 " + slot.SlotLength.ToString(CultureInfo.InvariantCulture)
                    + " 字节，字体名最多 " + slot.MaxFontBytes.ToString(CultureInfo.InvariantCulture) + " 字节。";
                return null;
            }

            byte[] data = File.ReadAllBytes(dllPath);
            if (String.IsNullOrEmpty(backupPath)) backupPath = dllPath + ".font.original.bak";

            QtFontApplyResult result = new QtFontApplyResult();
            result.DllPath = dllPath;
            result.BackupPath = backupPath;
            result.FontName = fontName;
            result.SlotOffset = slot.SlotOffset;
            result.SlotLength = slot.SlotLength;
            result.BeforeSha256 = HashTools.Sha256Bytes(data);

            string backupDir = Path.GetDirectoryName(backupPath);
            if (!String.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
            if (!File.Exists(backupPath))
            {
                File.Copy(dllPath, backupPath, false);
                result.BackupCreated = true;
            }

            byte[] patched = new byte[data.Length];
            Buffer.BlockCopy(data, 0, patched, 0, data.Length);
            for (int i = 0; i < slot.SlotLength; i++) patched[slot.SlotOffset + i] = 0;
            Buffer.BlockCopy(fontBytes, 0, patched, slot.SlotOffset, fontBytes.Length);

            string temp = dllPath + ".fontpatch.tmp";
            try
            {
                File.WriteAllBytes(temp, patched);
                File.Replace(temp, dllPath, null);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            byte[] verify = File.ReadAllBytes(dllPath);
            if (verify.Length != data.Length)
            {
                error = "写回后文件长度发生变化，已中止。请使用备份恢复。";
                return null;
            }
            for (int i = 0; i < fontBytes.Length; i++)
            {
                if (verify[slot.SlotOffset + i] != fontBytes[i])
                {
                    error = "写回后的字体槽位校验失败。请使用备份恢复。";
                    return null;
                }
            }
            int terminator = slot.SlotOffset + fontBytes.Length;
            if (terminator < verify.Length && verify[terminator] != 0)
            {
                error = "写回后的字体名缺少终止符。请使用备份恢复。";
                return null;
            }

            result.AfterSha256 = HashTools.Sha256Bytes(verify);
            result.Message = "已将 UI 字体替换为 " + fontName + "，重启 DaVinci Resolve 后生效。";
            return result;
        }

        public static bool Restore(string dllPath, string backupPath, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
            {
                error = "未找到 qwindows.dll。";
                return false;
            }
            if (String.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            {
                error = "未找到字体备份文件：" + backupPath;
                return false;
            }
            if (Patcher.IsProcessRunning("Resolve"))
            {
                error = "DaVinci Resolve 正在运行，请完全退出后再恢复字体。";
                return false;
            }

            string temp = dllPath + ".fontrestore.tmp";
            try
            {
                File.Copy(backupPath, temp, true);
                File.Replace(temp, dllPath, null);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static List<string> ReadFontsBefore(byte[] data, int start, int end)
        {
            List<string> fonts = new List<string>();
            int pos = end - 1;
            while (pos > start && fonts.Count < 8)
            {
                while (pos > start && data[pos] == 0) pos--;
                int runEnd = pos + 1;
                while (pos > start && data[pos] >= 32 && data[pos] <= 126) pos--;
                int runStart = pos + 1;
                if (runEnd <= runStart) break;
                fonts.Insert(0, Encoding.ASCII.GetString(data, runStart, runEnd - runStart));
                if (pos == runStart - 1) pos--;
            }
            return fonts;
        }

        private static int LastIndexOf(byte[] data, byte[] pattern, int start, int count)
        {
            if (data == null || pattern == null || pattern.Length == 0 || count <= 0) return -1;
            int begin = Math.Max(0, start);
            int end = Math.Min(data.Length, begin + count);
            for (int i = end - pattern.Length; i >= begin; i--)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static string ReadLastAsciiRunBefore(byte[] data, int start, int end, out int runStart)
        {
            runStart = -1;
            if (data == null || start < 0 || end > data.Length || start >= end) return null;
            int pos = end - 1;
            while (pos >= start && data[pos] == 0) pos--;
            int runEnd = pos + 1;
            while (pos >= start && data[pos] >= 32 && data[pos] <= 126) pos--;
            runStart = pos + 1;
            if (runEnd <= runStart) return null;
            return Encoding.ASCII.GetString(data, runStart, runEnd - runStart);
        }

        public static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            if (data == null || pattern == null || pattern.Length == 0) return -1;
            for (int i = Math.Max(0, start); i <= data.Length - pattern.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
