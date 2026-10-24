using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Tidy.Core;

namespace Tidy.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private bool allowClose;
    private bool waitingForScan;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => SetDarkTitleBar();
    }

    private void SetDarkTitleBar()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int valueSize);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (viewModel.IsFileOperationRunning)
        {
            e.Cancel = true;
            viewModel.BlockClose();
            return;
        }
        if (!viewModel.IsScanning) { viewModel.Dispose(); return; }
        e.Cancel = true;
        if (waitingForScan) return;
        waitingForScan = true;
        await viewModel.StopScanAsync();
        viewModel.Dispose();
        allowClose = true;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => ChooseFolder();
    private void EmptyState_Click(object sender, RoutedEventArgs e) => ChooseFolder();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await viewModel.RefreshAsync();

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to organize", Multiselect = false };
        if (Directory.Exists(viewModel.Folder)) dialog.InitialDirectory = viewModel.Folder;
        if (dialog.ShowDialog(this) == true) viewModel.SelectFolder(dialog.FolderName);
    }

    private async void Organize_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Move {viewModel.Plans.Count:N0} files into category folders inside:\n\n{viewModel.Folder}\n\nNo files will be overwritten. Undo is available until Tidy closes.", "Organize files?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await viewModel.OrganizeAsync();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Undo the most recent organization? Changed files and occupied original paths will be left in place.", "Undo organization?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await viewModel.UndoAsync();
    }
}
