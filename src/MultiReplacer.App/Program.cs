using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MultiReplacer.Core;

namespace MultiReplacer.App
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 提权实例：只做文件操作，不启动界面。
            if (args != null && args.Length >= 2 && String.Equals(args[0], ElevationService.ElevatedArgument, StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = ElevationService.RunElevatedRequest(args[1]);
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(delegate(ApplicationInitializationCallbackParams p)
            {
                DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));
                new App();
            });
        }
    }
}
