using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 可行性验证：用 Win32 资源更新 API 替换 PE 里的命名 PNG 资源。
/// 全程在 %TEMP% 的副本上操作，不触碰真实安装。
/// </summary>
internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResourceW(string fileName, bool deleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateResourceW(IntPtr hUpdate, string lpType, string lpName,
                                               ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr hUpdate, bool discard);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResourceW(IntPtr module, string name, string type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resourceData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string source = @"C:\Program Files\Adobe\Adobe After Effects 2026\Support Files\AfterFXLib.dll";
        string work = Path.Combine(Path.GetTempPath(), "mr-rtest");
        Directory.CreateDirectory(work);
        string dll = Path.Combine(work, "AfterFXLib.dll");

        if (!File.Exists(source)) { Console.WriteLine("找不到源 DLL"); return; }

        Stopwatch watch = Stopwatch.StartNew();
        File.Copy(source, dll, true);
        Console.WriteLine("复制 49MB DLL 用时 " + watch.ElapsedMilliseconds + " ms");

        string small = Path.Combine(Path.GetTempPath(), "mr_small.png");
        string big = Path.Combine(Path.GetTempPath(), "mr_big.png");

        Console.WriteLine("替换前 AE_SPLASH 大小: " + ReadResourceSize(dll, "AE_SPLASH"));
        Console.WriteLine("替换前 AE_SPLASH_AT_2X 大小: " + ReadResourceSize(dll, "AE_SPLASH_AT_2X"));

        // 1) 用小图替换（变小）
        bool ok = Replace(dll, "AE_SPLASH", 1033, File.ReadAllBytes(small), out string err);
        Console.WriteLine("小图替换 AE_SPLASH: " + ok + (ok ? "" : "  " + err));
        Console.WriteLine("替换后 AE_SPLASH 大小: " + ReadResourceSize(dll, "AE_SPLASH"));

        // 2) 用大图替换（变大，2.1MB > 原 378KB）
        ok = Replace(dll, "AE_SPLASH_AT_2X", 1033, File.ReadAllBytes(big), out err);
        Console.WriteLine("大图替换 AE_SPLASH_AT_2X: " + ok + (ok ? "" : "  " + err));
        Console.WriteLine("替换后 AE_SPLASH_AT_2X 大小: " + ReadResourceSize(dll, "AE_SPLASH_AT_2X"));

        // 3) 校验其余资源是否还在（抽查 BETA 与 XML）
        Console.WriteLine("AE_BETA_SPLASH 仍在: " + (ReadResourceSize(dll, "AE_BETA_SPLASH") > 0));
        Console.WriteLine("XML AE_SPLASH 仍在: " + ResourceExists(dll, "AE_SPLASH", "XML"));

        // 4) 文件是否还能作为 PE 映像被系统加载为数据文件
        IntPtr module = LoadLibraryExW(dll, IntPtr.Zero, 0x00000002); // LOAD_LIBRARY_AS_DATAFILE
        Console.WriteLine("LoadLibraryEx(AS_DATAFILE) 成功: " + (module != IntPtr.Zero) +
                          (module == IntPtr.Zero ? "  err=" + Marshal.GetLastWin32Error() : ""));
        if (module != IntPtr.Zero) FreeLibrary(module);

        FileInfo info = new FileInfo(dll);
        Console.WriteLine("替换后文件大小: " + info.Length.ToString("N0"));

        // 清理
        try { Directory.Delete(work, true); Console.WriteLine("临时副本已清理"); } catch { }
    }

    private static bool Replace(string dll, string name, ushort lang, byte[] data, out string error)
    {
        error = null;
        IntPtr handle = BeginUpdateResourceW(dll, false);
        if (handle == IntPtr.Zero)
        {
            error = "BeginUpdateResource 失败 err=" + Marshal.GetLastWin32Error();
            return false;
        }
        bool updated = UpdateResourceW(handle, "PNG", name, lang, data, (uint)data.Length);
        int updateError = Marshal.GetLastWin32Error();
        bool ended = EndUpdateResourceW(handle, false);
        if (!updated) { error = "UpdateResource 失败 err=" + updateError; return false; }
        if (!ended) { error = "EndUpdateResource 失败 err=" + Marshal.GetLastWin32Error(); return false; }
        return true;
    }

    private static long ReadResourceSize(string dll, string name)
    {
        IntPtr module = LoadLibraryExW(dll, IntPtr.Zero, 0x00000002);
        if (module == IntPtr.Zero) return -1;
        try
        {
            IntPtr res = FindResourceW(module, name, "PNG");
            if (res == IntPtr.Zero) return 0;
            return SizeofResource(module, res);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static bool ResourceExists(string dll, string name, string type)
    {
        IntPtr module = LoadLibraryExW(dll, IntPtr.Zero, 0x00000002);
        if (module == IntPtr.Zero) return false;
        try
        {
            return FindResourceW(module, name, type) != IntPtr.Zero;
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
