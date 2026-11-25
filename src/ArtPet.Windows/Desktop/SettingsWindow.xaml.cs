using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ArtPet.Windows.Media;
using ArtPet.Windows.Wardrobe;

namespace ArtPet.Windows.Desktop;

internal partial class SettingsWindow : Window
{
    private PetSettings _settings;
    private readonly CancellationTokenSource _updates = new();
    private readonly AssetInstallation? _installedArtwork;
    private readonly Action _stopVoice;
    private ReleaseUpdate? _availableUpdate;
    private CharacterPack[] _modelPacks = [];
    private bool _checkingProgram;
    private bool _installing;
    private int _storageRefresh;

    internal SettingsWindow(PetSettings settings, AssetSnapshot? artwork, Action stopVoice)
    {
        _settings = settings;
        _installedArtwork = artwork?.Installation;
        _stopVoice = stopVoice;
        InitializeComponent();

        SizeOptions.ItemsSource = PetSettings.Sizes.ToArray();
        SizeOptions.SelectedItem = settings.SizePercent;
        FrameRateOptions.ItemsSource = PetSettings.FrameRates.ToArray();
        FrameRateOptions.SelectedItem = settings.AnimationFps;
        IdleLoopsInput.Text = settings.IdleLoopsPerAction.ToString(CultureInfo.InvariantCulture);
        InteractionModeCheckBox.IsChecked = settings.InteractionEnabled;
        FreeModeCheckBox.IsChecked = settings.FreeModeEnabled;
        PhysicsModeCheckBox.IsChecked = settings.PhysicsModeEnabled;
        WalkingSpeedOptions.ItemsSource = PetSettings.WalkingSpeeds.ToArray();
        WalkingSpeedOptions.SelectedItem = settings.WalkingSpeedPercent;
        VoiceOptions.SelectedValue = settings.VoiceLanguage;
        SubtitleOptions.SelectedValue = settings.SubtitleLanguage;
        VoiceVolume.Value = settings.VoiceVolumePercent;
        string[] effectLevels = ["關閉", "低", "中", "高"];
        OutlineOptions.ItemsSource = effectLevels;
        ShadowOptions.ItemsSource = effectLevels;
        OutlineOptions.SelectedIndex = settings.OutlineLevel;
        ShadowOptions.SelectedIndex = settings.ShadowLevel;
        ReadStartup();
        CurrentVersionText.Text = $"程式 {ReleaseUpdater.CurrentVersion.ToString(3)}";
        RefreshAssetVersion();
        _modelPacks = artwork?.Packs ?? [];
        RefreshResourceControls();
        Activated += async (_, _) =>
        {
            RefreshResourceControls();
            await RefreshStorageUsageAsync(_updates.Token);
        };
        if (AssetReleaseUpdater.Pending is not null)
        {
            Loaded += async (_, _) =>
            {
                CancellationToken token = _updates.Token;
                await LoadInstalledModelsAsync(token);
            };
        }

        SizeOptions.SelectionChanged += ApplySelections;
        FrameRateOptions.SelectionChanged += ApplySelections;
        InteractionModeCheckBox.Click += ApplySelections;
        FreeModeCheckBox.Click += ApplySelections;
        PhysicsModeCheckBox.Click += ApplySelections;
        WalkingSpeedOptions.SelectionChanged += ApplySelections;
        VoiceOptions.SelectionChanged += ApplySelections;
        SubtitleOptions.SelectionChanged += ApplySelections;
        OutlineOptions.SelectionChanged += ApplySelections;
        ShadowOptions.SelectionChanged += ApplySelections;
        VoiceVolume.ValueChanged += ApplySelections;
        IdleLoopsInput.LostKeyboardFocus += (_, _) => CommitIdleLoops();
        Closing += (_, _) => CommitIdleLoops();
    }

    internal event Action<PetSettings>? SettingsSelected;

