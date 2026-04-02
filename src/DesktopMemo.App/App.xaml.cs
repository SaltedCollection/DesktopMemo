using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DesktopMemo.App.Localization;
using DesktopMemo.App.ViewModels;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Infrastructure.Repositories;
using DesktopMemo.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using WpfApp = System.Windows.Application;

namespace DesktopMemo.App;

/// <summary>
/// WPF 应用程序入口。
/// 负责搭建依赖注入容器、执行启动迁移并创建主窗口。
/// </summary>
public partial class App : WpfApp
{
    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
    }

    /// <summary>
    /// 注册应用运行所需的服务、仓储与 ViewModel。
    /// </summary>
    private IServiceProvider ConfigureServices()
    {
        var appDirectory = AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(appDirectory, ".memodata");

        var services = new ServiceCollection();

        // 核心服务负责日志、数据持久化、检索与兼容旧版本数据。
        services.AddSingleton<ILogService>(_ => new FileLogService(dataDirectory));
        services.AddSingleton<IMemoRepository>(_ => new SqliteIndexedMemoRepository(dataDirectory));
        services.AddSingleton<ITodoRepository>(_ => new SqliteTodoRepository(dataDirectory));
        services.AddSingleton<ISettingsService>(_ => new JsonSettingsService(dataDirectory));
        services.AddSingleton<IMemoSearchService, MemoSearchService>();
        services.AddSingleton(_ => new MemoMigrationService(dataDirectory, appDirectory));
        
        // 启动阶段迁移服务按“待办 -> 备忘录索引 -> front matter 时间同步”的顺序执行。
        services.AddSingleton<TodoMigrationService>();
        services.AddSingleton<MemoMetadataMigrationService>();
        services.AddSingleton(_ => new MemoFrontMatterTimestampSyncService(
            dataDirectory,
            _.GetRequiredService<ILogService>()));

        // 窗口和托盘服务与具体 UI 生命周期强相关，因此保持单例共享状态。
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<ITrayService, TrayService>();

        // 本地化服务在整个会话内共享当前语言状态。
        services.AddSingleton<ILocalizationService, LocalizationService>();

        // 主界面和子面板的 ViewModel 统一交给容器管理，便于跨窗口复用。
        services.AddSingleton<TodoListViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 启动时优先做数据兼容处理，避免 UI 加载后才发现旧数据结构无法读取。
            var appDirectory = AppContext.BaseDirectory;
            var dataDirectory = Path.Combine(appDirectory, ".memodata");
            
            // 尽早初始化日志和全局异常处理，保证后续启动失败也能留下诊断信息。
            var logService = Services.GetRequiredService<ILogService>();
            RegisterGlobalExceptionHandlers(logService);
            logService.Info("App", "应用程序启动");
            
            System.Diagnostics.Debug.WriteLine("========== 应用启动 - 开始数据迁移检查 ==========");
            
            // 1. Todo 数据迁移（JSON -> SQLite）
            logService.Info("Migration", "开始 Todo 数据迁移");
            var todoMigrationService = Services.GetRequiredService<TodoMigrationService>();
            var todoMigrationResult = await todoMigrationService.MigrateFromJsonToSqliteAsync(dataDirectory);
            if (todoMigrationResult.Success && todoMigrationResult.MigratedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[App启动] TodoList 迁移成功: {todoMigrationResult.Message}");
                logService.Info("Migration", $"TodoList 迁移成功: {todoMigrationResult.Message}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[App启动] TodoList 迁移结果: {todoMigrationResult.Message}");
                logService.Debug("Migration", $"TodoList 迁移结果: {todoMigrationResult.Message}");
            }
            
            // 2. 备忘录元数据迁移（index.json -> SQLite 索引）
            logService.Info("Migration", "开始备忘录元数据迁移");
            var memoMetadataMigrationService = Services.GetRequiredService<MemoMetadataMigrationService>();
            var memoMigrationResult = await memoMetadataMigrationService.MigrateToSqliteIndexAsync(dataDirectory);
            if (memoMigrationResult.Success && memoMigrationResult.MigratedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[App启动] ⚠️ 备忘录索引迁移执行: 迁移了 {memoMigrationResult.MigratedCount} 条（会更新文件时间戳）");
                logService.Warning("Migration", $"备忘录索引迁移执行: 迁移了 {memoMigrationResult.MigratedCount} 条（会更新文件时间戳）");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[App启动] 备忘录索引迁移结果: {memoMigrationResult.Message}");
                logService.Debug("Migration", $"备忘录索引迁移结果: {memoMigrationResult.Message}");
            }

            // 3. 备忘录时间同步（Markdown front matter -> SQLite）
            var memoTimestampSyncService = Services.GetRequiredService<MemoFrontMatterTimestampSyncService>();
            var syncResult = await memoTimestampSyncService.SyncAsync();
            System.Diagnostics.Debug.WriteLine(
                $"[App启动] Markdown 时间同步完成: 扫描 {syncResult.ScannedCount}，更新 {syncResult.UpdatedCount}，跳过 {syncResult.SkippedCount}，解析失败 {syncResult.ParseFailureCount}");
            
            System.Diagnostics.Debug.WriteLine("========== 数据迁移检查完成 ==========");
            logService.Info("Migration", "数据迁移检查完成");

            // 先恢复语言，再构建窗口，确保首帧 UI 就使用用户上次选择的文化。
            var settingsService = Services.GetRequiredService<ISettingsService>();
            var localizationService = Services.GetRequiredService<ILocalizationService>();
            
            var settings = await settingsService.LoadAsync();
            if (!string.IsNullOrEmpty(settings.PreferredLanguage))
            {
                localizationService.ChangeLanguage(settings.PreferredLanguage);
            }

            var viewModel = Services.GetRequiredService<MainViewModel>();
            var windowService = Services.GetRequiredService<IWindowService>();
            var trayService = Services.GetRequiredService<ITrayService>();

            var window = new MainWindow(viewModel, windowService, trayService, logService);

            if (windowService is WindowService ws)
            {
                ws.Initialize(window);
            }

            // 托盘是增强能力，不应因为初始化失败而阻止主窗口运行。
            try
            {
                trayService.Initialize();
            }
            catch
            {
                // 托盘服务初始化失败，但应用程序仍然可以运行
            }

            // 先初始化 ViewModel，再显示窗口，避免首帧出现“默认值闪一下”的情况。
            await viewModel.InitializeAsync();

            MainWindow = window;
            window.Show();
            
            logService.Info("App", "应用程序主窗口已显示");
        }
        catch (Exception ex)
        {
            var logService = Services.GetService<ILogService>();
            logService?.Error("App", "应用程序启动失败", ex);
            
            System.Windows.MessageBox.Show($"应用程序启动失败: {ex.Message}\n\n请尝试删除 .memodata 目录后重新启动应用程序。", 
                "DesktopMemo 启动错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// 注册全局异常处理器，尽量把前台和后台异常都收敛到统一日志中。
    /// </summary>
    private void RegisterGlobalExceptionHandlers(ILogService logService)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            logService.Error("App", "未处理异常导致进程终止", ex ?? new Exception("未知未处理异常"));
        };

        DispatcherUnhandledException += (s, e) =>
        {
            logService.Error("App", "UI线程未处理异常", e.Exception);
            e.Handled = true; // 关键：防止 UI 线程异常直接导致进程退出。

            // 显示友好的错误提示
            System.Windows.MessageBox.Show(
                $"应用程序遇到错误，但已恢复：\n{e.Exception.Message}\n\n详细信息已记录到日志文件。",
                "DesktopMemo 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            logService.Error("App", "未观察到的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var logService = Services.GetService<ILogService>();
        logService?.Info("App", "应用程序正在退出");
        
        Services.GetService<ITrayService>()?.Dispose();
        
        // 日志服务最后释放，尽量保证退出前的收尾日志能落盘。
        if (logService is IDisposable disposableLog)
        {
            disposableLog.Dispose();
        }
        
        base.OnExit(e);
    }
}

