using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using static small_window2.MyWin32;

namespace small_window2
{
    /// <summary>仅占 ROI 边框 3px 的分层窗口，可拖拽或拉伸，鼠标事件不影响 ROI 内部。</summary>
    internal sealed class RoiBorderWindow : NativeWindow, IDisposable
    {
        private bool _disposed;


        // ────────────────────────────────────────────────
        // 边框命中测试：外环 ≈3px → Resize；内环(剩余) → Move
        // ────────────────────────────────────────────────
        private const int RESIZE_MARGIN = 3;      // 外层用于 Resize 的宽度(像素)

        private const int BORDER = 20;            // 边框可拖拽宽度
        private const int MIN_W = 200, MIN_H = 150;
        private const int WS_EX_MASK =
            WS_EX_LAYERED | WS_EX_TOPMOST  | WS_EX_TOOLWINDOW;
        //WS_EX_NOACTIVATE

        private int _htActive = HTNOWHERE;   // 正在拖 / 缩放的 HT 方向
        private Rectangle _roiStart;         // 拖动开始瞬间的 ROI
        private Point _ptStart;              // 拖动开始的屏幕坐标

        private int _clickCount = 0;
        private long _lastClick = 0;

        private MaskWindow _mask;
        private Rectangle _roi;
        uint dbl = MyWin32.GetDoubleClickTime();
        private bool _dragging;
        private Point _dragStart;                // 鼠标相对 ROI 左上
        private long _lastClickTime = 0;
        private Point _lastClickPos = Point.Empty;
        private const int CLICK_SLOP = 4;           // 容忍光标抖动 px

