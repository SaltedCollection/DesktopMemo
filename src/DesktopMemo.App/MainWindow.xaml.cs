using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopMemo.App.Views;
using DesktopMemo.App.ViewModels;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Core.Models;
using WpfApp = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DesktopMemo.App;

/// <summary>
/// 主窗口代码隐藏层。
/// 负责承接 WPF 事件、协调 View 与 ViewModel 之间的瞬时交互，以及处理窗口级生命周期。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IWindowService _windowService;
    private readonly ITrayService _trayService;
    private readonly ILogService _logService;
    private bool _isClosing = false;
    private SettingsWindow? _settingsWindow; // 维持设置窗口单实例，避免重复打开造成状态分裂。
    private bool _isOpeningSettings = false; // 防止短时间内重复点击触发并发打开。

    public MainWindow(MainViewModel viewModel, IWindowService windowService, ITrayService trayService, ILogService logService)
    {
        try
        {
            InitializeComponent();

            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));

            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ThemeChanged += OnThemeChanged;
            _viewModel.OpenSettingsWindowRequested += OnOpenSettingsWindowRequested;
            _viewModel.TodoListViewModel.InputVisibilityChanged += OnTodoInputVisibilityChanged;

            ConfigureWindow();
            ConfigureTrayService();

            Loaded += OnLoaded;
            Closing += OnClosing;
            LocationChanged += OnLocationChanged; // 监听位置变化
            SizeChanged += OnSizeChanged;
            PreviewMouseDown += OnWindowPreviewMouseDown;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"窗口初始化错误: {ex.Message}", "DesktopMemo启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    /// <summary>
    /// 配置窗口首次加载时的层级与动画行为。
    /// </summary>
    private void ConfigureWindow()
    {
        Loaded += (s, e) =>
        {
            // 按用户恢复的模式应用窗口层级，而不是写死默认值。
            _windowService.SetTopmostMode(_viewModel.SelectedTopmostMode);
            _windowService.PlayFadeInAnimation();
        };
    }

    /// <summary>
    /// 把托盘菜单事件映射到 ViewModel 命令或窗口行为。
    /// </summary>
    private void ConfigureTrayService()
    {
        _trayService.TrayIconDoubleClick += (s, e) => _windowService.ToggleWindowVisibility();
        _trayService.ShowHideWindowClick += (s, e) => _windowService.ToggleWindowVisibility();
        _trayService.NewMemoClick += async (s, e) =>
        {
            _windowService.RestoreFromTray();
            await _viewModel.CreateMemoCommand.ExecuteAsync(null);
        };
        _trayService.SettingsClick += (s, e) =>
        {
            _windowService.RestoreFromTray();
            OpenSettingsWindow();
        };
        _trayService.MoveToPresetClick += (s, preset) => _viewModel.MoveToPresetCommand.Execute(preset);
        _trayService.RememberPositionClick += (s, e) => _viewModel.RememberPositionCommand.Execute(null);
        _trayService.RestorePositionClick += (s, e) => _viewModel.RestorePositionCommand.Execute(null);
        _trayService.ExportNotesClick += (s, e) => _viewModel.ExportMarkdownCommand.Execute(null);
        _trayService.ImportNotesClick += (s, e) => _viewModel.ImportLegacyCommand.Execute(null);
        _trayService.ClearContentClick += (s, e) => _viewModel.ClearEditorCommand.Execute(null);
        _trayService.AboutClick += (s, e) => _viewModel.ShowAboutCommand.Execute(null);
        _trayService.RestartTrayClick += (s, e) => _viewModel.TrayRestartCommand.Execute(null);
        _trayService.ClickThroughToggleClick += (s, enabled) => _viewModel.IsClickThroughEnabled = enabled;
        _trayService.ReenableExitPromptClick += async (s, e) =>
        {
            _viewModel.WindowSettings = _viewModel.WindowSettings with { ShowExitConfirmation = true };
            await _viewModel.GetSettingsService().SaveAsync(_viewModel.WindowSettings);
            _trayService.ShowBalloonTip("设置已更新", "已重新启用退出提示");
        };
        _trayService.ReenableDeletePromptClick += async (s, e) =>
        {
            _viewModel.WindowSettings = _viewModel.WindowSettings with { ShowDeleteConfirmation = true };
            await _viewModel.GetSettingsService().SaveAsync(_viewModel.WindowSettings);
            _trayService.ShowBalloonTip("设置已更新", "已重新启用删除确认提示");
        };
        _trayService.TopmostModeChangeClick += (s, mode) => _viewModel.SelectedTopmostMode = mode;
        _trayService.ExitClick += (s, e) => WpfApp.Current.Shutdown();
    }

    private void OnOpenSettingsWindowRequested(object? sender, EventArgs e)
    {
        OpenSettingsWindow();
    }

    /// <summary>
    /// 打开或激活设置窗口，确保整个应用只有一个设置窗口实例。
    /// </summary>
    private void OpenSettingsWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OpenSettingsWindow);
            return;
        }

        // 用户连续点击托盘或按钮时，这个保护可以避免创建出多个实例。
        if (_isOpeningSettings)
        {
            _logService.Debug("Settings", "设置窗口正在打开中，忽略重复请求");
            return;
        }

        try
        {
            _isOpeningSettings = true;
            _logService.Info("Settings", "请求打开设置窗口");
            _viewModel.IsSettingsPanelVisible = false;

            // 如果旧窗口还活着，就直接激活它，而不是新建。
            if (_settingsWindow != null)
            {
                try
                {
                    // 这里看 IsVisible 而不是 IsLoaded，因为窗口关闭过程中后者可能仍为 true。
                    if (_settingsWindow.IsVisible && _settingsWindow.WindowState != WindowState.Minimized)
                    {
                        _logService.Debug("Settings", "激活现有设置窗口");
                        ApplySettingsWindowState(_settingsWindow);
                        _settingsWindow.Activate();
                        _settingsWindow.Focus();
                        return;
                    }
                    else if (_settingsWindow.IsVisible && _settingsWindow.WindowState == WindowState.Minimized)
                    {
                        _logService.Debug("Settings", "恢复最小化的设置窗口");
                        _settingsWindow.WindowState = WindowState.Normal;
                        ApplySettingsWindowState(_settingsWindow);
                        _settingsWindow.Activate();
                        _settingsWindow.Focus();
                        return;
                    }
                    else
                    {
                        // 不可见说明窗口已经进入关闭流程，直接丢弃引用创建新实例。
                        _logService.Debug("Settings", "现有窗口不可见，将创建新实例");
                        _settingsWindow = null;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Warning("Settings", $"访问现有窗口失败: {ex.Message}");
                    _settingsWindow = null;
                }
            }

            // 仅在无法复用旧窗口时才创建新实例。
            _logService.Info("Settings", "创建新的设置窗口");
            var newWindow = new SettingsWindow(_viewModel);

            // 在 Closing 阶段就释放引用，避免窗口尚未 Closed 时阻塞下一次打开。
            newWindow.Closing += (s, e) =>
            {
                _logService.Debug("Settings", "设置窗口开始关闭，立即清空引用");
                if (_settingsWindow == s)
                {
                    _settingsWindow = null;
                }
            };

            // Closed 事件仅保留日志，用于区分“开始关闭”和“完全关闭”。
            newWindow.Closed += (s, e) =>
            {
                _logService.Debug("Settings", "设置窗口已完全关闭");
            };

            _settingsWindow = newWindow;
            ApplySettingsWindowState(_settingsWindow);
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _logService.Error("Settings", "打开设置窗口失败", ex);
            _settingsWindow = null;

            MessageBox.Show($"打开设置窗口失败: {ex.Message}", "DesktopMemo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 延后一拍重置标志，减少极短时间内重入。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isOpeningSettings = false;
            }), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 把主窗口的关键状态同步给设置窗口。
    /// </summary>
    private void ApplySettingsWindowState(SettingsWindow settingsWindow)
    {
        settingsWindow.Topmost = _viewModel.SelectedTopmostMode == TopmostMode.Always;
    }

    private void OpenSettingsWindowButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Info("UI", "点击按钮: 打开完整设置");
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSettingsPanelVisible))
        {
            Dispatcher.Invoke(() => ApplyQuickSettingsVisibility(_viewModel.IsSettingsPanelVisible));
        }
        else if (e.PropertyName == nameof(MainViewModel.BackgroundOpacity))
        {
            Dispatcher.Invoke(() => UpdateBackgroundOpacity(_viewModel.BackgroundOpacity));
        }
    }

    /// <summary>
    /// 控制右侧快捷设置面板的显隐与动画。
    /// </summary>
    private void ApplyQuickSettingsVisibility(bool show)
    {
        if (show)
        {
            QuickSettingsPanel.Visibility = Visibility.Visible;
            AnimateQuickSettingsPanel(true);
        }
        else
        {
            AnimateQuickSettingsPanel(false);
        }
    }

    /// <summary>
    /// 通过平移动画切换快捷设置面板，保持面板进出的一致手感。
    /// </summary>
    private void AnimateQuickSettingsPanel(bool show)
    {
        if (QuickSettingsPanel.RenderTransform is not System.Windows.Media.TranslateTransform transform)
        {
            transform = new System.Windows.Media.TranslateTransform();
            QuickSettingsPanel.RenderTransform = transform;
        }

        var animation = new DoubleAnimation
        {
            From = show ? 340 : 0,
            To = show ? 0 : 340,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new ExponentialEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        if (!show)
        {
            animation.Completed += (s, e) => QuickSettingsPanel.Visibility = Visibility.Collapsed;
        }

        transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            // 设置与数据已在 App 启动阶段恢复，这里只负责把状态映射到视觉层。
            ApplyQuickSettingsVisibility(_viewModel.IsSettingsPanelVisible);
            ApplyWindowSize(_viewModel.WindowSettings);
            
            // 首次加载时主动套一次主题，避免资源字典停留在默认态。
            ApplyTheme(_viewModel.SelectedTheme);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"应用程序初始化失败: {ex.Message}\n\n详细信息: {ex}", 
                "DesktopMemo 初始化错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            
            // 出错时尽量降级运行，避免一次初始化异常直接让应用不可用。
            try
            {
                ApplyQuickSettingsVisibility(false);
                _viewModel.SetStatus("初始化失败，使用默认设置运行");
            }
            catch
            {
                // 如果连默认设置都无法应用，则关闭应用程序
                WpfApp.Current.Shutdown();
            }
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.Invoke(() => ApplyTheme(theme));
    }

    private void ApplyTheme(AppTheme theme)
    {
        var actualTheme = theme;

        // “跟随系统”只是运行时策略，真正加载的仍然是 Light/Dark 资源字典。
        if (theme == AppTheme.System)
        {
            actualTheme = IsSystemDarkMode() ? AppTheme.Dark : AppTheme.Light;
        }

        try
        {
            // 主题资源字典按文件切换，避免在单个大字典里维护过多条件分支。
            var themeFileName = actualTheme == AppTheme.Dark
                ? "Dark.xaml"
                : "Light.xaml";

            var themeUri = new Uri(
                $"Resources/Themes/{themeFileName}",
                UriKind.Relative);

            // 先插入新字典再移除旧字典，可减少切换瞬间资源缺失导致的闪烁。
            var newThemeDict = new ResourceDictionary { Source = themeUri };

            // 只替换 Themes 目录下的那一本主题字典，其他资源保持不动。
            var oldThemeDict = System.Windows.Application.Current.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Themes/"));

            // 原子性替换：先插入新字典，再移除旧字典。
            System.Windows.Application.Current.Resources.MergedDictionaries.Insert(0, newThemeDict);

            if (oldThemeDict != null)
            {
                System.Windows.Application.Current.Resources.MergedDictionaries.Remove(oldThemeDict);
            }

            _logService?.Info("Theme", $"主题切换成功: {actualTheme}");
        }
        catch (Exception ex)
        {
            _logService?.Error("Theme", "主题切换失败", ex);
            // 主题只是表现层能力，失败时不能影响主功能。
            return;
        }

        // 主题切换后，部分控件样式需要重新按键名取一次资源。
        try
        {
            if (BackgroundOpacitySlider != null)
            {
                BackgroundOpacitySlider.Style = actualTheme == AppTheme.Dark
                    ? (Style)FindResource("AppleSliderStyleDark")
                    : (Style)FindResource("AppleSliderStyle");
            }
        }
        catch (Exception ex)
        {
            _logService?.Error("Theme", "更新滑块样式失败", ex);
        }
    }


    private bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false; // 默认为亮色模式
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ThemeChanged -= OnThemeChanged;
        _viewModel.OpenSettingsWindowRequested -= OnOpenSettingsWindowRequested;
        _viewModel.TodoListViewModel.InputVisibilityChanged -= OnTodoInputVisibilityChanged;
        PreviewMouseDown -= OnWindowPreviewMouseDown;
        SizeChanged -= OnSizeChanged;

        _viewModel.UpdateMainWindowSize(Width, Height, immediateSave: true);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // 位置变更用于更新设置面板上的只读坐标展示。
        _viewModel.UpdateCurrentPosition();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _viewModel.UpdateMainWindowSize(Width, Height);
    }

    private void ApplyWindowSize(WindowSettings settings)
    {
        if (!double.IsNaN(settings.Width) && settings.Width > 0)
        {
            Width = settings.Width;
        }

        if (!double.IsNaN(settings.Height) && settings.Height > 0)
        {
            Height = settings.Height;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !(_viewModel?.IsWindowPinned ?? false))
        {
            DragMove();
        }
    }

    private void QuickSettingsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == sender)
        {
            if (_viewModel.IsSettingsPanelVisible)
            {
                _viewModel.IsSettingsPanelVisible = false;
                e.Handled = true;
            }
        }
    }

    private void MainContentArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsSettingsPanelVisible)
        {
            _viewModel.IsSettingsPanelVisible = false;
            e.Handled = true;
        }
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // 关闭按钮可能被多次点击，这里做串行保护。
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        try
        {
            await HandleCloseButtonClickAsync();
        }
        catch (Exception ex)
        {
            // 关闭流程里牵涉对话框、托盘和持久化，异常需要尽量完整记录。
            _logService.Error("MainWindow", $"处理关闭按钮异常: {ex.Message}", ex);

            // 主关闭流程失败后，回退到尽可能简单的默认行为。
            try
            {
                if (_viewModel.WindowSettings.DefaultExitToTray)
                {
                    _viewModel.TrayHideWindowCommand.Execute(null);
                }
                else
                {
                    WpfApp.Current.Shutdown();
                }
            }
            catch (Exception fallbackEx)
            {
                _logService.Error("MainWindow", $"回退关闭失败: {fallbackEx.Message}", fallbackEx);
                // 最后的兜底，避免进程卡在半关闭状态。
                Environment.Exit(0);
            }
        }
        finally
        {
            _isClosing = false;
        }
    }

    private async Task HandleCloseButtonClickAsync()
    {
        // 关闭流程优先处理未保存内容，其次再处理“退出还是最小化到托盘”。
        if (_viewModel.IsEditMode && _viewModel.IsContentModified)
        {
            Views.UnsavedChangesDialog? unsavedDialog = null;
            bool? unsavedResult = null;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    unsavedDialog = new Views.UnsavedChangesDialog(_viewModel.LocalizationService)
                    {
                        Owner = this
                    };
                    unsavedResult = unsavedDialog.ShowDialog();
                }
                catch (Exception ex)
                {
                    _viewModel.SetStatus($"无法显示未保存确认对话框: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"UnsavedChangesDialog异常: {ex}");
                }
            });

            if (unsavedResult != true || unsavedDialog == null)
            {
                // 对话框失败时宁可取消关闭，也不要冒险丢失用户编辑内容。
                return;
            }

            switch (unsavedDialog.Action)
            {
                case Views.UnsavedChangesAction.Save:
                    await _viewModel.SaveMemoCommand.ExecuteAsync(null);
                    break;
                case Views.UnsavedChangesAction.Cancel:
                    return; // 取消关闭
                case Views.UnsavedChangesAction.Discard:
                    // 丢弃修改时把编辑器恢复到已保存版本，避免后续流程误保存脏内容。
                    _viewModel.EditorContent = _viewModel.SelectedMemo?.Content ?? string.Empty;
                    break; // 继续关闭流程
            }
        }

        // 检查是否需要显示退出确认
        if (_viewModel.WindowSettings.ShowExitConfirmation)
        {
            Views.ExitConfirmationDialog? dialog = null;
            bool? result = null;

            // 退出确认框需要在 UI 线程创建，确保 Owner 与模态行为正常。
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    dialog = new Views.ExitConfirmationDialog(_viewModel.LocalizationService)
                    {
                        Owner = this
                    };
                    result = dialog.ShowDialog();
                }
                catch (InvalidOperationException ex)
                {
                    _viewModel.SetStatus($"无法显示退出确认对话框: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"退出确认对话框InvalidOperationException: {ex}");
                }
                catch (Exception ex)
                {
                    _viewModel.SetStatus($"退出对话框错误: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"退出确认对话框异常: {ex}");
                }
            });

            if (result != true || dialog == null)
            {
                // 用户取消时直接中止；若对话框失败，则回退到默认关闭策略。
                if (result == null)
                {
                    // 对话框异常时，仍按已保存偏好执行默认动作。
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_viewModel.WindowSettings.DefaultExitToTray)
                        {
                            _viewModel.TrayHideWindowCommand.Execute(null);
                        }
                        else
                        {
                            WpfApp.Current.Shutdown();
                        }
                    });
                }
                return;
            }

            // “不再显示”要先更新内存，防止后续其他设置保存把这次选择覆盖掉。
            if (dialog.DontShowAgain)
            {
                bool exitToTray = dialog.Action == Views.ExitAction.MinimizeToTray;
                var newSettings = _viewModel.WindowSettings with
                {
                    ShowExitConfirmation = false,
                    DefaultExitToTray = exitToTray
                };
                _viewModel.WindowSettings = newSettings;

                // 退出动作优先，不等待设置保存完成。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _viewModel.GetSettingsService().SaveAsync(newSettings);
                        System.Diagnostics.Debug.WriteLine("退出确认设置已保存");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"保存退出设置失败: {ex}");
                        // 设置保存失败不影响退出动作本身。
                    }
                });
            }

            // 最终的窗口隐藏或进程退出必须回到 UI 线程执行。
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                switch (dialog.Action)
                {
                    case Views.ExitAction.MinimizeToTray:
                        _viewModel.TrayHideWindowCommand.Execute(null);
                        break;
                    case Views.ExitAction.Exit:
                        _viewModel.Dispose();
                        _trayService.Dispose();
                        if (_windowService is IDisposable disposableWindowService)
                        {
                            disposableWindowService.Dispose();
                        }
                        WpfApp.Current.Shutdown();
                        break;
                }
            });
        }
        else
        {
            // 已关闭确认框时，直接走持久化的默认退出策略。
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_viewModel.WindowSettings.DefaultExitToTray)
                {
                    _viewModel.TrayHideWindowCommand.Execute(null);
                }
                else
                {
                    _viewModel.Dispose();
                    _trayService.Dispose();
                    if (_windowService is IDisposable disposableWindowService)
                    {
                        disposableWindowService.Dispose();
                    }
                    WpfApp.Current.Shutdown();
                }
            });
        }
    }

    private void NoteTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 编辑器变更只更新“脏状态”，项目当前仍坚持显式保存。
        _viewModel.MarkEditing();
    }

    private void NoteTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    _ = _viewModel.SaveMemoCommand.ExecuteAsync(null);
                    break;
                case Key.N:
                    e.Handled = true;
                    _ = _viewModel.CreateMemoCommand.ExecuteAsync(null);
                    break;
                case Key.F:
                    e.Handled = true;
                    ShowFindDialog();
                    break;
                case Key.H:
                    e.Handled = true;
                    ShowReplaceDialog();
                    break;
                case Key.Tab:
                    e.Handled = true;
                    SwitchToNextMemo();
                    break;
                case Key.D:
                    e.Handled = true;
                    DuplicateCurrentLine();
                    break;
                case Key.OemOpenBrackets: // Ctrl + [
                    e.Handled = true;
                    DecreaseIndent();
                    break;
                case Key.OemCloseBrackets: // Ctrl + ]
                    e.Handled = true;
                    IncreaseIndent();
                    break;
            }
        }
        else if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.Tab:
                    e.Handled = true;
                    SwitchToPreviousMemo();
                    break;
            }
        }
        else if (e.KeyboardDevice.Modifiers == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.Tab:
                    e.Handled = true;
                    DecreaseIndent();
                    break;
                case Key.F3:
                    e.Handled = true;
                    // 查找上一个功能
                    _viewModel.SetStatus("查找上一个");
                    break;
            }
        }
        else if (e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Tab:
                    e.Handled = true;
                    InsertIndent();
                    break;
                case Key.F3:
                    e.Handled = true;
                    _viewModel.FindNextCommand?.Execute(null);
                    break;
            }
        }
    }

    private void ShowFindDialog()
    {
        // 当前先给出状态提示，后续可替换为真正的查找面板。
        _viewModel.SetStatus("查找功能 - 使用 F3 查找下一个");
    }

    private void ShowReplaceDialog()
    {
        // 当前先给出状态提示，后续可替换为真正的替换面板。
        _viewModel.SetStatus("替换功能 - 使用 Ctrl+H 替换");
    }

    private void SwitchToNextMemo()
    {
        var memos = _viewModel.Memos;
        if (_viewModel.SelectedMemo == null) return;

        var currentIndex = memos.IndexOf(_viewModel.SelectedMemo);
        if (currentIndex < memos.Count - 1)
        {
            _viewModel.SelectedMemo = memos[currentIndex + 1];
        }
        else if (memos.Count > 0)
        {
            _viewModel.SelectedMemo = memos[0];
        }
    }

    private void SwitchToPreviousMemo()
    {
        var memos = _viewModel.Memos;
        if (_viewModel.SelectedMemo == null) return;

        var currentIndex = memos.IndexOf(_viewModel.SelectedMemo);
        if (currentIndex > 0)
        {
            _viewModel.SelectedMemo = memos[currentIndex - 1];
        }
        else if (memos.Count > 0)
        {
            _viewModel.SelectedMemo = memos[memos.Count - 1];
        }
    }

    private void DuplicateCurrentLine()
    {
        var textBox = NoteTextBox;
        if (textBox == null) return;

        var caretIndex = textBox.CaretIndex;
        var text = textBox.Text;

        // 基于换行符定位当前逻辑行，然后把整行文本复制到下一行。
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caretIndex - 1)) + 1;
        var lineEnd = text.IndexOf('\n', caretIndex);
        if (lineEnd == -1) lineEnd = text.Length;

        var currentLine = text.Substring(lineStart, lineEnd - lineStart);
        var newText = text.Insert(lineEnd, "\n" + currentLine);

        textBox.Text = newText;
        textBox.CaretIndex = lineEnd + 1 + currentLine.Length;
    }

    private void InsertIndent()
    {
        var textBox = NoteTextBox;
        if (textBox == null) return;

        var caretIndex = textBox.CaretIndex;
        textBox.Text = textBox.Text.Insert(caretIndex, "    ");
        textBox.CaretIndex = caretIndex + 4;
    }

    private void IncreaseIndent()
    {
        var textBox = NoteTextBox;
        if (textBox == null) return;

        var caretIndex = textBox.CaretIndex;
        var text = textBox.Text;

        // 对整行统一增加 4 空格缩进，符合 Markdown 常见缩进约定。
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caretIndex - 1)) + 1;
        textBox.Text = text.Insert(lineStart, "    ");
        textBox.CaretIndex = caretIndex + 4;
    }

    private void DecreaseIndent()
    {
        var textBox = NoteTextBox;
        if (textBox == null) return;

        var caretIndex = textBox.CaretIndex;
        var text = textBox.Text;

        // 仅处理当前行开头的缩进，不试图做复杂的多行反缩进。
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caretIndex - 1)) + 1;

        // 优先删除一个标准缩进宽度，其次退化为删除单个空格。
        if (lineStart < text.Length && text.Substring(lineStart, Math.Min(4, text.Length - lineStart)) == "    ")
        {
            textBox.Text = text.Remove(lineStart, 4);
            textBox.CaretIndex = Math.Max(lineStart, caretIndex - 4);
        }
        else if (lineStart < text.Length && text[lineStart] == ' ')
        {
            // 删除单个空格
            textBox.Text = text.Remove(lineStart, 1);
            textBox.CaretIndex = Math.Max(lineStart, caretIndex - 1);
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            _ = _viewModel.CreateMemoCommand.ExecuteAsync(null);
        }
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider && _viewModel != null)
        {
            _viewModel.UpdateBackgroundOpacityFromPercent(slider.Value);
        }
    }

    private void UpdateBackgroundOpacity(double opacity)
    {
        if (MainContainer != null)
        {
            // 当前窗口背景透明度通过重新生成画刷来表达。
            var backgroundColor = System.Windows.Media.Color.FromArgb(
                (byte)(255 * opacity), // Alpha通道
                255, 255, 255); // RGB白色

            MainContainer.Background = new SolidColorBrush(backgroundColor);
        }
    }

    // ==================== Todo 编辑事件处理 ====================

    /// <summary>
    /// 当编辑 TextBox 加载时，自动聚焦并选择所有文本
    /// </summary>
    private void EditTodoTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    /// <summary>
    /// 当编辑 TextBox 失去焦点时，取消编辑
    /// </summary>
    private void EditTodoTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 延迟到后台优先级，避免与回车保存或点击保存按钮互相抢焦点。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_viewModel?.TodoListViewModel?.EditingTodoId != null)
            {
                _viewModel.TodoListViewModel.CancelEditTodoCommand.Execute(null);
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// 当新建待办 TextBox 加载时，自动聚焦
    /// </summary>
    private void NewTodoTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.Focus();
        }
    }

    /// <summary>
    /// 当TodoInput可见性改变时，自动聚焦输入框
    /// </summary>
    private void OnTodoInputVisibilityChanged(object? sender, bool isVisible)
    {
        if (isVisible)
        {
            Dispatcher.BeginInvoke(() =>
            {
                NewTodoTextBox?.Focus();
            }, DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// 窗口预览鼠标按下事件，用于检测外部点击
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.TodoListViewModel.IsInputVisible)
            return;

        // 点击区域不在输入框容器内时，视为用户结束了本次临时输入。
        if (TodoInputBorder != null && !TodoInputBorder.IsMouseOver)
        {
            _viewModel.TodoListViewModel.OnInputLostFocus();
        }
    }
}
