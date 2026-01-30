using NekoVpk.Core;
using NekoVpk.Views;
using SteamDatabase.ValvePak;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Narod.SteamGameFinder;
using ReactiveUI;
using Avalonia.Collections;
using SevenZip;
using System.Diagnostics;
using DotNet.Globbing;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace NekoVpk.ViewModels;

public class SelectableTag : ReactiveObject
{
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public SelectableTag(string name, string? display = null)
    {
        Name = name;
        Display = display ?? name;
    }
}

public class TagCategory
{
    public string Header { get; set; } = "";
    public List<SelectableTag> Tags { get; set; } = new();

    public TagCategory(string header, params string[] tags)
    {
        Header = header;
        foreach (var t in tags)
        {
            Tags.Add(new SelectableTag(t)); 
        }
    }
}

public partial class MainViewModel : ViewModelBase
{

    public FontFamily UserFont
    {
        get
        {
            var fontName = NekoSettings.Default.UserFont;
            if (string.IsNullOrWhiteSpace(fontName))
            {
                return FontFamily.Default;
            }
            return new FontFamily(fontName);
        }
    }
    
    public double UserFontSize => NekoSettings.Default.UserFontSize;
    
    private static readonly HttpClient _httpClient = new HttpClient();
    private CancellationTokenSource? _downloadCts;

    private HashSet<string> _localWorkshopIds = new();

    public string GameDir 
    {
        get => NekoSettings.Default.GameDir;
        set {
            if (NekoSettings.Default.GameDir != value)
            {
                NekoSettings.Default.GameDir = value;
                this.RaisePropertyChanged(nameof(GameDir));
            }
        }
    }

    private List<AddonAttribute> _addonList = [];

    private DataGridCollectionView _Addons;

    public DataGridCollectionView Addons => _Addons;

    string? _SearchKeywords = "";

    public string? SearchKeywords { get => _SearchKeywords; set => this.RaiseAndSetIfChanged(ref _SearchKeywords, value); }

