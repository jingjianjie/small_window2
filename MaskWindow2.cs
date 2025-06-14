using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using static System.Windows.Forms.VisualStyles.VisualStyleElement.Rebar;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using static small_window2.MyWin32;

//using static Vanara.PInvoke.Kernel32;
//using static Vanara.PInvoke.User32;



namespace small_window2
{
    /// <summary>全屏半透明遮罩，中心 ROI 透明，可被 RoiBorderWindow 通知实时刷新。</summary>  
    internal sealed class MaskWindow : NativeWindow, IDisposable
    {
        //用来拖拽的线
        private enum InitState { InitDraw, Preview, Active }
        private InitState _state = InitState.InitDraw;

        // 用于绘制 / 擦除可逆框
        private Rectangle _rubberLast = Rectangle.Empty;
        private bool _rubberVisible;

        private Point _ptStart;       // InitDraw 拖拽起点
        private Rectangle _roiPreview;    // Preview 模式下临时 ROI
        private const int PREVIEW_BORDER = 3;   // 蓝线宽度

        /* Preview 阶段用来判断正在干什么 */
        private int _previewHT = MyWin32.HTNOWHERE;   // 当前拖动的方向
        private Rectangle _roiStartPreview;              // 拖动起点 ROI
        private Point _ptStartPreview;               // 拖动起点鼠标
        private static readonly int PREVIEW_RESIZE_MARGIN = (int)(40 * GetDpiX()); // 判定“拖边/拖角”的宽度  
        /// <summary>在屏幕上异或方式绘制或擦除一个可逆矩形。</summary>
        /// 
        private static void DrawRubber(Rectangle rc)
        {
            if (rc.IsEmpty) return;
            // 必须确保宽高 ≥ 1，否则 API 不画
            if (rc.Width == 0) rc.Width = 1;
            if (rc.Height == 0) rc.Height = 1;

            // Screen 坐标 → 直接取屏幕 DC
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            ControlPaint.DrawReversibleFrame(rc, Color.CornflowerBlue, FrameStyle.Dashed);
        }

        // Add the following helper method to retrieve the DPI value:
        private static double GetDpiX()
        {
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                return graphics.DpiX / 144.0;
            }
        }


        private const byte ALPHA_DIM = 80;           // 遮罩透明度  
                                                     // MaskWindow ctor 里：先不给 WS_EX_TRANSPARENT
        const int WS_EX_MASK = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;

        private Rectangle _screen;
        private Rectangle _roi;                       // ROI 由外部窗口控制  
        private RoiBorderWindow? _borderWindow;

