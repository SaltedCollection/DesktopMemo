using System.ComponentModel;
using System.Globalization;
using System.Resources;
using DesktopMemo.Core.Contracts;

namespace DesktopMemo.App.Localization;

/// <summary>
/// 本地化服务实现，支持运行时动态切换语言。
/// 通过索引器暴露资源字符串，便于 XAML 和 ViewModel 统一访问。
/// </summary>
public class LocalizationService : ILocalizationService, INotifyPropertyChanged
{
    private readonly ResourceManager _resourceManager;
    private CultureInfo _currentCulture;
    
    // 支持的语言列表固定在客户端内，避免用户选择到没有资源文件的文化。
    private static readonly CultureInfo[] SupportedCultures = new[]
    {
        new CultureInfo("zh-CN"), // 简体中文
        new CultureInfo("en-US"), // 英语（美国）
        new CultureInfo("zh-TW"), // 繁体中文
        new CultureInfo("ja-JP"), // 日语
        new CultureInfo("ko-KR"), // 韩语
    };

    public LocalizationService()
    {
        // ResourceManager 统一管理多语言 resx 资源。
        _resourceManager = new ResourceManager(
            "DesktopMemo.App.Localization.Resources.Strings",
            typeof(LocalizationService).Assembly
        );
        
        // 默认文化与项目主要受众一致，首次启动无需依赖设置文件。
        _currentCulture = new CultureInfo("zh-CN");
    }

    /// <summary>
    /// 通过键获取本地化字符串
    /// </summary>
    public string this[string key]
    {
        get
        {
            try
            {
                var value = _resourceManager.GetString(key, _currentCulture);
                
                // 用 [key] 作为兜底，能让缺失翻译在 UI 中一眼暴露。
                return value ?? $"[{key}]";
            }
            catch
            {
                return $"[{key}]";
            }
        }
        set
        {
            // 忽略误绑定到 TwoWay 的写入请求；本地化资源只读。
        }
    }

    /// <summary>
    /// 当前语言文化
    /// </summary>
    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        private set
        {
            if (_currentCulture.Name != value.Name)
            {
                var oldCulture = _currentCulture;
                _currentCulture = value;
                
                // 同步线程文化，确保日期、数字和资源查找都切到新语言。
                Thread.CurrentThread.CurrentUICulture = value;
                Thread.CurrentThread.CurrentCulture = value;
                
                // 绑定到 CurrentCulture 的下拉框等控件需要收到属性变更。
                OnPropertyChanged(nameof(CurrentCulture));
                
                // 索引器绑定使用 Item[] 通知，让所有本地化文本整体刷新。
                OnPropertyChanged("Item[]");
                
                // 额外抛事件给需要做二次处理的组件，例如托盘菜单或组合文案。
                LanguageChanged?.Invoke(this, new LanguageChangedEventArgs(value, oldCulture));
            }
        }
    }

    /// <summary>
    /// 切换语言
    /// </summary>
    public void ChangeLanguage(string cultureName)
    {
        try
        {
            var newCulture = new CultureInfo(cultureName);
            
            // 只允许切换到显式支持的文化，避免资源缺失造成半翻译状态。
            if (!SupportedCultures.Any(c => c.Name == newCulture.Name))
            {
                throw new CultureNotFoundException(
                    $"Language '{cultureName}' is not supported. " +
                    $"Supported languages: {string.Join(", ", SupportedCultures.Select(c => c.Name))}"
                );
            }
            
            CurrentCulture = newCulture;
        }
        catch (CultureNotFoundException)
        {
            // 非法文化或不支持的语言都回退到默认中文。
            CurrentCulture = new CultureInfo("zh-CN");
        }
    }

    /// <summary>
    /// 获取所有支持的语言
    /// </summary>
    public IEnumerable<CultureInfo> GetSupportedLanguages()
    {
        return SupportedCultures;
    }

    /// <summary>
    /// 语言切换事件
    /// </summary>
    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    /// <summary>
    /// 属性变更事件（用于支持数据绑定）
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

