using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.IO;
using System;
using System.Linq;
using System.Diagnostics;

namespace NekoVpk.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            //ComboBox_CompressionLevel.ItemsSource = Enum.GetValues(typeof(SevenZip.CompressionLevel));
        }

        public async void SelectBackgroundImage()
        {
            var storage = this.StorageProvider;
            var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景",
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                if (DataContext is ViewModels.Settings vm)
                {
                    vm.BackgroundImagePath = path;
                }
            }
        }

        public async void SelectBackgroundFolder()
        {
            var storage = this.StorageProvider;
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择背景文件夹",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                if (DataContext is ViewModels.Settings vm)
                {
                    vm.BackgroundImagePath = path;
                }
            }
        }

        private async void Browser_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var storageProvider = topLevel.StorageProvider;
            if (storageProvider is null) return;

            IStorageFolder? suggestedStartLocation = null;
            if (NekoSettings.Default.GameDir != "")
            {
                suggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new Uri(NekoSettings.Default.GameDir));
            }
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions()
            {
                Title = "Select Game Directory",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedStartLocation
            });


            if (result is not null && result.Count == 1)
            {
                var dirInfo = new DirectoryInfo(result[0].Path.LocalPath);
                var dirs = dirInfo.GetDirectories("addons");
                if (dirs.Length == 0)
                {
                    dirs = dirInfo.GetDirectories("left4dead2");
                    if (dirs.Length != 0)
                    {
                        GameDir.Text = dirs[0].FullName;
                        return;
                    }
                }
                GameDir.Text = dirInfo.FullName;
            }
        }

        private void OpenSteamApiKey_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "https://steamcommunity.com/dev/apikey",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开申请页面失败: {ex.Message}");
            }
        }
    }
}
