﻿
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;




namespace small_window2
{
    /// <summary>最小 Win32 P/Invoke 封装，只声明示例用到的部分。</summary>
    internal static class MyWin32
    {
        // ---------- 常量 ----------
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_POPUP = unchecked((int)0x80000000);   
        // Win32.cs ── 加到 structs / 常量区下面就行
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;

        // Add the following constant definition to resolve the CS0103 error.  
        internal const int HTNOWHERE = 0; // Represents a hit test result indicating no part of the window was hit.  

        internal const int HTTOPLEFT = 13;
        internal const int HTTOPRIGHT = 14;
        internal const int HTBOTTOMLEFT = 16;
        internal const int HTBOTTOMRIGHT = 17;
        internal const int HTTOP = 12;
        internal const int HTBOTTOM = 15;
        internal const int HTLEFT = 10;
        internal const int HTRIGHT = 11;

        /* ----- 系统光标 ID -----  */
        internal const int IDC_ARROW = 32512;
        internal const int IDC_IBEAM = 32513;
        internal const int IDC_CROSS = 32515;
        internal const int IDC_SIZEALL = 32646;
        internal const int IDC_SIZENWSE = 32642;
        internal const int IDC_SIZENESW = 32643;
        internal const int IDC_SIZEWE = 32644;
        internal const int IDC_SIZENS = 32645;

        /* ----- P/Invoke ----- */
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr LoadCursor(IntPtr hInst, int cursorId);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetCursor(IntPtr hCursor);


        internal const int WM_NCLBUTTONDOWN = 0x00A1;   // 或用 0xA1