    public ObservableCollection<TagCategory> WorkshopTagCategories { get; } = new();

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private bool _isOnlineMode;
    public bool IsOnlineMode
    {
        get => _isOnlineMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOnlineMode, value);
            ToggleOnlineMode();
        }
    }

    private string _searchWatermark = "Search";
    public string SearchWatermark
    {
        get => _searchWatermark;
        set => this.RaiseAndSetIfChanged(ref _searchWatermark, value);
    }

    private bool _showNotImplemented;
    public bool ShowNotImplemented
    {
        get => _showNotImplemented;
        set => this.RaiseAndSetIfChanged(ref _showNotImplemented, value);
    }

    private async void ToggleOnlineMode()
    {
        if (IsOnlineMode)
        {
            if (string.IsNullOrWhiteSpace(NekoSettings.Default.SteamApiKey))
            {
                var box = MessageBoxManager.GetMessageBoxStandard(
                    "Failed missing key",
                    "在设置中配置API密钥才能使用在线搜索功能",
                    ButtonEnum.Ok);
                await box.ShowAsync();
                
                IsOnlineMode = false;
                return;
            }

            SearchWatermark = "搜索工坊模组(Enter)";
            _addonList.Clear();
            Addons.Refresh();
            ShowNotImplemented = false;
        }
        else
        {
            SearchWatermark = "搜索本地模组";
            ShowNotImplemented = false;
            LoadAddons();
        }
    }

    private Bitmap? _backgroundImage;
    public Bitmap? BackgroundImage
    {
        get => _backgroundImage;
        set => this.RaiseAndSetIfChanged(ref _backgroundImage, value);
    }

    public double BackgroundDimOpacity => 1.0 - (NekoSettings.Default.BackgroundBrightness / 100.0);

    public Stretch BackgroundStretch
    {
        get
        {
            string s = NekoSettings.Default.BackgroundStretch;
            if (Enum.TryParse<Stretch>(s, out var result))
            {
                return result;
            }
            return Stretch.UniformToFill;
        }
    }

    public MainViewModel()
    {
        NekoSettings.Default.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(NekoSettings.Default.UserFont))
            {
                Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(UserFont)));
            }
            else if (e.PropertyName == nameof(NekoSettings.Default.UserFontSize))
            {
                Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(UserFontSize)));
            }
        };

        if (NekoSettings.Default.GameDir == "")
        {
            NekoSettings.Default.GameDir = TryToFindGameDir() ?? NekoSettings.Default.GameDir;
        }

        _Addons = new(_addonList) { Filter = AddonsFilter };

        InitTags();
        UpdateBackground(); 
    }

    private void InitTags()
    {
        WorkshopTagCategories.Clear();

        WorkshopTagCategories.Add(new TagCategory("Survivors", 
            "Bill", "Francis", "Louis", "Zoey", "Coach", "Ellis", "Nick", "Rochelle"));

        WorkshopTagCategories.Add(new TagCategory("Infected", 
            "Common Infected", "Special Infected", "Boomer", "Charger", "Hunter", "Jockey", "Smoker", "Spitter", "Tank", "Witch"));

        var gameContent = new TagCategory("Game Content");
        gameContent.Tags.AddRange([
            new SelectableTag("Campaign", "Campaigns"),
            new SelectableTag("Weapon", "Weapons"),
            new SelectableTag("Items", "Items"),
            new SelectableTag("Sounds", "Sounds"),
            new SelectableTag("Scripts", "Scripts"),
            new SelectableTag("UI", "UI"),
            new SelectableTag("Model", "Models"),  
            new SelectableTag("Texture", "Textures")
        ]);
        WorkshopTagCategories.Add(gameContent);

        WorkshopTagCategories.Add(new TagCategory("Game Modes", 
            "Co-op", "Versus", "Survival", "Realism"));
        WorkshopTagCategories.Add(new TagCategory("Weapons Detail", 
            "Melee", "Pistol", "Rifle", "Shotgun", "SMG", "Sniper", "Throwable"));
        WorkshopTagCategories.Add(new TagCategory("Items Detail", 
            "Adrenaline", "Defibrillator", "Medkit", "Pills"));

        foreach (var category in WorkshopTagCategories)
        {
            foreach (var tag in category.Tags)
            {
                tag.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SelectableTag.IsSelected))
                    {
                        if (IsOnlineMode && !IsDownloading)
                        {
                            _ = SearchWorkshopAsync();
                        }
                    }
                };
            }
        }
    }

    public async Task SearchWorkshopAsync()
    {
        if (!IsOnlineMode) return;

        var selectedTags = WorkshopTagCategories
            .SelectMany(c => c.Tags)
            .Where(t => t.IsSelected)
            .Select(t => t.Name)
            .ToList();

        if (string.IsNullOrWhiteSpace(SearchKeywords) && selectedTags.Count == 0) return;

        try
        {
            _addonList.Clear();
            Addons.Refresh();

            var apiKey = NekoSettings.Default.SteamApiKey;
            var appId = 550;
            var searchText = Uri.EscapeDataString(SearchKeywords ?? "");

            string url = $"https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/?" +
             $"key={apiKey}&appid={appId}&search_text={searchText}&" +
             $"return_tags=1&return_details=1&numperpage=50&page=0&query_type=0" +
             $"&match_all_tags=0";

            for (int i = 0; i < selectedTags.Count; i++)
            {
                url += $"&requiredtags[{i}]={Uri.EscapeDataString(selectedTags[i])}";
            }

            var jsonStr = await _httpClient.GetStringAsync(url);
            var result = JsonSerializer.Deserialize<SteamApiResponse>(jsonStr);

            List<string> creatorIds = new();

            if (result?.Response?.PublishedFileDetails != null)
            {
                foreach (var item in result.Response.PublishedFileDetails)
                {
                    var info = new AddonInfo
                    {
                        Title = item.Title,
                        Author = item.Creator,
                        Description = item.Description,
                        Url0 = item.PreviewUrl 
                    };

                    string tagStr = "";
                    if (item.Tags != null)
                        tagStr = string.Join(", ", item.Tags.Select(t => t.Tag));

                    var attribute = new AddonAttribute(
                        enable: false, 
                        fileName: item.PublishedFileId + ".vpk", 
                        source: AddonSource.WorkShop, 
                        addonInfo: info, 
                        types: tagStr
                    );
                    
                    attribute.WorkShopID = item.PublishedFileId;
                    
                    if (!string.IsNullOrEmpty(attribute.WorkShopID) && _localWorkshopIds.Contains(attribute.WorkShopID))
                    {
                        attribute.IsInstalled = true;
                    }

                    if (long.TryParse(item.FileSizeStr, out long sizeBytes))
                    {
                        attribute.FileSizeRaw = sizeBytes;
                    }
                    attribute.Subscriptions = item.Subscriptions;

                    if (item.TimeUpdated > 0)
                    {
                        attribute.LastUpdate = DateTimeOffset.FromUnixTimeSeconds(item.TimeUpdated).DateTime.ToLocalTime();
                    }

                    if (!string.IsNullOrEmpty(item.Creator))
                        creatorIds.Add(item.Creator);

                    _addonList.Add(attribute);
                }
            }
            Addons.Refresh();

            if (creatorIds.Count > 0)
            {
                await UpdateAuthorNamesAsync(creatorIds.Distinct().ToList());
            }
        }
        catch (Exception ex)
        {
            var box = MessageBoxManager.GetMessageBoxStandard("搜索失败", $"{ex.Message}", ButtonEnum.Ok);
            await box.ShowAsync();
        }
    }

    private async Task UpdateAuthorNamesAsync(List<string> steamIds)
    {
        try
        {
            var apiKey = NekoSettings.Default.SteamApiKey;
            var idsStr = string.Join(",", steamIds.Take(100));
            string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={apiKey}&steamids={idsStr}";

            var jsonStr = await _httpClient.GetStringAsync(url);
            var result = JsonSerializer.Deserialize<SteamUserResponse>(jsonStr);

            if (result?.Response?.Players != null)
            {
                foreach (var player in result.Response.Players)
                {
                    foreach (var addon in _addonList.Where(a => a.AddonInfo_Author == player.SteamId))
                    {
                        addon.UpdateAuthorName(player.PersonaName);
                    }
                }
                Addons.Refresh();
            }
        }
        catch { }
    }

    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    public async Task<bool> DownloadAddonAsync(AddonAttribute addon)
    {
        if (addon.Source != AddonSource.WorkShop || string.IsNullOrEmpty(addon.WorkShopID)) return false;
        if (IsDownloading) return false;
        if (addon.IsInstalled) return false; 

        IsDownloading = true;
        DownloadProgress = 0;
        _downloadCts = new CancellationTokenSource();

        string? originalAuthor = addon.Author;

        try
        {
            var apiKey = NekoSettings.Default.SteamApiKey;
            string detailsUrl = $"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?key={apiKey}&publishedfileids[0]={addon.WorkShopID}";
            
            var jsonStr = await _httpClient.GetStringAsync(detailsUrl, _downloadCts.Token);
            var result = JsonSerializer.Deserialize<SteamApiResponse>(jsonStr);
            
            var details = result?.Response?.PublishedFileDetails?.FirstOrDefault();
            
            if (details == null || string.IsNullOrEmpty(details.FileUrl))
            {
                throw new Exception("没有找到下载链接 请尝试手动订阅");
            }

            string fileName = $"{addon.WorkShopID}.vpk";
            
            string targetDir = Path.Join(GameDir, "addons");
            string targetPath = Path.Join(targetDir, fileName);

            if (!Directory.Exists(targetDir))
            {
                throw new Exception($"找不到游戏模组文件夹 {targetDir}");
            }
            
            addon.UpdateAuthorName("下载中"); 

            using (var response = await _httpClient.GetAsync(details.FileUrl, HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1;

                using (var contentStream = await response.Content.ReadAsStreamAsync(_downloadCts.Token))
                using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, _downloadCts.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, _downloadCts.Token);
                        totalRead += read;

                        if (canReportProgress)
                        {
                            DownloadProgress = (double)totalRead / totalBytes * 100;
                        }
                    }
                }
            }

            addon.IsInstalled = true;
            if (!string.IsNullOrEmpty(addon.WorkShopID))
            {
                _localWorkshopIds.Add(addon.WorkShopID);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            var box = MessageBoxManager.GetMessageBoxStandard("下载失败", ex.Message, ButtonEnum.Ok);
            await box.ShowAsync();
            return false;
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 0;
            _downloadCts = null;
            addon.UpdateAuthorName(originalAuthor);
        }
    }

    public void DeleteAddon(AddonAttribute addon)
    {
        string path = addon.GetAbsolutePath(GameDir);
        
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string jpgPath = Path.ChangeExtension(path, ".jpg");
        if (File.Exists(jpgPath))
        {
            File.Delete(jpgPath);
        }

        string bakPath = path + ".nekobak";
        if (File.Exists(bakPath))
        {
            File.Delete(bakPath);
        }

        _addonList.Remove(addon);
        
        if (!string.IsNullOrEmpty(addon.WorkShopID))
        {
            _localWorkshopIds.Remove(addon.WorkShopID);
        }
        Addons.Refresh();
    }

    public void UpdateBackground()
    {
        var path = NekoSettings.Default.BackgroundImagePath;
        
        BackgroundImage?.Dispose();

        if (string.IsNullOrEmpty(path))
        {
            BackgroundImage = null;
            return;
        }

        string? targetFile = null;

        try
        {
            if (File.Exists(path))
            {
                targetFile = path;
            }
            else if (Directory.Exists(path))
            {
                var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
                var files = Directory.EnumerateFiles(path)
                                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                                    .ToList();

                if (files.Count > 0)
                {
                    var rand = new Random();
                    targetFile = files[rand.Next(files.Count)];
                }
            }

            if (targetFile != null && File.Exists(targetFile))
            {
                using var stream = File.OpenRead(targetFile);
                BackgroundImage = new Bitmap(stream);
            }
            else
            {
                BackgroundImage = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载图片失败: {ex.Message}");
            BackgroundImage = null;
        }

        this.RaisePropertyChanged(nameof(BackgroundDimOpacity));
        this.RaisePropertyChanged(nameof(BackgroundStretch));
    }

    public static string? TryToFindGameDir()
    {
        SteamGameLocator steamGameLocator = new();
        if (steamGameLocator.getIsSteamInstalled())
        {
            SteamGameLocator.GameStruct result = steamGameLocator.getGameInfoByFolder("Left 4 Dead 2");
            if (result.steamGameLocation != null)
            {
                return Path.Join(result.steamGameLocation, "left4dead2");
            }
        }
        return null;
    }

    public async void LoadAddons()
    {
        TaggedAssets.Load();
        var addonDir = new DirectoryInfo(Path.Join(GameDir, "addons"));
        var workshopDir = new DirectoryInfo(Path.Join(GameDir, "addons", "workshop"));

        if (!addonDir.Exists)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(
                "Failed Game directory",
                "非预期的游戏目录结构",
                ButtonEnum.Ok);
            await box.ShowAsync();
            return;
        }

        _localWorkshopIds.Clear();

        var files = addonDir.GetFiles("*.vpk").ToList();
        if (workshopDir.Exists)
            files.AddRange(workshopDir.GetFiles("*.vpk"));

        AddonList addonList = new();
        try
        {
            addonList.Load(GameDir);
        }
        catch (Exception ex)
        {
            App.Logger.Error(ex);
        }

        _addonList.Clear();
        foreach (FileInfo fileInfo in files)
        {
            bool? addonEnabled = null;
            AddonSource addonSource = AddonSource.Local;

            string keyForAddonList = fileInfo.Name;

            if (fileInfo.Directory!.Name == workshopDir.Name)
            {
                addonSource = AddonSource.WorkShop;
                keyForAddonList = "workshop\\" + fileInfo.Name;
            }
            else
            {
                addonSource = AddonSource.Local;
                keyForAddonList = fileInfo.Name;
            }

            addonEnabled = addonList.IsEnabled(keyForAddonList);

            Package pak = new();
                try
                {
                    pak.Read(fileInfo.FullName);
                } 
                catch(Exception ex)
                {
                    if (!NekoSettings.Default.IgnoreVpkErrors) 
                    {
                        var box = MessageBoxManager.GetMessageBoxStandard(
                            "读取VPK失败",
                            $"无法打开：\n{fileInfo.FullName}\n{ex.Message}\n\n下次启动还要提示这类错误吗?", 
                            ButtonEnum.YesNo);

                        var result = await box.ShowAsync();

                        if (result == ButtonResult.No)
                        {
                            NekoSettings.Default.IgnoreVpkErrors = true;
                            NekoSettings.Default.Save(); 
                        }
                    }
                    continue; 
                }

            PackageEntry? addonInfoEntry = null;

            List<AssetTag> tags = [];

            Func<string, bool, bool> checkPath = (p, isHidden) => {
                if (TaggedAssets.GetAssetTag(p, isHidden) is AssetTag tag)
                {
                    if (!tags.Contains(tag))
                        tags.Add(tag);
                }
                return false;
            };

            if (pak.Version != 1 && pak.Version != 2)
            {
                string glob = $"VPK-Version-{pak.Version}";
                AssetTag? versionTag = TaggedAssets.GetAssetTag(glob, false);
                if (versionTag is null)
                {
                    TaggedAssets.Tags.Add(new AssetTagProperty("0x" + Convert.ToString(pak.Version, 16).ToUpper(), [Glob.Parse(glob)]));
                    versionTag = new AssetTag(TaggedAssets.Tags.Count - 1, false);
                }
                tags.Add(versionTag);
            }

            var entties = pak.Entries;
            foreach (var entity in entties)
            {
                foreach (var file in entity.Value)
                {
                    var path = file.GetFullPath();
                    if (path == "addoninfo.txt")
                        addonInfoEntry = file;
                    else if (file.TypeName == "neko7z" && file.FileName == "0")
                    {
                        pak.ReadEntry(file, out byte[] neko7zBytes);
                        SevenZipExtractor extractor = new(new MemoryStream(neko7zBytes));
                        var archiveFileNames = extractor.ArchiveFileNames;
                        foreach (var zipFile in archiveFileNames)
                        {
                            checkPath(zipFile, false);
                        }
                    }
                    else
                    {
                        checkPath(path, true);
                    }
                }
            }


            AddonInfo? addonInfo = null;
            if (addonInfoEntry != null)
            {
                try
                {
                    pak.ReadEntry(addonInfoEntry, out byte[] addonInfoContents);
                    addonInfo = AddonInfo.Load(addonInfoContents);
                }
                catch (Exception)
                {
                    //App.Logger.Error($"加载模组信息出现错误 \"{fileInfo.FullName}\".\n{ex.Message}");
                }
            }
            addonInfo ??= new();
            

            string types = string.Empty;
            foreach (var t in tags)
            {
                if (t.Type is null) { continue; }
                foreach (var t2 in t.Type)
                {
                    if (!types.Contains(t2))
                    {
                        if (types.Length > 0)
                            types += $", {t2}";
                        else
                            types = t2;
                    }
                }
            }

            AddonAttribute newItem = new(addonEnabled, fileInfo.Name, addonSource, addonInfo, types);
            newItem.Tags = [.. tags.OrderBy(x => x.Name)];


            var baseName = Path.ChangeExtension(fileInfo.Name, null);
            if (newItem.IsSubscribed || baseName.All(char.IsDigit))
            {
                newItem.WorkShopID = baseName;
                
                if (!string.IsNullOrEmpty(newItem.WorkShopID))
                {
                    _localWorkshopIds.Add(newItem.WorkShopID);
                }
            }

            newItem.ModificationTime = fileInfo.LastWriteTime;
            newItem.CreationTime = fileInfo.CreationTime;
            newItem.FileSizeRaw = fileInfo.Length;
            
            newItem.IsInstalled = true;

            _addonList.Add(newItem);
        }
        Addons.Refresh();
    }

    bool AddonsFilter(object obj)
    {
        if (String.IsNullOrEmpty(SearchKeywords))
        {
            return true;
        }
        if (obj is AddonAttribute att)
        {
            var keywordList = new List<string>(SearchKeywords.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            int match = 0;
            foreach (var str in keywordList)
            {
                foreach(var tag in att.Tags)
                {
                    if (tag.Name.Contains(str, StringComparison.OrdinalIgnoreCase))
                    {
                        match++; continue;
                    } 
                }

                if (match >= keywordList.Count) return true;
                if (att.Title.Contains(str, StringComparison.OrdinalIgnoreCase))
                {
                    match++; continue;
                }

                if (match >= keywordList.Count) return true;
                if (att.Author is not null 
                    && att.Author.Contains(str, StringComparison.OrdinalIgnoreCase))
                {
                    match++; continue;
                }

                if (match >= keywordList.Count) return true;
                if (att.FileName.Contains(str, StringComparison.OrdinalIgnoreCase))
                {
                    match++; continue;
                }

                if (match >= keywordList.Count) return true;
                if (att.Type.Contains(str, StringComparison.OrdinalIgnoreCase))
                {
                    match++; continue;
                }
            }

            if (match >= keywordList.Count) return true;

        }
        return false;
    }
}

