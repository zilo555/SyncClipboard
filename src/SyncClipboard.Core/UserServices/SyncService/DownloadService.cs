using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NativeNotification.Interface;
using SharpHook;
using SharpHook.Native;
using SyncClipboard.Abstract;
using SyncClipboard.Core.Clipboard;
using SyncClipboard.Core.Commons;
using SyncClipboard.Core.Interfaces;
using SyncClipboard.Core.Models;
using SyncClipboard.Core.Models.UserConfigs;
using SyncClipboard.Core.Utilities;
using SyncClipboard.Core.ViewModels;

namespace SyncClipboard.Core.UserServices;

public class DownloadService : Service
{
    private readonly static string SERVICE_NAME = I18n.Strings.DownloadService;
    private const string LOG_TAG = "PULL";
    private bool _isPullLoopRunning = false;
    private bool _isQuickDownload = false;
    private bool _isQuickDownloadAndPaste = false;
    private readonly object _isPullLoopRunningLocker = new();
    private ProgressToastReporter? _toastReporter;
    private Profile? _remoteProfileCache;
    private Profile? _localProfileCache;

    private bool _downServiceChangingLocal = false;

    private readonly INotificationManager _notificationManager;
    private readonly ILogger _logger;
    private readonly ConfigManager _configManager;
    private readonly IClipboardFactory _clipboardFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IClipboardChangingListener _clipboardListener;
    private readonly IClipboardMoniter _clipboardMoniter;
    private readonly ITrayIcon _trayIcon;
    private readonly IMessenger _messenger;
    private readonly IEventSimulator _keyEventSimulator;
    private readonly HotkeyManager _hotkeyManager;
    private readonly UploadService _uploadService;
    private SyncConfig _syncConfig;
    private ServerConfig _serverConfig;

    private bool SwitchOn => _syncConfig.SyncSwitchOn && _syncConfig.PullSwitchOn && (!_serverConfig.ClientMixedMode || !_serverConfig.SwitchOn);
    private bool ClientSwitchOn => _syncConfig.SyncSwitchOn || (_serverConfig.ClientMixedMode && _serverConfig.SwitchOn);

    #region Hotkey
    private static readonly string QuickDownloadAndPasteGuid = "8a4a033e-31da-1b87-76ea-548885866b66";

    private UniqueCommandCollection CommandCollection => new(PageDefinition.SyncSetting.Title, PageDefinition.SyncSetting.FontIcon!)
    {
        Commands = {
            new UniqueCommand(
                I18n.Strings.SwitchClipboardSyncing,
                "26D8A39E-F50D-CC71-FE15-647F67FDB2F9",
                () => SwitchClipboardSyncing(!_syncConfig.SyncSwitchOn)
            ),
            new UniqueCommand(
                I18n.Strings.SwitchBuiltInServer,
                "145740F4-03F7-6F6C-5B93-B027C7C49C59",
                () => SwitchBuiltInServer(!_serverConfig.SwitchOn)
            ),
            new UniqueCommand(
                I18n.Strings.SwitchMixedClientMode,
                "1D5C8163-E2E0-D099-A334-62A4B4F2BCE5",
                () => SwitchMixedClientMode(!_serverConfig.ClientMixedMode)
            ),
            _uploadService.QuickUploadCommand,
            _uploadService.QuickUploadWithoutFilterCommand,
            new UniqueCommand(
                I18n.Strings.DownloadOnce,
                "95396FFF-E5FE-45D3-9D70-4A43FA34FF31",
                QuickDownload
            ),
            _uploadService.CopyAndQuickUploadCommand,
            _uploadService.CopyAndQuickUploadWithoutFilterCommand,
            new UniqueCommand(
                I18n.Strings.DownloadAndPaste,
                QuickDownloadAndPasteGuid,
                QuickDownloadAndPaste
            ),
        }
    };

    private void SwitchClipboardSyncing(bool isOn)
    {
        _configManager.SetConfig(_syncConfig with { SyncSwitchOn = isOn });
        var notification = _notificationManager.Shared;
        notification.Title = isOn ? I18n.Strings.SwitchOnClipboardSyncing : I18n.Strings.SwitchOffClipboardSyncing;
        notification.Show(new NotificationDeliverOption { Duration = TimeSpan.FromSeconds(2) });
    }

