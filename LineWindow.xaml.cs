using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clothesline;

public partial class LineWindow : Window
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_TRAYICON = 0x8000 + 2; // WM_APP + 2

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    private const int HOTKEY_ID = 1;

    private const double WinH = 190;
    private const int MaxPegs = 10;
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(6);

    // 忽略词黑名单：某些程序会往剪贴板写垃圾文字（如 360 桌面打开"此电脑"时写 "Clink"），
    // 文字（trim 后）与黑名单完全一致就不挂卡片
    private static readonly HashSet<string> TextBlacklist = new(StringComparer.Ordinal)
    {
        "Clink",
    };
    private static readonly TimeSpan PeekHold = TimeSpan.FromSeconds(2.8);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(350);

    private readonly ObservableCollection<PegItem> _pegs = new();
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly Random _rng = new();

    private HwndSource? _src;
    private FolderWatcher? _watcher;
    private TrayIcon? _tray;
    private Window? _hotZone;

    // 与上一张挂出的内容（6 秒内）同像素则跳过：
    // 一次 Win+Shift+S 会同时进剪贴板和自动保存，剪贴板消息也可能连发两次
    private string? _lastHash;
    private DateTime _lastHashAt;
    private DateTime _suppressClipboardUntil; // 自己复制时别再监听自己

    private bool _shown;
    private bool _mouseInside;
    private string _hotkeyLabel = "不可用";

    // 拖拽状态
    private Point? _pressPoint;
    private PegItem? _pressItem;
    private bool _dragging;

    // 右键延迟弹菜单：等一会儿看是不是双击（双击 = 直接取下卡片）
    private readonly DispatcherTimer _rightClickTimer;
    private PegItem? _pendingRightItem;
    private FrameworkElement? _pendingRightTarget;

    public LineWindow()
    {
        InitializeComponent();

        Width = SystemParameters.PrimaryScreenWidth;
        Height = WinH;
        Top = -Height; // 初始完全移出屏幕（白底窗口不能压在桌面上）
        Rope.Data = RopeGeometry();
        Pegs.ItemsSource = _pegs;

        // 监听集合变化
        _pegs.CollectionChanged += (_, _) => { };

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!_mouseInside) SlideTo(false); };
        _peekTimer = new DispatcherTimer { Interval = PeekHold };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); if (!_mouseInside) SlideTo(false); };

        _rightClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _rightClickTimer.Tick += (_, _) =>
        {
            _rightClickTimer.Stop();
            var item = _pendingRightItem;
            var target = _pendingRightTarget;
            _pendingRightItem = null;
            _pendingRightTarget = null;
            if (item != null && target != null) ShowPegMenu(target, item);
        };

        MouseEnter += (_, _) => { _mouseInside = true; _hideTimer.Stop(); };
        MouseLeave += (_, _) => { _mouseInside = false; if (_shown) _hideTimer.Start(); };
        SourceInitialized += (_, _) => OnHwndReady();
    }

    private PathGeometry RopeGeometry()
    {
        var segment = new QuadraticBezierSegment(new Point(Width / 2, 27), new Point(Width, 13), true);
        var figure = new PathFigure(new Point(0, 13), new PathSegment[] { segment }, false);
        return new PathGeometry(new[] { figure });
    }

    private double HiddenTop => -Height;

    // ---------- HWND 相关初始化 ----------

    internal void SelftestPeg(string path) => AddCapture(path, fromClipboard: false);

    /// <summary>挂两张合成截图，渲染窗口内容为 PNG 后退出（公开仓库的演示图，不含桌面内容）。</summary>
    internal void StartDemo(string outPath)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            PegSynthetic(SyntheticCapture(Color.FromRgb(0x2E, 0x86, 0xAB), "Screenshot 01"));
            PegSynthetic(SyntheticCapture(Color.FromRgb(0xD3, 0x71, 0x2E), "Screenshot 02"));
            Top = 0;
            await Task.Delay(250);
            var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) encoder.Save(fs);
            Logger.Log("demo saved: " + outPath);
            Application.Current.Shutdown();
        });
    }

    private void PegSynthetic(BitmapSource bmp)
    {
        var item = new PegItem("(demo)", bmp, _rng.NextDouble() * 10 - 5);
        _pegs.Add(item);
        while (_pegs.Count > MaxPegs) _pegs.RemoveAt(0);
    }

    private static BitmapSource SyntheticCapture(Color bg, string label)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, 800, 500));
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 44, Brushes.White, 1.25);
            dc.DrawText(text, new Point(60, 215));
        }
        var rtb = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private void OnHwndReady()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _src = HwndSource.FromHwnd(hwnd);
        _src?.AddHook(WndProc);

        // 注意：不要在 SourceInitialized 里改 GWL_EXSTYLE——会把 AllowsTransparency
        // 窗口的 per-pixel alpha 弄坏（整窗变白底）。TOOLWINDOW/NOACTIVATE 由
        // ShowInTaskbar=False + ShowActivated=False 承担。

        if (!AddClipboardFormatListener(hwnd)) Logger.Log("clipboard listener FAILED");

        // Ctrl+Alt+T 常被占用，失败则回退 Ctrl+Alt+L
        if (RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, (uint)'T'))
        {
            _hotkeyLabel = "Ctrl+Alt+T";
            Logger.Log("hotkey: Ctrl+Alt+T");
        }
        else if (RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, (uint)'L'))
        {
            _hotkeyLabel = "Ctrl+Alt+L";
            Logger.Log("hotkey fallback: Ctrl+Alt+L");
        }
        else
        {
            Logger.Log("hotkey register FAILED (both Ctrl+Alt+T and Ctrl+Alt+L are taken)");
        }

        _watcher = new FolderWatcher(App.ScreenshotsDir);
        _watcher.CaptureFound = path => Dispatcher.BeginInvoke(() => AddCapture(path, fromClipboard: false));

        _tray = new TrayIcon(hwnd, WM_TRAYICON, "晾衣绳 - 截图管理工具");
        CreateHotZone();
        Logger.Log("ready");
    }

    /// <summary>
    /// 独立热区窗口：屏幕顶边 3px 高的透明条，鼠标碰上去唤出晾衣绳。
    /// 主窗口隐藏时整个在屏幕外，得靠它把绳子拉回来。
    /// </summary>
    private void CreateHotZone()
    {
        _hotZone = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
            Left = 0,
            Top = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = 3,
            Content = HitTestBrush(),
        };
        _hotZone.MouseEnter += (_, _) =>
        {
            _mouseInside = true;
            _hideTimer.Stop();
            Logger.Log("hotzone hover");
            if (!_shown) Peek();
        };
        _hotZone.MouseLeave += (_, _) => { _mouseInside = false; };
        _hotZone.Show();
        Logger.Log("hotzone ready");
    }

    /// <summary>alpha=1 的画刷：肉眼不可见，但操作系统的分层窗口命中测试需要 alpha&gt;0。</summary>
    private static Border HitTestBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return new Border { Background = brush };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                if (DateTime.UtcNow >= _suppressClipboardUntil)
                    Dispatcher.BeginInvoke(ReadClipboard);
                handled = true;
                break;
            case WM_HOTKEY:
                ToggleShow();
                handled = true;
                break;
            case WM_TRAYICON:
                int m = unchecked((int)lParam.ToInt64());
                if (m == WM_LBUTTONDBLCLK) ToggleShow();
                else if (m == WM_RBUTTONUP) ShowTrayMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    // ---------- 截图来源 ----------

    private void ReadClipboard()
    {
        try
        {
            // --- 图片 ---
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var opaque = Opaque(img);
                    string path = Path.Combine(App.CapturesDir, $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(opaque));
                    using (var fs = File.Create(path)) encoder.Save(fs);
                    AddCapture(path, fromClipboard: true);
                }
            }
            // --- 文字 ---
            if (Clipboard.ContainsText(TextDataFormat.Text))
            {
                var text = Clipboard.GetText(TextDataFormat.Text);
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 500)
                {
                    if (TextBlacklist.Contains(text.Trim()))
                    {
                        Logger.Log($"blacklisted text ignored: {text.Trim()}");
                    }
                    else
                    {
                        AddTextCapture(text);
                        Logger.Log($"clipboard text pegged ({text.Length} chars)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("clipboard read: " + ex.Message);
        }
    }

    /// <summary>将剪贴板文字渲染为缩略图（蓝色卡片风格），供晾衣绳展示。</summary>
    private static BitmapSource? RenderTextToBitmap(string text)
    {
        try
        {
            const int W = 420, H = 80;
            var px = new byte[W * H * 4]; // BGRA, alpha=255 全不透明

            // 浅蓝底色填充
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i]     = 0xFE; // B
                px[i + 1] = 0xF0; // G
                px[i + 2] = 0xE8; // R
                px[i + 3] = 0xFF; // A
            }
            // 底部装饰条（深蓝）
            for (int y = H - 4; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    px[i]     = 0xFB;
                    px[i + 1] = 0xDE;
                    px[i + 2] = 0xBB;
                }
            // 文字（深蓝，使用 DrawingContext 画到临时 Visual 再用 RenderTargetBitmap 提取）
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var display = text.Length > 60 ? text.Substring(0, 57) + "…" : text;
                var ft = new FormattedText(display, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14,
                    new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x1A, 0x73, 0xDA)), 1.25);
                dc.DrawText(ft, new Point(8, (H - ft.Height) / 2.0));
            }
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            // 把 DrawingVisual 渲染结果合成到底色缓冲区
            int stride = W * 4;
            int[] srcPx = new int[W * H];
            rtb.CopyPixels(srcPx, stride, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                int si = i / 4;
                int srcA = srcPx[si] >> 24;
                if (srcA == 0) continue; // 背景保持原色
                int sB = srcPx[si] & 0xFF;
                int sG = (srcPx[si] >> 8) & 0xFF;
                int sR = (srcPx[si] >> 16) & 0xFF;
                int dR = px[i + 2], dG = px[i + 1], dB = px[i];
                px[i + 2] = (byte)(dR + (sR - dR) * srcA / 255);
                px[i + 1] = (byte)(dG + (sG - dG) * srcA / 255);
                px[i]     = (byte)(dB + (sB - dB) * srcA / 255);
            }

            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Log("render text bitmap: " + ex.Message);
            return null;
        }
    }

    private void AddTextCapture(string text)
    {
        // 永不重复挂：绳上已有相同内容的文本卡片就跳过
        // （有些程序会反复把同一个词写进剪贴板，不去重就会挂一排一样的卡）
        foreach (var p in _pegs)
        {
            if (p.Type == PegType.Text && p.Text == text)
            {
                Logger.Log("text dedup skip (same text already on the line)");
                return;
            }
        }

        var item = new PegItem(text, _rng.NextDouble() * 10 - 5);
        _pegs.Add(item);
        while (_pegs.Count > MaxPegs) _pegs.RemoveAt(0);
        Logger.Log($"text pegged ({text.Length} chars)");
        if (_shown) SwingOne(item);
        else Peek();
    }

    private void AddCapture(string path, bool fromClipboard)
    {
        try
        {
            if (!File.Exists(path)) return;
            var thumb = LoadThumbnail(path);
            if (thumb == null) { Logger.Log("unreadable: " + path); return; }

            string hash = Hash(thumb);
            if (hash == _lastHash && DateTime.UtcNow - _lastHashAt < DedupWindow)
            {
                Logger.Log("dedup skip: " + path);
                return;
            }
            _lastHash = hash;
            _lastHashAt = DateTime.UtcNow;

            var item = new PegItem(path, thumb, _rng.NextDouble() * 10 - 5);
            _pegs.Add(item);
            while (_pegs.Count > MaxPegs) _pegs.RemoveAt(0);
            Logger.Log($"pegged ({(fromClipboard ? "clipboard" : "file")}): {path}");

            if (_shown) SwingOne(item);
            else Peek();
        }
        catch (Exception ex)
        {
            Logger.Log("addcapture: " + ex);
        }
    }

    /// <summary>alpha 抬满。微信等 DIB 来源的图 alpha 全 0 但 RGB 有效；
    /// 不能用 FormatConvertedBitmap（会过预乘空间，A=0 的图直接变全黑），只能按字节改。</summary>
    private static BitmapSource Opaque(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32)
        {
            int stride = src.PixelWidth * 4;
            var buffer = new byte[stride * (long)src.PixelHeight];
            src.CopyPixels(buffer, stride, 0);
            for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
            var opaque = BitmapSource.Create(src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY,
                PixelFormats.Bgra32, null, buffer, stride);
            opaque.Freeze();
            return opaque;
        }
        if (src.Format == PixelFormats.Pbgra32)
        {
            var opaque = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        return src;
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 420; // 展示用缩略图，省内存
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return Opaque(bmp);
        }
        catch (Exception ex)
        {
            Logger.Log("thumb " + path + ": " + ex.Message);
            return null;
        }
    }

    private static BitmapSource? LoadFullBitmap(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return Opaque(bmp);
        }
        catch (Exception ex)
        {
            Logger.Log("full " + path + ": " + ex.Message);
            return null;
        }
    }

    private static string Hash(BitmapSource src)
    {
        try
        {
            int stride = (src.PixelWidth * src.Format.BitsPerPixel + 7) / 8;
            long total = (long)stride * src.PixelHeight;
            int h = total <= 4_000_000 ? src.PixelHeight : Math.Max(1, (int)(4_000_000 / stride));
            var buffer = new byte[stride * h];
            src.CopyPixels(new Int32Rect(0, 0, src.PixelWidth, h), buffer, stride, 0);
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(buffer));
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    // ---------- 显示 / 收起 ----------

    private void Peek()
    {
        if (_shown) return;
        if (Fullscreen.Suppressed())
        {
            Logger.Log("peek suppressed (fullscreen)");
            return;
        }
        SlideTo(true);
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void ToggleShow()
    {
        _peekTimer.Stop();
        _hideTimer.Stop();
        SlideTo(!_shown);
    }

    private void SlideTo(bool show, bool instant = false)
    {
        _shown = show;
        double target = show ? 0 : HiddenTop;
        if (instant)
        {
            BeginAnimation(Window.TopProperty, null);
            Top = target;
        }
        else
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(show ? 300 : 230))
            {
                EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn },
            };
            BeginAnimation(Window.TopProperty, anim);
        }
        // 绳子显示期间热区让位（不然顶边 3px 两窗争鼠标会来回闪）
        if (_hotZone != null) _hotZone.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        if (show) SwingAll();
        Logger.Log(show ? "slide down" : "slide up");
    }

    private void SwingAll()
    {
        int i = 0;
        foreach (var item in _pegs)
        {
            SwingOne(item, 120 + i * 45);
            i++;
        }
    }

    private void SwingOne(PegItem item, int delayMs = 0)
    {
        double a = item.Angle;
        var anim = new DoubleAnimation(a - 13, a, TimeSpan.FromMilliseconds(950))
        {
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 5 },
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        item.Rot.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void UpdateAllCardVisibility() { }

    // ---------- 照片交互 ----------

    private void Peg_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PegItem item)
        {
            if (e.ChangedButton != MouseButton.Left) return; // 右键走 Peg_RightDown

            if (e.ClickCount >= 2)
            {
                // 双击：图片用默认看图程序打开，文字卡片用系统默认文本编辑器打开
                if (item.Type == PegType.Text) OpenTextInEditor(item);
                else OpenFile(item);
                _pressPoint = null;
                _pressItem = null;
                _dragging = false;
                return;
            }
            _pressPoint = e.GetPosition(Root);
            _pressItem = item;
            _dragging = false;
        }
    }

    private void Peg_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint == null || _pressItem == null || _dragging) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { _pressPoint = null; _pressItem = null; return; }
        var p = e.GetPosition(Root);
        if (Math.Abs(p.X - _pressPoint.Value.X) + Math.Abs(p.Y - _pressPoint.Value.Y) < 8) return;

        _dragging = true;
        var item = _pressItem;
        try
        {
            if (item.Type == PegType.Text && item.Text != null)
            {
                // 文字卡片：拖拽复制文本
                var data = new DataObject(DataFormats.Text, item.Text);
                DragDrop.DoDragDrop(Pegs, data, DragDropEffects.Copy | DragDropEffects.Link);
            }
            else
            {
                // 图片卡片：拖拽文件
                var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
                DragDrop.DoDragDrop(Pegs, data, DragDropEffects.Copy | DragDropEffects.Link);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("drag: " + ex.Message);
        }
        _pressPoint = null;
        _pressItem = null;
        _dragging = false;
    }

    private void Peg_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = _pressItem;
        var wasDragging = _dragging;
        _pressPoint = null;
        _pressItem = null;
        _dragging = false;
        if (item == null || wasDragging) return;
        if (e.ClickCount >= 2) return; // 双击交给 Peg_DoubleClick
        CopyToClipboard(item);
    }

    private void OpenFile(PegItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log("open: " + ex.Message);
        }
    }

    /// <summary>把文字落到临时 txt，再用系统默认文本编辑器打开（UTF-8 带 BOM，记事本不乱码）。</summary>
    private void OpenTextInEditor(PegItem item)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Clothesline", "texts");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "text_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
            File.WriteAllText(path, item.Text ?? string.Empty, new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Logger.Log("text opened in default editor: " + path);
        }
        catch (Exception ex)
        {
            Logger.Log("open text: " + ex.Message);
        }
    }

    private void Peg_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PegItem item) return;

        if (e.ClickCount >= 2)
        {
            // 右键双击：直接取下卡片（取消待弹出的菜单）
            _rightClickTimer.Stop();
            _pendingRightItem = null;
            _pendingRightTarget = null;
            _pegs.Remove(item);
            Logger.Log("right double-click: peg taken down");
            e.Handled = true;
            return;
        }

        // 右键单击：先等 320ms，确认不是双击再弹菜单
        _pendingRightItem = item;
        _pendingRightTarget = fe;
        _rightClickTimer.Stop();
        _rightClickTimer.Start();
        e.Handled = true;
    }

    private void ShowPegMenu(FrameworkElement fe, PegItem item)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = fe };

        if (item.Type == PegType.Text)
        {
            // 文字卡片：打开文本编辑器 / 复制文字
            var edit = new MenuItem { Header = "用文本编辑器打开" };
            edit.Click += (_, _) => OpenTextInEditor(item);
            var copy = new MenuItem { Header = "复制文字" };
            copy.Click += (_, _) =>
            {
                if (item.Text != null)
                {
                    Clipboard.SetText(item.Text);
                    FlashCopied(item);
                }
            };
            menu.Items.Add(edit);
            menu.Items.Add(copy);
        }
        else
        {
            // 图片卡片：打开图片/文件夹
            var open = new MenuItem { Header = "打开图片" };
            open.Click += (_, _) => OpenFile(item);
            var folder = new MenuItem { Header = "在文件夹中显示" };
            folder.Click += (_, _) => RevealFile(item.FilePath);
            menu.Items.Add(open);
            menu.Items.Add(folder);
        }

        var remove = new MenuItem { Header = "取下" };
        remove.Click += (_, _) => _pegs.Remove(item);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void Peg_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PegItem item) AnimateScale(item, 1.06);
    }

    private void Peg_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PegItem item) AnimateScale(item, 1.0);
    }

    private static void AnimateScale(PegItem item, double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void CopyToClipboard(PegItem item)
    {
        try
        {
            _suppressClipboardUntil = DateTime.UtcNow.AddMilliseconds(700); // 别监听自己
            if (item.Type == PegType.Text && item.Text != null)
            {
                Clipboard.SetText(item.Text);
                FlashCopied(item);
                Logger.Log($"text copied to clipboard ({item.Text.Length} chars)");
            }
            else
            {
                var full = LoadFullBitmap(item.FilePath); // 复制原图，不用缩略图
                if (full == null) return;
                Clipboard.SetImage(full);
                FlashCopied(item);
                Logger.Log("copied to clipboard: " + item.FilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("copy: " + ex.Message);
        }
    }

    private void FlashCopied(PegItem item)
    {
        item.CopyFeedback = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        t.Tick += (_, _) => { t.Stop(); item.CopyFeedback = false; };
        t.Start();
    }

    private void RevealFile(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");

    /// <summary>点在照片上时不收起，点空白绳子/空白区才收。</summary>
    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindPeg(d) != null) return;
        _peekTimer.Stop();
        SlideTo(false);
    }

    private static PegItem? FindPeg(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is PegItem item) return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------- 托盘 / 退出 ----------

    private void ShowTrayMenu()
    {
        try
        {
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = this };

            var toggle = new MenuItem { Header = $"显示 / 隐藏 ({_hotkeyLabel})" };
            toggle.Click += (_, _) => ToggleShow();
            var shots = new MenuItem { Header = "打开截图文件夹" };
            shots.Click += (_, _) => OpenFolder(App.ScreenshotsDir);
            var caps = new MenuItem { Header = "打开剪贴板截图文件夹" };
            caps.Click += (_, _) => OpenFolder(App.CapturesDir);
            var exit = new MenuItem { Header = "退出 Clothesline" };
            exit.Click += (_, _) => ExitApp();

            menu.Items.Add(toggle);
            menu.Items.Add(new Separator());
            menu.Items.Add(shots);
            menu.Items.Add(caps);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Logger.Log("traymenu: " + ex.Message);
        }
    }

    private void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""));
    }

    private void ExitApp()
    {
        Logger.Log("exit requested");
        try
        {
            _watcher?.Dispose();
            _tray?.Dispose();
            if (_src != null)
            {
                UnregisterHotKey(_src.Handle, HOTKEY_ID);
                RemoveClipboardFormatListener(_src.Handle);
            }
        }
        catch { }
        Application.Current.Shutdown();
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