public class SteamApiResponse
{
    [JsonPropertyName("response")]
    public SteamQueryFilesResponse? Response { get; set; }
}

public class SteamQueryFilesResponse
{
    [JsonPropertyName("publishedfiledetails")]
    public List<SteamPublishedFileDetails>? PublishedFileDetails { get; set; }
}

public class SteamPublishedFileDetails
{
    [JsonPropertyName("publishedfileid")]
    public string? PublishedFileId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("file_description")]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    public List<SteamTag>? Tags { get; set; }

    [JsonPropertyName("creator")]
    public string? Creator { get; set; } 

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("file_url")]
    public string? FileUrl { get; set; }

    [JsonPropertyName("time_updated")]
    public long TimeUpdated { get; set; }

    [JsonPropertyName("file_size")]
    public object? FileSizeRaw { get; set; } 
    
    [JsonIgnore]
    public string FileSizeStr => FileSizeRaw?.ToString() ?? "0";

    [JsonPropertyName("subscriptions")]
    public int Subscriptions { get; set; }
}

public class SteamTag
{
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

public class SteamUserResponse
{
    [JsonPropertyName("response")]
    public SteamUserResponseData? Response { get; set; }
}

public class SteamUserResponseData
{
    [JsonPropertyName("players")]
    public List<SteamPlayer>? Players { get; set; }
}

public class SteamPlayer
{
    [JsonPropertyName("steamid")]
    public string? SteamId { get; set; }

    [JsonPropertyName("personaname")]
    public string? PersonaName { get; set; }
}