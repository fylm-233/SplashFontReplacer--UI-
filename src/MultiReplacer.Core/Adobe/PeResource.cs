using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiReplacer.Core
{
    /// <summary>
    /// PE 资源树中的一个叶子节点（type / name / language 三级定位）。
    /// </summary>
    public sealed class PeResourceEntry
    {
        public string TypeName;
        public int TypeId;
        public bool TypeIsNamed;

        public string NameName;
        public int NameId;
        public bool NameIsNamed;

        public int LanguageId;
        public long DataOffset;
        public int Size;
        public uint CodePage;

        /// <summary>Resource Hacker 风格的定位串，例如 PNG\AE_SPLASH\1033。</summary>
        public string Path
        {
            get
            {
                return TypeName + "\\" + NameName + "\\" + LanguageId.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string ShortPath
        {
            get { return TypeName + "\\" + NameName; }
        }
    }

    /// <summary>
    /// 只读的 PE 资源枚举器。刻意不复用 PngScanner：
    /// 后者在文件里盲扫 PNG 签名，适合裸嵌资源；这里需要精确的 type/name/lang 三元组，
    /// 因为写回时必须按同一组标识调用 UpdateResource。
    /// </summary>
    public sealed class PeResourceFile
    {
        private struct Section
        {
            public uint VirtualAddress;
            public uint VirtualSize;
            public uint RawPointer;
            public uint RawSize;
        }

        private readonly List<Section> sections = new List<Section>();

        public string FilePath { get; private set; }
        public long FileSize { get; private set; }
        public ushort OptionalMagic { get; private set; }
        public uint StoredChecksum { get; private set; }
        public long ChecksumOffset { get; private set; }
        public bool HasSecurityDirectory { get; private set; }
        public bool ResourceSectionPresent { get; private set; }
        public List<PeResourceEntry> Entries { get; private set; }

        private PeResourceFile()
        {
            Entries = new List<PeResourceEntry>();
        }

        public static PeResourceFile Load(string path, out string error)
        {
            error = null;
            PeResourceFile file = new PeResourceFile();
            try
            {
                file.FilePath = path;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    file.FileSize = fs.Length;
                    if (fs.Length < 0x40) { error = "文件太小，不是有效的 PE。"; return null; }

                    fs.Position = 0x3C;
                    int peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 24 > fs.Length) { error = "e_lfanew 非法。"; return null; }

                    fs.Position = peOffset;
                    if (br.ReadUInt32() != 0x00004550) { error = "缺少 PE 签名。"; return null; }

                    br.ReadUInt16();                       // Machine
                    int sectionCount = br.ReadUInt16();
                    br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                    int optionalSize = br.ReadUInt16();
                    br.ReadUInt16();                       // Characteristics

                    long optionalOffset = fs.Position;
                    file.OptionalMagic = br.ReadUInt16();
                    bool pe32Plus = file.OptionalMagic == 0x20B;
                    if (!pe32Plus && file.OptionalMagic != 0x10B)
                    {
                        error = "未知的可选头 magic: 0x" + file.OptionalMagic.ToString("X4", CultureInfo.InvariantCulture);
                        return null;
                    }

                    file.ChecksumOffset = optionalOffset + 64;
                    fs.Position = file.ChecksumOffset;
                    file.StoredChecksum = br.ReadUInt32();

                    int dataDirOffset = (int)(optionalOffset + (pe32Plus ? 112 : 96));
                    fs.Position = dataDirOffset + 2 * 8;   // DataDirectory[2] = 资源表
                    uint resRva = br.ReadUInt32();
                    uint resSize = br.ReadUInt32();

                    fs.Position = dataDirOffset + 4 * 8;   // DataDirectory[4] = 安全目录
                    uint secOffset = br.ReadUInt32();
                    uint secSize = br.ReadUInt32();
                    file.HasSecurityDirectory = secOffset != 0 && secSize != 0;

                    long sectionOffset = optionalOffset + optionalSize;
                    fs.Position = sectionOffset;
                    for (int i = 0; i < sectionCount; i++)
                    {
                        byte[] name = br.ReadBytes(8);
                        uint virtualSize = br.ReadUInt32();
                        uint virtualAddress = br.ReadUInt32();
                        uint rawSize = br.ReadUInt32();
                        uint rawPointer = br.ReadUInt32();
                        br.ReadBytes(16);
                        Section s;
                        s.VirtualAddress = virtualAddress;
                        s.VirtualSize = virtualSize;
                        s.RawPointer = rawPointer;
                        s.RawSize = rawSize;
                        file.sections.Add(s);
                    }

                    if (resRva == 0 || resSize == 0)
                    {
                        file.ResourceSectionPresent = false;
                        return file;
                    }

                    long resBase = file.RvaToOffset(resRva);
                    if (resBase < 0 || resBase + 16 > fs.Length)
                    {
                        file.ResourceSectionPresent = false;
                        error = null;
                        return file;
                    }

                    file.ResourceSectionPresent = true;
                    file.WalkDirectory(br, fs, resBase, resBase, 0, null, null);
                }
                return file;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private long RvaToOffset(uint rva)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                Section s = sections[i];
                uint span = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span)
                    return s.RawPointer + (long)(rva - s.VirtualAddress);
            }
            return -1;
        }

        private void WalkDirectory(BinaryReader br, FileStream fs, long resourceBase, long dirOffset,
                                   int depth, string typeName, string nameName)
        {
            if (depth > 3) return;
            if (dirOffset + 16 > fs.Length) return;

            fs.Position = dirOffset;
            br.ReadUInt32();                       // Characteristics
            br.ReadUInt32();                       // TimeDateStamp
            br.ReadUInt16();                       // MajorVersion
            br.ReadUInt16();                       // MinorVersion
            int namedCount = br.ReadUInt16();
            int idCount = br.ReadUInt16();
            int total = namedCount + idCount;
            if (total <= 0 || total > 8192) return;

            for (int i = 0; i < total; i++)
            {
                long entryOffset = dirOffset + 16 + i * 8;
                if (entryOffset + 8 > fs.Length) return;
                fs.Position = entryOffset;
                uint nameField = br.ReadUInt32();
                uint dataField = br.ReadUInt32();

                string label = ReadName(br, fs, resourceBase, nameField, out int id);

                if ((dataField & 0x80000000) != 0)
                {
                    long child = resourceBase + (dataField & 0x7FFFFFFF);
                    if (depth == 0) WalkDirectory(br, fs, resourceBase, child, 1, label, null);
                    else if (depth == 1) WalkDirectory(br, fs, resourceBase, child, 2, typeName, label);
                }
                else if (depth == 2)
                {
                    long dataEntry = resourceBase + dataField;
                    if (dataEntry + 16 > fs.Length) continue;
                    fs.Position = dataEntry;
                    uint dataRva = br.ReadUInt32();
                    uint size = br.ReadUInt32();
                    uint codePage = br.ReadUInt32();

                    long offset = RvaToOffset(dataRva);
                    if (offset < 0 || size == 0) continue;

                    PeResourceEntry e = new PeResourceEntry();
                    e.TypeName = typeName ?? "?";
                    e.TypeIsNamed = typeName != null && !typeName.StartsWith("#", StringComparison.Ordinal);
                    e.TypeId = e.TypeIsNamed ? -1 : ParseId(typeName);
                    e.NameName = nameName ?? "?";
                    e.NameIsNamed = nameName != null && !nameName.StartsWith("#", StringComparison.Ordinal);
                    e.NameId = e.NameIsNamed ? -1 : ParseId(nameName);
                    e.LanguageId = id;
                    e.DataOffset = offset;
                    e.Size = (int)Math.Min(size, Int32.MaxValue);
                    e.CodePage = codePage;
                    Entries.Add(e);
                }
            }
        }

        private static int ParseId(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            if (text[0] == '#')
            {
                int value;
                if (Int32.TryParse(text.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            }
            return 0;
        }

        private static string ReadName(BinaryReader br, FileStream fs, long resourceBase, uint field, out int id)
        {
            id = 0;
            if ((field & 0x80000000) == 0)
            {
                id = (int)(field & 0xFFFF);
                return "#" + id.ToString(CultureInfo.InvariantCulture);
            }

            long offset = resourceBase + (field & 0x7FFFFFFF);
            if (offset + 2 > fs.Length) return "#?";
            long back = fs.Position;
            fs.Position = offset;
            int length = br.ReadUInt16();
            if (length > 512 || offset + 2 + length * 2 > fs.Length) { fs.Position = back; return "#?"; }
            byte[] raw = br.ReadBytes(length * 2);
            fs.Position = back;
            return Encoding.Unicode.GetString(raw);
        }

        public byte[] ReadData(PeResourceEntry entry, out string error)
        {
            error = null;
            try
            {
                using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (entry.DataOffset + entry.Size > fs.Length)
                    {
                        error = "资源数据超出文件末尾。";
                        return null;
                    }
                    fs.Position = entry.DataOffset;
                    byte[] buffer = new byte[entry.Size];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = fs.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != buffer.Length)
                    {
                        error = "资源数据读取不完整。";
                        return null;
                    }
                    return buffer;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>读取资源头部少量字节，用于嗅探真实格式而不必整块读入。</summary>
        public static bool ProbeSignature(string path, long offset, int count, byte[] expected)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset + count > fs.Length) return false;
                    fs.Position = offset;
                    byte[] head = new byte[count];
                    if (fs.Read(head, 0, count) != count) return false;
                    for (int i = 0; i < count; i++) if (head[i] != expected[i]) return false;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsPngSignature(byte[] data, int offset)
        {
            if (data == null || offset + 8 > data.Length) return false;
            return data[offset] == 0x89 && data[offset + 1] == 0x50 && data[offset + 2] == 0x4E && data[offset + 3] == 0x47
                && data[offset + 4] == 0x0D && data[offset + 5] == 0x0A && data[offset + 6] == 0x1A && data[offset + 7] == 0x0A;
        }

        /// <summary>从 PNG 的 IHDR 读取宽高，不做完整解码。</summary>
        public static bool TryReadPngSize(byte[] data, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!IsPngSignature(data, 0) || data.Length < 24) return false;
            if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return false;
            width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return width > 0 && height > 0;
        }
    }

    public sealed class ResourceWriteItem
    {
        public PeResourceEntry Entry;
        public byte[] Data;
    }

    /// <summary>
    /// 基于 Win32 资源 API 的写回器。
    /// 与达芬奇模块的「等长原地覆盖」不同：资源是独立的资源项，长度可以自由变化，
    /// 由系统负责重排 .rsrc 节，因此这里不要求替换图与原图字节数一致。
    /// </summary>
    public static class PeResourceWriter
    {
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResourceW(string fileName, bool deleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language,
                                                   byte[] data, uint dataSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResourceW(IntPtr update, bool discard);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        /// <summary>
        /// 在副本上完成资源替换。sourcePath 不会被修改，调用方负责把 outputPath 部署回去。
        /// </summary>
        public static bool ReplaceBatch(string sourcePath, string outputPath, List<ResourceWriteItem> items, out string error)
        {
            error = null;
            if (items == null || items.Count == 0)
            {
                error = "没有需要写入的资源项。";
                return false;
            }

            List<IntPtr> allocations = new List<IntPtr>();
            IntPtr handle = IntPtr.Zero;
            try
            {
                File.Copy(sourcePath, outputPath, true);
                File.SetAttributes(outputPath, File.GetAttributes(outputPath) & ~FileAttributes.ReadOnly);

                handle = BeginUpdateResourceW(outputPath, false);
                if (handle == IntPtr.Zero)
                {
                    error = "BeginUpdateResource 失败，错误码 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    ResourceWriteItem item = items[i];
                    IntPtr typePtr = ToResourceId(item.Entry.TypeIsNamed, item.Entry.TypeName, item.Entry.TypeId, allocations);
                    IntPtr namePtr = ToResourceId(item.Entry.NameIsNamed, item.Entry.NameName, item.Entry.NameId, allocations);

                    bool ok = UpdateResourceW(handle, typePtr, namePtr, (ushort)item.Entry.LanguageId,
                                              item.Data, (uint)item.Data.Length);
                    if (!ok)
                    {
                        int code = Marshal.GetLastWin32Error();
                        EndUpdateResourceW(handle, true);
                        handle = IntPtr.Zero;
                        error = "UpdateResource 失败（" + item.Entry.Path + "），错误码 " + code.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }

                if (!EndUpdateResourceW(handle, false))
                {
                    int code = Marshal.GetLastWin32Error();
                    handle = IntPtr.Zero;
                    error = "EndUpdateResource 失败，错误码 " + code.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                handle = IntPtr.Zero;
                return true;
            }
            catch (Exception ex)
            {
                if (handle != IntPtr.Zero)
                {
                    try { EndUpdateResourceW(handle, true); } catch { }
                }
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                for (int i = 0; i < allocations.Count; i++) Marshal.FreeHGlobal(allocations[i]);
            }
        }

        private static IntPtr ToResourceId(bool isNamed, string name, int id, List<IntPtr> allocations)
        {
            if (isNamed && !String.IsNullOrEmpty(name))
            {
                IntPtr ptr = Marshal.StringToHGlobalUni(name);
                allocations.Add(ptr);
                return ptr;
            }
            return (IntPtr)id;
        }

        /// <summary>写回后自检：重新枚举确认尺寸，并用 LOAD_LIBRARY_AS_DATAFILE 确认 PE 结构未损坏。</summary>
        public static bool Verify(string path, List<ResourceWriteItem> items, out string error, out string detail)
        {
            error = null;
            detail = null;

            PeResourceFile file = PeResourceFile.Load(path, out error);
            if (file == null) return false;

            for (int i = 0; i < items.Count; i++)
            {
                ResourceWriteItem item = items[i];
                PeResourceEntry match = null;
                for (int j = 0; j < file.Entries.Count; j++)
                {
                    PeResourceEntry e = file.Entries[j];
                    if (e.LanguageId == item.Entry.LanguageId
                        && String.Equals(e.TypeName, item.Entry.TypeName, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(e.NameName, item.Entry.NameName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = e;
                        break;
                    }
                }
                if (match == null)
                {
                    error = "写回后找不到资源 " + item.Entry.Path + "。";
                    return false;
                }
                if (match.Size != item.Data.Length)
                {
                    error = String.Format(CultureInfo.InvariantCulture,
                        "写回尺寸不符：{0} 期望 {1} 实际 {2}。", item.Entry.Path, item.Data.Length, match.Size);
                    return false;
                }
            }

            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (module == IntPtr.Zero)
            {
                error = "写回后的文件无法作为数据文件加载，PE 结构可能已损坏。错误码 "
                        + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }
            FreeLibrary(module);

            detail = String.Format(CultureInfo.InvariantCulture,
                "资源 {0} 项写回成功，PE 结构自检通过。", items.Count);
            return true;
        }
    }
}