        public MaskWindow(RoiBorderWindow? border, Rectangle? initialRoi = null)
        {
            var s = GetDpiX();
            _borderWindow = border;                     // ★★
            _screen = Screen.PrimaryScreen!.Bounds;     // ★★


            _state = InitState.InitDraw;  // 初始状态：绘制 ROI


            // 初始无洞，先填满；真正的 _roi 由用户拖出
            _roi = Rectangle.Empty;
            IntPtr hWnd = CreateWindowEx(
                WS_EX_MASK, "STATIC", null, WS_POPUP,
                0, 0, _screen.Width, _screen.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // 设置窗口样式
            AssignHandle(hWnd);
            UpdateLayered_FullDark();     // 只画全屏暗色（函数见下）
        }

        public void Show() => ShowWindow(Handle, SW_SHOWNOACTIVATE);
        private void UpdateLayered_FullDark()
        {
            _roiPreview = Rectangle.Empty;          // 无洞
            using var bmp = new Bitmap(_screen.Width, _screen.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(ALPHA_DIM, Color.Black));
            PushBitmap(bmp);                        // 复用你的位图推送逻辑
        }
        /// <summary>被 RoiBorderWindow 调用，更新 ROI 并重绘遮罩。</summary>  
        public void UpdateRoi(Rectangle newRoi)
        {
            _roi = newRoi;
            UpdateLayered();
        }
        /// <summary>
        /// 把准备好的 32bpp 位图推送到分层窗口 (Handle)。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushBitmap(Bitmap bmp)
        {
            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr hOld = SelectObject(hMem, hBmp);

            var size = new SIZE(bmp.Width, bmp.Height);
            var srcPt = new POINT(0, 0);
            var topPt = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, hScreen, ref topPt,
                                ref size, hMem, ref srcPt,
                                0, ref blend, ULW_ALPHA);

            SelectObject(hMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }
        // ★★ 工具：根据当前屏幕生成居中的初始 ROI

        protected override void WndProc(ref Message m)
        {
            Point cur;
            switch ((WindowMessage)m.Msg)
            {
                // 让整块遮罩都算作 client-area，才能收到正常鼠标消息
                case WindowMessage.WM_NCHITTEST:
                    cur = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);


                    if (_state == InitState.Preview)
                    {
                        // 运行和 RoiBorder 同一套命中测试
                        int ht = PreviewHitTest(cur);
                        m.Result = (IntPtr)ht;
                        return;            // 让系统根据 HT 值自动设置光标
                    }

                    // InitDraw 或 Active 阶段：整片客户端
                    m.Result = (IntPtr)MyWin32.HTCLIENT;
                    return;

                // ───── 1) InitDraw：拖出 ROI ─────
                case WindowMessage.WM_LBUTTONDOWN when _state == InitState.InitDraw:
                    _ptStart = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                    MyWin32.SetCapture(Handle);
                    return;

                case WindowMessage.WM_MOUSEMOVE when _state == InitState.InitDraw &&
                                   MyWin32.GetCapture() == Handle:
                    cur = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                    _roiPreview = Rectangle.FromLTRB(
                        Math.Min(_ptStart.X, cur.X),
                        Math.Min(_ptStart.Y, cur.Y),
                        Math.Max(_ptStart.X, cur.X),
                        Math.Max(_ptStart.Y, cur.Y));
                    UpdateLayered_Preview();
                    return;


                // ───── 2) Preview 模式：ROI 内拖动移动 ─────
                //Preview 拖动：统一用屏幕坐标 & 捕获判断
                case WindowMessage.WM_MOUSEMOVE when _state == InitState.Preview && GetCapture() == Handle:

                    {
                        cur = MyWin32.LParamToScreen(Handle,
                                                          (WindowMessage)m.Msg, m.LParam);


                        // --- 1) 先擦掉上一根框 ---
                        if (_rubberVisible) { DrawRubber(_rubberLast); _rubberVisible = false; }



                        int dx = cur.X - _ptStartPreview.X;
                        int dy = cur.Y - _ptStartPreview.Y;

                        int L = _roiStartPreview.Left;
                        int T = _roiStartPreview.Top;
                        int R = _roiStartPreview.Right;
                        int B = _roiStartPreview.Bottom;

                        switch (_previewHT)
                        {
                            case MyWin32.HTCAPTION:        // 移动
                                L += dx; R += dx; T += dy; B += dy; break;

                            case MyWin32.HTLEFT: L += dx; break;
                            case MyWin32.HTRIGHT: R += dx; break;
                            case MyWin32.HTTOP: T += dy; break;
                            case MyWin32.HTBOTTOM: B += dy; break;
                            case MyWin32.HTTOPLEFT: L += dx; T += dy; break;
                            case MyWin32.HTTOPRIGHT: R += dx; T += dy; break;
                            case MyWin32.HTBOTTOMLEFT: L += dx; B += dy; break;
                            case MyWin32.HTBOTTOMRIGHT: R += dx; B += dy; break;
                        }

                        // 最小尺寸限制
                        if (R - L < 50)
                        {
                            if (_previewHT == MyWin32.HTLEFT ||
                                              _previewHT == MyWin32.HTTOPLEFT ||
                                              _previewHT == MyWin32.HTBOTTOMLEFT) L = R - 50;
                            else R = L + 50;
                        }
                        if (B - T < 50)
                        {
                            if (_previewHT == MyWin32.HTTOP ||
                                              _previewHT == MyWin32.HTTOPLEFT ||
                                              _previewHT == MyWin32.HTTOPRIGHT) T = B - 50;
                            else B = T + 50;
                        }

                        _roiPreview = Rectangle.FromLTRB(L, T, R, B);

                        // --- 3) 画新框并记录 ---
                        _rubberLast = _roiPreview;
                        DrawRubber(_rubberLast);
                        _rubberVisible = true;
                        return;
                    }

                /* ★★ 重新加入：InitDraw 松键 → 进入 Preview ★★ */
                case WindowMessage.WM_LBUTTONUP when _state == InitState.InitDraw &&
                                                  MyWin32.GetCapture() == Handle:
                    {
                        MyWin32.ReleaseCapture();

                        if (_roiPreview.Width >= 50 && _roiPreview.Height >= 50)
                        {
                            _state = InitState.Preview;

                            // 记录起点，供 Preview 拖动/缩放用
                            _ptStartPreview = cur = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                            _roiStartPreview = _roiPreview;
                            _previewHT = MyWin32.HTCAPTION;   // 默认整体拖动

                            UpdateLayered_Preview();   // 最后再画一次蓝框
                        }
                        return;
                    }

                /* ───── 2) Preview —— 唯一的 WM_LBUTTONUP 分支 ───── */
                case WindowMessage.WM_LBUTTONUP when _state == InitState.Preview &&
                                                   MyWin32.GetCapture() == Handle:
                    {
                        MyWin32.ReleaseCapture();
                        _previewHT = MyWin32.HTNOWHERE;

                        if (_rubberVisible) { DrawRubber(_rubberLast); _rubberVisible = false; }
                        UpdateLayered_Preview();       // 真正渲染
                        return;
                    }

                /* Preview 拖动 */
                case WindowMessage.WM_NCLBUTTONDOWN when _state == InitState.Preview:
                case WindowMessage.WM_LBUTTONDOWN when _state == InitState.Preview:
                    {
                        _ptStartPreview = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        _roiStartPreview = _roiPreview;
                        _previewHT = PreviewHitTest(_ptStartPreview);
                        _rubberVisible = false;
                        MyWin32.SetCapture(Handle);
                        return;
                    }

                // ───── 3) 双击确认，切换到 Active ─────
                case WindowMessage.WM_NCLBUTTONDBLCLK when _state == InitState.Preview:  // 非客户区双击
                case WindowMessage.WM_LBUTTONDBLCLK when _state == InitState.Preview: // 客户区双击
                    {
                        // 如果蓝框太小 (<50×50) 则无效，避免误触
                        if (_roiPreview.Width < 50 || _roiPreview.Height < 50)
                            return;

                        // —— 要求：双击“框外”才确认 ——  
                        //    如果你想“框内 / 框外都能确认”，把下行 `if` 删掉即可。
                        cur = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        if (_roiPreview.Contains(cur))
                            return;         // 框内：忽略

                        // 框外：正式激活
                        ActivateRoi();
                        return;
                    }



                case WindowMessage.WM_DISPLAYCHANGE:
                case WindowMessage.WM_DPICHANGED:
                    {
                        // 1. 记录旧屏幕 & 新屏幕
                        var oldScreen = _screen;
                        _screen = Screen.PrimaryScreen!.Bounds;

                        // 2. 把旧 ROI 按比例映射到新屏幕
                        _roi = Helpers.ScaleRoi(_roi, oldScreen, _screen);

                        // 3. 调整遮罩窗口自身大小（全屏）
                        MyWin32.SetWindowPos(Handle, IntPtr.Zero,
                            0, 0, _screen.Width, _screen.Height,
                            MyWin32.SWP_NOZORDER | MyWin32.SWP_NOACTIVATE);

                        // 4. 重新绘制遮罩
                        UpdateLayered();

                        // 5. 通知边框窗口自适应
                        _borderWindow?.NotifyScreenChanged(_screen, _roi);
                        return;
                    }

            }
            base.WndProc(ref m);
        }
        Rectangle inner;
        /// <summary>
        /// 在 Preview 阶段，根据鼠标点判定 Move / Resize 方向（HT 常量）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PreviewHitTest(Point pt)
        {
            // 1. 若在 ROI 内侧  (留 8px 余量) → 移动
            inner = Rectangle.Inflate(_roiPreview, -PREVIEW_RESIZE_MARGIN,
                                                              -PREVIEW_RESIZE_MARGIN);
            if (inner.Contains(pt))
                return MyWin32.HTCAPTION;

            // 2. 其余：在 ROI 四周 8px 宽带里 → 判断具体 Resize 方向
            bool onLeft = Math.Abs(pt.X - _roiPreview.Left) < PREVIEW_RESIZE_MARGIN;
            bool onRight = Math.Abs(pt.X - _roiPreview.Right) < PREVIEW_RESIZE_MARGIN;
            bool onTop = Math.Abs(pt.Y - _roiPreview.Top) < PREVIEW_RESIZE_MARGIN;
            bool onBottom = Math.Abs(pt.Y - _roiPreview.Bottom) < PREVIEW_RESIZE_MARGIN;

            if (onLeft && onTop) return MyWin32.HTTOPLEFT;
            if (onRight && onTop) return MyWin32.HTTOPRIGHT;
            if (onLeft && onBottom) return MyWin32.HTBOTTOMLEFT;
            if (onRight && onBottom) return MyWin32.HTBOTTOMRIGHT;
            if (onLeft) return MyWin32.HTLEFT;
            if (onRight) return MyWin32.HTRIGHT;
            if (onTop) return MyWin32.HTTOP;
            if (onBottom) return MyWin32.HTBOTTOM;

            return MyWin32.HTCAPTION;    // 回退：整块拖动
        }

