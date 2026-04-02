using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Core.Constants;
using System.Diagnostics;

namespace DesktopMemo.Infrastructure.Services;

/// <summary>
/// 封装 WPF 主窗口的显示、层级、透明度和穿透行为。
/// 该服务是 UI 层与 Win32 窗口操作之间的隔离层。
/// </summary>
public class WindowService : IWindowService, IDisposable
{
    private Window? _window;
    private bool _disposed;
    private TopmostMode _currentTopmostMode = TopmostMode.Desktop;
    private bool _isClickThroughEnabled = false;
    private bool _isWindowActivatedHandlerAttached = false;

    // Win32 API 扩展样式常量，用于控制穿透与分层渲染。
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>
    /// 绑定实际窗口实例，并挂接桌面模式需要的激活事件。
    /// </summary>
    public void Initialize(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        // 桌面模式下窗口被系统重新激活后，层级可能变化，需要再次校正。
        if (!_isWindowActivatedHandlerAttached)
        {
            _window.Activated += OnWindowActivated;
            _isWindowActivatedHandlerAttached = true;
        }
    }

    public void SetTopmostMode(TopmostMode mode)
    {
        if (_window == null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _currentTopmostMode = mode;

        // 三种模式分别映射到普通层级、桌面层级和系统置顶层级。
        switch (mode)
        {
            case TopmostMode.Normal:
                _window.Topmost = false;
                SafeSetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "Normal模式");
                break;

            case TopmostMode.Desktop:
                _window.Topmost = false;
                // 改进的桌面置顶实现
                SetDesktopTopmost(hwnd);
                break;

            case TopmostMode.Always:
                _window.Topmost = true;
                SafeSetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "Always模式");
                break;
        }
    }

    public TopmostMode GetCurrentTopmostMode() => _currentTopmostMode;

    public void SetClickThrough(bool enabled)
    {
        if (_window == null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (exStyle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"获取窗口样式失败，错误代码: {error}");
            return;
        }

        if (enabled)
        {
            // 透明点击依赖 WS_EX_TRANSPARENT；分层窗口样式则保证透明度/命中表现稳定。
            exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        }
        else
        {
            // 关闭穿透时保留分层样式，避免影响窗口透明度控制。
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        var result = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        if (result == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"设置窗口样式失败，错误代码: {error}");
        }
        _isClickThroughEnabled = enabled;
    }

    public bool IsClickThroughEnabled => _isClickThroughEnabled;

    public void SetWindowPosition(double x, double y)
    {
        if (_window == null)
        {
            return;
        }

        // 验证输入值是否有效
        if (double.IsNaN(x) || double.IsInfinity(x) || 
            double.IsNaN(y) || double.IsInfinity(y))
        {
            return;
        }

        try
        {
            var workingArea = SystemParameters.WorkArea;
            // 保留一小段可见区域，避免窗口被拖出屏幕后用户无法再拖回来。
            double minX = workingArea.Left - _window.Width + 50;
            double maxX = workingArea.Right - 50;
            double minY = workingArea.Top;
            double maxY = workingArea.Bottom - _window.Height;

            _window.Left = Math.Max(minX, Math.Min(maxX, x));
            _window.Top = Math.Max(minY, Math.Min(maxY, y));
        }
        catch
        {
            // 如果设置位置失败，忽略错误
        }
    }

    public (double X, double Y) GetWindowPosition()
    {
        if (_window == null)
        {
            return (0, 0);
        }

        return (_window.Left, _window.Top);
    }

    public void MoveToPresetPosition(string position)
    {
        if (_window == null)
        {
            return;
        }

        var workingArea = SystemParameters.WorkArea;
        double newX = 0,
            newY = 0;
        double windowWidth = _window.Width;
        double windowHeight = _window.Height;

        // 预设位置本质上是基于工作区做九宫格定位。
        switch (position)
        {
            case "TopLeft":
                newX = workingArea.Left + 10;
                newY = workingArea.Top + 10;
                break;
            case "TopCenter":
                newX = workingArea.Left + (workingArea.Width - windowWidth) / 2;
                newY = workingArea.Top + 10;
                break;
            case "TopRight":
                newX = workingArea.Right - windowWidth - 10;
                newY = workingArea.Top + 10;
                break;
            case "MiddleLeft":
                newX = workingArea.Left + 10;
                newY = workingArea.Top + (workingArea.Height - windowHeight) / 2;
                break;
            case "Center":
                newX = workingArea.Left + (workingArea.Width - windowWidth) / 2;
                newY = workingArea.Top + (workingArea.Height - windowHeight) / 2;
                break;
            case "MiddleRight":
                newX = workingArea.Right - windowWidth - 10;
                newY = workingArea.Top + (workingArea.Height - windowHeight) / 2;
                break;
            case "BottomLeft":
                newX = workingArea.Left + 10;
                newY = workingArea.Bottom - windowHeight - 10;
                break;
            case "BottomCenter":
                newX = workingArea.Left + (workingArea.Width - windowWidth) / 2;
                newY = workingArea.Bottom - windowHeight - 10;
                break;
            case "BottomRight":
                newX = workingArea.Right - windowWidth - 10;
                newY = workingArea.Bottom - windowHeight - 10;
                break;
            default:
                return;
        }

        SetWindowPosition(newX, newY);
    }

    private double _backgroundOpacity = WindowConstants.DEFAULT_TRANSPARENCY; // 存储背景透明度值

    /// <summary>
    /// 保存背景透明度数值。
    /// 实际视觉生效由 View 层绑定或样式逻辑消费该值。
    /// </summary>
    public void SetWindowOpacity(double opacity)
    {
        // 验证透明度值是否有效
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            opacity = WindowConstants.DEFAULT_TRANSPARENCY; // 使用默认透明度值
            System.Diagnostics.Debug.WriteLine($"透明度值无效，使用默认值: {opacity}");
        }

        _backgroundOpacity = Math.Max(0, Math.Min(WindowConstants.MAX_TRANSPARENCY, opacity)); // 确保在有效范围内
        System.Diagnostics.Debug.WriteLine($"窗口服务存储背景透明度: {_backgroundOpacity}");
    }

    public double GetWindowOpacity()
    {
        return _backgroundOpacity; // 返回存储的背景透明度值
    }

    /// <summary>
    /// 播放窗口淡入动画，用于首次显示或从托盘恢复时的过渡。
    /// </summary>
    public void PlayFadeInAnimation()
    {
        if (_window == null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
        };

        _window.BeginAnimation(Window.OpacityProperty, animation);
    }

    public void ToggleWindowVisibility()
    {
        if (_window == null)
        {
            return;
        }

        if (_window.Visibility == Visibility.Visible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
            _window.Activate();
        }
    }

    public void MinimizeToTray()
    {
        if (_window == null)
        {
            return;
        }

        _window.Hide();
    }

    public void RestoreFromTray()
    {
        if (_window == null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
        _window.Focus();
    }

    /// <summary>
    /// 改进的桌面置顶实现，确保窗口始终在桌面背景上方
    /// </summary>
    private void SetDesktopTopmost(IntPtr hwnd)
    {
        try
        {
            // 先取消系统级置顶，避免它压过正常应用窗口。
            SafeSetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-设置非置顶");

            // Program Manager / WorkerW 是 Windows 桌面图标层级体系中的关键窗口。
            IntPtr progmanHwnd = FindWindow("Progman", "Program Manager");

            if (progmanHwnd != IntPtr.Zero && IsWindow(progmanHwnd))
            {
                // 某些系统需要先唤起 WorkerW，桌面图标视图才会暴露出来。
                SendMessage(progmanHwnd, 0x052C, IntPtr.Zero, IntPtr.Zero);

                // 遍历 WorkerW，定位包含桌面图标视图的那一层。
                IntPtr workerw = IntPtr.Zero;
                IntPtr shellDllDefView = IntPtr.Zero;

                do
                {
                    workerw = FindWindowEx(IntPtr.Zero, workerw, "WorkerW", null!);
                    if (workerw != IntPtr.Zero)
                    {
                        shellDllDefView = FindWindowEx(workerw, IntPtr.Zero, "SHELLDLL_DefView", null!);
                        if (shellDllDefView != IntPtr.Zero)
                        {
                            break;
                        }
                    }
                } while (workerw != IntPtr.Zero);
                
                if (workerw != IntPtr.Zero)
                {
                    // 放到 WorkerW 之上，可实现“压在桌面上、但又不抢到应用最前”的效果。
                    SafeSetWindowPos(hwnd, workerw, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-WorkerW上方");
                }
                else
                {
                    // 老系统或特殊壳层下可能找不到 WorkerW，退回到较保守的放置方式。
                    SafeSetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-底部");
                    SafeSetWindowPos(hwnd, progmanHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-Progman上方");
                }
            }
            else
            {
                // 若系统窗口结构识别失败，至少保证不会错误置顶到普通应用之上。
                SafeSetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-找不到Progman");
            }
        }
        catch
        {
            // 任一步骤出错都退回到底层放置，优先保证稳定性。
            SafeSetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE, "桌面模式-异常处理");
        }
    }
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 窗口激活事件处理器 - 用于桌面置顶模式
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // 普通模式和系统置顶模式无需重复纠正层级。
        if (_currentTopmostMode == TopmostMode.Desktop && _window != null)
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // 延后到后台优先级执行，避免与当前激活过程抢占消息循环。
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => SetDesktopTopmost(hwnd)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    /// <summary>
    /// 安全的SetWindowPos调用，包含错误检查和日志记录
    /// </summary>
    private static void SafeSetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags, string operation)
    {
        try
        {
            var success = SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"SetWindowPos失败 - 操作: {operation}, 错误代码: {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetWindowPos异常 - 操作: {operation}, 异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        
        // 释放前取消事件订阅，避免窗口对象被服务意外持有。
        if (_window != null && _isWindowActivatedHandlerAttached)
        {
            _window.Activated -= OnWindowActivated;
            _isWindowActivatedHandlerAttached = false;
        }
        
        _window = null;
    }
}