        internal static Point GetCursorPosScreen()
        {
            MyWin32.GetCursorPos(out var p); return new Point(p.X, p.Y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int GET_WHEEL_DELTA(IntPtr wParam)
        {
            // 取 wParam 的高 16 位再强转成有符号 short
            return (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        }

        // ★★ 在常量区追加 ↓ ★★
        internal const int WS_SIZEBOX = 0x00040000;   // == WS_THICKFRAME
        internal const int HTCAPTION = 2;
        internal const uint SWP_SHOWWINDOW = 0x0040;       // 后面备用

        internal const int WM_ENTERSIZEMOVE = 0x0231;
        internal const int WM_EXITSIZEMOVE = 0x0232;

        internal const int WM_MOUSEWHEEL = 0x020A;
        internal static int GET_WHEEL_DELTA(int wp) => (short)(wp >> 16);

        // ★★ 追加 GetWindowRect 与 RECT ★★
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);

        // ★★ 如果前面没写 SetWindowPos，请保留 ★★
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
        //按键
        internal const int VK_ESCAPE = 0x1B;
        internal const int MOD_NONE = 0;          // RegisterHotKey 无修饰
        internal const int HOTKEY_ID_ESC = 1;      // 随便取，只要全程唯一即可

        internal const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---------- 鼠标相关 ----------
        internal const int HTCLIENT = 1;
        internal const int HTTRANSPARENT = -1;
        // ---------- 透明窗口相关 ----------
        internal const byte AC_SRC_OVER = 0;
        internal const byte AC_SRC_ALPHA = 1;
        internal const int ULW_ALPHA = 0x02;
        // ---------- 窗口显示相关 ----------
        internal const int SW_SHOWNOACTIVATE = 4;

        internal enum WindowMessage : uint
        {
            // ────────── 布局 / 生命周期 ──────────
            /// 0x0084 –【非客户区命中测试】  
            /// 鼠标移动 / 点击前系统先发此消息，让窗口返回
            /// HTCLIENT、HTLEFT、HTCAPTION … 等命中码。
            /// 该返回值同时决定接下来收到的是 “客户区” 还是 “非客户区” 鼠标消息，
            /// 也决定系统自动切换的光标形状。
            WM_NCHITTEST = 0x0084,
            WM_NCLBUTTONDOWN = 0x00A1,

            /// 0x0082 –【非客户区销毁】  
            /// 在 WM_DESTROY 之后、窗口句柄被真正释放之前发送，  
            /// 适合做最终清理；之后不会再收到任何消息。
            WM_NCDESTROY = 0x0082,

            // ────────── 客户区鼠标 ──────────
            /// 0x0200 – 鼠标在客户区移动
            WM_MOUSEMOVE = 0x0200,

            /// 0x0201 – 客户区左键按下
            WM_LBUTTONDOWN = 0x0201,

            /// 0x0202 – 客户区左键抬起
            WM_LBUTTONUP = 0x0202,

            /// 0x0203 – 客户区左键双击（按下-抬起-按下，且两次间隔 < DoubleClickTime）
            WM_LBUTTONDBLCLK = 0x0203,

            // ────────── 非客户区鼠标 ──────────
            /// 0x00A3 – 非客户区左键双击（标题栏、窗口边框、滚动条等）
            WM_NCLBUTTONDBLCLK = 0x00A3,

            // ────────── 显示 / DPI 变化 ──────────
            /// 0x007E – 【显示配置改变】  
            /// 主屏分辨率、颜色深度、旋转或显示器拓扑发生变化时发送。  
            /// 处理后通常要重新布局/重绘全屏窗口。
            WM_DISPLAYCHANGE = 0x007E,

            /// 0x02E0 – 【每显示器 DPI 改变】(Win 8.1+)  
            /// HIWORD(wParam)=Y DPI，LOWORD(wParam)=X DPI；  
            /// lParam 指向系统建议的新窗口矩形（屏幕坐标）。
            WM_DPICHANGED = 0x02E0,
            WM_RBUTTONDOWN = 0x0204,
            WM_RBUTTONUP = 0x0205,

        }
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int nExitCode);
        // ---------- structs ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE { public int cx, cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        // ---------- P/Invoke ----------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr CreateWindowEx(int exStyle, string lpClassName, string? lpWindowName,
            int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr lpParam);

        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        /// <summary>
        /// Updates the position, size, shape, content, and translucency of a layered window
        ///更新分层窗口的位置，大小，形状，内容和半透明
        /// </summary>
        /// <param name="hwnd"></param>
        /// <param name="hdcDst"></param>
        /// <param name="pptDst"></param>
        /// <param name="psize"></param>
        /// <param name="hdcSrc"></param>
        /// <param name="pptSrc"></param>
        /// <param name="crKey"></param>
        /// <param name="pblend"></param>
        /// <param name="dwFlags"></param>
        /// <returns></returns>
        [DllImport("user32.dll")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        /// <summary>
        /// 获取指定窗口（或整个屏幕）的设备环境（HDC）。
        /// </summary>
        /// <param name="hWnd">
        /// 窗口句柄。  
        /// 传入 <see cref="IntPtr.Zero"/> 可取得整屏幕的 DC。
        /// </param>
        /// <returns>
        /// 设备环境句柄 (HDC)。失败时返回 <see cref="IntPtr.Zero"/>。  
        /// **必须**配合 <see cref="ReleaseDC"/> 释放。
        /// </returns>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        /// <summary>
        /// 释放通过 <see cref="GetDC"/> 获取的设备环境。
        /// </summary>
        /// <param name="hWnd">
        /// 与 <see cref="GetDC"/> 调用时相同的窗口句柄。
        /// </param>
        /// <param name="hDC">要释放的设备环境句柄。</param>
        /// <returns>
        /// 成功返回 1；失败返回 0（可用 <c>Marshal.GetLastWin32Error()</c> 了解错误）。
        /// </returns>
        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        /// <summary>
        /// 将鼠标捕获设置到指定窗口，使其接收所有鼠标输入。
        /// </summary>
        /// <param name="hWnd">目标窗口句柄。</param>
        /// <remarks>
        /// 调用 <see cref="ReleaseCapture"/> 可取消捕获。
        /// </remarks>
        [DllImport("user32.dll")]
        internal static extern void SetCapture(IntPtr hWnd);

        /// <summary>
        /// 释放鼠标捕获，让系统重新将鼠标输入按默认规则分发。
        /// </summary>
        /// <returns><c>true</c> 表示成功；失败返回 <c>false</c>。</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();


        /// <summary>
        /// 创建与指定 HDC 兼容的内存 DC（Memory Device Context）。
        /// </summary>
        /// <param name="hdc">
        /// 参考设备环境句柄。  
        /// 传入 <see cref="IntPtr.Zero"/> 表示与当前屏幕兼容。
        /// </param>
        /// <returns>
        /// 新建的内存 DC。使用完毕后应调用 <see cref="DeleteDC"/> 释放。
        /// </returns>
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        /// <summary>
        /// 删除（释放）内存 DC 及其选入的任何 GDI 对象。
        /// </summary>
        /// <param name="hdc">要删除的 DC 句柄。</param>
        /// <returns><c>true</c> 表示删除成功；否则为 <c>false</c>。</returns>
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr hdc);

        /// <summary>
        /// 将 GDI 对象（位图、画笔、字体等）选入指定 DC，并返回原对象句柄。
        /// </summary>
        /// <param name="hdc">目标设备环境句柄。</param>
        /// <param name="h">要选入的 GDI 对象句柄。</param>
        /// <returns>
        /// 之前在 DC 中的对象句柄。  
        /// 结束绘制后应先用该返回值重新选回旧对象，再对新对象调用 <see cref="DeleteObject"/> 释放。
        /// </returns>
        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        /// <summary>
        /// 删除指定的 GDI 对象（位图、画笔、字体、区域等），回收 GDI 资源。
        /// </summary>
        /// <param name="ho">要删除的 GDI 对象句柄。</param>
        /// <returns><c>true</c> 表示删除成功；否则为 <c>false</c>。</returns>
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr ho);