        /// <summary>
        /// 结束 Preview，进入 Active：创建正式遮罩并弹出可交互边框。
        /// </summary>
        private void ActivateRoi()
        {
            // 若还在捕获，先放掉
            if (MyWin32.GetCapture() == Handle)
                MyWin32.ReleaseCapture();

            _state = InitState.Active;
            _roi = _roiPreview;          // 固定 ROI

            // 1) 添加 WS_EX_TRANSPARENT，使整片遮罩可穿透
            const int ADD_EXSTYLE = WS_EX_TRANSPARENT;
            int ex = (int)MyWin32.GetWindowLongPtr(Handle, MyWin32.WindowLongFlags.GWL_EXSTYLE);
            if ((ex & ADD_EXSTYLE) == 0)
                MyWin32.SetWindowLongPtr(Handle, MyWin32.WindowLongFlags.GWL_EXSTYLE,
                                         (IntPtr)(ex | ADD_EXSTYLE));

            // 2) 换成“半透明黑 + 透明洞”位图
            UpdateLayered();

            // 3) 创建白色交互边框窗
            _borderWindow = new RoiBorderWindow(this, _roi);
            _borderWindow.Show();
        }


        // 预览阶段：半透明黑 + 透明 ROI 洞 + 蓝色预览边框
        //显示预览边框；
        