        public RoiBorderWindow(MaskWindow maskRef, Rectangle roi)
        {
            _mask = maskRef;
            _roi = roi;
            // 其余初始化流程保持一致，但别再 new MaskWindow
            IntPtr hWnd = CreateWindowEx(
            WS_EX_MASK, "STATIC", null, WS_POPUP | WS_SIZEBOX,
            _roi.X - BORDER, _roi.Y - BORDER,
            _roi.Width + BORDER * 2, _roi.Height + BORDER * 2,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            AssignHandle(hWnd);
            RedrawBorder();

        }


        public RoiBorderWindow(out MaskWindow mask)
        {
    
            _roi = new Rectangle(
               (Screen.PrimaryScreen!.Bounds.Width - 800) / 2,
               (Screen.PrimaryScreen.Bounds.Height - 400) / 2,
               800, 400);
            // 初始 ROI 矩形，位于屏幕中央，大小 800x400
            // 创建一个默认的 ROI 矩形
            IntPtr hWnd = CreateWindowEx(
                WS_EX_MASK, "STATIC", null, WS_POPUP | WS_SIZEBOX,
                _roi.X - BORDER, _roi.Y - BORDER,
                _roi.Width + BORDER * 2, _roi.Height + BORDER * 2,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            AssignHandle(hWnd);
            RedrawBorder();

            // ★★ 构造 MaskWindow，并把 this 传进去
            mask = new MaskWindow(this, _roi);
            _mask = mask;
        }
        //
        public void Show() => ShowWindow(Handle, SW_SHOWNOACTIVATE);

        #region 绘制
        private void RedrawBorder()
        {
            int w = _roi.Width + BORDER * 2;
            int h = _roi.Height + BORDER * 2;

            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);

            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var br = new SolidBrush(Color.FromArgb(120, Color.White));  // 纯白边
            g.FillRectangle(br, 0, 0, w, h);      // 先全填
            g.FillRectangle(Brushes.Transparent, BORDER, BORDER, _roi.Width, _roi.Height); // 中间挖空

            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr hOld = SelectObject(hMem, hBmp);

            var size = new SIZE(w, h);
            var srcPt = new POINT(0, 0);
            var top = new POINT(_roi.X - BORDER, _roi.Y - BORDER);
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

            UpdateLayeredWindow(Handle, hScreen, ref top, ref size,
                hMem, ref srcPt, 0, ref blend, ULW_ALPHA);

            SelectObject(hMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }
        #endregion

        // ★★ 由 MaskWindow 调用，告诉我“屏幕/DPI 变了”
        public void NotifyScreenChanged(Rectangle newScreen, Rectangle newRoi)
        {
            _roi = newRoi;

            // 重新摆放并重绘，注意加上边框宽度
            SetWindowPos(Handle, IntPtr.Zero,
                _roi.X - BORDER, _roi.Y - BORDER,
                _roi.Width + BORDER * 2, _roi.Height + BORDER * 2,
                SWP_NOZORDER | SWP_NOACTIVATE);

            RedrawBorder();          // 单次重绘边框位图
        }

        #region 消息处理
        Point cur = new Point(0, 0);
        protected override void WndProc(ref Message m)
        {
            switch ((WindowMessage)m.Msg)
            {
                // 系统把窗口销毁时保证同步关闭
                case WindowMessage.WM_NCDESTROY:
                    Dispose();
                    base.WndProc(ref m);
                    return;

                case WindowMessage.WM_NCHITTEST:
                    HandleNcHitTest(ref m);
                    return;

                case (WindowMessage)WM_NCLBUTTONDOWN:
                    {
                        // ——— 三击检测 ———
                        //long now = Environment.TickCount64;
                        //uint dbl = MyWin32.GetDoubleClickTime();   // 系统双击时间(ms)
                        //_clickCount = (now - _lastClick <= dbl) ? _clickCount + 1 : 1;
                        //_lastClick = now;

                        //if (_clickCount >= 3)
                        //{
                        //    Dispose();                 // 统一关闭(→ 也关遮罩)
                        //    return;
                        //}


                        _htActive = (int)m.WParam;                 // 记录方向
                        _roiStart = _roi;                          // 记录起始矩形
                        _ptStart = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        SetCapture(Handle);                        // 我们自己捕获
                        return;                                    // 不给系统
                    }


                case WindowMessage.WM_MOUSEMOVE:
                    {
                        if (_htActive == HTNOWHERE) break;          // 没在拖

                        cur = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        int dx = cur.X - _ptStart.X;
                        int dy = cur.Y - _ptStart.Y;

                        // --- 1) 以起始四边为基准 ---
                        int L = _roiStart.Left;
                        int T = _roiStart.Top;
                        int R = _roiStart.Right;
                        int B = _roiStart.Bottom;

                        // --- 2) 按方向调整相应边 ---
                        switch (_htActive)
                        {
                            case HTCAPTION:   // Move 整体
                                L += dx; R += dx;
                                T += dy; B += dy;
                                break;

                            case HTLEFT: L += dx; break;
                            case HTRIGHT: R += dx; break;
                            case HTTOP: T += dy; break;
                            case HTBOTTOM: B += dy; break;
                            case HTTOPLEFT: L += dx; T += dy; break;
                            case HTTOPRIGHT: R += dx; T += dy; break;
                            case HTBOTTOMLEFT: L += dx; B += dy; break;
                            case HTBOTTOMRIGHT: R += dx; B += dy; break;
                        }

                        // --- 3) 最小尺寸约束 ---
                        if (R - L < MIN_W)
                        {
                            if (_htActive == HTLEFT || _htActive == HTTOPLEFT || _htActive == HTBOTTOMLEFT)
                                L = R - MIN_W;
                            else
                                R = L + MIN_W;
                        }
                        if (B - T < MIN_H)
                        {
                            if (_htActive == HTTOP || _htActive == HTTOPLEFT || _htActive == HTTOPRIGHT)
                                T = B - MIN_H;
                            else
                                B = T + MIN_H;
                        }

                        Rectangle r = Rectangle.FromLTRB(L, T, R, B);

                        // --- 4) 仅移动/缩放窗口，不重绘 ---
                        SetWindowPos(Handle, IntPtr.Zero,
                            r.X - BORDER, r.Y - BORDER,
                            r.Width + BORDER * 2, r.Height + BORDER * 2,
                            SWP_NOZORDER | SWP_NOACTIVATE);

                        _roi = r;          // 实时更新，供下一次命中测试
                        return;
                    }

                case WindowMessage.WM_LBUTTONUP:
                    {
                        if (_htActive != HTNOWHERE)          // 结束拖拽
                        {
                            _htActive = HTNOWHERE;
                            MyWin32.ReleaseCapture();
                            RedrawBorder();
                            _mask.UpdateRoi(_roi);
                        }

                        // —— 点击计数（只在 MouseUp 时统计）——
                        long now = Environment.TickCount64;


                        cur = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        bool closeCandidate =
                            (now - _lastClickTime <= dbl) &&                   // 时间间隔
                            Math.Abs(cur.X - _lastClickPos.X) <= CLICK_SLOP &&  // 位移很小
                            Math.Abs(cur.Y - _lastClickPos.Y) <= CLICK_SLOP;

                        _clickCount = closeCandidate ? _clickCount + 1 : 1;
                        _lastClickTime = now;
                        _lastClickPos = cur;

                        if (_clickCount >= 2)
                        {
                            Dispose();                 // 关闭边框 + 遮罩
                            return;
                        }

                        return;
                    }
              }

            base.WndProc(ref m);
        }

        private void HandleNcHitTest(ref Message m)
        {
            Point pt = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);

            // ① ROI 内：完全透传
            if (_roi.Contains(pt))
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            // ② 点在 ROI 外？直接透传
            Rectangle outer = Rectangle.Inflate(_roi, BORDER, BORDER);
            if (!outer.Contains(pt))
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            // ③ 计算到各边的距离（正值）
            int dLeft = _roi.Left - pt.X;   // pt 在左侧 ⇒ 正数
            int dRight = pt.X - _roi.Right;    // pt 在右侧 ⇒ 正数
            int dTop = _roi.Top - pt.Y;   // pt 在上方 ⇒ 正数
            int dBottom = pt.Y - _roi.Bottom;   // pt 在下方 ⇒ 正数

            bool onLeft = dLeft >= 0 && dLeft < BORDER;
            bool onRight = dRight >= 0 && dRight < BORDER;
            bool onTop = dTop >= 0 && dTop < BORDER;
            bool onBottom = dBottom >= 0 && dBottom < BORDER;

            //bool resizeLeft = onLeft && dLeft < RESIZE_MARGIN;
            //bool resizeRight = onRight && dRight < RESIZE_MARGIN;
            //bool resizeTop = onTop && dTop < RESIZE_MARGIN;
            //bool resizeBottom = onBottom && dBottom < RESIZE_MARGIN;
            
            //用于判断边框内外
            bool resizeLeft = onLeft && dLeft >= BORDER - RESIZE_MARGIN;
            bool resizeRight = onRight && dRight >= BORDER - RESIZE_MARGIN;
            bool resizeTop = onTop && dTop >= BORDER - RESIZE_MARGIN;
            bool resizeBottom = onBottom && dBottom >= BORDER - RESIZE_MARGIN;


            // ④ 判定 Resize ：先看四角，再看四边
            if (resizeLeft || resizeRight || resizeTop || resizeBottom)
            {
                int ht;
                if (resizeLeft && resizeTop) ht = HTTOPLEFT;
                else if (resizeRight && resizeTop) ht = HTTOPRIGHT;
                else if (resizeLeft && resizeBottom) ht = HTBOTTOMLEFT;
                else if (resizeRight && resizeBottom) ht = HTBOTTOMRIGHT;
                else if (resizeLeft) ht = HTLEFT;
                else if (resizeRight) ht = HTRIGHT;
                else if (resizeTop) ht = HTTOP;
                else ht = HTBOTTOM;

                m.Result = (IntPtr)ht;
                return;
            }

            // ⑤ 剩余环形（7px）→ 整体拖动
            m.Result = (IntPtr)HTCAPTION;
        }

        #endregion


        public void Dispose() {

            if (_disposed) return;

            _disposed = true;
            _mask.Dispose();                           // 先关遮罩
            if (Handle != IntPtr.Zero) DestroyWindow(Handle);   // 再关自己

        }
    }
    /// <summary>最小 Win32 P/Invoke 封装，只声明示例用到的部分。</summary>
  
}
