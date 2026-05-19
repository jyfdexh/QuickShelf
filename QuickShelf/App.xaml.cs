using System.Threading;
using System.Windows;
using QuickShelf.Services;

namespace QuickShelf;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\QuickShelf.SingleInstance";
    private const string ActivateEventName = "Local\\QuickShelf.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private ManualResetEvent? _shutdownEvent;
    private MainWindow? _mainWindow;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryStartSingleInstance())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var startHidden = e.Args.Any(arg =>
            string.Equals(arg, QuickShelf.MainWindow.StartupArgument, StringComparison.OrdinalIgnoreCase));

        _mainWindow = new MainWindow(startHidden);
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownEvent?.Set();

        _activateEvent?.Dispose();
        _shutdownEvent?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    AppLog.Warn("释放单实例锁失败。", ex);
                }
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private bool TryStartSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            TrySignalExistingInstance();
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _shutdownEvent = new ManualResetEvent(false);
        StartActivationListener();
        return true;
    }

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            AppLog.Warn("已有实例存在，但无法发送激活信号。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Warn("已有实例存在，但没有权限发送激活信号。", ex);
        }
    }

    private void StartActivationListener()
    {
        var activateEvent = _activateEvent;
        var shutdownEvent = _shutdownEvent;
        if (activateEvent is null || shutdownEvent is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handles = new WaitHandle[] { activateEvent, shutdownEvent };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                Dispatcher.BeginInvoke(() => _mainWindow?.ShowAndActivate());
            }
        });
    }
}
