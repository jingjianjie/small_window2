
using System.Runtime.InteropServices;


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

        private MaskWindow _mask;
        private Rectangle _roi;
        uint dbl = MyWin32.GetDoubleClickTime();


        private long _lastClickTime = 0;
        private Point _lastClickPos = Point.Empty;
        private const int CLICK_SLOP = 4;           // 容忍光标抖动 px

        public RoiBorderWindow(MaskWindow maskRef, Rectangle roi)
        {
            _mask = maskRef;
            _roi = roi;

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
        /// <summary>
        /// 绘制显式边框；
        /// </summary>
        private void RedrawBorder()
        {
            //HDC 是对屏幕、打印机、内存位图等抽象后的绘图上下文
            /* 1) 取得 HDC                2) 选入或修改 GDI 对象       3) 调用绘图 API              4) 收尾释放
        +---------------------+    +------------------------+    +-------------------------+    +-----------------+
        | GetDC / BeginPaint  | -> | SelectObject(hPen)     | -> | MoveToEx / LineTo       | -> | DeleteObject     |
        | CreateCompatibleDC  |    | SelectObject(hBitmap)  |    | BitBlt / AlphaBlend     |    | DeleteDC         |
        | CreateDC(Printer)   |    | SetBkMode / SetROP2... |    | TextOut / Polygon / ... |    | ReleaseDC        |
        +---------------------+    +------------------------+    +-------------------------+    +-----------------+
            */
            int w = _roi.Width + BORDER * 2;
            int h = _roi.Height + BORDER * 2;

            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);

            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var br = new SolidBrush(Color.FromArgb(120, Color.White));  // 纯白边
            g.FillRectangle(br, 0, 0, w, h);      // 先全填
            g.FillRectangle(Brushes.Transparent, BORDER, BORDER, _roi.Width, _roi.Height); // 中间挖空

            IntPtr hScreen = GetDC(IntPtr.Zero); //DC：设备相关环境
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
                // ------ 窗口销毁时同步关闭（非鼠标消息，仅保留原注释）------
                case WindowMessage.WM_NCDESTROY:
                    Dispose();
                    base.WndProc(ref m);
                    return;

                // ------ 鼠标命中测试（检测鼠标位于窗口哪部分，用于后续拖拽/缩放）------
                // 1) 鼠标点击/移动到窗口时，系统会发送 WM_NCHITTEST 消息，询问鼠标位于窗口的哪个区域。返回值写到 m.Result 中，
                //          - 系统随即按这个 HT 值决定光标形状，并在下一条 WM_SETCURSOR 中切换 ↔︎、↕︎ 等调整大小箭头。→ 一切正常。
                // 1） 命中到 ROI 范围内，
                // 2) 命中到自定义的拖动区域，R
                // 3) 命中到 ROI 之外
                case WindowMessage.WM_NCHITTEST:
                    HandleNcHitTest(ref m);
                    return;

                // ==================== 鼠标相关消息 ====================

                // 1) 非工作区左键按下 —— 开始拖拽或缩放
                //    系统把方向编号放在 wParam，我们记录方向、起始矩形、起始坐标，
                //    并主动调用 SetCapture 把鼠标捕获到当前窗口，以便后续即使鼠标
                //    移出窗口也能持续收到 WM_MOUSEMOVE/WM_LBUTTONUP。
                case (WindowMessage)WM_NCLBUTTONDOWN:
                    {
                        _htActive = (int)m.WParam;                 // 记录方向(HTLEFT/HTRIGHT/HTTOP.../HTCAPTION)
                        _roiStart = _roi;                          // 记录初始 ROI，用于计算差值
                        _ptStart = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam); // 记录起始屏幕坐标
                        SetCapture(Handle);                        // 抓取鼠标
                        return;                                    // 不继续给系统默认处理
                    }

                // 2) 鼠标滚轮 —— 调整遮罩透明度
                //    读取滚轮增量 delta，传给 _mask.ChangeDim 来改变窗口遮罩的 Alpha。
                case (WindowMessage)MyWin32.WM_MOUSEWHEEL:
                    {
                        int delta = MyWin32.GET_WHEEL_DELTA(m.WParam);
                        _mask.ChangeDim(delta);                   // 改变透明度
                        m.Result = IntPtr.Zero;                   // 告诉系统我们已处理
                        return;
                    }

                // 3) 鼠标移动 —— 拖拽或缩放进行中
                //    若 _htActive != HTNOWHERE 说明正在拖拽/缩放，根据方向计算新矩形，
                //    同时保证宽高不小于最小尺寸 (MIN_W / MIN_H)，
                //    最后调用 SetWindowPos 仅移动/缩放窗口而不重绘，实时更新 _roi。
                case WindowMessage.WM_MOUSEMOVE:
                    {
                        if (_htActive == HTNOWHERE) break;         // 未处于拖拽状态

                        Point cur = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        int dx = cur.X - _ptStart.X;
                        int dy = cur.Y - _ptStart.Y;

                        // 1) 以起始四边为基准
                        int L = _roiStart.Left;
                        int T = _roiStart.Top;
                        int R = _roiStart.Right;
                        int B = _roiStart.Bottom;

                        // 2) 根据拖拽方向调整相应边
                        switch (_htActive)
                        {
                            case HTCAPTION:   // 整体移动
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

                        // 3) 强制最小尺寸
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

                        // 4) 只移动/缩放窗口，不触发重绘
                        SetWindowPos(Handle, IntPtr.Zero,
                            r.X - BORDER, r.Y - BORDER,
                            r.Width + BORDER * 2, r.Height + BORDER * 2,
                            SWP_NOZORDER | SWP_NOACTIVATE);

                        _roi = r;          // 更新当前矩形供下次命中测试
                        return;
                    }

                // 4) 左键释放 —— 结束拖拽/统计点击次数
                //    1) 若正在拖拽则释放鼠标捕获、通知遮罩更新 ROI 并重绘边框
                //    2) 统计两次点击间隔与位移，用于判断双击；双击次数 >=2 则关闭窗口
                case WindowMessage.WM_LBUTTONUP:
                    {
                        if (_htActive != HTNOWHERE)           // 结束拖拽/缩放
                        {
                            _htActive = HTNOWHERE;
                            MyWin32.ReleaseCapture();
                            RedrawBorder();                   // 重绘边框
                            _mask.UpdateRoi(_roi);            // 通知遮罩新 ROI
                        }

                        // —— 点击计数（MouseUp 时统计）——
                        long now = Environment.TickCount64;

                        cur = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        bool closeCandidate =
                            (now - _lastClickTime <= (dbl + 50)) &&         // 时间间隔足够短
                            Math.Abs(cur.X - _lastClickPos.X) <= CLICK_SLOP && // 位移足够小
                            Math.Abs(cur.Y - _lastClickPos.Y) <= CLICK_SLOP;

                        _clickCount = closeCandidate ? _clickCount + 1 : 1; // 连续点击累加，否则重置
                        _lastClickTime = now;
                        _lastClickPos = cur;

                        if (_clickCount >= 2)                               // 双击关闭
                        {
                            Dispose();                 // 关闭边框 + 遮罩
                            return;
                        }

                        return;
                    }
            }

            //上方过滤指定的信息，并写 m.Result，可能会触发 SetCursor( m.Result) 从而改变箭头形态。
            base.WndProc(ref m);

        }
        // 允许在 ROI 下方额外捕捉拖拽的范围（像素）
        private const int EXTRA_BOTTOM_RANGE = 30; // ← 根据需求可调
        private void HandleNcHitTest(ref Message m)
        {
            Point pt = LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);

            // ① ROI 内：完全透传
            if (_roi.Contains(pt))
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            // ② ROI 外，但仍在可拖范围？ 判断前先建立“可交互外环”
            //    我们希望左右/上保持 Border+2，底部则额外 +EXTRA_BOTTOM_RANGE
            int inflateH = BORDER + 2;          // 左右/上扩张值
            int inflateBottom = BORDER + EXTRA_BOTTOM_RANGE; // 底部扩张值

            Rectangle outer = Rectangle.FromLTRB(
                _roi.Left - inflateH,
                _roi.Top - inflateH,
                _roi.Right + inflateH,
                _roi.Bottom + inflateBottom);

            if (!outer.Contains(pt))
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            // ③ 计算到各边的距离（正值）
            int dLeft = _roi.Left - pt.X;   // pt 在左侧 ⇒ 正数
            int dRight = pt.X - _roi.Right;   // pt 在右侧 ⇒ 正数
            int dTop = _roi.Top - pt.Y;   // pt 在上方 ⇒ 正数
            int dBottom = pt.Y - _roi.Bottom;  // pt 在下方 ⇒ 正数

            bool onLeft = dLeft >= 0 && dLeft < inflateH;
            bool onRight = dRight >= 0 && dRight < inflateH;
            bool onTop = dTop >= 0 && dTop < inflateH;
            bool onBottom = dBottom >= 0 && dBottom < inflateBottom;

            //   |<─.──|───|       → dLeft / dRight 边界示意
            // 手感：需要鼠标更靠近 ROI 才出现 Resize，而不是整个外环
            bool resizeLeft = onLeft && dLeft >= BORDER - RESIZE_MARGIN;
            bool resizeRight = onRight && dRight >= BORDER - RESIZE_MARGIN;
            bool resizeTop = onTop && dTop >= BORDER - RESIZE_MARGIN;
            bool resizeBottom = dBottom >= BORDER - RESIZE_MARGIN &&
                                                dBottom <= BORDER+1;
            bool resizeBottomRight = onBottom && onRight; // 右下角宽泛一点提升手感

            // ④ 判定 Resize ：先四角，再四边
            if (resizeLeft || resizeRight || resizeTop || resizeBottom || resizeBottomRight)
            {
                int ht;
                if (resizeLeft && resizeTop) ht = HTTOPLEFT;
                else if (resizeRight && resizeTop) ht = HTTOPRIGHT;
                else if (resizeLeft && resizeBottom) ht = HTBOTTOMLEFT;
                else if (resizeBottomRight) ht = HTBOTTOMRIGHT;
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