        // ---------- Helper ----------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static System.Drawing.Point GetCursorPosScreen(IntPtr lParam) =>
            new((short)(lParam.ToInt32() & 0xFFFF), (short)(lParam.ToInt32() >> 16));

        internal const int WM_SYSCOMMAND = 0x0112;
        internal const int SC_MOVE = 0xF010;
        internal const int SC_SIZE = 0xF000;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(
        IntPtr hWnd,
        WindowLongFlags nIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(
            IntPtr hWnd,
            WindowLongFlags nIndex,
            IntPtr dwNewLong);
        internal enum WindowLongFlags : int
        {
            GWL_EXSTYLE = -20,
            GWL_STYLE = -16,
            // 其他索引按需添加…
        }
        /// <summary>返回当前拥有鼠标捕获的窗口句柄；若无捕获则返回 NULL。</summary>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetCapture();
        // Win32.cs  (追加到文件任意位置都行)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Point GetCursorPos() { MyWin32.GetCursorPos(out var p); return new Point(p.X, p.Y); }

        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        /// <summary>
        /// 把 mouse 消息里的 lParam 转成**屏幕坐标**。
        /// 对 NCHITTEST 直接用 lParam；对客户端消息先调用 ClientToScreen。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Point LParamToScreen(IntPtr hWnd, WindowMessage msg, IntPtr lParam)
        {
            // 1. 先把 lParam 拆成短整型
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)(lParam.ToInt64() >> 16);

            // 2. 只要是 “非客户区” 消息，lParam 就已经是屏幕坐标
            if ((int)msg >= 0xA0 && (int)msg <= 0xAF)   // WM_NC… 系列
                return new Point(x, y);

            if (msg == WindowMessage.WM_NCHITTEST)
                return new Point(x, y);                  // 也是屏幕坐标

            // 3. 其余情况按客户区处理
            POINT pt = new POINT(x, y);
            ClientToScreen(hWnd, ref pt);
            return new Point(pt.X, pt.Y);
        }
        #region FindWindow

        /// <summary>
        /// 根据窗口类名和（可选的）窗口标题查找顶层窗口。
        /// 任何参数可设 null 表示“任意匹配”。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        #endregion

        #region SendMessageTimeout

        internal const int WM_NULL = 0x0000;

        /// <summary>
        /// SendMessageTimeout flags (SMTO_*) for lpdwResult = SendMessageTimeout()
        /// </summary>
        internal const uint SMTO_NORMAL = 0x0000;
        internal const uint SMTO_BLOCK = 0x0001;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;
        internal const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;

        /// <summary>
        /// 向指定窗口发送消息，并在超时前等待回应。
        /// 常用于探测窗口是否无响应（挂死）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,       // 取 SMTO_xxx 组合
            uint uTimeout,      // 超时毫秒
            out IntPtr lpdwResult // 返回值，可忽略
        );
        #region SetForegroundWindow / ShowWindow / IsIconic

        /// <summary>
        /// 将指定窗口设为前台并尝试恢复到正常显示状态（若最小化则还原）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        internal const int SW_RESTORE = 9;   // 如果最小化/最大化则还原
        internal const int SW_SHOW = 5;   // 否则保持现状显示


        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsIconic(IntPtr hWnd);     // 判断窗口是否最小化

        #endregion

        #endregion
        internal static class Helpers
        {
            /// <summary>
            /// 依据新旧屏幕大小，把旧 ROI 按比例映射到新屏幕。
            /// </summary>
            internal static Rectangle ScaleRoi(Rectangle oldRoi,
                                               Rectangle oldScreen,
                                               Rectangle newScreen)
            {
                // 1. 计算在旧屏幕中的比例（double 可避免整数截断）
                double xRatio = (double)oldRoi.X / oldScreen.Width;
                double yRatio = (double)oldRoi.Y / oldScreen.Height;
                double wRatio = (double)oldRoi.Width / oldScreen.Width;
                double hRatio = (double)oldRoi.Height / oldScreen.Height;

                // 2. 按比例投射到新屏幕并取整
                int newX = (int)Math.Round(newScreen.Width * xRatio);
                int newY = (int)Math.Round(newScreen.Height * yRatio);
                int newW = (int)Math.Round(newScreen.Width * wRatio);
                int newH = (int)Math.Round(newScreen.Height * hRatio);

                // 3. 防止越界：确保 ROI 仍留在屏幕里
                if (newX + newW > newScreen.Width) newX = newScreen.Width - newW;
                if (newY + newH > newScreen.Height) newY = newScreen.Height - newH;

                return new Rectangle(newX, newY, newW, newH);
            }
        }
    }

}