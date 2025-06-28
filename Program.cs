using small_window2;
using System.Diagnostics;
using System.Runtime;

static class Program
{
    const string MUTEX_NAME = @"Global\small_window2Mutex";
    const string WINDOW_CLASS = "small_window2Wnd";   // ��ע������ش�����

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(false, MUTEX_NAME);
        bool owns = false;

        try
        {
            owns = mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)   // ��һʵ������
        {
            owns = true;
        }

        //��ֹ�࿪
        if (!owns)
        {
            // �� δ�� -> Ping ������
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
                    // ��ʵ������ �� ����˳�
                    MyWin32.SetForegroundWindow(hwnd);
                    return;
                }
            }

            // �� ����������Ӧ �� ǿ��ɱ���ɽ���
            foreach (var p in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
            {
                if (p.Id != Environment.ProcessId &&
                    p.MainModule?.FileName == Environment.ProcessPath)
                {
                    try { p.Kill(); p.WaitForExit(2000); }
                    catch { /* ����Ȩ�޴��� */ }
                }
            }

            // �� ���¶�������� 2 �룩
            try { owns = mutex.WaitOne(2000, false); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)   // ���ò�����˵��Ȩ��/ϵͳ��ԭ��
            {
                MessageBox.Show("�������ʵ�����޷��رգ����ֶ��������̡�",
                                "FocusMask", MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                return;
            }
        }
        // �� ��� JIT Ԥ��
        ProfileOptimization.SetProfileRoot(AppContext.BaseDirectory);
        ProfileOptimization.StartProfile("winforms.jitprofile");

        // ---- ��ʵ����ʽ���� ----
        ApplicationConfiguration.Initialize();
        var mask = new MaskWindow(null, null);
        
        mask.Show();
        Application.Run();
    }
}