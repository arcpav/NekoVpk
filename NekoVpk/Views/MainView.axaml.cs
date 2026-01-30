using Avalonia.Controls;
using Avalonia.Media.Imaging;
using NekoVpk.Core;
using NekoVpk.ViewModels;
using SteamDatabase.ValvePak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using SevenZip;
using System.Linq;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using System.ComponentModel;

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NekoVpk.Views;

public partial class MainView : UserControl
{
    private static readonly HttpClient _imageHttpClient = new HttpClient();
    private CancellationTokenSource? _imageCts;
    private static readonly Dictionary<string, Bitmap> _imageCache = new();

    public MainView()
    {
        InitializeComponent();
    }

    public override void EndInit()
    {
        //ReloadAddonList();
        base.EndInit();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged -= ViewModel_PropertyChanged;
            vm.PropertyChanged += ViewModel_PropertyChanged;
            
            ReloadAddonList();
        }
        base.OnDataContextChanged(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsOnlineMode))
        {
            ResetDetailPanel();
        }
    }

    private void ResetDetailPanel()
    {
        AddonList.SelectedItem = null;
        AddonDetailPanel.IsVisible = false;
        AddonImage.Source = null;
        _imageCts?.Cancel();
        CancelAssetTagChange();
    }

    private void DataGrid_CurrentCellChanged(object? sender, System.EventArgs e)
    {
        CancelAssetTagChange();
        
        _imageCts?.Cancel();

        if (AddonList.SelectedItem == null)
        {
            AddonDetailPanel.IsVisible = false;
            AddonImage.Source = null;
            return;
        }

        if (sender is DataGrid dg && dg.SelectedItem is AddonAttribute att)
        {
            AddonDetailPanel.IsVisible = true;
            
            if (att.Source == AddonSource.WorkShop)
            {
                AddonImage.Source = null;
                
                string localPath = att.GetAbsolutePath(NekoSettings.Default.GameDir);
                if (!File.Exists(localPath))
                {
                    if (!string.IsNullOrEmpty(att.PreviewUrl))
                    {
                        LoadOnlineImage(att.PreviewUrl);
                    }
                    return;
                }
            }

            Package? pak = null;
            try
            {

                pak = att.LoadPackage(NekoSettings.Default.GameDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取 VPK 失败: {ex.Message}");
                pak = null;
            }

            if (pak == null) 
            {
                if (att.Source == AddonSource.WorkShop && !string.IsNullOrEmpty(att.PreviewUrl))
                {
                    LoadOnlineImage(att.PreviewUrl);
                }
                else
                {
                    AddonImage.Source = null;
                }
                return;
            }

            var entry = pak.FindEntry("addonimage.jpg");
            if (entry != null)
            {
                try 
                {
                    pak.ReadEntry(entry, out byte[] output);
                    AddonImage.Source = Bitmap.DecodeToHeight(new System.IO.MemoryStream(output), 128);
                }
                catch
                {
                    AddonImage.Source = null;
                }
            }
            else
            {
                FileInfo jpg = new(Path.ChangeExtension(att.GetAbsolutePath(NekoSettings.Default.GameDir), "jpg"));
                if (jpg.Exists)
                {
                    try
                    {
                        using var fileStream = jpg.OpenRead();
                        AddonImage.Source = Bitmap.DecodeToHeight(fileStream, 128);
                    }
                    catch
                    {
                         AddonImage.Source = null;
                    }
                }
                else
                {
                    if (att.Source == AddonSource.WorkShop && !string.IsNullOrEmpty(att.PreviewUrl))
                    {
                         LoadOnlineImage(att.PreviewUrl);
                    }
                    else
                    {
                         AddonImage.Source = null;
                    }
                }
            }
            pak.Dispose();
        }
    }

    private async void LoadOnlineImage(string url)
    {
        if (_imageCache.TryGetValue(url, out var cachedBitmap))
        {
            AddonImage.Source = cachedBitmap;
            return;
        }

        _imageCts = new CancellationTokenSource();
        var token = _imageCts.Token;

        try
        {
            await Task.Delay(100, token);
            var imageBytes = await _imageHttpClient.GetByteArrayAsync(url, token);
            using var stream = new MemoryStream(imageBytes);
            var bitmap = Bitmap.DecodeToHeight(stream, 256);

            if (_imageCache.Count > 100) _imageCache.Clear();
            _imageCache[url] = bitmap;

            if (!token.IsCancellationRequested)
            {
                AddonImage.Source = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载预览图失败: {ex.Message}");
        }
    }

    private void Button_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ReloadAddonList();
    }

    private void ReloadAddonList()
    {
        if (DataContext is MainViewModel vm)
        {
            Dispatcher.UIThread.Post(() => {
                vm.LoadAddons();
            }, DispatcherPriority.Background);
        }
    }


    private void DataGrid_BeginningEdit(object? sender, Avalonia.Controls.DataGridBeginningEditEventArgs e)
    {
        // if (e.Row.DataContext is AddonAttribute { Source: AddonSource.WorkShop })
        // {
        //     e.Cancel = true;
        // }
    }

    private void DataGrid_CellEditEnded(object? sender, Avalonia.Controls.DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            AddonList addonList = new();
            addonList.Load(NekoSettings.Default.GameDir);
            bool modified = false;
            foreach (var v in AddonAttribute.dirty)
            {
                if (v.Enable.HasValue)
                {
                    modified = true;
                    string keyForAddonList = v.FileName;
                    if (v.Source == AddonSource.WorkShop)
                    {
                        keyForAddonList = "workshop\\" + v.FileName;
                    }
                    addonList.SetEnable(keyForAddonList, (bool)v.Enable);
                }
            }

            if (modified)
            {
                addonList.Save(NekoSettings.Default.GameDir);
            }
        }
    }

    private void AddonList_Menu_LocateFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is AddonAttribute att)
        {
            string gameDir = NekoSettings.Default.GameDir;
            string primaryPath = att.GetAbsolutePath(gameDir);
            FileInfo fileInfo = new(primaryPath);
            
            if (!fileInfo.Exists && att.Source == AddonSource.WorkShop)
            {
                string altPath = Path.Combine(gameDir, "addons", att.FileName);
                FileInfo altFileInfo = new(altPath);
                if (altFileInfo.Exists)
                {
                    fileInfo = altFileInfo;
                }
                else
                {
                    return;
                }
            }
            else if (!fileInfo.Exists) 
            {
                return;
            }

            Process.Start(new ProcessStartInfo() {
                FileName = "explorer.exe",
                Arguments = $"/select, \"{fileInfo.FullName}\"",
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }

    private void AddonList_Menu_OpenPage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is AddonAttribute att && !string.IsNullOrEmpty(att.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = att.Url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private async void AddonList_Menu_Download(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && AddonList.SelectedItems != null && AddonList.SelectedItems.Count > 0)
        {
            var selectedItems = AddonList.SelectedItems.Cast<AddonAttribute>().ToList();
            bool anySuccess = false;

            foreach (var att in selectedItems)
            {
                if (await vm.DownloadAddonAsync(att))
                {
                    anySuccess = true;
                }
            }

            if (anySuccess)
            {
                if (NekoSettings.Default.ClearSearchAfterDownload)
                {
                    vm.SearchKeywords = string.Empty;
                }
                vm.IsOnlineMode = false;
            }
        }
    }

    private async void AddonList_Menu_Delete(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && AddonList.SelectedItems != null && AddonList.SelectedItems.Count > 0)
        {
            var selectedItems = AddonList.SelectedItems.Cast<AddonAttribute>().ToList();
            int count = selectedItems.Count;
            string msg = count == 1 
                ? $"确定要删除 \"{selectedItems[0].Title}\"({selectedItems[0].FileName})吗?"
                : $"确定要删除选中的 {count} 个模组吗?";

            var result = await ShowMessageBoxAsync("删除确认", msg + "\n此操作无法撤销", ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Warning);

            if (result == ButtonResult.Yes)
            {
                foreach (var att in selectedItems)
                {
                    try
                    {
                        vm.DeleteAddon(att);
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageBoxAsync("删除失败", $"无法删除 {att.FileName}:\n{ex.Message}", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error);
                        break;
                    }
                }
            }
        }
    }

    private void AddonList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        foreach (var item in AddonList.SelectedItems)
        {
            if (item is AddonAttribute att)
            {
                if (att.Source == AddonSource.WorkShop)
                {
                    string localPath = att.GetAbsolutePath(NekoSettings.Default.GameDir);
                    if (!File.Exists(localPath))
                    {
                        if (!string.IsNullOrEmpty(att.WorkShopID))
                        {
                            var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={att.WorkShopID}";
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = url,
                                UseShellExecute = true
                            });
                        }
                        return;
                    }
                }

                Process.Start(new ProcessStartInfo()
                {
                    FileName = att.GetAbsolutePath(NekoSettings.Default.GameDir),
                    UseShellExecute = true,
                    Verb = "open",
                });
            }
        }
    }

    private async void Button_Download_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && AddonList.SelectedItem is AddonAttribute att)
        {
            if (await vm.DownloadAddonAsync(att))
            {
                if (NekoSettings.Default.ClearSearchAfterDownload)
                {
                    vm.SearchKeywords = string.Empty;
                }
                vm.IsOnlineMode = false;
            }
        }
    }

    private void Button_CancelDownload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CancelDownload();
        }
    }

    private void AssetTag_Label_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Label label)
        {
            if (label.DataContext is AssetTag tag)
            {
                label.Classes.Add(tag.Color);
            }
        }
    }


    private readonly Dictionary<AssetTag, bool> ModifiedAssetTags = [];

    private void AssetTag_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Label label && label.DataContext is AssetTag tag)
        {
            if (tag.Type == null || !tag.Type.Contains("Survivor"))
                return;

            if (!ModifiedAssetTags.ContainsKey(tag))
            {
                ModifiedAssetTags[tag] = tag.Enable;
            }
            tag.Enable = !tag.Enable;
            AssetTagModifiedPanel.IsVisible = true;
        }
    }

    private async void Button_AssetTagModifiedPanel_Apply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is AddonAttribute att && DataContext is MainViewModel vm)
        {
            Package? pkg = null;
            SevenZipExtractor? extractor = null;
            SevenZipCompressor? compressor = null;
            DirectoryInfo? tmpDir = null; 
            try
            {
                pkg = att.LoadPackage(vm.GameDir);
                tmpDir = new(pkg.FileName + "_nekotmp");

                if (tmpDir.Exists)
                    tmpDir.Delete(true);
                tmpDir.Create();
                tmpDir.Attributes |= FileAttributes.Hidden;

                FileInfo tmpFile = new(Path.Join(tmpDir.FullName, att.FileName + ".nekotmp"));

                // find neko7z
                foreach (var entry in pkg.Entries)
                {
                    if (entry.Key == "neko7z" && entry.Value[0].FileName == "0")
                    {
                        pkg.ExtratFile(entry.Value[0], tmpFile);
                        tmpFile.Refresh();
                        pkg.RemoveFile(entry.Value[0]);
                        extractor = new SevenZipExtractor(tmpFile.FullName, InArchiveFormat.SevenZip);
                        break;
                    }
                }
            
                if (!tmpFile.Exists) tmpFile.Create().Close();
                tmpFile.Attributes |= FileAttributes.Temporary;

                Dictionary<int, string?> disableZipFiles = [];
                List<PackageEntry> disableEntries = [];
                List<string> vpkFiles = [];
                Dictionary<string, string> zipFiles = [];

                if (extractor is not null)
                {
                    foreach (var zipFileData in extractor.ArchiveFileData)
                    {
                        foreach (var tag in ModifiedAssetTags.Keys)
                        {
                            if (tag.Enable && tag.Proporty.IsMatch(zipFileData.FileName))
                            {
                                disableZipFiles.Add(zipFileData.Index, null);
                                vpkFiles.Add(zipFileData.FileName);
                                break;
                            }
                        }
                    }
                }
                foreach (var entry in pkg.Entries)
                {
                    foreach (var f in entry.Value)
                    {
                        foreach (var tag in ModifiedAssetTags.Keys)
                        {
                            if (!tag.Enable && tag.Proporty.IsMatch(f.GetFullPath()))
            {
                                disableEntries.Add(f);
                                break;
                            }
                        }
                    }
                }

                compressor = new SevenZipCompressor()
                {
                    CompressionMode = extractor is null ? CompressionMode.Create : CompressionMode.Append,
                    ArchiveFormat = OutArchiveFormat.SevenZip,
                    CompressionLevel = (CompressionLevel)NekoSettings.Default.CompressionLevel,
                    CompressionMethod = CompressionMethod.Lzma2,
                    EventSynchronization = EventSynchronizationStrategy.AlwaysSynchronous,
                };

                // move zip files to vpk
                if (extractor != null && disableZipFiles.Count > 0) {
                    await extractor.ExtractFilesAsync(tmpDir.FullName, disableZipFiles.Keys.ToArray());
                    foreach (var v in vpkFiles)
                    {
                        FileInfo file = new (Path.Join(tmpDir.FullName, v));
                        if (pkg.AddFile(v, file) != null)
                        {
                        }
                    }
                    // delete file in archive
                    int originCount = extractor.ArchiveFileNames.Count;
                    compressor.ModifyArchive(tmpFile.FullName, disableZipFiles);

                    extractor = new SevenZipExtractor(tmpFile.FullName, InArchiveFormat.SevenZip);
                    if ( extractor.ArchiveFileNames.Count != originCount - disableZipFiles.Count)
                    {
                        throw new Exception("Modified archive has an unexpected number of files.");
                    }
                }

                // move vpk files to zip
                foreach (var entry in disableEntries)
                {
                    FileInfo outFile = new(Path.Join(tmpDir.FullName, entry.GetFullPath()));
                    pkg.ExtratFile(entry, outFile);

                    zipFiles.Add(entry.GetFullPath(), outFile.FullName);
                    pkg.RemoveFile(entry);
                }
                if (zipFiles.Count > 0)
                {
                    compressor.CompressFileDictionary(zipFiles, tmpFile.FullName);
                }

                
                if (zipFiles.Count > 0 || disableZipFiles.Count > 0)
                {
                    tmpFile.Refresh();
                    pkg.AddFile(pkg.GenNekoDir() + "0.neko7z", tmpFile);
                    tmpFile.Delete();
                }

                string originFilePath = pkg.FileName + ".vpk";
                FileInfo srcPakFile = new(originFilePath);
                pkg.Write(tmpFile.FullName, 1);
                pkg.Dispose();

                srcPakFile.MoveTo(Path.ChangeExtension(originFilePath, ".vpk.nekobak"), true);

                // overwrite origin file
                tmpFile.Refresh();
                tmpFile.LastWriteTime = srcPakFile.LastWriteTime;
                tmpFile.CreationTime = srcPakFile.CreationTime;
                tmpFile.MoveTo(originFilePath, true);

                // update UI
                ModifiedAssetTags.Clear();
                AssetTagModifiedPanel.IsVisible = false;
            }
            catch (Exception ex)
            {
                CancelAssetTagChange();
                Debug.WriteLine(ex);

                await ShowMessageBoxAsync("应用失败", $"{ex.Message}\n\n{att.FileName} 被其他程序使用中 关闭游戏或可能的程序后重试", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error);
            }
            finally
            {
                pkg?.Dispose();
                extractor?.Dispose();
                if (tmpDir is not null && tmpDir.Exists)
                    tmpDir.Delete(true);
            }
        }
    }

    private void CancelAssetTagChange()
    {
        AssetTagModifiedPanel.IsVisible = false;
        foreach (var tag in ModifiedAssetTags)
        {
            tag.Key.Enable = tag.Value;
        }
        ModifiedAssetTags.Clear();
    }

    private void Button_AssetTagModifiedPanel_Cancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CancelAssetTagChange();
    }

    private void SubmitAddonSearch()
    {
        if (DataContext is MainViewModel vm)
        {
            if (!vm.IsOnlineMode)
            {
                vm.Addons.Refresh();
            }
        }
    }

    private void Button_AddonSearch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsOnlineMode)
        {
            _ = vm.SearchWorkshopAsync();
        }
        else
        {
            SubmitAddonSearch();
        }
    }

    private void TextBox_AddonSearch_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            if (DataContext is MainViewModel vm && vm.IsOnlineMode)
            {
                _ = vm.SearchWorkshopAsync();
            }
            else
            {
                SubmitAddonSearch();
            }
        }
    }

    private void TextBox_AddonSearch_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsOnlineMode)
        {
            SubmitAddonSearch();
        }
    }

    private void DataGrid_Sorting(object? sender, Avalonia.Controls.DataGridColumnEventArgs e)
    {
        
    }

    private async void Settings_Button_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.VisualRoot is Window window)
        {
            var settingsWindow = new SettingsWindow()
            {
                DataContext = new ViewModels.Settings(),
                Background = window.Background,
            };
            await settingsWindow.ShowDialog(window);
            
            if (DataContext is MainViewModel vm)
            {
                vm.UpdateBackground();
            }
        }
    }


    private void SetAllEnabled(bool enabled)
    {
        if (DataContext is MainViewModel vm)
        {
            foreach (AddonAttribute item in vm.Addons)
            {
                item.Enable = enabled;
            }
            SaveDirtyChanges();
        }
    }

    private void SaveDirtyChanges()
    {
        if (AddonAttribute.dirty.Count == 0) return;

        AddonList addonList = new();
        addonList.Load(NekoSettings.Default.GameDir);
        bool modified = false;

        foreach (var v in AddonAttribute.dirty)
        {
            if (v.Enable.HasValue)
            {
                modified = true;
                string key = v.FileName;
                if (v.Source == AddonSource.WorkShop)
                {
                    key = "workshop\\" + v.FileName;
                }
                addonList.SetEnable(key, (bool)v.Enable);
            }
        }

        if (modified)
        {
            addonList.Save(NekoSettings.Default.GameDir);
        }

        AddonAttribute.dirty.Clear();
    }

    private void MenuItem_SelectAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetAllEnabled(true);
    }
    private void MenuItem_DeselectAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetAllEnabled(false);
    }
    private void MenuItem_InvertSelection_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            foreach (AddonAttribute item in vm.Addons)
            {
                if (item.Enable.HasValue)
                {
                    item.Enable = !item.Enable.Value;
                }
                else
                {
                    item.Enable = true;
                }
            }
            SaveDirtyChanges();
        }
    }

    private async Task<ButtonResult> ShowMessageBoxAsync(string title, string message, ButtonEnum buttons, MsBox.Avalonia.Enums.Icon icon)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(
            new MessageBoxStandardParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = buttons,
                Icon = icon,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, 
                CanResize = false,
                ShowInCenter = true
            });

        if (this.VisualRoot is Window window)
        {
            return await box.ShowWindowDialogAsync(window);
        }
        else
        {
            return await box.ShowAsync();
        }
    }
    
    private void Button_ResetTags_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            foreach (var category in vm.WorkshopTagCategories)
            {
                foreach (var tag in category.Tags)
                {
                    tag.IsSelected = false;
                }
            }
        }
    }
}