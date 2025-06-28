using small_window2;
using System.Diagnostics;
using System.Runtime;

static class Program
{
    const string MUTEX_NAME = @"Global\small_window2Mutex";
    const string WINDOW_CLASS = "small_window2Wnd";   // 你注册的隐藏窗口类

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(false, MUTEX_NAME);
        bool owns = false;

        try
        {
            owns = mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)   // 上一实例崩溃
        {
            owns = true;
        }

        //防止多开
        if (!owns)
        {
            // ① 未获 -> Ping 主窗口
            IntPtr hwnd = MyWin32.FindWindow(WINDOW_CLASS, null);
            if (hwnd != IntPtr.Zero)
            {
                const uint SMTO_ABORTIFHUNG = 0x0002;
                IntPtr ping = IntPtr.Zero;
                var ok = MyWin32.SendMessageTimeout(
                    hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 100 /*ms*/, out ping);

                if (ok != IntPtr.Zero)
                {
                    // 旧实例正常 → 激活并退出
                    MyWin32.SetForegroundWindow(hwnd);
                    return;
                }
            }

            // ② 主窗口无响应 → 强制杀死旧进程
            foreach (var p in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
            {
                if (p.Id != Environment.ProcessId &&
                    p.MainModule?.FileName == Environment.ProcessPath)
                {
                    try { p.Kill(); p.WaitForExit(2000); }
                    catch { /* 忽略权限错误 */ }
                }
            }

            // ③ 重新夺锁（最多 2 秒）
            try { owns = mutex.WaitOne(2000, false); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)   // 仍拿不到，说明权限/系统级原因
            {
                MessageBox.Show("软件已有实例且无法关闭，请手动结束进程。",
                                "FocusMask", MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                return;
            }
        }
        // ① 多核 JIT 预热
        ProfileOptimization.SetProfileRoot(AppContext.BaseDirectory);
        ProfileOptimization.StartProfile("winforms.jitprofile");

        // ---- 单实例正式启动 ----
        ApplicationConfiguration.Initialize();
        var mask = new MaskWindow(null, null);
        
        mask.Show();
        Application.Run();
    }
}