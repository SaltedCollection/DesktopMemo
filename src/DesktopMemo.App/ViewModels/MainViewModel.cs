using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Core.Models;
using DesktopMemo.Core.Constants;
using DesktopMemo.Core.Helpers;
using DesktopMemo.Infrastructure.Services;
using System.IO;

namespace DesktopMemo.App.ViewModels;

/// <summary>
/// 应用主视图模型。
/// 负责协调备忘录编辑、待办模式切换、窗口设置、托盘交互以及本地化状态。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMemoRepository _memoRepository;
    private readonly ISettingsService _settingsService;
    private readonly IWindowService _windowService;
    private readonly ITrayService _trayService;
    private readonly IMemoSearchService _searchService;
    private readonly MemoMigrationService _migrationService;
    private readonly TodoListViewModel _todoListViewModel;
    private readonly ILocalizationService _localizationService;
    private readonly ILogService _logService;
    private readonly LogViewModel _logViewModel;
    // 高频设置变更统一走防抖保存，避免拖拽窗口或快速切换选项时持续刷盘。
    private readonly DebounceHelper _settingsSaveDebouncer;

    [ObservableProperty]
    private ObservableCollection<Memo> _memos = new();

    [ObservableProperty]
    private Memo? _selectedMemo;

    [ObservableProperty]
    private string _editorTitle = string.Empty;

    [ObservableProperty]
    private string _editorContent = string.Empty;

    [ObservableProperty]
    private WindowSettings _windowSettings = WindowSettings.Default;

    [ObservableProperty]
    private bool _showExitConfirmation;

    [ObservableProperty]
    private bool _showDeleteConfirmation;

    [ObservableProperty]
    private bool _defaultExitToTray;

    [ObservableProperty]
    private bool _isSettingsPanelVisible;

    [ObservableProperty]
    private bool _isLogPanelVisible;

    [ObservableProperty]
    private bool _isWindowPinned;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _isInTodoListMode;

    [ObservableProperty]
    private string _appInfo = string.Empty;

    [ObservableProperty]
    private TopmostMode _selectedTopmostMode = TopmostMode.Desktop;

    [ObservableProperty]
    private double _backgroundOpacity = 0.0;

    [ObservableProperty]
    private double _backgroundOpacityPercent = 0.0;

    [ObservableProperty]
    private bool _isClickThroughEnabled;

    [ObservableProperty]
    private bool _isAutoStartEnabled;

    [ObservableProperty]
    private double _currentLeft;

    [ObservableProperty]
    private double _currentTop;

    [ObservableProperty]
    private string _customPositionX = "0";

    [ObservableProperty]
    private string _customPositionY = "0";

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private string _replaceKeyword = string.Empty;

    [ObservableProperty]
    private bool _isCaseSensitive;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private bool _isTrayEnabled = true;

    private bool _isDisposing;
    private bool _isInitializing;

    // 进入编辑模式时记录初始正文，用于判断是否存在未保存修改。
    private string? _originalContent;

    [ObservableProperty]
    private bool _isContentModified;

    public int MemoCount => Memos.Count;

    public bool HasSelectedMemo => SelectedMemo is not null;

    public TodoListViewModel TodoListViewModel => _todoListViewModel;

    public LogViewModel LogViewModel => _logViewModel;

    public ILocalizationService LocalizationService => _localizationService;

    public IEnumerable<CultureInfo> AvailableLanguages => _localizationService.GetSupportedLanguages();

    [ObservableProperty]
    private CultureInfo? _selectedLanguage;

    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.Light;

    private bool _disposed;

    public MainViewModel(
        IMemoRepository memoRepository,
        ISettingsService settingsService,
        IWindowService windowService,
        ITrayService trayService,
        IMemoSearchService searchService,
        MemoMigrationService migrationService,
        TodoListViewModel todoListViewModel,
        ILocalizationService localizationService,
        ILogService logService,
        LogViewModel logViewModel)
    {
        _memoRepository = memoRepository;
        _settingsService = settingsService;
        _windowService = windowService;
        _trayService = trayService;
        _searchService = searchService;
        _migrationService = migrationService;
        _todoListViewModel = todoListViewModel;
        _localizationService = localizationService;
        _logService = logService;
        _logViewModel = logViewModel;
        _settingsSaveDebouncer = new DebounceHelper(500); // 500毫秒防抖延迟

        Memos.CollectionChanged += OnMemosCollectionChanged;

        // 订阅语言切换事件
        _localizationService.LanguageChanged += OnLanguageChanged;

        // 订阅待办事项输入区域可见性变化事件
        _todoListViewModel.InputVisibilityChanged += OnTodoInputVisibilityChanged;

        // 初始化应用信息
        InitializeAppInfo();
    }

    /// <summary>
    /// 启动时恢复设置、加载数据并初始化托盘与待办视图。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _isInitializing = true;
        
        var settings = await _settingsService.LoadAsync(cancellationToken);
        ApplyWindowSettings(settings);

        var memos = await _memoRepository.GetAllAsync(cancellationToken);
        System.Diagnostics.Debug.WriteLine($"[MainViewModel初始化] 从仓库加载了 {memos.Count} 条备忘录");

        if (memos.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[MainViewModel初始化] 备忘录为空，尝试从旧版本导入");
            var migrated = await _migrationService.LoadFromLegacyAsync();
            System.Diagnostics.Debug.WriteLine($"[MainViewModel初始化] 找到 {migrated.Count} 条旧版本备忘录");
            foreach (var migratedMemo in migrated)
            {
                await _memoRepository.AddAsync(migratedMemo, cancellationToken);
            }

            memos = await _memoRepository.GetAllAsync(cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[MainViewModel初始化] 导入后共有 {memos.Count} 条备忘录");
        }

        Memos = new ObservableCollection<Memo>(memos.OrderByDescending(m => m.UpdatedAt));

        // 默认选中最新一条备忘录，让编辑区在启动后马上可用。
        SelectedMemo = Memos.FirstOrDefault();
        if (SelectedMemo is not null)
        {
            EditorTitle = SelectedMemo.Title;
            EditorContent = SelectedMemo.Content;
        }
        else
        {
            EditorTitle = string.Empty;
            EditorContent = string.Empty;
        }

        if (IsTrayEnabled)
        {
            _trayService.Show();
        }
        else
        {
            _trayService.Hide();
        }

        // 待办子视图模型在主流程中统一初始化，保证页面切换时状态已就绪。
        await _todoListViewModel.InitializeAsync(cancellationToken);

        // 初始化选择的语言
        SelectedLanguage = _localizationService.CurrentCulture;
        
        // 初始化主题
        SelectedTheme = settings.Theme;
        
        // 初始化托盘菜单文本
        _trayService.UpdateMenuTexts(key => _localizationService[key]);

        // 恢复“备忘录页 / 待办页”停留位置，减少重启后的上下文丢失。
        if (settings.CurrentPage == "todo")
        {
            IsInTodoListMode = true;
        }

        // 恢复待办事项输入区域的显示状态
        _todoListViewModel.IsInputVisible = settings.TodoInputVisible;

        _logService.Info("App", "应用程序初始化完成");
        _trayService.UpdateText("DesktopMemo");
        _trayService.UpdateTopmostState(SelectedTopmostMode);
        _trayService.UpdateClickThroughState(IsClickThroughEnabled);

        // 自启动状态依赖注册表，需要在 UI 与服务初始化后补查。
        CheckAutoStartStatus();
        
        _isInitializing = false;
    }

    /// <summary>
    /// 把持久化设置映射为当前运行态，并同步到底层窗口服务。
    /// </summary>
    private void ApplyWindowSettings(WindowSettings settings)
    {
        WindowSettings = settings;
        ShowExitConfirmation = settings.ShowExitConfirmation;
        ShowDeleteConfirmation = settings.ShowDeleteConfirmation;
        DefaultExitToTray = settings.DefaultExitToTray;
        IsTrayEnabled = settings.IsTrayEnabled;

        if (!double.IsNaN(settings.Left) && !double.IsNaN(settings.Top))
        {
            _windowService.SetWindowPosition(settings.Left, settings.Top);
        }

        // 设置文件可能来自旧版本或被用户手工修改，需要先做归一化。
        var transparencyValue = TransparencyHelper.NormalizeTransparency(settings.Transparency);
        System.Diagnostics.Debug.WriteLine($"设置加载: 原始透明度={settings.Transparency}, 规范化后={transparencyValue}");

        _windowService.SetWindowOpacity(transparencyValue);
        _windowService.SetClickThrough(settings.IsClickThrough);
        var mode = settings.IsTopMost
            ? TopmostMode.Always
            : (settings.IsDesktopMode ? TopmostMode.Desktop : TopmostMode.Normal);
        _windowService.SetTopmostMode(mode);

        SelectedTopmostMode = _windowService.GetCurrentTopmostMode();

        // 再次读取窗口服务中的实际值，确保 ViewModel 和底层服务状态一致。
        var actualOpacity = _windowService.GetWindowOpacity();
        BackgroundOpacity = actualOpacity;

        // 百分比是 UI 展示值，真实透明度仍以 0~MAX_TRANSPARENCY 的值保存。
        var calculatedPercent = TransparencyHelper.ToPercent(actualOpacity);
        BackgroundOpacityPercent = double.IsNaN(calculatedPercent) ? 0.0 : calculatedPercent;

        System.Diagnostics.Debug.WriteLine($"透明度初始化完成: 实际值={actualOpacity}, 百分比={BackgroundOpacityPercent}");
        
        IsClickThroughEnabled = _windowService.IsClickThroughEnabled;
        
        // 固定状态只影响界面行为，由 ViewModel 维护并持久化。
        System.Diagnostics.Debug.WriteLine($"[ApplyWindowSettings] 恢复窗口固定状态: {settings.IsWindowPinned}");
        IsWindowPinned = settings.IsWindowPinned;
        
        UpdateCurrentPosition();
    }

    public void UpdateCurrentPosition()
    {
        var (left, top) = _windowService.GetWindowPosition();
        CurrentLeft = left;
        CurrentTop = top;
        CustomPositionX = left.ToString("F0");
        CustomPositionY = top.ToString("F0");
    }

    public void UpdateBackgroundOpacityFromPercent(double percent)
    {
        var actualOpacity = TransparencyHelper.FromPercent(percent);
        BackgroundOpacity = actualOpacity;
        BackgroundOpacityPercent = percent;
        SetStatus($"透明度已调整为 {(int)percent}%");
    }

    /// <summary>
    /// 主窗口尺寸变化时更新内存设置，并在高频变化场景下延迟保存。
    /// </summary>
    public void UpdateMainWindowSize(double width, double height, bool immediateSave = false)
    {
        if (_isInitializing)
        {
            return;
        }

        if (double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(height) || double.IsInfinity(height))
        {
            return;
        }

        WindowSettings = WindowSettings.WithSize(width, height);

        if (immediateSave)
        {
            _ = Task.Run(async () => await _settingsService.SaveAsync(WindowSettings));
            return;
        }

        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"主窗口大小已保存: {width}x{height}");
        });
    }

    /// <summary>
    /// 设置窗口移动或缩放后同步边界信息。
    /// </summary>
    public void UpdateSettingsWindowBounds(double width, double height, double left, double top, bool immediateSave = false)
    {
        if (_isInitializing)
        {
            return;
        }

        if (double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(height) || double.IsInfinity(height))
        {
            return;
        }

        if (double.IsNaN(left) || double.IsInfinity(left) || double.IsNaN(top) || double.IsInfinity(top))
        {
            return;
        }

        WindowSettings = WindowSettings with
        {
            SettingsWindowWidth = width,
            SettingsWindowHeight = height,
            SettingsWindowLeft = left,
            SettingsWindowTop = top
        };

        if (immediateSave)
        {
            _ = Task.Run(async () => await _settingsService.SaveAsync(WindowSettings));
            return;
        }

        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"设置窗口尺寸已保存: {width}x{height}");
        });
    }

    [RelayCommand]
    private async Task CreateMemoAsync()
    {
        var memo = Memo.CreateNew(_localizationService["Memo_NewTitle"], string.Empty);
        await _memoRepository.AddAsync(memo);

        // 新建后直接切换到编辑态，符合便签工具“即建即写”的使用预期。
        Memos.Insert(0, memo);
        SelectedMemo = memo;
        EditorTitle = memo.Title;
        EditorContent = memo.Content;
        IsEditMode = true;
        OnPropertyChanged(nameof(MemoCount));
        _logService.Info("Memo", $"创建新备忘录: {memo.Title}");
        SetStatus("已新建备忘录");
    }

    [RelayCommand]
    private async Task SaveMemoAsync()
    {
        if (SelectedMemo is null)
        {
            return;
        }

        // 内容和元数据采用不可变 record 链式更新，保证修改轨迹清晰。
        var updated = SelectedMemo
            .WithContent(EditorContent, DateTimeOffset.Now)
            .WithMetadata(EditorTitle, SelectedMemo.Tags, SelectedMemo.IsPinned, DateTimeOffset.Now);

        await _memoRepository.UpdateAsync(updated);

        var index = Memos.IndexOf(SelectedMemo);
        if (index >= 0)
        {
            Memos[index] = updated;
            SelectedMemo = updated;
        }

        // 保存成功后重置“脏状态”基线。
        _originalContent = EditorContent;
        IsContentModified = false;

        OnPropertyChanged(nameof(MemoCount));
        _logService.Info("Memo", $"保存备忘录: {updated.Title}");
        SetStatus("已保存");
    }

    [RelayCommand]
    private async Task DeleteMemoAsync()
    {
        if (SelectedMemo is null)
        {
            return;
        }

        // 删除确认由设置控制；真正删除前先在 UI 线程显示对话框。
        if (WindowSettings.ShowDeleteConfirmation)
        {
            Views.ConfirmationDialog? dialog = null;
            bool? result = null;

            // 对话框必须在 UI 线程创建和显示，否则 WPF Owner 关系会不稳定。
            try
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new Views.ConfirmationDialog(
                        _localizationService,
                        _localizationService["Dialog_DeleteConfirm_Title"],
                        string.Format(_localizationService["Dialog_DeleteConfirm_Message"], SelectedMemo.DisplayTitle));

                    // Owner 仅在主窗口已加载时设置，避免 ShowDialog 因窗口状态异常抛错。
                    if (System.Windows.Application.Current.MainWindow != null &&
                        System.Windows.Application.Current.MainWindow.IsLoaded)
                    {
                        dialog.Owner = System.Windows.Application.Current.MainWindow;
                    }

                    result = dialog.ShowDialog();
                });
            }
            catch (InvalidOperationException ex)
            {
                // 对话框无法显示（可能是Owner问题或窗口状态异常）
                _logService.Error("UI", "无法显示删除确认对话框", ex);
                SetStatus($"无法显示删除确认对话框: {ex.Message}", LogLevel.Error);
                System.Diagnostics.Debug.WriteLine($"删除确认对话框InvalidOperationException: {ex}");
                return;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Win32相关错误
                _logService.Error("UI", "系统错误，无法显示对话框", ex);
                SetStatus($"系统错误，无法显示对话框: {ex.Message}", LogLevel.Error);
                System.Diagnostics.Debug.WriteLine($"删除确认对话框Win32Exception: {ex}");
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 权限问题
                _logService.Error("UI", "权限不足，无法显示对话框", ex);
                SetStatus($"权限不足，无法显示对话框: {ex.Message}", LogLevel.Error);
                System.Diagnostics.Debug.WriteLine($"删除确认对话框UnauthorizedAccessException: {ex}");
                return;
            }
            catch (Exception ex)
            {
                // 其他未预期异常
                _logService.Error("UI", "对话框错误", ex);
                SetStatus($"对话框错误: {ex.Message}", LogLevel.Error);
                System.Diagnostics.Debug.WriteLine($"删除确认对话框未知异常: {ex}");
                return;
            }

            if (result != true || dialog == null)
            {
                SetStatus("已取消删除");
                return;
            }

            // “不再显示”需要立即落到内存，避免后续其他设置保存把它覆盖回去。
            if (dialog.DontShowAgain)
            {
                var newSettings = WindowSettings with { ShowDeleteConfirmation = false };
                WindowSettings = newSettings;

                // 删除动作不应被设置保存阻塞，因此这里 fire-and-forget。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _settingsService.SaveAsync(newSettings);
                        System.Diagnostics.Debug.WriteLine("删除确认设置已保存");
                    }
                    catch (Exception ex)
                    {
                        // 删除已获用户确认，设置保存失败不应反向阻断删除流程。
                        System.Diagnostics.Debug.WriteLine($"保存删除设置失败: {ex}");
                    }
                });
            }
        }

        // 仓储层是软删除；UI 层则把当前项从可见集合中移除。
        var deleting = SelectedMemo;
        _logService.Info("Memo", $"删除备忘录: {deleting.Title}");
        await _memoRepository.DeleteAsync(deleting.Id);

        Memos.Remove(deleting);
        SelectedMemo = Memos.FirstOrDefault();

        if (SelectedMemo is not null)
        {
            EditorTitle = SelectedMemo.Title;
            EditorContent = SelectedMemo.Content;
        }
        else
        {
            EditorTitle = string.Empty;
            EditorContent = string.Empty;
        }

        IsEditMode = false;
        OnPropertyChanged(nameof(MemoCount));
        SetStatus("已删除");
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsPanelVisible = !IsSettingsPanelVisible;
        if (IsSettingsPanelVisible)
        {
            IsLogPanelVisible = false; // 设置与日志面板互斥，避免右侧空间冲突。
        }
        SetStatus(IsSettingsPanelVisible ? "打开设置" : "关闭设置");
    }

    [RelayCommand]
    private void OpenSettingsWindow()
    {
        OpenSettingsWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleLog()
    {
        if (IsLogPanelVisible)
        {
            // 从日志页面返回设置页面
            IsLogPanelVisible = false;
            SetStatus("返回设置");
        }
        else
        {
            // 从设置页面打开日志页面
            IsLogPanelVisible = true;
            _logViewModel.RefreshLogsCommand.Execute(null); // 刷新日志
            SetStatus("打开日志");
        }
    }

    [RelayCommand]
    private void TogglePin()
    {
        System.Diagnostics.Debug.WriteLine($"[TogglePin] 切换前: IsWindowPinned={IsWindowPinned}, _isInitializing={_isInitializing}");
        IsWindowPinned = !IsWindowPinned;
        System.Diagnostics.Debug.WriteLine($"[TogglePin] 切换后: IsWindowPinned={IsWindowPinned}");
        SetStatus(IsWindowPinned ? "窗口已固定，无法拖动" : "窗口已解除固定");
    }

    [RelayCommand]
    private async Task PersistWindowSettingsAsync()
    {
        _windowService.SetTopmostMode(SelectedTopmostMode);

        bool isTopMost = SelectedTopmostMode == TopmostMode.Always;
        bool isDesktopMode = SelectedTopmostMode == TopmostMode.Desktop;

        _windowService.SetClickThrough(IsClickThroughEnabled);
        _windowService.SetWindowOpacity(BackgroundOpacity);

        UpdateCurrentPosition();

        WindowSettings = WindowSettings.WithLocation(CurrentLeft, CurrentTop)
            .WithAppearance(BackgroundOpacity, isTopMost, isDesktopMode, IsClickThroughEnabled);

        await _settingsService.SaveAsync(WindowSettings);
        _logService.Info("Settings", "保存窗口设置");
        SetStatus("设置已保存");
        _trayService.UpdateTopmostState(SelectedTopmostMode);
        _trayService.UpdateClickThroughState(IsClickThroughEnabled);
    }

    [RelayCommand]
    private void EnterEditMode(Memo? memo)
    {
        if (memo is null)
        {
            return;
        }

        SelectedMemo = memo;
        IsEditMode = true;
        EditorTitle = memo.Title;
        EditorContent = memo.Content;
        _originalContent = memo.Content;
        IsContentModified = false;
        SetStatus("编辑中...");
    }

    [RelayCommand]
    private async Task SaveAndBackAsync()
    {
        await SaveMemoAsync();
        IsEditMode = false;
        SetStatus("已保存并返回");
    }

    [RelayCommand]
    private async Task BackToListAsync()
    {
        if (IsContentModified)
        {
            Views.UnsavedChangesDialog? dialog = null;
            bool? result = null;

            try
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new Views.UnsavedChangesDialog(_localizationService);

                    if (System.Windows.Application.Current.MainWindow != null &&
                        System.Windows.Application.Current.MainWindow.IsLoaded)
                    {
                        dialog.Owner = System.Windows.Application.Current.MainWindow;
                    }

                    result = dialog.ShowDialog();
                });
            }
            catch (Exception ex)
            {
                _logService.Error("UI", "无法显示未保存确认对话框", ex);
                System.Diagnostics.Debug.WriteLine($"UnsavedChangesDialog异常: {ex}");
            }

            if (result != true || dialog == null)
            {
                SetStatus("已取消返回");
                return;
            }

            switch (dialog.Action)
            {
                case Views.UnsavedChangesAction.Save:
                    await SaveMemoAsync();
                    break;
                case Views.UnsavedChangesAction.Cancel:
                    SetStatus("已取消返回");
                    return;
                case Views.UnsavedChangesAction.Discard:
                    // 恢复编辑内容为原始值，防止自动保存将修改保存
                    EditorContent = _originalContent ?? string.Empty;
                    break;
            }
        }

        IsEditMode = false;
        IsContentModified = false;
        _originalContent = null;
        SetStatus("返回列表");
    }

    [RelayCommand]
    private void ToggleTodoList()
    {
        if (IsEditMode)
        {
            // 如果在编辑模式，先返回列表
            IsEditMode = false;
        }

        IsInTodoListMode = !IsInTodoListMode;
        SetStatus(IsInTodoListMode ? "切换到待办事项" : "切换到备忘录");
    }

    [RelayCommand]
    private async Task ApplyCustomPositionAsync()
    {
        if (double.TryParse(CustomPositionX, out double x) && double.TryParse(CustomPositionY, out double y))
        {
            _windowService.SetWindowPosition(x, y);
            UpdateCurrentPosition();
            WindowSettings = WindowSettings.WithLocation(CurrentLeft, CurrentTop);
            await _settingsService.SaveAsync(WindowSettings);
            SetStatus("已应用自定义位置");
        }
        else
        {
            SetStatus("位置格式错误");
        }
    }

    [RelayCommand]
    private async Task MoveToPresetAsync(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        _windowService.MoveToPresetPosition(preset);
        UpdateCurrentPosition();
        WindowSettings = WindowSettings.WithLocation(CurrentLeft, CurrentTop);
        await _settingsService.SaveAsync(WindowSettings);
        SetStatus("窗口已移动");
    }

    [RelayCommand]
    private async Task RememberPositionAsync()
    {
        UpdateCurrentPosition();
        WindowSettings = WindowSettings.WithLocation(CurrentLeft, CurrentTop);
        await _settingsService.SaveAsync(WindowSettings);
        SetStatus("位置已记录");
    }

    [RelayCommand]
    private void RestorePosition()
    {
        if (double.IsNaN(WindowSettings.Left) || double.IsNaN(WindowSettings.Top))
        {
            SetStatus("尚未记录位置");
            return;
        }

        _windowService.SetWindowPosition(WindowSettings.Left, WindowSettings.Top);
        UpdateCurrentPosition();
        SetStatus("已恢复位置");
    }

    [RelayCommand]
    private void TrayShowWindow()
    {
        IsTrayEnabled = true;
        _trayService.Show();
        _windowService.RestoreFromTray();
        SetStatus("窗口已显示");
    }

    [RelayCommand]
    private void TrayHideWindow()
    {
        _windowService.MinimizeToTray();
        SetStatus("窗口已隐藏");
    }

    [RelayCommand]
    private void TrayRestart()
    {
        _trayService.Hide();
        _trayService.Initialize();
        if (IsTrayEnabled)
        {
            _trayService.Show();
        }
        _trayService.UpdateTopmostState(SelectedTopmostMode);
        _trayService.UpdateClickThroughState(IsClickThroughEnabled);
        SetStatus("托盘已重载");
    }

    [RelayCommand]
    private void ClearEditor()
    {
        EditorContent = string.Empty;
        SetStatus("已清空内容");
    }

    [RelayCommand]
    private void ShowAbout()
    {
        SetStatus("关于 DesktopMemo");
    }

    partial void OnSelectedTopmostModeChanged(TopmostMode value)
    {
        _windowService.SetTopmostMode(value);
        _trayService.UpdateTopmostState(value);

        // 使用防抖机制自动保存置顶模式设置
        bool isTopMost = value == TopmostMode.Always;
        bool isDesktopMode = value == TopmostMode.Desktop;

        WindowSettings = WindowSettings.WithAppearance(BackgroundOpacity, isTopMost, isDesktopMode, IsClickThroughEnabled);
        
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine("置顶模式设置已保存");
        });
    }

    partial void OnBackgroundOpacityChanged(double value)
    {
        // 使用辅助类规范化透明度值
        var normalizedValue = TransparencyHelper.NormalizeTransparency(value);
        if (Math.Abs(value - normalizedValue) > 0.001) // 如果值被调整了
        {
            BackgroundOpacity = normalizedValue; // 更新为调整后的值
            return; // 避免递归调用
        }

        _windowService.SetWindowOpacity(normalizedValue);

        // 使用防抖机制自动保存透明度设置
        bool isTopMost = SelectedTopmostMode == TopmostMode.Always;
        bool isDesktopMode = SelectedTopmostMode == TopmostMode.Desktop;

        WindowSettings = WindowSettings.WithAppearance(normalizedValue, isTopMost, isDesktopMode, IsClickThroughEnabled);
        
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"透明度设置已保存: {normalizedValue}");
        });
    }

    partial void OnIsClickThroughEnabledChanged(bool value)
    {
        _windowService.SetClickThrough(value);
        _trayService.UpdateClickThroughState(value);

        if (_isInitializing)
        {
            return;
        }

        if (value && IsSettingsPanelVisible)
        {
            IsSettingsPanelVisible = false;
        }

        bool isTopMost = SelectedTopmostMode == TopmostMode.Always;
        bool isDesktopMode = SelectedTopmostMode == TopmostMode.Desktop;

        WindowSettings = WindowSettings.WithAppearance(BackgroundOpacity, isTopMost, isDesktopMode, value);
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine("穿透模式设置已保存");
        });
    }

    partial void OnIsAutoStartEnabledChanged(bool value)
    {
        try
        {
            ManageAutoStart(value);
            _logService.Info("Settings", value ? "启用开机自启动" : "禁用开机自启动");
            SetStatus(value ? "已启用开机自启动" : "已禁用开机自启动");
        }
        catch (Exception ex)
        {
            _logService.Error("Settings", "设置开机自启动失败", ex);
            SetStatus($"设置开机自启动失败: {ex.Message}", LogLevel.Error);
        }
    }

    private void ManageAutoStart(bool enable)
    {
        var appName = GetUniqueAutoStartKeyName();
        var keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true);
        if (key == null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue(appName, $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue(appName, false);
        }
    }

    private void CheckAutoStartStatus()
    {
        try
        {
            var appName = GetUniqueAutoStartKeyName();
            var keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, false);
            var value = key?.GetValue(appName);
            
            // 检查值是否存在且路径匹配当前应用
            if (value is string registeredPath && !string.IsNullOrEmpty(Environment.ProcessPath))
            {
                // 移除引号并比较路径
                var cleanPath = registeredPath.Trim('"');
                IsAutoStartEnabled = string.Equals(cleanPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                IsAutoStartEnabled = false;
            }
            
            // 清理无效的 DesktopMemo 自启动项
            CleanupInvalidAutoStartEntries();
        }
        catch
        {
            IsAutoStartEnabled = false;
        }
    }

    /// <summary>
    /// 获取基于应用程序路径的唯一自启动注册表键名
    /// 这样可以让不同位置的应用实例独立管理自己的自启动状态
    /// </summary>
    private string GetUniqueAutoStartKeyName()
    {
        var exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(exePath))
        {
            return "DesktopMemo";
        }

        // 使用路径的哈希值生成唯一标识符
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
        var hashString = BitConverter.ToString(hashBytes.Take(4).ToArray()).Replace("-", "");
        
        return $"DesktopMemo_{hashString}";
    }

    /// <summary>
    /// 清理注册表中无效的 DesktopMemo 自启动项
    /// 删除指向不存在文件的条目，防止注册表污染
    /// </summary>
    private void CleanupInvalidAutoStartEntries()
    {
        try
        {
            var keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true);
            if (key == null) return;

            var valueNames = key.GetValueNames();
            var currentAppName = GetUniqueAutoStartKeyName();
            
            foreach (var valueName in valueNames)
            {
                // 只处理 DesktopMemo 相关的键
                if (!valueName.StartsWith("DesktopMemo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = key.GetValue(valueName);
                if (value is string path)
                {
                    // 清理引号
                    var cleanPath = path.Trim('"');
                    
                    // 如果是当前应用的键，跳过
                    if (valueName == currentAppName)
                    {
                        continue;
                    }
                    
                    // 如果文件不存在，删除这个注册表项
                    if (!File.Exists(cleanPath))
                    {
                        key.DeleteValue(valueName, false);
                        System.Diagnostics.Debug.WriteLine($"已清理无效的自启动项: {valueName} -> {cleanPath}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响主要功能
            System.Diagnostics.Debug.WriteLine($"清理无效自启动项时出错: {ex.Message}");
        }
    }

    partial void OnIsTrayEnabledChanged(bool value)
    {
        if (_isDisposing)
        {
            return;
        }

        if (value)
        {
            _trayService.Show();
        }
        else
        {
            _trayService.Hide();
        }

        if (_isInitializing)
        {
            return;
        }

        WindowSettings = WindowSettings with { IsTrayEnabled = value };
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"托盘启用状态已保存: {value}");
        });
    }

    partial void OnSelectedMemoChanged(Memo? oldValue, Memo? newValue)
    {
        OnPropertyChanged(nameof(HasSelectedMemo));

        if (newValue is null)
        {
            if (!IsEditMode)
            {
                EditorTitle = string.Empty;
                EditorContent = string.Empty;
            }
            return;
        }

        EditorTitle = newValue.Title;
        EditorContent = newValue.Content;
    }

    partial void OnEditorContentChanged(string value)
    {
        if (IsEditMode && _originalContent != null)
        {
            IsContentModified = !string.Equals(value, _originalContent, StringComparison.Ordinal);
        }
    }

    partial void OnMemosChanging(ObservableCollection<Memo> value)
    {
        if (value is not null)
        {
            value.CollectionChanged -= OnMemosCollectionChanged;
        }
    }

    partial void OnMemosChanged(ObservableCollection<Memo> value)
    {
        value.CollectionChanged += OnMemosCollectionChanged;
        OnPropertyChanged(nameof(MemoCount));
    }

    private void InitializeAppInfo()
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.0";
            var appDir = AppContext.BaseDirectory;
            var dataPath = Path.Combine(appDir, ".memodata");
            var versionLabel = _localizationService["Settings_Status_Version"];
            var dataDirectoryLabel = _localizationService["Settings_Status_DataDirectory"];
            AppInfo = $"{versionLabel}：{version} | {dataDirectoryLabel}：{dataPath}";
        }
        catch
        {
            var versionLabel = _localizationService["Settings_Status_Version"];
            var dataDirectoryLabel = _localizationService["Settings_Status_DataDirectory"];
            AppInfo = $"{versionLabel}：2.3.0 | {dataDirectoryLabel}：<应用目录>\\.memodata";
        }
    }

    private void OnMemosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(MemoCount));
    }

    /// <summary>
    /// 记录操作状态到日志系统
    /// </summary>
    public void SetStatus(string status, LogLevel logLevel = LogLevel.Info)
    {
        // 所有用户可感知状态都统一进日志，便于问题排查和后续日志面板展示。
        switch (logLevel)
        {
            case LogLevel.Debug:
                _logService.Debug("UI", status);
                break;
            case LogLevel.Info:
                _logService.Info("UI", status);
                break;
            case LogLevel.Warning:
                _logService.Warning("UI", status);
                break;
            case LogLevel.Error:
                _logService.Error("UI", status);
                break;
        }
        
        // 托盘文本只同步信息级别及以上状态，避免调试日志刷屏。
        if (logLevel >= LogLevel.Info)
        {
            _trayService.UpdateText($"DesktopMemo - {status}");
        }
    }

    public void MarkEditing()
    {
        if (SelectedMemo is not null)
        {
            SetStatus("编辑中...");
        }
    }

    public ISettingsService GetSettingsService() => _settingsService;

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        // 索引器式本地化绑定需要主动触发属性变更，UI 才会重新取值。
        OnPropertyChanged(nameof(LocalizationService));

        // 依赖本地化字符串的组合文本都需要重新生成。
        InitializeAppInfo();

        _trayService.UpdateMenuTexts(key => _localizationService[key]);
    }

    partial void OnSelectedLanguageChanged(CultureInfo? value)
    {
        if (value != null && value.Name != _localizationService.CurrentCulture.Name)
        {
            // 先切运行时语言，再异步持久化，确保界面响应优先。
            _localizationService.ChangeLanguage(value.Name);

            WindowSettings = WindowSettings with { PreferredLanguage = value.Name };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _settingsService.SaveAsync(WindowSettings);
                    System.Diagnostics.Debug.WriteLine($"语言设置已保存: {value.Name}");
                }
                catch (Exception ex)
                {
                    _logService.Error("Settings", "保存语言设置失败", ex);
                    System.Diagnostics.Debug.WriteLine($"保存语言设置失败: {ex}");
                }
            });
            
            _logService.Info("Settings", $"切换语言到 {value.NativeName}");
            SetStatus($"语言已切换到 {value.NativeName}");
        }
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (!_isInitializing)
        {
            // 主题切换同样采用“先生效、后持久化”的策略。
            WindowSettings = WindowSettings with { Theme = value };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _settingsService.SaveAsync(WindowSettings);
                    System.Diagnostics.Debug.WriteLine($"主题设置已保存: {value}");
                }
                catch (Exception ex)
                {
                    _logService.Error("Settings", "保存主题设置失败", ex);
                    System.Diagnostics.Debug.WriteLine($"保存主题设置失败: {ex}");
                }
            });
            
            var themeName = value switch
            {
                AppTheme.Light => "亮色",
                AppTheme.Dark => "暗色",
                AppTheme.System => "跟随系统",
                _ => "未知"
            };
            _logService.Info("Settings", $"切换主题到 {themeName}");
            SetStatus($"主题已切换到 {themeName}");
            
            // MainWindow 需要额外处理资源字典切换，因此通过事件通知 View。
            ThemeChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<AppTheme>? ThemeChanged;
    public event EventHandler? OpenSettingsWindowRequested;

    partial void OnWindowSettingsChanged(WindowSettings value)
    {
        ShowExitConfirmation = value.ShowExitConfirmation;
        ShowDeleteConfirmation = value.ShowDeleteConfirmation;
        DefaultExitToTray = value.DefaultExitToTray;
        IsTrayEnabled = value.IsTrayEnabled;
    }

    partial void OnShowExitConfirmationChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        WindowSettings = WindowSettings with { ShowExitConfirmation = value };
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"退出确认设置已保存: {value}");
        });
    }

    partial void OnShowDeleteConfirmationChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        WindowSettings = WindowSettings with { ShowDeleteConfirmation = value };
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"删除确认设置已保存: {value}");
        });
    }

    partial void OnDefaultExitToTrayChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        WindowSettings = WindowSettings with { DefaultExitToTray = value };
        _settingsSaveDebouncer.Debounce(async () =>
        {
            await _settingsService.SaveAsync(WindowSettings);
            System.Diagnostics.Debug.WriteLine($"默认退出到托盘设置已保存: {value}");
        });
    }

    partial void OnIsInTodoListModeChanged(bool value)
    {
        // 初始化阶段只是回放历史状态，不应立刻覆盖磁盘中的原值。
        if (_isInitializing)
        {
            return;
        }
        
        var currentPage = value ? "todo" : "memo";
        if (WindowSettings.CurrentPage == currentPage)
        {
            return;
        }

        WindowSettings = WindowSettings with { CurrentPage = currentPage };
        
        // 页面切换不需要阻塞 UI，直接异步保存即可。
        _ = Task.Run(async () =>
        {
            try
            {
                await _settingsService.SaveAsync(WindowSettings);
                System.Diagnostics.Debug.WriteLine($"当前页面已保存: {currentPage}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存当前页面失败: {ex}");
            }
        });
    }

    private void OnTodoInputVisibilityChanged(object? sender, bool isVisible)
    {
        // 待办输入框可见性属于用户偏好，由主视图模型统一纳入设置。
        WindowSettings = WindowSettings with { TodoInputVisible = isVisible };

        _ = Task.Run(async () =>
        {
            try
            {
                await _settingsService.SaveAsync(WindowSettings);
                System.Diagnostics.Debug.WriteLine($"待办输入区域状态已保存: {isVisible}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存待办输入区域状态失败: {ex}");
            }
        });
    }

    partial void OnIsWindowPinnedChanged(bool value)
    {
        System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] 被触发: value={value}, _isInitializing={_isInitializing}");
        
        // 初始化阶段只是回放历史状态，不应立刻覆盖磁盘中的原值。
        if (_isInitializing)
        {
            System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] 初始化期间，跳过保存");
            return;
        }
        
        var oldSettings = WindowSettings;
        WindowSettings = WindowSettings with { IsWindowPinned = value };
        System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] 更新设置: {oldSettings.IsWindowPinned} -> {WindowSettings.IsWindowPinned}");
        
        _ = Task.Run(async () =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] 开始保存到磁盘: {value}");
                await _settingsService.SaveAsync(WindowSettings);
                System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] ✓ 窗口固定状态已保存: {value}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] ✗ 保存窗口固定状态失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[OnIsWindowPinnedChanged] 异常详情: {ex}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isDisposing = true;

        // 释放顺序上先隐藏托盘再释放其资源，避免图标残留。
        if (IsTrayEnabled)
        {
            _trayService.Hide();
        }

        _trayService.Dispose();
        _settingsSaveDebouncer.Dispose();
    }

    [RelayCommand]
    private void FindNext()
    {
        if (SelectedMemo is null || string.IsNullOrWhiteSpace(SearchKeyword))
        {
            SetStatus("没有可以搜索的内容");
            return;
        }

        var matches = _searchService.FindMatches(EditorContent, SearchKeyword, IsCaseSensitive, UseRegex).ToList();
        if (!matches.Any())
        {
            SetStatus("未找到匹配");
            return;
        }

        SetStatus($"找到 {matches.Count} 个匹配");
    }

    [RelayCommand]
    private void ReplaceAll()
    {
        if (SelectedMemo is null || string.IsNullOrWhiteSpace(SearchKeyword))
        {
            SetStatus("替换条件不完整");
            return;
        }

        var newContent = _searchService.Replace(EditorContent, SearchKeyword, ReplaceKeyword ?? string.Empty, IsCaseSensitive, UseRegex);
        if (!ReferenceEquals(newContent, EditorContent))
        {
            EditorContent = newContent;
            SetStatus("已替换所有匹配项");
        }
        else
        {
            SetStatus("无匹配项可替换");
        }
    }

    [RelayCommand]
    private async Task ImportLegacyAsync()
    {
        _logService.Info("Migration", "开始导入旧版本备忘录");
        var legacyMemos = await _migrationService.LoadFromLegacyAsync();
        int importCount = 0;

        foreach (var memo in legacyMemos)
        {
            await _memoRepository.AddAsync(memo);
            importCount++;
        }

        if (importCount > 0)
        {
            var memos = await _memoRepository.GetAllAsync();
            Memos = new ObservableCollection<Memo>(memos.OrderByDescending(m => m.UpdatedAt));
            SelectedMemo = Memos.FirstOrDefault();
            _logService.Info("Migration", $"成功导入 {importCount} 条备忘录");
        }
        else
        {
            _logService.Info("Migration", "未发现旧版本数据");
        }

        SetStatus(importCount > 0 ? $"导入 {importCount} 条备忘录" : "未发现旧数据");
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        var exportDir = Path.Combine(_migrationService.ExportDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(exportDir);

        var memos = await _memoRepository.GetAllAsync();
        int count = 0;

        foreach (var memo in memos)
        {
            var path = Path.Combine(exportDir, $"{memo.Id:N}.md");
            await File.WriteAllTextAsync(path, memo.Content);
            count++;
        }

        if (count > 0)
        {
            _logService.Info("Export", $"导出 {count} 条备忘录到 {exportDir}");
        }
        else
        {
            _logService.Warning("Export", "没有可导出的备忘录");
        }

        SetStatus(count > 0 ? $"导出 {count} 条备忘录" : "没有可导出的备忘录");
    }
}