    private void SwitchBuiltInServer(bool isOn)
    {
        _configManager.SetConfig(_serverConfig with { SwitchOn = isOn });
        var notification = _notificationManager.Shared;
        notification.Title = isOn ? I18n.Strings.SwitchOnBuiltInServer : I18n.Strings.SwitchOffBuiltInServer;
        notification.Show(new NotificationDeliverOption { Duration = TimeSpan.FromSeconds(2) });
    }

    private void SwitchMixedClientMode(bool isOn)
    {
        _configManager.SetConfig(_serverConfig with { ClientMixedMode = isOn });
        var notification = _notificationManager.Shared;
        notification.Title = isOn ? I18n.Strings.SwitchOnMixedClientMode : I18n.Strings.SwitchOffMixedClientMode;
        notification.Show(new NotificationDeliverOption { Duration = TimeSpan.FromSeconds(2) });
    }

    #endregion

    public DownloadService(
        IServiceProvider serviceProvider,
        IMessenger messenger,
        UploadService uploadService,
        IEventSimulator keyEventSimulator,
        IClipboardMoniter clipboardMoniter,
        IClipboardChangingListener clipboardChangingListener,
        HotkeyManager hotkeyManager)
    {
        _serviceProvider = serviceProvider;
        _logger = _serviceProvider.GetRequiredService<ILogger>();
        _configManager = _serviceProvider.GetRequiredService<ConfigManager>();
        _configManager.ListenConfig<SyncConfig>(SyncConfigChanged);
        _syncConfig = _configManager.GetConfig<SyncConfig>();
        _configManager.ListenConfig<ServerConfig>(ServerConfigChanged);
        _serverConfig = _configManager.GetConfig<ServerConfig>();
        _clipboardFactory = _serviceProvider.GetRequiredService<IClipboardFactory>();
        _notificationManager = _serviceProvider.GetRequiredService<INotificationManager>();
        _trayIcon = _serviceProvider.GetRequiredService<ITrayIcon>();
        _messenger = messenger;
        _uploadService = uploadService;
        _keyEventSimulator = keyEventSimulator;
        _hotkeyManager = hotkeyManager;
        _clipboardMoniter = clipboardMoniter;
        _clipboardListener = clipboardChangingListener;

        _hotkeyManager.RegisterCommands(CommandCollection);
    }

    private void SyncConfigChanged(SyncConfig newConfig)
    {
        _syncConfig = newConfig;
        StopAndReload();
    }

    private void ServerConfigChanged(ServerConfig newConfig)
    {
        _serverConfig = newConfig;
        StopAndReload();
    }

    private void StopAndReload()
    {
        SwitchOffPullLoop();
        ReLoad();
    }

    private void StopAndReloadByNewClipboard()
    {
        if (_downServiceChangingLocal)
            return;
        StopAndReload();
    }

    private void ReLoad()
    {
        if (ClientSwitchOn)
            _trayIcon.SetActiveStatus(true);
        else
            _trayIcon.SetActiveStatus(false);

        if (SwitchOn)
            SwitchOnPullLoop();
        else
            SwitchOffPullLoop();
    }

    private CancellationTokenSource? _cancelSource;

    protected override void StartService()
    {
        ReLoad();
    }

    protected override void StopSerivce()
    {
        SwitchOffPullLoop();
    }

    private void SwitchOnPullLoop()
    {
        lock (_isPullLoopRunningLocker)
        {
            if (!_isPullLoopRunning)
            {
                _isPullLoopRunning = true;
                _clipboardMoniter.ClipboardChanged -= StopAndReloadByNewClipboard;
                _clipboardMoniter.ClipboardChanged += StopAndReloadByNewClipboard;
                _clipboardListener.Changed -= ClipboardProfileChanged;
                _clipboardListener.Changed += ClipboardProfileChanged;
                StartPullLoop();
            }
        }
    }

    private void SwitchOffPullLoop()
    {
        lock (_isPullLoopRunningLocker)
        {
            if (_isPullLoopRunning)
            {
                _isPullLoopRunning = false;
                _clipboardMoniter.ClipboardChanged -= StopAndReloadByNewClipboard;
                _clipboardListener.Changed -= ClipboardProfileChanged;
                _localProfileCache = null;
                StopPullLoop();
            }
        }
    }

