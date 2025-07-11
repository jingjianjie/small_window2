﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

        private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 16 }; // 60 FPS
                                                                                             // ---- Esc 双击判定 ----
        private long _lastEscTime = 0;
        private int _escClickCount = 0;
        private static readonly uint ESC_DBL_TIME = MyWin32.GetDoubleClickTime(); // 系统双击时间

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


        private readonly Brush _brushDim = new SolidBrush(Color.FromArgb(80, Color.Black));
        private readonly Pen _penBlue = new Pen(Color.CornflowerBlue, PREVIEW_BORDER);


        private static double GetDpiX()
        {
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                return graphics.DpiX / 144.0;
            }
        }

        private byte _alphadim = MainContext.Alpha;           // 遮罩透明度  
                                                     // MaskWindow ctor 里：先不给 WS_EX_TRANSPARENT
        const int WS_EX_MASK = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
        private Rectangle _screen;
        private Point _origin;          // ← 左上可能是负数
        private Rectangle _roi;                       // ROI 由外部窗口控制  
        private RoiBorderWindow? _borderWindow;

        public MaskWindow(RoiBorderWindow? border, Rectangle? initialRoi = null)
        {
            //初始化 Preview GDI 缓存
            var vs = SystemInformation.VirtualScreen; //多屏幕完全遮罩优化
            _screen = new Rectangle(0, 0, vs.Width, vs.Height);
            _origin = new Point(vs.Left, vs.Top);   // 可能为负
            _borderWindow = border;                     // ★★


            _state = InitState.InitDraw;  // 初始状态：绘制 ROI
                                          // 初始无洞，先填满；真正的 _roi 由用户拖出
                                          // ❷ 创建分层窗时用虚拟桌面左上角
            IntPtr hWnd = CreateWindowEx(
                WS_EX_MASK, "STATIC", null, WS_POPUP,
                _origin.X, _origin.Y, _screen.Width, _screen.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // 设置窗口样式
            AssignHandle(hWnd);
            UpdateLayered_FullDark();     // 只画全屏暗色（函数见下）

            //注册退出键
            if (!MyWin32.RegisterHotKey(Handle, MyWin32.HOTKEY_ID_ESC,
                            MyWin32.MOD_NONE, MyWin32.VK_ESCAPE))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Show() => ShowWindow(Handle, SW_SHOWNOACTIVATE);

        /// <summary>更新遮罩：全屏半透明黑 + 无洞。</summary>
        private void UpdateLayered_FullDark()
        {
            _roiPreview = Rectangle.Empty;          // 无洞
            using var bmp = new Bitmap(_screen.Width, _screen.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(80, Color.Black));
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
            // 1.将 bmp 写入和屏幕内存对应的草稿区(DC)
            //  创建草稿
            IntPtr hScreen = GetDC(IntPtr.Zero);
            IntPtr hMem = CreateCompatibleDC(hScreen); //memory device context；创建与对应指针兼容的临时 DC 内存
            //  BMP 作为 GDI 对象移入草稿
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr hOld = SelectObject(hMem, hBmp); // 选中位图到内存 DC
            // 2. 位运算混合入屏幕区内存；
            var size = new SIZE(bmp.Width, bmp.Height);
            var srcPt = new POINT(0, 0);
            var topPt = new POINT(_origin.X, _origin.Y);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            // 以 DC 为基本单位做位运算；
            UpdateLayeredWindow(Handle, hScreen, ref topPt,
                                ref size, hMem, ref srcPt,
                                0, ref blend, ULW_ALPHA);  //实际绘制，UpdateLayeredWindow 需要 hMem 中已经选中了正确的位图，这样才能读取位图数据进行分层窗口渲染。
            
            // 3. 释放 DC 区
            // 先释放 hbmp 后删除
            SelectObject(hMem, hOld); //select & activate GDI
 
            DeleteObject(hBmp);
            // 再删除 DC；
            DeleteDC(hMem);
            ReleaseDC(IntPtr.Zero, hScreen);
        }
        // ★★ 工具：根据当前屏幕生成居中的初始 ROI
        const int DRAG_THRESHOLD = 3;
//        1. 初始状态 - 创建内存DC
//┌─────────────────┐    ┌─────────────────┐
//│   屏幕 DC       │    │   内存 DC       │
//│  (用户可见)     │    │  (用户不可见)   │
//│                 │    │                 │
//│  ┌───────────┐  │    │  ┌───────────┐  │
//│  │           │  │    │  │           │  │
//│  │   空白    │  │    │  │   空白    │  │
//│  │           │  │    │  │           │  │
//│  └───────────┘  │    │  └───────────┘  │
//└─────────────────┘    └─────────────────┘
//      hScreen hMem
//2. 绘图阶段 - 所有操作都在内存中
//时间轴: T1 → T2 → T3 → T4

//T1: Rectangle()
//┌─────────────────┐    ┌─────────────────┐
//│   屏幕 DC       │    │   内存 DC       │
//│                 │    │                 │
//│  ┌───────────┐  │    │  ┌───────────┐  │
//│  │           │  │    │  │ ┌─────┐   │  │
//│  │   空白    │  │    │  │ │█████│   │  │ ← 绘制矩形
//│  │           │  │    │  │ └─────┘   │  │
//│  └───────────┘  │    │  └───────────┘  │
//└─────────────────┘    └─────────────────┘

//T2: Ellipse()  
//┌─────────────────┐    ┌─────────────────┐
//│   屏幕 DC       │    │   内存 DC       │
//│                 │    │                 │
//│  ┌───────────┐  │    │  ┌───────────┐  │
//│  │           │  │    │  │ ┌─────┐   │  │
//│  │   空白    │  │    │  │ │█████│ ◯ │  │ ← 增加圆形
//│  │           │  │    │  │ └─────┘   │  │
//│  └───────────┘  │    │  └───────────┘  │
//└─────────────────┘    └─────────────────┘

//T3: TextOut()
//┌─────────────────┐    ┌─────────────────┐
//│   屏幕 DC       │    │   内存 DC       │
//│                 │    │                 │
//│  ┌───────────┐  │    │  ┌───────────┐  │
//│  │           │  │    │  │ ┌─────┐   │  │
//│  │   空白    │  │    │  │ │█████│ ◯ │  │
//│  │           │  │    │  │ └─────┘   │  │
//│  └───────────┘  │    │  │   ABC     │  │ ← 增加文字
//└─────────────────┘    └───┴───────────┴──┘

//用户全程看到: 空白屏幕 (无闪烁！)
        protected override void WndProc(ref Message m)
        {
            Point cur;
            switch ((WindowMessage)m.Msg)
            {
                // ───── 新增：InitDraw 阶段右键立即退出 ─────
                case WindowMessage.WM_RBUTTONDOWN when _state == InitState.InitDraw:
                case WindowMessage.WM_RBUTTONUP when _state == InitState.InitDraw:
                    {
                        // 若正捕获鼠标，先释放
                        if (MyWin32.GetCapture() == Handle)
                            MyWin32.ReleaseCapture();

                        Dispose();          // 同步关闭遮罩 + PostQuitMessage
                        return;             // 不再往下传
                    }

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
                    {
                        _ptStart = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);

                        MyWin32.SetCapture(Handle);
                        return;
                    }

                case WindowMessage.WM_MOUSEMOVE when _state == InitState.InitDraw &&
                                     MyWin32.GetCapture() == Handle:
                    {
                        cur = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);

                        // 抹掉旧框
                        if (_rubberVisible) {
                            DrawRubber(_rubberLast); _rubberVisible = false; }

                        _roiPreview = Rectangle.FromLTRB(
                            Math.Min(_ptStart.X, cur.X),
                            Math.Min(_ptStart.Y, cur.Y),
                            Math.Max(_ptStart.X, cur.X),
                            Math.Max(_ptStart.Y, cur.Y));

                        // 画新框
                        _rubberLast = _roiPreview;
                        DrawRubber(_rubberLast);
                        _rubberVisible = true;
                        return;
                    }


                // ───── 2) Preview 模式：ROI 内拖动移动 ─────
                //Preview 拖动：统一用屏幕坐标 & 捕获判断
                case WindowMessage.WM_MOUSEMOVE when _state == InitState.Preview && GetCapture() == Handle:

                    {
                        cur = MyWin32.LParamToScreen(Handle,
                                                          (WindowMessage)m.Msg, m.LParam);

                        // 擦旧
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



                        // 画新
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
                        _previewHT = MyWin32.HTNOWHERE;

                        // 擦掉最后一根蓝框
                        if (_rubberVisible) { DrawRubber(_rubberLast); _rubberVisible = false; }

                        // 真正重绘半透明黑 + 透明洞
                        // 以前这里调用 UpdateLayered()，但它只画全屏暗 ➜ 改掉
                        _state = InitState.Preview;
                        _ptStartPreview = _ptStart;
                        _roiStartPreview = _roiPreview;
                        _previewHT = MyWin32.HTCAPTION;

                        // (2) 真正绘制遮罩   蓝框
                        RenderMaskWithRoi();
                        return;
                    }


                /* ───── 2) Preview —— 唯一的 WM_LBUTTONUP 分支 ───── */
                case WindowMessage.WM_LBUTTONUP when _state == InitState.Preview &&
                                                   MyWin32.GetCapture() == Handle:
                    {
                        MyWin32.ReleaseCapture();
                        _previewHT = MyWin32.HTNOWHERE;


                        RenderMaskWithRoi();
                        return;
                    }

                /* Preview 拖动 */
                case WindowMessage.WM_NCLBUTTONDOWN when _state == InitState.Preview:
                case WindowMessage.WM_LBUTTONDOWN when _state == InitState.Preview:
                    {
                        _ptStartPreview = MyWin32.LParamToScreen(Handle, (WindowMessage)m.Msg, m.LParam);
                        _roiStartPreview = _roiPreview;
                        _previewHT = PreviewHitTest(_ptStartPreview);
    
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
                        MyWin32.UnregisterHotKey(Handle, MyWin32.HOTKEY_ID_ESC);

                        ActivateRoi();
                        
                        
                        
                        return;
                    }
                // ============ 分辨率 / DPI 变化 ============  
                case WindowMessage.WM_DISPLAYCHANGE:
                case WindowMessage.WM_DPICHANGED:
                    {
                        // a) 旧虚拟桌面，用于按比例映射 ROI  
                        var oldVS = new Rectangle(_origin, _screen.Size);

                        // b) 取新的虚拟桌面  
                        Rectangle vs = SystemInformation.VirtualScreen;        // 可能 Left/Top 为负  
                        _origin = new Point(vs.Left, vs.Top);
                        _screen = new Rectangle(0, 0, vs.Width, vs.Height);

                        // c) 若是 DPI 消息，用系统给的建议矩形调整窗口  
                        if ((WindowMessage)m.Msg == WindowMessage.WM_DPICHANGED)
                        {
                            var rc = Marshal.PtrToStructure<MyWin32.RECT>(m.LParam);
                            MyWin32.SetWindowPos(Handle, IntPtr.Zero,
                                rc.Left, rc.Top,
                                rc.Right - rc.Left, rc.Bottom - rc.Top,
                                MyWin32.SWP_NOZORDER | MyWin32.SWP_NOACTIVATE);
                        }
                        else // DISPLAYCHANGE：把窗口搬到新虚拟桌面左上角  
                        {
                            MyWin32.SetWindowPos(Handle, IntPtr.Zero,
                                _origin.X, _origin.Y,
                                _screen.Width, _screen.Height,
                                MyWin32.SWP_NOZORDER | MyWin32.SWP_NOACTIVATE);
                        }

                        // d) 按比例把旧 ROI 投射到新坐标系（Helpers.ScaleRoi 是你已有的）  
                        _roi = Helpers.ScaleRoi(_roi, oldVS, new Rectangle(_origin, _screen.Size));

                        // e) 重新绘制遮罩  
                        if (_roi.IsEmpty)
                            UpdateLayered_FullDark(); // 还在 InitDraw，没有 ROI——直接整屏暗  
                        else
                            UpdateLayered();          // Preview / Active——半透明黑 + 新 ROI 洞

                        // f) 通知边框窗口同步  
                        _borderWindow?.NotifyScreenChanged(_screen, _roi);
                        return;
                    }
                case (WindowMessage)MyWin32.WM_HOTKEY when _state == InitState.Preview:
                case (WindowMessage)MyWin32.WM_HOTKEY when _state == InitState.InitDraw:
                    {
                        if ((int)m.WParam != MyWin32.HOTKEY_ID_ESC)
                            break;                                         // 不是 Esc 热键

                        // ───────── 0) “未拖任何框” → 单击 Esc 即退 ─────────
                        if (_state == InitState.InitDraw && _roiPreview.IsEmpty)
                        {
                            Dispose();
                            return;
                        }

                        // ───────── 1) 仅在 InitDraw / Preview 下处理双击退出 ─────────
                        if (!(_state == InitState.InitDraw || _state == InitState.Preview))
                            break;

                        // 1. 获取鼠标屏幕坐标
                        MyWin32.GetCursorPos(out var p);
                         cur = new Point(p.X, p.Y);

                        // 2. 鼠标必须在 ROI 外才计数
                        if (!_roiPreview.Contains(cur))
                        {
                            long now = Environment.TickCount64;
                            bool within = now - _lastEscTime <= ESC_DBL_TIME;

                            _escClickCount = within ? _escClickCount + 1 : 1;
                            _lastEscTime = now;

                            if (_escClickCount >= 2)
                            {
                                Dispose();          // 双击达成 → 退出
                                return;
                            }
                        }
                        else
                        {
                            _escClickCount = 0;     // 在框内按 Esc 不计数
                        }

                        break;                      // 继续其他消息
                    }


            }
            base.WndProc(ref m);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawRubber(Rectangle rc)
        {
            if (rc.IsEmpty) return;

            // 防止 GDI 抛异常
            if (rc.Width == 0) rc.Width = 1;
            if (rc.Height == 0) rc.Height = 1;

            // XOR 可逆框 – “先画新 → 再擦旧” 的逻辑在调用方保证
            ControlPaint.DrawReversibleFrame(rc, Color.CornflowerBlue, FrameStyle.Dashed);

        }
        /// <summary>
        /// 把当前 _roiPreview 渲染成“半透明黑 + 透明洞 + 蓝框”并推送到遮罩窗口。
        /// 仅在鼠标抬起时调用一次。
        /// </summary>
        private void RenderMaskWithRoi()
        {
            int w = _screen.Width, h = _screen.Height;
            Rectangle roi = _roiPreview;
            roi.Offset(-_origin.X, -_origin.Y);               // 先平移
            roi = NormalizeAndClamp(roi, w, h);               // 再裁剪 0-w/h

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);

            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(_brushDim, 0, 0, w, h);          // 半透明黑


            g.FillRectangle(Brushes.Transparent, roi);       // ← 挖当前 ROI 洞
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawRectangle(_penBlue, roi);                  // 蓝框

            PushBitmap(bmp);    // ← 你已有的工具函数，内部做 GetHbitmap + ULW
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




        #region 绘制  
        /// <summary>
        /// 把拖拽中得到的 ROI 规范化，并裁剪到屏幕内：
        ///   1. 保证 Left ≤ Right, Top ≤ Bottom
        ///   2. 保证 X ≥ 0, Y ≥ 0
        ///   3. 让 X+W ≤ screenW, Y+H ≤ screenH
        ///   4. 最终宽高至少为 1
        /// </summary>
        private static Rectangle NormalizeAndClamp(Rectangle src, int screenW, int screenH)
        {
            // 1) 先把左/上/宽/高转成四边
            int L = Math.Min(src.Left, src.Right);
            int R = Math.Max(src.Left, src.Right);
            int T = Math.Min(src.Top, src.Bottom);
            int B = Math.Max(src.Top, src.Bottom);

            // 2) 裁剪到屏幕
            L = Math.Max(0, L);
            T = Math.Max(0, T);
            R = Math.Min(screenW, R);
            B = Math.Min(screenH, B);

            // 3) 至少 1×1
            if (R == L) R++;
            if (B == T) B++;

            return Rectangle.FromLTRB(L, T, R, B);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "<挂起>")]
        private void UpdateLayered()
        {
            using var bmp = new Bitmap(_screen.Width, _screen.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);

            g.Clear(Color.Transparent);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

            using var dimBrush = new SolidBrush(Color.FromArgb(_alphadim, Color.Black));
            g.FillRectangle(dimBrush, _screen);               // 整屏半透明黑  

            var roiRel = _roi;            // 全局→位图
            roiRel.Offset(-_origin.X, -_origin.Y);
            roiRel = NormalizeAndClamp(roiRel, _screen.Width, _screen.Height);
            g.FillRectangle(Brushes.Transparent, roiRel);
   
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
        /// <summary>调整半透明黑的 Alpha 并重绘遮罩。</summary>
        public void ChangeDim(int delta)
        {
            // delta 由滚轮传来：120 一格，正→变亮(减暗)，负→更暗
            int v = _alphadim - delta / 24;          // 120/24=5 每格改 5
            _alphadim = (byte)Math.Clamp(v, 30, 200); // 30~200 之间
            UpdateLayered();                          // 重新推遮罩位图
        }
        public void Dispose()
        {
            MainContext.Alpha = _alphadim; // 保存当前遮罩透明度
            MainContext.Save();
            MyWin32.UnregisterHotKey(Handle, MyWin32.HOTKEY_ID_ESC);
            DestroyWindow(Handle);
            MyWin32.PostQuitMessage(0);         // 直接告诉消息泵退出
        }



    }


}
