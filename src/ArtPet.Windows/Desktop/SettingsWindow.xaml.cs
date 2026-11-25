using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArtPet.Windows.Media;

namespace ArtPet.Windows.Desktop;

internal partial class SettingsWindow : Window
{
    private PetSettings _settings;
    private readonly CancellationTokenSource _updates = new();
    private readonly CharacterPack? _startupHoolheyak;
    private ReleaseUpdate? _availableUpdate;
    private AssetReleaseUpdate? _availableAssets;
    private bool _checkingProgram;
    private bool _checkingAssets;
    private bool _installing;

    internal SettingsWindow(PetSettings settings, CharacterPack? startupHoolheyak)
    {
        _settings = settings;
        _startupHoolheyak = startupHoolheyak;
        InitializeComponent();

        SizeOptions.ItemsSource = PetSettings.Sizes.ToArray();
        SizeOptions.SelectedItem = settings.SizePercent;
        FrameRateOptions.ItemsSource = PetSettings.FrameRates.ToArray();
        FrameRateOptions.SelectedItem = settings.AnimationFps;
        IdleLoopsInput.Text = settings.IdleLoopsPerAction.ToString(CultureInfo.InvariantCulture);
        InteractionModeCheckBox.IsChecked = settings.InteractionEnabled;
        FreeModeCheckBox.IsChecked = settings.FreeModeEnabled;
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

        SizeOptions.SelectionChanged += ApplySelections;
        FrameRateOptions.SelectionChanged += ApplySelections;
        InteractionModeCheckBox.Click += ApplySelections;
        FreeModeCheckBox.Click += ApplySelections;
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
            _availableUpdate = result.Update;
            UpdateStatus.Text = result.Message;
            if (result.Update is not null && ReleaseUpdater.CanInstall)
            {
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
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

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } update)
        {
            return;
        }

        CancellationToken token = _updates.Token;
        _installing = true;
        RefreshUpdateButtons();
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
        }
    }

    private async void CheckForAssets(object sender, RoutedEventArgs e)
    {
        CancellationToken token = _updates.Token;
        _checkingAssets = true;
        RefreshUpdateButtons();
        _availableAssets = null;
        InstallAssetButton.Visibility = Visibility.Collapsed;
        AssetStatus.Text = "正在檢查 GitHub…";
        try
        {
            AssetReleaseCheck result = await AssetReleaseUpdater.CheckAsync(_startupHoolheyak?.Installation, token);
            token.ThrowIfCancellationRequested();
            _availableAssets = result.Update;
            AssetStatus.Text = result.Message;
            InstallAssetButton.Visibility = result.Update is null ? Visibility.Collapsed : Visibility.Visible;
            RefreshAssetVersion();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException or IOException or InvalidDataException)
        {
            if (!token.IsCancellationRequested)
            {
                AssetStatus.Text = "無法檢查美術資源，請稍後再試。";
            }
        }
        finally
        {
            _checkingAssets = false;
            RefreshUpdateButtons();
        }
    }

    private async void InstallAssets(object sender, RoutedEventArgs e)
    {
        if (_availableAssets is not { } update)
        {
            return;
        }

        CancellationToken token = _updates.Token;
        _installing = true;
        RefreshUpdateButtons();
        AssetStatus.Text = "正在下載並驗證美術資源…";
        try
        {
            var progress = new Progress<string>(message => AssetStatus.Text = message);
            await Task.Run(() => AssetReleaseUpdater.InstallAsync(update, _startupHoolheyak?.Installation, progress, token), token);
            _availableAssets = null;
            InstallAssetButton.Visibility = Visibility.Collapsed;
            RefreshAssetVersion();
            AssetStatus.Text = "美術資源已安裝，將於下次啟動生效。";
        }
        catch (InvalidDataException error)
        {
            AssetStatus.Text = error.Message;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException || CharacterPack.IsAssetError(error))
        {
            if (!token.IsCancellationRequested)
            {
                AssetStatus.Text = "無法下載或安裝美術資源，請確認網路連線及資料夾寫入權限後再試。";
            }
        }
        finally
        {
            _installing = false;
            RefreshUpdateButtons();
        }
    }

    private void RefreshAssetVersion()
    {
        string current = _startupHoolheyak?.Installation is { } installed
            ? $"目前版本 {installed.Version}"
            : _startupHoolheyak is null ? "尚未安裝" : "正在使用既有素材";
        AssetVersionText.Text = AssetReleaseUpdater.Pending is { } pending
            ? $"{current} · {pending.Version} 將於下次啟動生效"
            : current;
    }

    private void RefreshUpdateButtons()
    {
        CheckUpdateButton.IsEnabled = !_installing && !_checkingProgram;
        CheckAssetButton.IsEnabled = !_installing && !_checkingAssets;
        InstallUpdateButton.IsEnabled = !_installing;
        InstallAssetButton.IsEnabled = !_installing;
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
