using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using static Vanara.PInvoke.User32;

namespace small_window
{
    // OverlayWindow.cs  ───────────────────────────────────────────────────
    // OverlayWindow.cs

    /// <summary>
    /// 在全屏绘制 50 % 暗色遮罩，中央 400×300 透明 ROI，可在边缘 ±3 px 拖动移动。<br/>
    /// ROI 内外区域都透传鼠标，不影响其他应用。
    /// </summary>
    internal sealed class OverlayWindow : NativeWindow, IDisposable
    {
        #region 固定参数与字段
        private const int RoiWidth = 800;
        private const int RoiHeight = 400;
        private const int RoiBorder = 30;          // 可拖拽宽度 ± px
        private readonly Rectangle _screen = Screen.PrimaryScreen!.Bounds;

        private Rectangle _roi = Rectangle.Empty;

        private bool _dragging;
        private Point _dragStart;                      // 光标相对 ROI 左上

        // ── Win32 样式 / 消息
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOPMOST = 0x8;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int ULW_ALPHA = 0x2;

        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;
        //const int EXSTYLE_BASE = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
        const int EXSTYLE_BASE = WS_EX_LAYERED | WS_EX_TOPMOST| WS_EX_TRANSPARENT;   // ← 删 WS_EX_TRANSPARENT
        private const byte ALPHA_SEMITRANS = 128;
        #endregion

        #region ctor / Dispose
        public OverlayWindow()
        {
            // 1. 初始化 ROI 在屏幕中央
            _roi = new Rectangle((_screen.Width - RoiWidth) / 2,
                                 (_screen.Height - RoiHeight) / 2,
                                 RoiWidth, RoiHeight);

            // 2. 创建分层全屏窗口
            IntPtr hWnd = CreateWindowEx(
                EXSTYLE_BASE,     
                "STATIC", null, WS_POPUP,
                0, 0, _screen.Width, _screen.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            AssignHandle(hWnd);
            Redraw();
            ShowWindow(hWnd, 1); // SW_SHOWNORMAL
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
            GC.SuppressFinalize(this);
        }
        #endregion

        #region 主要绘制逻辑
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private const int FrameMs = 15;                // 节流 ≈66 FPS


        private void Redraw(bool force = false)
        {
            if (!force && _sw.ElapsedMilliseconds < FrameMs) return;
            _sw.Restart();

            using var bmp = new Bitmap(_screen.Width, _screen.Height,
                                       System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);

            // 1) 先把整个位图清成透明
            g.Clear(Color.Transparent);

            // 2) 用 SourceCopy 覆盖模式绘制“半透明黑遮罩”
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using (var dim = new SolidBrush(Color.FromArgb(ALPHA_SEMITRANS, Color.Black)))
            {
                g.FillRectangle(dim, _screen);
            }

            // 3) 再在 ROI 位置“挖一个透明洞”
            g.FillRectangle(Brushes.Transparent, _roi);

            // 4) 切回默认混合模式，画一圈白色边框
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using (var pen = new Pen(Color.White, 1))
            {
                g.DrawRectangle(pen, _roi);
            }

            // ---- 把位图推送到分层窗口 ----
            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr hOld = SelectObject(hMem, hBmp);

            var size = new SIZE(_screen.Width, _screen.Height);
            var ptSrc = new POINT(0, 0);
            var top = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,    // AC_SRC_OVER
                SourceConstantAlpha = 255,
                AlphaFormat = 1     // AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, hScreen, ref top, ref size,
                                hMem, ref ptSrc, 0, ref blend, ULW_ALPHA);

            // ---- 释放临时 GDI 资源 ----
            SelectObject(hMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }
        #endregion

        #region 消息处理

        private void SetExStyle(int flag)
        {
            int style = GetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE);
            if ((style & flag) == 0)
                SetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE, style | flag);
        }

        private void ClearExStyle(int flag)
        {
            int style = GetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE);
            if ((style & flag) != 0)
                SetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE, style & ~flag);
        }

        protected override void WndProc(ref Message m)
        {
            Debug.WriteLine($"Msg=0x{m.Msg:X}, hitBorder={_hitRoiBorder}, dragging={_dragging}");
            switch ((WindowMessage)m.Msg)
            {
                case WindowMessage.WM_NCHITTEST:
                    var pt = GetCursorPosScreen(m.LParam);
                    bool hit = IsOnRoiBorder(pt);
                    m.Result = (IntPtr)(hit ? HTCLIENT : HTTRANSPARENT);
                    return;                              // 别调 base

                case WindowMessage.WM_MOUSEMOVE:
                    if (_dragging)
                    {
                        //var cur = GetCursorPosScreen(m.LParam);
                        //_roi.Location = new Point(cur.X - _dragStart.X, cur.Y - _dragStart.Y);
                        int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                        int y = (short)(m.LParam.ToInt32() >> 16);
                        _roi.Location = new Point(x - _dragStart.X, y - _dragStart.Y);
                        //Redraw();
                    }
                    return;

                case WindowMessage.WM_LBUTTONDOWN:
                    pt = GetCursorPosScreen(m.LParam);

                    if (IsOnRoiBorder(pt))
                    {
                        _dragging = true;
                        _dragStart = new Point(pt.X - _roi.X, pt.Y - _roi.Y);

                        ClearExStyle(WS_EX_TRANSPARENT); // 临时取消透明，接收后续消息
                        SetCapture(Handle);              // 捕获鼠标
                    }
                    return;



                case WindowMessage.WM_LBUTTONUP:
                    if (_dragging)
                    {
                        _dragging = false;
                        SetExStyle(WS_EX_TRANSPARENT);   // 恢复透明样式
                        ReleaseCapture();                // 释放鼠标捕获
                        Redraw(true);                    // 强制重绘遮罩
                    }
                    return;                              // ★ 别调 base
            }
            base.WndProc(ref m);
        }




        private bool _hitRoiBorder;   // 最近一次 WM_NCHITTEST 是否命中 ROI 边缘

        private void HandleNcHitTest(ref Message m)
        {
            var pt = GetCursorPosScreen(m.LParam);
            bool hitBorder = IsOnRoiBorder(pt);

            m.Result = (IntPtr)(hitBorder ? HTCLIENT : HTTRANSPARENT);
            _hitRoiBorder = hitBorder;   // 用于 LBUTTONDOWN 判断
            return;                      // ❌ 别再调用 base.WndProc
        }

        private static Point GetCursorPosScreen(IntPtr lParam)
    => new((short)(lParam.ToInt32() & 0xFFFF), (short)(lParam.ToInt32() >> 16));



        #endregion

        #region 工具函数

        private bool IsOnRoiBorder(Point pt)
        {
            var inner = Rectangle.Inflate(_roi, -RoiBorder, -RoiBorder);
            return _roi.Contains(pt) && !inner.Contains(pt);
        }
        #endregion

        #region Win32 导入
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(int exStyle, string lpClassName, string? lpWindowName,
            int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr lpParam);

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmd);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
        #endregion
    }

}
