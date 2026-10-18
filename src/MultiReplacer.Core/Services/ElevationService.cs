using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MultiReplacer.Core
{
    public sealed class ElevatedRequest
    {
        public string Op { get; set; }
        public string Source { get; set; }
        public string Destination { get; set; }
        public string ResultPath { get; set; }
    }

    public sealed class ElevatedResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 写 Program Files 需要管理员权限。这里用"以管理员身份重启自身 + 请求文件"的方式完成提权，
    /// 主界面不整体提权，避免日常浏览/预览也要求管理员。
    /// </summary>
    public static class ElevationService
    {
        public const string ElevatedArgument = "--elevated";

        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        WindowsPrincipal principal = new WindowsPrincipal(identity);
                        return principal.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool TryElevate(string op, string source, string destination, out string error)
        {
            error = null;
            string exePath = Environment.ProcessPath;
            if (String.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                error = "无法定位当前程序路径，提权操作不可用。";
                return false;
            }

            string requestPath = Path.Combine(Path.GetTempPath(), "MultiReplacer-elevate-" + Guid.NewGuid().ToString("N") + ".json");
            string resultPath = requestPath + ".result";
            ElevatedRequest request = new ElevatedRequest();
            request.Op = op;
            request.Source = source;
            request.Destination = destination;
            request.ResultPath = resultPath;

            try
            {
                File.WriteAllText(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = ElevatedArgument + " \"" + requestPath + "\"";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        error = "提权进程启动失败。";
                        return false;
                    }
                    process.WaitForExit(120000);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                error = "已取消管理员授权，操作未执行。";
                return false;
            }
            catch (Exception ex)
            {
                error = "提权执行失败：" + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
            }

            if (!File.Exists(resultPath))
            {
                error = "提权进程未返回结果，可能被系统拦截。";
                return false;
            }

            try
            {
                ElevatedResponse response = JsonSerializer.Deserialize<ElevatedResponse>(File.ReadAllText(resultPath, Encoding.UTF8));
                if (response == null || !response.Success)
                {
                    error = response == null ? "提权执行结果无法解析。" : response.Error;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "读取提权结果失败：" + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
            }
        }

        /// <summary>
        /// 提权实例的入口。返回进程退出码。
        /// </summary>
        public static int RunElevatedRequest(string requestPath)
        {
            ElevatedResponse response = new ElevatedResponse();
            string resultPath = null;
            try
            {
                string json = File.ReadAllText(requestPath, Encoding.UTF8);
                ElevatedRequest request = JsonSerializer.Deserialize<ElevatedRequest>(json);
                if (request == null) throw new InvalidOperationException("请求内容为空。");
                resultPath = request.ResultPath;

                switch (request.Op)
                {
                    case "copyTree":
                        AdobeDictionary.CopyDirectory(request.Source, request.Destination);
                        break;
                    case "copyFile":
                        string parent = Path.GetDirectoryName(request.Destination);
                        if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.Copy(request.Source, request.Destination, true);
                        break;
                    default:
                        throw new InvalidOperationException("未知的提权操作：" + request.Op);
                }
                response.Success = true;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Error = ex.Message;
            }

            try
            {
                if (!String.IsNullOrEmpty(resultPath))
                {
                    File.WriteAllText(resultPath, JsonSerializer.Serialize(response), new UTF8Encoding(false));
                }
            }
            catch
            {
            }
            return response.Success ? 0 : 1;
        }
    }
}