    public void SetRemoteCache(Profile profile)
    {
        if (profile.Type != ProfileType.Unknown)
        {
            _remoteProfileCache = profile;
        }
    }

    private async void StartPullLoop()
    {
        _trayIcon.SetStatusString(SERVICE_NAME, "Running.");
        _cancelSource = new CancellationTokenSource();
        try
        {
            _logger.Write(LOG_TAG, "Loop start");
            await PullLoop(_cancelSource.Token);
            _logger.Write(LOG_TAG, "Loop exit normally");
        }
        catch (OperationCanceledException)
        {
            _toastReporter?.CancelSicent();
            _toastReporter = null;
            _logger.Write(LOG_TAG, "Canceled");
        }
        catch (Exception ex)
        {
            _logger.Write(LOG_TAG, "Exit with exception" + ex.Message);
        }
    }

    private void StopPullLoop()
    {
        _trayIcon.SetStatusString(SERVICE_NAME, "Stopped.");
        _cancelSource?.Cancel();
        _cancelSource = null;
    }

    private void ClipboardProfileChanged(ClipboardMetaInfomation _, Profile profile)
    {
        _localProfileCache = profile;
    }

    public override void RegistEventHandler()
    {
        Event.RegistEventHandler(SyncService.PUSH_START_ENENT_NAME, PushStartedHandler);
        Event.RegistEventHandler(SyncService.PUSH_STOP_ENENT_NAME, PushStoppedHandler);
    }

    public override void UnRegistEventHandler()
    {
        Event.UnRegistEventHandler(SyncService.PUSH_START_ENENT_NAME, PushStartedHandler);
        Event.UnRegistEventHandler(SyncService.PUSH_STOP_ENENT_NAME, PushStoppedHandler);
    }

    public void PushStartedHandler()
    {
        _logger.Write(LOG_TAG, "due to upload service start, cancel");
        SwitchOffPullLoop();
    }

    public void PushStoppedHandler()
    {
        _logger.Write(LOG_TAG, "due to upload service stop, restart");
        StopAndReload();
    }