        private void UpdateLayered_Preview()
        {
            int w = _screen.Width;
            int h = _screen.Height;

            // ① 先在内存位图上完成所有绘制
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);

            // (a) 清背景
            g.Clear(Color.Transparent);

            // (b) 半透明黑遮罩 (SourceCopy：完全覆盖)
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var dimBrush = new SolidBrush(Color.FromArgb(ALPHA_DIM, Color.Black));
            g.FillRectangle(dimBrush, 0, 0, w, h);

            // (c) 挖 ROI 洞
            g.FillRectangle(Brushes.Transparent, _roiPreview);

            // (d) 画蓝色预览框 (SourceOver)
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using var pen = new Pen(Color.CornflowerBlue, PREVIEW_BORDER);
            g.DrawRectangle(pen, _roiPreview);

            // ② 将位图推送到分层窗口
            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));   // 0 = 透明背景
            IntPtr hOld = SelectObject(hMem, hBmp);

            var size = new SIZE(w, h);
            var srcPt = new POINT(0, 0);
            var topPt = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(
                Handle,                     // 分层窗口句柄
                hScreen,                    // 屏幕 DC
                ref topPt,                  // 目标位置 (全屏 0,0)
                ref size,                   // 窗口大小
                hMem,                       // 内存 DC
                ref srcPt,                  // 源坐标
                0,                          // 颜色键 (不用)
                ref blend,
                ULW_ALPHA);

            // ③ 释放 GDI 资源
            SelectObject(hMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }

        #region 绘制  
        
        private void UpdateLayered()
        {
            using var bmp = new Bitmap(_screen.Width, _screen.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);

            g.Clear(Color.Transparent);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

            using var dimBrush = new SolidBrush(Color.FromArgb(ALPHA_DIM, Color.Black));
            g.FillRectangle(dimBrush, _screen);               // 整屏半透明黑  

            g.FillRectangle(Brushes.Transparent, _roi);       // 在 ROI 位置挖洞  

            // 可选：画白色虚线框  
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using var pen = new Pen(Color.White, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(pen, _roi);

            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr hOld = SelectObject(hMem, hBmp);

            var size = new SIZE(_screen.Width, _screen.Height);
            var ptSrc = new POINT(0, 0);
            var top = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, hScreen, ref top, ref size,
                hMem, ref ptSrc, 0, ref blend, ULW_ALPHA);

            SelectObject(hMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }
        #endregion

        public void Dispose()
        {

            DestroyWindow(Handle);
            MyWin32.PostQuitMessage(0);         // 直接告诉消息泵退出
        }

    }

    internal sealed class PreviewBorderWindow : NativeWindow, IDisposable
    {
        private const int BORDER = 2;  // 蓝框宽度
        private readonly int _thickness;
        private readonly IntPtr _screenDC;
        private readonly IntPtr _memDC;
        private readonly Bitmap _bmp;
        private readonly IntPtr _hBmp;
        private readonly IntPtr _hOld;

        public PreviewBorderWindow(Rectangle roi, int thickness = 2)
        {
            _thickness = thickness;
            _screenDC = MyWin32.GetDC(IntPtr.Zero);
            _memDC = MyWin32.CreateCompatibleDC(_screenDC);

            _bmp = DrawBorderBitmap(roi.Width, roi.Height);
            _hBmp = _bmp.GetHbitmap(Color.FromArgb(0));
            _hOld = MyWin32.SelectObject(_memDC, _hBmp);

            IntPtr hWnd = MyWin32.CreateWindowEx(
                MyWin32.WS_EX_LAYERED | MyWin32.WS_EX_TOPMOST | MyWin32.WS_EX_TOOLWINDOW,
                "STATIC", null, MyWin32.WS_POPUP,
                roi.X, roi.Y, roi.Width, roi.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            AssignHandle(hWnd);
            PushBitmap(roi.Location);

            // 透明点击：不拦截鼠标
            MyWin32.SetWindowLongPtr(Handle, MyWin32.WindowLongFlags.GWL_EXSTYLE,
                (IntPtr)(MyWin32.WS_EX_LAYERED | MyWin32.WS_EX_TOPMOST |
                         MyWin32.WS_EX_TOOLWINDOW | MyWin32.WS_EX_TRANSPARENT));
        }

        private Bitmap DrawBorderBitmap(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.CornflowerBlue, _thickness);
            g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            return bmp;
        }

        private void PushBitmap(Point topLeft)
        {
            var size = new MyWin32.SIZE(_bmp.Width, _bmp.Height);
            var srcPt = new MyWin32.POINT(0, 0);
            var dstPt = new MyWin32.POINT(topLeft.X, topLeft.Y);
            var blend = new MyWin32.BLENDFUNCTION
            { BlendOp = MyWin32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = MyWin32.AC_SRC_ALPHA };

            MyWin32.UpdateLayeredWindow(Handle, _screenDC, ref dstPt,
                ref size, _memDC, ref srcPt, 0, ref blend, MyWin32.ULW_ALPHA);
        }

        public void UpdateRect(Rectangle roi)
        {
            // 只移动/缩放窗口，不重绘位图
            MyWin32.SetWindowPos(Handle, IntPtr.Zero,
                roi.X, roi.Y, roi.Width, roi.Height,
                MyWin32.SWP_NOZORDER | MyWin32.SWP_NOACTIVATE);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) MyWin32.DestroyWindow(Handle);
            MyWin32.SelectObject(_memDC, _hOld);
            MyWin32.DeleteObject(_hBmp);
            MyWin32.DeleteDC(_memDC);
            MyWin32.ReleaseDC(IntPtr.Zero, _screenDC);
            _bmp.Dispose();
        }
    }

}