    internal void BringToFront()
    {
        // Let the context menu finish closing before asking Windows to activate settings.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!Activate())
            {
                Topmost = true;
                try
                {
                    Activate();
                }
                finally
                {
                    Topmost = false;
                }
            }
        }, DispatcherPriority.Background);
    }

    private void ApplySelections(object sender, RoutedEventArgs e)
    {
        if (SizeOptions.SelectedItem is int size && FrameRateOptions.SelectedItem is int frameRate &&
            WalkingSpeedOptions.SelectedItem is int walkingSpeed &&
            VoiceOptions.SelectedValue is string voice && SubtitleOptions.SelectedValue is string subtitle)
        {
            Apply(_settings with
            {
                SizePercent = size,
                AnimationFps = frameRate,
                InteractionEnabled = InteractionModeCheckBox.IsChecked == true,
                FreeModeEnabled = FreeModeCheckBox.IsChecked == true,
                PhysicsModeEnabled = PhysicsModeCheckBox.IsChecked == true,
                WalkingSpeedPercent = walkingSpeed,
                OutlineLevel = OutlineOptions.SelectedIndex,
                ShadowLevel = ShadowOptions.SelectedIndex,
                VoiceVolumePercent = (int)VoiceVolume.Value,
                VoiceLanguage = voice,
                SubtitleLanguage = subtitle
            });
        }
    }

    private void ReadStartup()
    {
        try
        {
            StartupCheckBox.IsChecked = StartupRegistration.IsEnabled();
            StartupCheckBox.IsEnabled = StartupRegistration.CanRegister || StartupCheckBox.IsChecked == true;
            if (!StartupCheckBox.IsEnabled)
            {
                ShowStartupStatus("請從 ArtPet.Windows.exe 啟動以設定開機啟動。");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            StartupCheckBox.IsEnabled = false;
            ShowStartupStatus("無法讀取 Windows 的開機啟動設定。");
        }
    }

    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.SetEnabled(StartupCheckBox.IsChecked == true);
            StartupStatus.Visibility = Visibility.Collapsed;
            ReadStartup();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ReadStartup();
            ShowStartupStatus("無法儲存 Windows 的開機啟動設定。");
        }
    }

    private void ShowStartupStatus(string message)
    {
        StartupStatus.Text = message;
        StartupStatus.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _updates.Cancel();
        _updates.Dispose();
        base.OnClosed(e);
    }

    private async void CheckForUpdate(object sender, RoutedEventArgs e)
    {
        CancellationToken token = _updates.Token;
        _checkingProgram = true;
        RefreshUpdateButtons();
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _availableUpdate = null;
        UpdateStatus.Text = "正在檢查 GitHub…";
        try
        {
            ReleaseCheck result = await ReleaseUpdater.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            ShowProgramUpdate(result);
        }
        catch (Exception error) when (token.IsCancellationRequested &&
            error is OperationCanceledException or HttpRequestException)
        {
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException or IOException or InvalidDataException)
        {
            UpdateStatus.Text = "無法檢查程式更新，請稍後再試。";
        }
        finally
        {
            _checkingProgram = false;
            RefreshUpdateButtons();
        }
    }

    internal Task InstallStartupUpdateAsync(ReleaseCheck result)
    {
        UpdateTab.IsSelected = true;
        ShowProgramUpdate(result);
        return InstallProgramUpdateAsync();
    }

    private void ShowProgramUpdate(ReleaseCheck result)
    {
        _availableUpdate = result.Update;
        UpdateStatus.Text = result.Message;
        InstallUpdateButton.Visibility = result.Update is not null && ReleaseUpdater.CanInstall
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e) => await InstallProgramUpdateAsync();

    private async Task InstallProgramUpdateAsync()
    {
        if (_installing || _checkingProgram || _availableUpdate is not { } update)
        {
            return;
        }

        CancellationToken token = _updates.Token;
        _installing = true;
        RefreshUpdateButtons();
        RefreshResourceControls();
        UpdateStatus.Text = "正在下載並驗證更新包…";
        try
        {
            await Task.Run(() => ReleaseUpdater.InstallAsync(update, token), token);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (InvalidDataException error)
        {
            UpdateStatus.Text = error.Message;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            BadImageFormatException or Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            UpdateStatus.Text = "無法下載或安裝更新，請確認網路連線及程式資料夾的寫入權限後再試。";
        }
        finally
        {
            _installing = false;
            RefreshUpdateButtons();
            RefreshResourceControls();
        }
    }

    private async void CheckForAssets(object sender, RoutedEventArgs e)
    {
        CancellationToken token = _updates.Token;
        _installing = true;
        RefreshUpdateButtons();
        RefreshResourceControls();
        AssetStatus.Text = "正在檢查資源…";
        try
        {
            AssetReleaseCheck result = await AssetReleaseUpdater.CheckAsync(_installedArtwork, token);
            token.ThrowIfCancellationRequested();
            if (result.Update is not { } update)
            {
                AssetStatus.Text = result.Message;
                RefreshAssetVersion();
                return;
            }

            var progress = new Progress<string>(message => AssetStatus.Text = message);
            await Task.Run(() => AssetReleaseUpdater.InstallAsync(update, _installedArtwork, progress, token), token);
            await LoadInstalledModelsAsync(token);
            RefreshAssetVersion();
            AssetStatus.Text = "全部模型已下載。可在這裡建置，衣櫃將於下次啟動更新。";
            if (update.Manifest.HasSplitResources && !VoiceLibrary.HasChoice(PetSettings.Japanese))
            {
                VoicePackStatus.Text = "日語配音尚未安裝，可在此重試下載。";
            }
        }
        catch (InvalidDataException error)
        {
            AssetStatus.Text = error.Message;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException || CharacterPack.IsAssetError(error))
        {
            if (!token.IsCancellationRequested)
            {
                AssetStatus.Text = "無法下載或安裝資源，請確認網路連線及資料夾寫入權限後再試。";
            }
        }
        finally
        {
            _installing = false;
            RefreshUpdateButtons();
            RefreshResourceControls();
            await RefreshStorageUsageAsync(token);
        }
    }

    private async Task LoadInstalledModelsAsync(CancellationToken token)
    {
        try
        {
            AssetSnapshot? installed = await Task.Run(() => ManagedCharacterAssets.LoadInstalled(ReleaseUpdater.CurrentVersion), token);
            if (!token.IsCancellationRequested)
            {
                _modelPacks = installed?.Packs ?? [];
                RefreshResourceControls();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void OpenModelManager(object sender, RoutedEventArgs e)
    {
        var manager = new WardrobeWindow(null, _modelPacks)
        {
            Owner = this,
            Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        manager.ShowDialog();
    }

    private async void DownloadVoice(object sender, RoutedEventArgs e) => await ManageVoiceAsync(sender, remove: false);
    private async void DeleteVoice(object sender, RoutedEventArgs e) => await ManageVoiceAsync(sender, remove: true);

    private async Task ManageVoiceAsync(object sender, bool remove)
    {
        if (_installing || sender is not Button { Tag: string language } || !VoiceAssets.IsAudioLanguage(language))
        {
            return;
        }
        CancellationToken token = _updates.Token;
        _installing = true;
        RefreshUpdateButtons();
        RefreshResourceControls();
        VoicePackStatus.Text = remove ? "正在刪除配音…" : "正在檢查配音資源…";
        try
        {
            if (remove)
            {
                _stopVoice();
                await Task.Run(() => AssetReleaseUpdater.DeleteVoiceAsync(language, token), token);
                VoicePackStatus.Text = "已刪除下載的配音。";
            }
            else
            {
                var progress = new Progress<string>(message => VoicePackStatus.Text = message);
                Version version = await Task.Run(() => AssetReleaseUpdater.DownloadVoiceAsync(language, progress, token), token);
                VoicePackStatus.Text = $"配音 {version} 已可使用。";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException || CharacterPack.IsAssetError(error))
        {
            VoicePackStatus.Text = error is InvalidDataException ? error.Message : "無法變更配音，請檢查網路或資料夾權限後再試。";
        }
        finally
        {
            _installing = false;
            RefreshUpdateButtons();
            RefreshResourceControls();
            await RefreshStorageUsageAsync(token);
        }
    }

    private async Task RefreshStorageUsageAsync(CancellationToken token)
    {
        int refresh = ++_storageRefresh;
        try
        {
            ResourceStorageUsage usage = await Task.Run(() => ResourceStorageUsage.Read(token), token);
            if (refresh == _storageRefresh && !token.IsCancellationRequested)
            {
                ModelStorage.Text = FormatStorageUsage(usage.Models);
                JapaneseVoiceStorage.Text = FormatStorageUsage(usage.Japanese);
                MandarinVoiceStorage.Text = FormatStorageUsage(usage.Mandarin);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private static string FormatStorageUsage(long? bytes) => bytes switch
    {
        null => "無法計算佔用空間",
        >= 1_000_000_000 => $"佔用空間 {bytes.Value / 1_000_000_000d:0.##} GB",
        0 => "佔用空間 0 MB",
        < 10_000 => "佔用空間 < 0.01 MB",
        _ => $"佔用空間 {bytes.Value / 1_000_000d:0.##} MB"
    };

    private void RefreshResourceControls()
    {
        OpenModelManagerButton.IsEnabled = !_installing && _modelPacks.Any(pack => pack.Models is not null);
        RefreshVoiceStatus(PetSettings.Japanese, JapaneseVoiceStatus, DownloadJapaneseButton, DeleteJapaneseButton);
        RefreshVoiceStatus(PetSettings.Mandarin, MandarinVoiceStatus, DownloadMandarinButton, DeleteMandarinButton);
    }

    private void RefreshVoiceStatus(string language, TextBlock status, Button download, Button delete)
    {
        AssetInstallation? installed = VoiceLibrary.GetInstalled(language);
        status.Text = installed is null ? "未下載獨立配音包" : $"已安裝 {installed.Version}";
        download.IsEnabled = !_installing;
        delete.IsEnabled = !_installing && installed is not null;
    }

    private void RefreshAssetVersion()
    {
        string current = _installedArtwork is { } installed
            ? $"目前版本 {installed.Version}"
            : "正在使用既有素材";
        AssetVersionText.Text = AssetReleaseUpdater.Pending is { } pending
            ? $"{current} · {pending.Version} 將於下次啟動生效"
            : current;
    }

    private void RefreshUpdateButtons()
    {
        CheckUpdateButton.IsEnabled = !_installing && !_checkingProgram;
        CheckAssetButton.IsEnabled = !_installing;
        InstallUpdateButton.IsEnabled = !_installing;
    }

    private void Apply(PetSettings settings)
    {
        if (settings != _settings)
        {
            _settings = settings;
            SettingsSelected?.Invoke(settings);
        }
    }

    private bool TryReadIdleLoops(out int idleLoops) =>
        int.TryParse(IdleLoopsInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out idleLoops) &&
        idleLoops is >= PetSettings.MinIdleLoopsPerAction and <= PetSettings.MaxIdleLoopsPerAction;

    private void ValidateIdleLoops(object sender, RoutedEventArgs e)
    {
        if (IdleLoopsError is not null)
        {
            IdleLoopsError.Visibility = TryReadIdleLoops(out _) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void CommitIdleLoops()
    {
        if (TryReadIdleLoops(out int idleLoops))
        {
            Apply(_settings with { IdleLoopsPerAction = idleLoops });
        }
    }

    private void StepIdleLoops(int amount)
    {
        int current = TryReadIdleLoops(out int idleLoops) ? idleLoops : _settings.IdleLoopsPerAction;
        int next = Math.Clamp(current + amount, PetSettings.MinIdleLoopsPerAction, PetSettings.MaxIdleLoopsPerAction);
        IdleLoopsInput.Text = next.ToString(CultureInfo.InvariantCulture);
        Apply(_settings with { IdleLoopsPerAction = next });
    }

    private void DecreaseIdleLoops(object sender, RoutedEventArgs e) => StepIdleLoops(-1);

    private void IncreaseIdleLoops(object sender, RoutedEventArgs e) => StepIdleLoops(1);

    private void HandleIdleLoopsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitIdleLoops();
                break;
            case Key.Up:
                StepIdleLoops(1);
                break;
            case Key.Down:
                StepIdleLoops(-1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void CloseOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