    private void SetStatusOnError(ref int errorTimes, Exception ex)
    {
        errorTimes++;
        _trayIcon.SetStatusString(SERVICE_NAME, $"Error. Failed times: {errorTimes}.\n{ex.Message}", true);

        _logger.Write(ex.ToString());
        if (errorTimes > _syncConfig.RetryTimes)
        {
            _notificationManager.ShowText(I18n.Strings.FailedToDownloadClipboard, ex.Message);
            throw new Exception("Download retry times reach limit");
        }
    }
    private async Task PullLoop(CancellationToken cancelToken)
    {
        int errorTimes = 0;
        await Task.Delay(TimeSpan.FromSeconds(0.5), cancelToken);
        while (!cancelToken.IsCancellationRequested && (_isQuickDownload || SwitchOn))
        {
            await SyncService.remoteProfilemutex.WaitAsync(cancelToken);
            try
            {
                await DownloadProcess(cancelToken);
                errorTimes = 0;
                OnDownloadCompleted();
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException)
                {
                    cancelToken.ThrowIfCancellationRequested();
                    ex = new Exception("Request timeout");
                }

                if (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                }
                _toastReporter?.Cancel();
                _toastReporter = null;
                SetStatusOnError(ref errorTimes, ex);
            }
            finally
            {
                SyncService.remoteProfilemutex.Release();
            }

            await Task.Delay(GetIntervalTime(), cancelToken);
        }
    }

    private TimeSpan GetIntervalTime()
    {
        var configTime = TimeSpan.FromSeconds(_syncConfig.IntervalTime);
        var minTime = TimeSpan.FromSeconds(0.5);
        return minTime > configTime ? minTime : configTime;
    }

    private async Task DownloadProcess(CancellationToken token)
    {
        await LocalClipboard.Semaphore.WaitAsync(token);
        using var scopeGuard = new ScopeGuard(() => LocalClipboard.Semaphore.Release());

        var remoteProfile = await _clipboardFactory.CreateProfileFromRemote(token);
        _logger.Write(LOG_TAG, "remote is " + remoteProfile.ToJsonString());

        if (NeedUpdate(remoteProfile))
        {
            await remoteProfile.EnsureAvailable(token);
            await SetRemoteProfileToLocal(remoteProfile, token);
            _remoteProfileCache = remoteProfile;
        }

        _trayIcon.SetStatusString(SERVICE_NAME, "Running.", false);
    }

    private bool NeedUpdate(Profile remoteProfile)
    {
        if (!_isQuickDownload && Profile.Same(remoteProfile, _remoteProfileCache))
        {
            return false;
        }

        return remoteProfile.IsAvailableFromRemote();
    }

    private async Task SetRemoteProfileToLocal(Profile remoteProfile, CancellationToken cancelToken)
    {
        var meta = await _clipboardFactory.GetMetaInfomation(cancelToken);
        Profile localProfile = await _clipboardFactory.CreateProfileFromMeta(meta, cancelToken);
        if (!Profile.Same(remoteProfile, localProfile))
        {
            await DownloadAndSetRemoteProfileToLocal(remoteProfile, cancelToken);
        }
    }

    private async Task DownloadAndSetRemoteProfileToLocal(Profile remoteProfile, CancellationToken cancelToken)
    {
        _trayIcon.SetStatusString(SERVICE_NAME, "Downloading");
        _trayIcon.ShowDownloadAnimation();
        try
        {
            if (Profile.Same(remoteProfile, _remoteProfileCache))
            {
                remoteProfile = _remoteProfileCache!;
            }
            else if (remoteProfile is FileProfile fileProfile)
            {
                _logger.Write("start download: " + remoteProfile.Text);
                _toastReporter = new ProgressToastReporter(remoteProfile.FileName, I18n.Strings.DownloadingFile, _notificationManager);
                await remoteProfile.BeforeSetLocal(cancelToken, _toastReporter);
                _logger.Write("end download: " + remoteProfile.Text);
            }
            _downServiceChangingLocal = true;
            _messenger.Send(EmptyMessage.Instance, SyncService.PULL_START_ENENT_NAME);

            if (!await IsLocalProfileObsolete(cancelToken))
            {
                await remoteProfile.SetLocalClipboard(true, cancelToken, false);
                _localProfileCache = remoteProfile;
                _logger.Write("Success set Local clipboard with remote profile: " + remoteProfile.Text);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancelToken);   // 设置本地剪贴板可能有延迟，延迟发送事件
            }
        }
        finally
        {
            _trayIcon.StopAnimation();
            _downServiceChangingLocal = false;
            _messenger.Send(remoteProfile, SyncService.PULL_STOP_ENENT_NAME);
        }
    }

    private async Task<bool> IsLocalProfileObsolete(CancellationToken token)
    {
        if (_localProfileCache is null)
        {
            return false;
        }
        var profile = await _clipboardFactory.CreateProfileFromLocal(token);
        return !Profile.Same(profile, _localProfileCache);
    }

    private void QuickDownload() => QuickDownload(false);

    private void QuickDownloadAndPaste()
    {
        if (_hotkeyManager.HotkeyStatusMap.TryGetValue(QuickDownloadAndPasteGuid, out var status))
        {
            status.Hotkey?.Keys.ForEach(key => _keyEventSimulator.SimulateKeyRelease(KeyCodeMap.MapReverse[key]));
        }

        QuickDownload(true);
    }

    private void QuickDownload(bool paste)
    {
        SwitchOffPullLoop();
        _remoteProfileCache = null;
        _isQuickDownload = true;
        _isQuickDownloadAndPaste = paste;
        SwitchOnPullLoop();
    }

    private void OnDownloadCompleted()
    {
        if (_isQuickDownloadAndPaste)
        {
            KeyCode modifier = OperatingSystem.IsMacOS() ? KeyCode.VcLeftMeta : KeyCode.VcLeftControl;

            _keyEventSimulator.SimulateKeyPress(modifier);
            _keyEventSimulator.SimulateKeyPress(KeyCode.VcV);

            _keyEventSimulator.SimulateKeyRelease(KeyCode.VcV);
            _keyEventSimulator.SimulateKeyRelease(modifier);
        }
        _isQuickDownload = false;
        _isQuickDownloadAndPaste = false;
    }
}
