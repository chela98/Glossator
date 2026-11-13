using System.ComponentModel;
using System.IO;
using System.Windows;
using Glossator.App.Services;
using Glossator.App.ViewModels;
using Wpf.Ui.Controls;

namespace Glossator.App.Views;

/// <summary>
/// Glossator Studio ana Fluent penceresi; drag-drop, diyalog host ve güvenli kapanışı koordine eder.
/// </summary>
public partial class ShellWindow : FluentWindow
{
    private readonly ShellViewModel _viewModel;
    private bool _allowClose;
    private bool _isClosingInProgress;

    public ShellWindow(ShellViewModel viewModel, StudioDialogService dialogService)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();
        dialogService.AttachDialogHost(RootContentDialogHost);
    }

    private async void ShellWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void ShellWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        // Çeviri aktif değilse kapatmayı iptal edip yeniden Close() çağırmaya gerek yok;
        // Closing olayı içinden senkron Close() çağırmak WPF'de InvalidOperationException fırlatır.
        if (!_viewModel.IsTranslating)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        if (_isClosingInProgress)
            return;

        _isClosingInProgress = true;
        try
        {
            if (await _viewModel.HandleClosingAsync())
            {
                _allowClose = true;
                // Closing yığını tamamlandıktan hemen sonra pencereyi güvenli kapat
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        finally
        {
            _isClosingInProgress = false;
        }
    }

    private void ShellWindow_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (TryExtractDropPath(e.Data, out _))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void ShellWindow_PreviewDrop(object sender, DragEventArgs e)
    {
        if (TryExtractDropPath(e.Data, out var path))
        {
            e.Handled = true;
            await _viewModel.OpenBookAsync(path, navigateToWorkspace: true);
        }
    }

    private static bool TryExtractDropPath(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return false;

        var candidate = files[0];
        if (Directory.Exists(candidate) ||
            (File.Exists(candidate) && string.Equals(Path.GetExtension(candidate), ".epub", StringComparison.OrdinalIgnoreCase)))
        {
            path = candidate;
            return true;
        }

        return false;
    }
}
