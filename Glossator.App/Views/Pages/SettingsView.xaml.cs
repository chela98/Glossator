using System.Windows;
using System.Windows.Controls;
using Glossator.App.ViewModels;

namespace Glossator.App.Views.Pages;

public partial class SettingsView : UserControl
{
    private bool _syncingPasswordBox;
    private bool _syncingPromptCombo;
    private SettingsViewModel? _boundVm;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncFromViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.ApiKeyReloaded -= OnApiKeyReloaded;
        }

        _boundVm = DataContext as SettingsViewModel;

        if (_boundVm != null)
        {
            _boundVm.ApiKeyReloaded += OnApiKeyReloaded;
            SyncFromViewModel();
        }
    }

    private void OnApiKeyReloaded(object? sender, EventArgs e)
    {
        SyncPasswordBoxFromVm();
    }

    private void SyncFromViewModel()
    {
        SyncPasswordBoxFromVm();

        if (_boundVm != null)
        {
            _syncingPromptCombo = true;
            try
            {
                CmbPromptFiles.SelectedItem = _boundVm.SelectedPromptFile;
            }
            finally
            {
                _syncingPromptCombo = false;
            }
        }
    }

    private void SyncPasswordBoxFromVm()
    {
        if (_boundVm == null) return;

        _syncingPasswordBox = true;
        try
        {
            TxtProviderApiKey.SetCurrentValue(Wpf.Ui.Controls.PasswordBox.IsPasswordRevealedProperty, false);
            TxtProviderApiKey.Password = _boundVm.EditApiKey;
        }
        finally
        {
            _syncingPasswordBox = false;
        }
    }

    private void TxtProviderApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswordBox || _boundVm == null) return;
        _boundVm.EditApiKey = TxtProviderApiKey.Password;
    }

    private async void CmbPromptFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_syncingPromptCombo || _boundVm == null || CmbPromptFiles.SelectedItem is not string targetFile)
                return;

            if (targetFile == _boundVm.SelectedPromptFile)
                return;

            bool switched = await _boundVm.TrySwitchPromptAsync(targetFile);
            if (!switched)
            {
                _syncingPromptCombo = true;
                try
                {
                    CmbPromptFiles.SelectedItem = _boundVm.SelectedPromptFile;
                }
                finally
                {
                    _syncingPromptCombo = false;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"CmbPromptFiles_SelectionChanged error: {ex}");
        }
    }
}
