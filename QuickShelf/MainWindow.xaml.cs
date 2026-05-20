using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QuickShelf.Models;
using QuickShelf.Services;
using ToolGood.Words.Pinyin;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace QuickShelf;

public partial class MainWindow : Window
{
    private const int HotKeyId = 0x5153;
    private const int WmHotKey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const double ResizeBorder = 9;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int DoubleCtrlIntervalMilliseconds = 420;
    private const string DefaultHotKey = "Ctrl+Alt+A";
    private const string DoubleCtrlHotKey = "Ctrl+Ctrl";
    internal const string StartupArgument = "--startup";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValueName = "QuickShelf";
    private const string SourceStartMenu = "开始菜单";
    private const string SourceRegistry = "注册表";
    private const string SourceManual = "手动添加";
    private const string SourceDragDrop = "拖拽添加";
    private const string SourceAppsFolder = "AppsFolder";
    private const string AllFavoriteGroupsLabel = "全部";
    private const string DefaultFavoriteGroupName = "应用";
    private const string FavoriteGroupDragDataFormat = "QuickShelf.FavoriteGroup";

    private static readonly string[] NoisyAllAppFragments =
    [
        "uninstall",
        "unins",
        "uninstaller",
        "updater",
        "update",
        "maintenance",
        "setup",
        "installshield",
        "crashhandler",
        "卸载",
        "更新程序"
    ];

    private readonly AppScanner _scanner = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly LauncherService _launcher = new();
    private readonly IconCache _iconCache = new();
    private readonly ObservableCollection<LaunchItem> _allItems = [];
    private readonly ObservableCollection<LaunchItem> _favorites = [];
    private readonly ObservableCollection<string> _favoriteGroups = [];
    private readonly ICollectionView _allItemsView;
    private readonly ICollectionView _favoriteItemsView;
    private readonly Dictionary<string, SearchIndex> _searchIndexes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compactHiddenAllItemIds = new(StringComparer.Ordinal);
    private readonly bool _startHidden;

    private AppSettings _settings = new();
    private string _activeFavoriteGroup = AllFavoriteGroupsLabel;
    private WinForms.NotifyIcon? _notifyIcon;
    private HwndSource? _hwndSource;
    private bool _allowClose;
    private bool _isBusy;
    private bool _isApplyingSettings;
    private bool _isUpdatingFavoriteGroups;
    private bool _hotKeyRegistered;
    private bool _favoriteOrderChangedDuringDrag;
    private IntPtr _keyboardHookHandle;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private long _lastCtrlKeyDownTick;
    private long _lastDoubleCtrlTriggerTick;
    private long _lastCaptureCtrlTick;
    private System.Windows.Point _favoriteDragStartPoint;
    private System.Windows.Point _favoriteDragPointerOffset;
    private System.Windows.Point _favoriteGroupDragStartPoint;
    private ListBoxItem? _draggedFavoriteContainer;
    private FavoriteDragPreviewWindow? _favoriteDragPreviewWindow;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        InitializeComponent();
        if (_startHidden)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }

        _allItemsView = CollectionViewSource.GetDefaultView(_allItems);
        _allItemsView.Filter = FilterAllItems;
        _favoriteItemsView = CollectionViewSource.GetDefaultView(_favorites);
        _favoriteItemsView.Filter = FilterFavorites;
        AllItemsList.ItemsSource = _allItemsView;
        FavoritesList.ItemsSource = _favoriteItemsView;
        FavoriteGroupList.ItemsSource = _favoriteGroups;

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        LoadSettings();
        if (_startHidden)
        {
            HideStartupWindow();
        }

        await RefreshItemsAsync();
        if (!_startHidden)
        {
            SearchBox.Focus();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshItemsAsync();
    }

    private void AddApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        AddExecutableOrShortcut();
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        AddFile("*.*", "选择文件", "所有文件 (*.*)|*.*");
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择要快捷打开的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var name = new DirectoryInfo(dialog.SelectedPath).Name;
        var item = LaunchItem.Create(name, dialog.SelectedPath, LaunchItemKind.Folder, SourceManual);
        AddManualItem(item);
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (AllItemsList.SelectedItem is LaunchItem item)
        {
            AddFavorite(item);
            return;
        }

        AddExecutableOrShortcut();
    }

    private void AddFromAllItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LaunchItem item)
        {
            AddFavorite(item);
        }
    }

    private void LaunchSelectedFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is LaunchItem item)
        {
            OpenItem(item);
            return;
        }

        var first = _favorites.FirstOrDefault();
        if (first is not null)
        {
            OpenItem(first);
            return;
        }

        SetStatus("堆栈里还没有可启动项目。");
    }

    private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is not LaunchItem item)
        {
            SetStatus("请选择要移除的项目。");
            return;
        }

        RemoveFavorite(item);
    }

    private void MoveFavoriteUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveFavorite(-1);
    }

    private void MoveFavoriteDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveFavorite(1);
    }

    private void EditStackButton_Click(object sender, RoutedEventArgs e)
    {
        FavoritesList.Focus();
        if (FavoritesList.SelectedIndex < 0 && FavoritesList.Items.Count > 0)
        {
            FavoritesList.SelectedIndex = 0;
        }

        SetStatus("选择堆栈项目后，可用排序或移除按钮管理。");
    }

    private void FavoriteGroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFavoriteGroups || FavoriteGroupList.SelectedItem is not string groupName)
        {
            return;
        }

        _activeFavoriteGroup = groupName;
        _favoriteItemsView.Refresh();
        UpdateFavoriteCount();
        SetStatus(groupName == AllFavoriteGroupsLabel
            ? "正在查看全部堆栈项目。"
            : $"正在查看「{groupName}」分组。");
    }

    private void FavoriteGroupList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _favoriteGroupDragStartPoint = e.GetPosition(null);

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } listBoxItem)
        {
            listBoxItem.IsSelected = true;
        }
    }

    private void FavoriteGroupList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _favoriteGroupDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _favoriteGroupDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } listBoxItem ||
            listBoxItem.DataContext is not string groupName ||
            string.Equals(groupName, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var data = new System.Windows.DataObject();
        data.SetData(FavoriteGroupDragDataFormat, groupName);
        DragDrop.DoDragDrop(FavoriteGroupList, data, System.Windows.DragDropEffects.Move);
    }

    private void FavoriteGroupList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(LaunchItem)))
        {
            e.Effects = GetTargetFavoriteGroup(e) is null
                ? System.Windows.DragDropEffects.None
                : System.Windows.DragDropEffects.Move;
        }
        else if (e.Data.GetDataPresent(FavoriteGroupDragDataFormat))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void FavoriteGroupList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LaunchItem)) is LaunchItem item)
        {
            if (GetTargetFavoriteGroup(e) is { } groupName)
            {
                SetFavoriteGroup(item, groupName);
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetData(FavoriteGroupDragDataFormat) is string draggedGroup)
        {
            MoveFavoriteGroup(e, draggedGroup);
            e.Handled = true;
        }
    }

    private void AddFavoriteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        NewGroupNameBox.Text = string.Empty;
        NewGroupPopup.IsOpen = true;
        NewGroupNameBox.Focus();
    }

    private void CreateFavoriteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        CreateFavoriteGroupFromInput();
    }

    private void NewGroupNameBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateFavoriteGroupFromInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NewGroupPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void CreateFavoriteGroupFromInput()
    {
        var groupName = NewGroupNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(groupName))
        {
            SetStatus("请输入分组名。");
            NewGroupNameBox.Focus();
            return;
        }

        groupName = NormalizeFavoriteGroupName(groupName, null);
        EnsureFavoriteGroup(groupName);
        if (FavoritesList.SelectedItem is LaunchItem item)
        {
            item.GroupName = groupName;
        }

        _activeFavoriteGroup = groupName;
        NewGroupPopup.IsOpen = false;
        SaveSettings();
        UpdateFavoriteGroups();
        FavoriteGroupList.SelectedItem = groupName;
        SetStatus(FavoritesList.SelectedItem is LaunchItem selected
            ? $"已创建「{groupName}」，并将 {selected.Name} 移入该分组。"
            : $"已创建「{groupName}」分组。");
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void FocusSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void EverythingButton_Click(object sender, RoutedEventArgs e)
    {
        OpenEverythingSearch(SearchBox.Text);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void GlassRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowClip();
    }

    private void GlassToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        _settings.UseGlass = GlassToggle.IsChecked == true;
        ApplyVisualSettings();
        SaveSettings();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var enabled = StartupToggle.IsChecked == true;
        if (TryApplyStartupSetting(enabled, out var message))
        {
            _settings.StartWithWindows = enabled;
            SaveSettings();
            SetStatus(enabled ? "已开启开机自启。" : "已关闭开机自启。");
            return;
        }

        _isApplyingSettings = true;
        StartupToggle.IsChecked = _settings.StartWithWindows;
        _isApplyingSettings = false;
        SetStatus(message);
    }

    private void AllAppsSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var compactAllApps = CompactAllAppsToggle.IsChecked == true;
        var showStartMenu = ShowStartMenuToggle.IsChecked == true;
        var showRegistry = ShowRegistryToggle.IsChecked == true;
        var showAppsFolder = ShowAppsFolderToggle.IsChecked == true;
        if (compactAllApps && !showStartMenu && !showRegistry && !showAppsFolder)
        {
            _isApplyingSettings = true;
            if (sender is System.Windows.Controls.CheckBox changedToggle)
            {
                changedToggle.IsChecked = true;
            }

            _isApplyingSettings = false;
            SetStatus("至少保留一个全部应用来源。");
            return;
        }

        _settings.CompactAllApps = compactAllApps;
        _settings.HideShortcutItems = HideShortcutItemsToggle.IsChecked == true;
        _settings.ShowStartMenuItems = showStartMenu;
        _settings.ShowRegistryItems = showRegistry;
        _settings.ShowAppsFolderItems = showAppsFolder;

        UpdateCompactOptionsVisibility();
        RebuildCompactAllItemFilter();
        _allItemsView.Refresh();
        SaveSettings();
        SetStatus(BuildAllAppsDisplayStatus());
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingSettings || !IsLoaded)
        {
            return;
        }

        _settings.GlassOpacity = OpacitySlider.Value;
        ApplyVisualSettings();
        SaveSettings();
    }

    private void ThemeColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || (sender as FrameworkElement)?.Tag is not string colorValue)
        {
            return;
        }

        var accentColor = ParseAccentColor(colorValue);
        _settings.AccentColor = ColorToHex(accentColor);
        ApplyVisualSettings();
        SaveSettings();
    }

    private void SaveHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        HotKeyCaptureBox.Focus();
        HotKeyCaptureBox.SelectAll();
        SetStatus("请直接按下新的快捷键组合。");
    }

    private void HotKeyCaptureBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotKeyCaptureBox.SelectAll();
    }

    private void HotKeyCaptureBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = NormalizeHotKeyInput(e);
        if (IsCtrlKey(key))
        {
            var now = Environment.TickCount64;
            if (_lastCaptureCtrlTick > 0 &&
                now - _lastCaptureCtrlTick <= DoubleCtrlIntervalMilliseconds)
            {
                _lastCaptureCtrlTick = 0;
                SaveCapturedHotKey(DoubleCtrlHotKey);
                return;
            }

            _lastCaptureCtrlTick = now;
            HotKeyCaptureBox.Text = "再按一次 Ctrl...";
            SetStatus("连续按两次 Ctrl 可设置 Ctrl+Ctrl。");
            return;
        }

        if (IsModifierKey(key))
        {
            _lastCaptureCtrlTick = 0;
            HotKeyCaptureBox.Text = "按下组合键...";
            return;
        }

        var modifiers = GetKeyboardHotKeyModifiers();
        if (modifiers == 0)
        {
            SetStatus("快捷键至少需要 Ctrl、Alt 或 Shift。");
            HotKeyCaptureBox.Text = _settings.HotKey;
            return;
        }

        var hotKey = BuildHotKeyString(modifiers, key);
        _lastCaptureCtrlTick = 0;
        SaveCapturedHotKey(hotKey);
    }

    private void SaveCapturedHotKey(string hotKey)
    {
        _settings.HotKey = hotKey;
        ApplyHotKeyToControls(hotKey);
        SaveSettings();
        RegisterConfiguredHotKey();
        UpdateHotKeySummary();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<System.Windows.Controls.TextBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<System.Windows.Controls.ComboBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<System.Windows.Controls.ListBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<Slider>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize_Click(sender, e);
            return;
        }

        DragMove();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _allItemsView.Refresh();
    }

    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Down && AllItemsList.Items.Count > 0)
        {
            AllItemsList.SelectedIndex = Math.Max(0, AllItemsList.SelectedIndex);
            AllItemsList.Focus();
            e.Handled = true;
        }
    }

    private void List_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LaunchSelectedFromFocusedList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && FavoritesList.IsKeyboardFocusWithin)
        {
            RemoveFavoriteButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AllItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AllItemsList.SelectedItem is LaunchItem item)
        {
            AddFavorite(item);
        }
    }

    private void FavoritesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesList.SelectedItem is LaunchItem item)
        {
            OpenItem(item);
        }
    }

    private void FavoritesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _favoriteDragStartPoint = e.GetPosition(null);
        _favoriteDragPointerOffset = new System.Windows.Point(46, 48);
        _draggedFavoriteContainer = null;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } listBoxItem)
        {
            listBoxItem.IsSelected = true;
            _favoriteDragPointerOffset = e.GetPosition(listBoxItem);
        }
    }

    private void FavoritesList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _favoriteDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _favoriteDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } listBoxItem ||
            listBoxItem.DataContext is not LaunchItem item)
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Hand;
            _favoriteOrderChangedDuringDrag = false;
            HideOriginalFavoriteDuringDrag(listBoxItem);
            ShowFavoriteDragPreview(item);
            DragDrop.DoDragDrop(FavoritesList, item, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            if (_favoriteOrderChangedDuringDrag)
            {
                SaveSettings();
                SetStatus("已更新堆栈顺序。");
            }

            HideFavoriteDragPreview();
            RestoreOriginalFavoriteAfterDrag();
            Mouse.OverrideCursor = null;
            _favoriteOrderChangedDuringDrag = false;
        }
    }

    private void FavoritesList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(LaunchItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            if (e.Data.GetData(typeof(LaunchItem)) is LaunchItem dragged)
            {
                MoveFavoriteDuringDrag(e, dragged);
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void FavoritesList_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LaunchItem)))
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void FavoritesList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LaunchItem)) is LaunchItem dragged)
        {
            MoveDroppedFavorite(e, dragged);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] droppedPaths)
        {
            AddDroppedPaths(droppedPaths);
            e.Handled = true;
        }
    }

    private void FavoritesList_GiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        if (_favoriteDragPreviewWindow?.IsVisible == true)
        {
            UpdateFavoriteDragPreviewPosition();
            e.UseDefaultCursors = false;
            Mouse.SetCursor(System.Windows.Input.Cursors.Hand);
            e.Handled = true;
        }
    }

    private void MoveDroppedFavorite(System.Windows.DragEventArgs e, LaunchItem dragged)
    {
        if (dragged is null)
        {
            return;
        }

        if (_favoriteOrderChangedDuringDrag)
        {
            FavoritesList.SelectedItem = dragged;
            return;
        }

        var oldIndex = _favorites.IndexOf(dragged);
        if (oldIndex < 0)
        {
            return;
        }

        var newIndex = GetFavoriteDropIndex(e, dragged);
        if (oldIndex == newIndex)
        {
            return;
        }

        _favorites.Move(oldIndex, newIndex);
        FavoritesList.SelectedItem = dragged;
        SaveSettings();
        SetStatus("已更新堆栈顺序。");
    }

    private void MoveFavoriteDuringDrag(System.Windows.DragEventArgs e, LaunchItem dragged)
    {
        var oldIndex = _favorites.IndexOf(dragged);
        if (oldIndex < 0)
        {
            return;
        }

        var newIndex = GetFavoriteDropIndexFromPointer(e, dragged);
        if (newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        _favorites.Move(oldIndex, newIndex);
        FavoritesList.SelectedItem = dragged;
        KeepDraggedFavoriteHidden(dragged);
        _favoriteOrderChangedDuringDrag = true;
    }

    private void AddDroppedPaths(IEnumerable<string> paths)
    {
        var addedCount = 0;
        var skippedCount = 0;
        LaunchItem? lastAdded = null;

        foreach (var path in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = CreateDroppedItem(path);
            if (item is null)
            {
                skippedCount++;
                continue;
            }

            item.IconPath = _iconCache.TryGetIconPath(item);
            AddOrReplaceAllItem(item);

            if (FindDuplicateFavorite(item) is not null)
            {
                skippedCount++;
                continue;
            }

            var favorite = item.Clone();
            favorite.GroupName = GetNewFavoriteGroupName(item);
            _favorites.Add(favorite);
            lastAdded = favorite;
            addedCount++;
        }

        if (lastAdded is not null)
        {
            FavoritesList.SelectedItem = lastAdded;
        }

        if (addedCount > 0)
        {
            SaveSettings();
            UpdateFavoriteGroups();
        }

        SetStatus(addedCount > 0
            ? skippedCount > 0
                ? $"已拖入 {addedCount} 个项目，跳过 {skippedCount} 个重复或不可用项目。"
                : $"已拖入 {addedCount} 个项目。"
            : "拖入的项目已在堆栈中或不可用。");
    }

    private LaunchItem? CreateDroppedItem(string path)
    {
        if (Directory.Exists(path))
        {
            var name = new DirectoryInfo(path).Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return LaunchItem.Create(name, path, LaunchItemKind.Folder, SourceDragDrop);
        }

        return File.Exists(path) ? CreateManualFileItem(path, SourceDragDrop) : null;
    }

    private void HideOriginalFavoriteDuringDrag(ListBoxItem listBoxItem)
    {
        RestoreOriginalFavoriteAfterDrag();
        _draggedFavoriteContainer = listBoxItem;
        _draggedFavoriteContainer.Opacity = 0;
    }

    private void RestoreOriginalFavoriteAfterDrag()
    {
        if (_draggedFavoriteContainer is null)
        {
            return;
        }

        _draggedFavoriteContainer.Opacity = 1;
        _draggedFavoriteContainer = null;
    }

    private void KeepDraggedFavoriteHidden(LaunchItem dragged)
    {
        if (_draggedFavoriteContainer?.DataContext is LaunchItem current &&
            current.Id == dragged.Id)
        {
            _draggedFavoriteContainer.Opacity = 0;
            return;
        }

        if (_draggedFavoriteContainer is not null)
        {
            _draggedFavoriteContainer.Opacity = 1;
            _draggedFavoriteContainer = null;
        }

        if (FavoritesList.ItemContainerGenerator.ContainerFromItem(dragged) is ListBoxItem container)
        {
            _draggedFavoriteContainer = container;
            _draggedFavoriteContainer.Opacity = 0;
        }
    }

    private void ShowFavoriteDragPreview(LaunchItem item)
    {
        HideFavoriteDragPreview();
        _favoriteDragPreviewWindow = new FavoriteDragPreviewWindow(CreateFavoriteDragPreview(item))
        {
            Owner = this
        };
        MoveFavoriteDragPreviewToCursor();
        _favoriteDragPreviewWindow.Show();
        _favoriteDragPreviewWindow.PlayShowAnimation();
    }

    private FrameworkElement CreateFavoriteDragPreview(LaunchItem item)
    {
        var root = new Border
        {
            Width = 92,
            Height = 96,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(246, 231, 240, 255)),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Effect = Resources["SoftShadow"] as System.Windows.Media.Effects.Effect
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var iconFrame = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(236, 244, 255)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (TryCreateBitmapImage(item.IconPath) is { } iconSource)
        {
            iconFrame.Child = new System.Windows.Controls.Image
            {
                Source = iconSource,
                Width = 44,
                Height = 44,
                Stretch = Stretch.Uniform
            };
        }

        var title = new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 64, 84)),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        Grid.SetRow(title, 1);
        grid.Children.Add(iconFrame);
        grid.Children.Add(title);
        root.Child = grid;
        return root;
    }

    private void UpdateFavoriteDragPreviewPosition()
    {
        if (_favoriteDragPreviewWindow?.IsVisible != true)
        {
            return;
        }

        MoveFavoriteDragPreviewToCursor();
    }

    private void MoveFavoriteDragPreviewToCursor()
    {
        if (_favoriteDragPreviewWindow is null || !GetCursorPos(out var point))
        {
            return;
        }

        var screenPoint = new System.Windows.Point(point.X, point.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            screenPoint = target.TransformFromDevice.Transform(screenPoint);
        }

        _favoriteDragPreviewWindow.MoveWithCursorOffset(
            screenPoint.X,
            screenPoint.Y,
            _favoriteDragPointerOffset.X,
            _favoriteDragPointerOffset.Y);
    }

    private void HideFavoriteDragPreview()
    {
        if (_favoriteDragPreviewWindow is null)
        {
            return;
        }

        _favoriteDragPreviewWindow.Close();
        _favoriteDragPreviewWindow = null;
    }

    private static BitmapImage? TryCreateBitmapImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void FavoriteTile_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LaunchItem item)
        {
            FavoritesList.SelectedItem = item;
            FavoritesList.Focus();
        }
    }

    private void OpenFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemLaunchItem(sender) is LaunchItem item)
        {
            OpenItem(item);
        }
    }

    private void RunFavoriteAsAdminMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemLaunchItem(sender) is LaunchItem item)
        {
            OpenItemAsAdministrator(item);
        }
    }

    private void OpenFavoriteLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemLaunchItem(sender) is LaunchItem item)
        {
            OpenItemLocation(item);
        }
    }

    private void CopyFavoritePathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemLaunchItem(sender) is not LaunchItem item)
        {
            return;
        }

        System.Windows.Clipboard.SetText(ToShellPath(item.Path));
        SetStatus($"已复制路径：{item.Name}");
    }

    private void RemoveFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemLaunchItem(sender) is LaunchItem item)
        {
            RemoveFavorite(item);
        }
    }

    private async Task RefreshItemsAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            SetStatus("正在扫描应用...");

            var items = await _scanner.ScanAsync();
            await Task.Run(() => _iconCache.PopulateIcons(items));

            _allItems.Clear();
            _searchIndexes.Clear();
            foreach (var item in items)
            {
                _allItems.Add(item);
            }

            RebuildCompactAllItemFilter();
            _allItemsView.Refresh();
            SetStatus(BuildAllAppsScanStatus());
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描应用失败。", ex);
            SetStatus("扫描失败：" + ex.Message);
        }
        finally
        {
            _isBusy = false;
            UpdateFavoriteCount();
        }
    }

    private void LoadSettings()
    {
        _settings = _settingsStore.Load();
        _settings.FavoriteGroups = NormalizeFavoriteGroups(_settings.FavoriteGroups).ToList();
        if (!_settings.ShowStartMenuItems && !_settings.ShowRegistryItems && !_settings.ShowAppsFolderItems)
        {
            _settings.ShowStartMenuItems = true;
            _settings.ShowRegistryItems = true;
            _settings.ShowAppsFolderItems = true;
        }

        _favorites.Clear();

        foreach (var item in _settings.Favorites)
        {
            item.GroupName = NormalizeFavoriteGroupName(item.GroupName, item);
            item.IconPath = _iconCache.TryGetIconPath(item);
            _favorites.Add(item);
        }

        DeduplicateFavorites();

        _isApplyingSettings = true;
        GlassToggle.IsChecked = _settings.UseGlass;
        OpacitySlider.Value = Math.Clamp(_settings.GlassOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        CompactAllAppsToggle.IsChecked = _settings.CompactAllApps;
        HideShortcutItemsToggle.IsChecked = _settings.HideShortcutItems;
        ShowStartMenuToggle.IsChecked = _settings.ShowStartMenuItems;
        ShowRegistryToggle.IsChecked = _settings.ShowRegistryItems;
        ShowAppsFolderToggle.IsChecked = _settings.ShowAppsFolderItems;
        UpdateCompactOptionsVisibility();
        var startupRegistryValue = GetStartupRegistryValue();
        var startupEnabledForCurrentExecutable = IsStartupCommandForCurrentExecutable(startupRegistryValue);
        if (_settings.StartWithWindows && !startupEnabledForCurrentExecutable)
        {
            if (TryApplyStartupSetting(true, out var startupMessage))
            {
                startupEnabledForCurrentExecutable = true;
            }
            else
            {
                _settings.StartWithWindows = false;
                AppLog.Warn(startupMessage);
            }
        }
        else if (!_settings.StartWithWindows &&
                 !startupEnabledForCurrentExecutable &&
                 !string.IsNullOrWhiteSpace(startupRegistryValue))
        {
            _ = TryApplyStartupSetting(false, out _);
        }

        _settings.StartWithWindows = startupEnabledForCurrentExecutable || _settings.StartWithWindows;
        StartupToggle.IsChecked = _settings.StartWithWindows;
        ApplyHotKeyToControls(_settings.HotKey);
        _isApplyingSettings = false;

        ApplyVisualSettings();
        RegisterConfiguredHotKey();
        UpdateHotKeySummary();
        UpdateFavoriteGroups();
        SaveSettings();
        SetStatus($"配置文件：{_settingsStore.SettingsPath}");
    }

    private void SaveSettings()
    {
        _settings.Favorites = _favorites.Select(item => item.Clone()).ToList();
        _settingsStore.Save(_settings);
    }

    private bool FilterFavorites(object value)
    {
        if (value is not LaunchItem item)
        {
            return false;
        }

        return _activeFavoriteGroup == AllFavoriteGroupsLabel ||
               string.Equals(
                   NormalizeFavoriteGroupName(item.GroupName, item),
                   _activeFavoriteGroup,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterAllItems(object value)
    {
        if (value is not LaunchItem item)
        {
            return false;
        }

        if (!ShouldShowAllItemSource(item) ||
            (_settings.CompactAllApps && _compactHiddenAllItemIds.Contains(item.Id)))
        {
            return false;
        }

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return true;
        }

        var index = GetSearchIndex(item);
        return index.Fields.Any(field =>
            field.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            IsSubsequenceMatch(field, normalizedQuery));
    }

    private bool ShouldShowAllItemSource(LaunchItem item)
    {
        if (!_settings.CompactAllApps)
        {
            return true;
        }

        if (IsManualSource(item))
        {
            return true;
        }

        if (IsStartMenuSource(item))
        {
            return _settings.ShowStartMenuItems;
        }

        if (IsRegistrySource(item))
        {
            return _settings.ShowRegistryItems;
        }

        if (IsAppsFolderSource(item))
        {
            return _settings.ShowAppsFolderItems;
        }

        return true;
    }

    private void RebuildCompactAllItemFilter()
    {
        _compactHiddenAllItemIds.Clear();
        if (!_settings.CompactAllApps)
        {
            return;
        }

        foreach (var item in _allItems.Where(IsLowValueAllItem))
        {
            _compactHiddenAllItemIds.Add(item.Id);
        }

        if (_settings.HideShortcutItems)
        {
            foreach (var item in _allItems.Where(IsShortcutFilterCandidate))
            {
                _compactHiddenAllItemIds.Add(item.Id);
            }
        }

        var duplicateGroups = _allItems
            .Where(item => !_compactHiddenAllItemIds.Contains(item.Id))
            .Where(IsDuplicateCandidate)
            .GroupBy(item => NormalizeDuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var itemToKeep = group
                .OrderBy(GetAllItemDisplayPriority)
                .ThenBy(item => item.Path.Length)
                .ThenBy(item => item.Source, StringComparer.CurrentCultureIgnoreCase)
                .First();

            foreach (var item in group)
            {
                if (item.Id != itemToKeep.Id)
                {
                    _compactHiddenAllItemIds.Add(item.Id);
                }
            }
        }
    }

    private string BuildAllAppsScanStatus()
    {
        var visibleCount = GetVisibleAllItemCount();
        if (_settings.CompactAllApps && _compactHiddenAllItemIds.Count > 0)
        {
            return $"已扫描 {_allItems.Count} 个可启动项，展示 {visibleCount} 个，已精简 {_compactHiddenAllItemIds.Count} 个。";
        }

        return visibleCount == _allItems.Count
            ? $"已扫描 {_allItems.Count} 个可启动项。"
            : $"已扫描 {_allItems.Count} 个可启动项，当前展示 {visibleCount} 个。";
    }

    private string BuildAllAppsDisplayStatus()
    {
        var visibleCount = GetVisibleAllItemCount();
        if (_settings.CompactAllApps && _compactHiddenAllItemIds.Count > 0)
        {
            return $"全部应用展示 {visibleCount} 个，已精简 {_compactHiddenAllItemIds.Count} 个重复或低价值入口。";
        }

        return $"全部应用展示 {visibleCount} 个。";
    }

    private int GetVisibleAllItemCount()
    {
        return _allItemsView.Cast<object>().Count();
    }

    private static bool IsLowValueAllItem(LaunchItem item)
    {
        if (IsAppsFolderSource(item))
        {
            if (item.Path.StartsWith("Microsoft.AutoGenerated.", StringComparison.OrdinalIgnoreCase) ||
                IsNonFileUri(item.Path))
            {
                return true;
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(ToShellPath(item.Path));
        return ContainsNoisyAllAppFragment(item.Name) ||
               ContainsNoisyAllAppFragment(fileName);
    }

    private static bool ContainsNoisyAllAppFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeSearchText(value);
        return NoisyAllAppFragments.Any(fragment =>
            normalized.Contains(NormalizeSearchText(fragment), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDuplicateCandidate(LaunchItem item)
    {
        return !IsManualSource(item) &&
               item.Kind is LaunchItemKind.Shortcut or LaunchItemKind.Executable or LaunchItemKind.AppsFolder;
    }

    private static bool IsShortcutFilterCandidate(LaunchItem item)
    {
        return !IsManualSource(item) && item.Kind == LaunchItemKind.Shortcut;
    }

    private static int GetAllItemDisplayPriority(LaunchItem item)
    {
        if (IsManualSource(item))
        {
            return 0;
        }

        if (IsStartMenuSource(item))
        {
            return 1;
        }

        if (item.Kind == LaunchItemKind.Shortcut)
        {
            return 2;
        }

        if (IsRegistrySource(item))
        {
            return 3;
        }

        if (IsAppsFolderSource(item))
        {
            return 4;
        }

        return 5;
    }

    private static string NormalizeDuplicateKey(string value)
    {
        return NormalizeSearchText(value);
    }

    private static bool IsManualSource(LaunchItem item)
    {
        return string.Equals(item.Source, SourceManual, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(item.Source, SourceDragDrop, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartMenuSource(LaunchItem item)
    {
        return string.Equals(item.Source, SourceStartMenu, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistrySource(LaunchItem item)
    {
        return string.Equals(item.Source, SourceRegistry, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppsFolderSource(LaunchItem item)
    {
        return item.Kind == LaunchItemKind.AppsFolder ||
               string.Equals(item.Source, SourceAppsFolder, StringComparison.OrdinalIgnoreCase);
    }

    private void AddExecutableOrShortcut()
    {
        AddFile(
            "*.exe;*.lnk",
            "选择应用",
            "应用和快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*");
    }

    private void AddFile(string defaultExtension, string title, string filter)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
            DefaultExt = defaultExtension
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var item = CreateManualFileItem(dialog.FileName);
        AddManualItem(item);
    }

    private void OpenEverythingSearch(string? query)
    {
        var everythingPath = FindEverythingExecutable();
        if (everythingPath is null)
        {
            SetStatus("未找到 Everything。请安装 voidtools Everything，或把 Everything.exe 放入 PATH。");
            return;
        }

        try
        {
            var trimmedQuery = query?.Trim();
            var startInfo = new ProcessStartInfo
            {
                FileName = everythingPath,
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(trimmedQuery))
            {
                startInfo.ArgumentList.Add("-s");
                startInfo.ArgumentList.Add(trimmedQuery);
            }

            Process.Start(startInfo);
            SetStatus(string.IsNullOrWhiteSpace(trimmedQuery)
                ? "已打开 Everything。"
                : $"已用 Everything 搜索：{trimmedQuery}");
        }
        catch (Exception ex)
        {
            SetStatus("打开 Everything 失败：" + ex.Message);
        }
    }

    private string? FindEverythingExecutable()
    {
        var candidateFromScannedApps = _allItems
            .Where(item => item.Name.Contains("Everything", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path.Replace('/', '\\'))
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "Everything.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path));

        if (!string.IsNullOrWhiteSpace(candidateFromScannedApps))
        {
            return candidateFromScannedApps;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return SearchExecutableOnPath("Everything.exe");
    }

    private static string? SearchExecutableOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // PATH 中可能存在无效路径，跳过即可。
            }
        }

        return null;
    }

    private void AddManualItem(LaunchItem item)
    {
        item.IconPath = _iconCache.TryGetIconPath(item);
        AddOrReplaceAllItem(item);
        AddFavorite(item);
    }

    private void AddFavorite(LaunchItem item)
    {
        if (FindDuplicateFavorite(item) is { } duplicate)
        {
            SetStatus($"已在堆栈中：{duplicate.Name}");
            return;
        }

        var favorite = item.Clone();
        favorite.GroupName = GetNewFavoriteGroupName(item);
        _favorites.Add(favorite);
        FavoritesList.SelectedItem = favorite;
        SaveSettings();
        UpdateFavoriteGroups();
        SetStatus($"已加入「{favorite.GroupName}」：{item.Name}");
    }

    private void AddOrReplaceAllItem(LaunchItem item)
    {
        var existing = _allItems.FirstOrDefault(current => current.Id == item.Id);
        if (existing is not null)
        {
            var index = _allItems.IndexOf(existing);
            _allItems[index] = item;
            _searchIndexes.Remove(item.Id);
        }
        else
        {
            _allItems.Insert(0, item);
            _searchIndexes.Remove(item.Id);
        }

        RebuildCompactAllItemFilter();
        _allItemsView.Refresh();
    }

    private LaunchItem CreateManualFileItem(string path, string source = SourceManual)
    {
        var extension = Path.GetExtension(path);
        var kind = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
            ? LaunchItemKind.Shortcut
            : string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                ? LaunchItemKind.Executable
                : LaunchItemKind.File;

        var name = GetFriendlyFileName(path);
        return LaunchItem.Create(name, path, kind, source);
    }

    private static string GetFriendlyFileName(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                {
                    return versionInfo.FileDescription;
                }
            }
            catch
            {
                // 文件描述只是显示名来源，失败时使用文件名。
            }
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private void MoveFavorite(int direction)
    {
        if (FavoritesList.SelectedItem is not LaunchItem item)
        {
            SetStatus("请选择要排序的项目。");
            return;
        }

        var visibleFavorites = GetVisibleFavorites();
        var visibleIndex = visibleFavorites.IndexOf(item);
        var targetVisibleIndex = visibleIndex + direction;
        if (visibleIndex < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visibleFavorites.Count)
        {
            return;
        }

        var oldIndex = _favorites.IndexOf(item);
        var targetIndex = _favorites.IndexOf(visibleFavorites[targetVisibleIndex]);
        if (oldIndex < 0 || targetIndex < 0 || oldIndex == targetIndex)
        {
            return;
        }

        _favorites.Move(oldIndex, targetIndex);
        FavoritesList.SelectedItem = item;
        SaveSettings();
        _favoriteItemsView.Refresh();
        SetStatus("已更新堆栈顺序。");
    }

    private void RemoveFavorite(LaunchItem item)
    {
        var index = _favorites.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        _favorites.RemoveAt(index);
        SaveSettings();
        UpdateFavoriteGroups();

        var visibleFavorites = GetVisibleFavorites();
        if (visibleFavorites.Count > 0)
        {
            FavoritesList.SelectedItem = visibleFavorites[Math.Min(index, visibleFavorites.Count - 1)];
        }

        SetStatus($"已移除：{item.Name}");
    }

    private void SetFavoriteGroup(LaunchItem item, string groupName)
    {
        var normalizedGroup = NormalizeFavoriteGroupName(groupName, item);
        if (string.Equals(normalizedGroup, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureFavoriteGroup(normalizedGroup);
        item.GroupName = normalizedGroup;
        SaveSettings();
        UpdateFavoriteGroups();
        _activeFavoriteGroup = normalizedGroup;
        FavoriteGroupList.SelectedItem = normalizedGroup;
        _favoriteItemsView.Refresh();
        FavoritesList.SelectedItem = item;
        SetStatus($"已将 {item.Name} 移到「{normalizedGroup}」。");
    }

    private string? GetTargetFavoriteGroup(System.Windows.DragEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not string groupName ||
            string.Equals(groupName, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return groupName;
    }

    private void MoveFavoriteGroup(System.Windows.DragEventArgs e, string draggedGroup)
    {
        if (string.IsNullOrWhiteSpace(draggedGroup) ||
            string.Equals(draggedGroup, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var groups = _favoriteGroups
            .Where(groupName => !string.Equals(groupName, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var oldIndex = groups.FindIndex(groupName =>
            string.Equals(groupName, draggedGroup, StringComparison.OrdinalIgnoreCase));
        if (oldIndex < 0)
        {
            return;
        }

        var targetIndex = GetFavoriteGroupDropIndex(e, groups, draggedGroup, oldIndex);
        if (targetIndex < 0 || targetIndex == oldIndex)
        {
            return;
        }

        var movedGroup = groups[oldIndex];
        groups.RemoveAt(oldIndex);
        targetIndex = Math.Clamp(targetIndex, 0, groups.Count);
        groups.Insert(targetIndex, movedGroup);

        _settings.FavoriteGroups = groups;
        _activeFavoriteGroup = movedGroup;
        SaveSettings();
        UpdateFavoriteGroups();
        FavoriteGroupList.SelectedItem = movedGroup;
        SetStatus("已更新分组顺序。");
    }

    private int GetFavoriteGroupDropIndex(
        System.Windows.DragEventArgs e,
        IReadOnlyList<string> groups,
        string draggedGroup,
        int oldIndex)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } targetItem ||
            targetItem.DataContext is not string targetGroup)
        {
            return groups.Count - 1;
        }

        var targetIndex = string.Equals(targetGroup, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase)
            ? 0
            : groups.ToList().FindIndex(groupName =>
                string.Equals(groupName, targetGroup, StringComparison.OrdinalIgnoreCase));

        if (targetIndex < 0 ||
            string.Equals(targetGroup, draggedGroup, StringComparison.OrdinalIgnoreCase))
        {
            return oldIndex;
        }

        var position = e.GetPosition(targetItem);
        if (position.X > targetItem.ActualWidth / 2)
        {
            targetIndex++;
        }

        if (oldIndex < targetIndex)
        {
            targetIndex--;
        }

        return Math.Clamp(targetIndex, 0, groups.Count - 1);
    }

    private void UpdateFavoriteGroups()
    {
        var previousGroup = _activeFavoriteGroup;
        var groups = new List<string> { AllFavoriteGroupsLabel };

        foreach (var groupName in NormalizeFavoriteGroups(_settings.FavoriteGroups))
        {
            groups.Add(groupName);
        }

        foreach (var groupName in _favorites
                     .Select(item => NormalizeFavoriteGroupName(item.GroupName, item))
                     .Where(groupName => !string.IsNullOrWhiteSpace(groupName))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(groupName => groupName == DefaultFavoriteGroupName ? 0 : 1)
                     .ThenBy(groupName => groupName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!groups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(groupName);
            }
        }

        _settings.FavoriteGroups = groups
            .Where(groupName => !string.Equals(groupName, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!groups.Contains(previousGroup, StringComparer.OrdinalIgnoreCase))
        {
            previousGroup = AllFavoriteGroupsLabel;
        }

        _isUpdatingFavoriteGroups = true;
        _favoriteGroups.Clear();
        foreach (var groupName in groups)
        {
            _favoriteGroups.Add(groupName);
        }

        _activeFavoriteGroup = previousGroup;
        FavoriteGroupList.SelectedItem = _activeFavoriteGroup;
        _isUpdatingFavoriteGroups = false;

        _favoriteItemsView.Refresh();
        UpdateFavoriteCount();
    }

    private void EnsureFavoriteGroup(string groupName)
    {
        var normalizedGroup = NormalizeFavoriteGroupName(groupName, null);
        if (string.IsNullOrWhiteSpace(normalizedGroup) ||
            _settings.FavoriteGroups.Contains(normalizedGroup, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.FavoriteGroups.Add(normalizedGroup);
    }

    private static IEnumerable<string> NormalizeFavoriteGroups(IEnumerable<string>? groupNames)
    {
        if (groupNames is null)
        {
            return [];
        }

        return groupNames
            .Select(groupName => groupName.Trim())
            .Where(groupName => !string.IsNullOrWhiteSpace(groupName) &&
                                !string.Equals(groupName, AllFavoriteGroupsLabel, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private List<LaunchItem> GetVisibleFavorites()
    {
        return _favoriteItemsView.Cast<LaunchItem>().ToList();
    }

    private string GetNewFavoriteGroupName(LaunchItem item)
    {
        return _activeFavoriteGroup == AllFavoriteGroupsLabel
            ? NormalizeFavoriteGroupName(item.GroupName, item)
            : _activeFavoriteGroup;
    }

    private static string NormalizeFavoriteGroupName(string? groupName, LaunchItem? item)
    {
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            return groupName.Trim();
        }

        return item?.Kind is LaunchItemKind.File or LaunchItemKind.Folder
            ? "文件"
            : DefaultFavoriteGroupName;
    }

    private LaunchItem? FindDuplicateFavorite(LaunchItem item)
    {
        var duplicateKey = GetFavoriteDuplicateKey(item);
        return _favorites.FirstOrDefault(existing =>
            string.Equals(GetFavoriteDuplicateKey(existing), duplicateKey, StringComparison.OrdinalIgnoreCase));
    }

    private void DeduplicateFavorites()
    {
        var deduplicated = _favorites
            .GroupBy(GetFavoriteDuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var kept = items
                    .OrderBy(GetFavoriteKeepPriority)
                    .ThenBy(item => item.Path.Length)
                    .First();
                kept.GroupName = items
                    .Select(item => NormalizeFavoriteGroupName(item.GroupName, item))
                    .FirstOrDefault(groupName => !string.IsNullOrWhiteSpace(groupName)) ?? NormalizeFavoriteGroupName(kept.GroupName, kept);
                return kept;
            })
            .ToList();

        if (deduplicated.Count == _favorites.Count)
        {
            return;
        }

        _favorites.Clear();
        foreach (var item in deduplicated)
        {
            _favorites.Add(item);
        }
    }

    private static string GetFavoriteDuplicateKey(LaunchItem item)
    {
        if (item.Kind is LaunchItemKind.Shortcut or LaunchItemKind.Executable or LaunchItemKind.AppsFolder)
        {
            return "app:" + NormalizeDuplicateKey(item.Name);
        }

        return item.Kind + ":" + NormalizePathKey(item.Path);
    }

    private static int GetFavoriteKeepPriority(LaunchItem item)
    {
        return item.Kind switch
        {
            LaunchItemKind.Executable => 0,
            LaunchItemKind.AppsFolder => 1,
            LaunchItemKind.Shortcut => 2,
            LaunchItemKind.Folder => 3,
            LaunchItemKind.File => 4,
            _ => 5
        };
    }

    private static string NormalizePathKey(string value)
    {
        return Environment.ExpandEnvironmentVariables(value.Trim())
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();
    }

    private void LaunchSelectedFromFocusedList()
    {
        if (FavoritesList.IsKeyboardFocusWithin && FavoritesList.SelectedItem is LaunchItem favorite)
        {
            OpenItem(favorite);
            return;
        }

        if (AllItemsList.SelectedItem is LaunchItem item)
        {
            AddFavorite(item);
        }
    }

    private void OpenItem(LaunchItem item)
    {
        try
        {
            _launcher.Launch(item);
            SetStatus($"已打开：{item.Name}");
            Hide();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开失败：{item.Name}", ex);
            SetStatus($"打开失败：{item.Name}，{ex.Message}");
        }
    }

    private void OpenItemAsAdministrator(LaunchItem item)
    {
        if (item.Kind is LaunchItemKind.Folder or LaunchItemKind.AppsFolder)
        {
            SetStatus($"该项目不支持管理员启动：{item.Name}");
            return;
        }

        if (IsNonFileUri(item.Path))
        {
            SetStatus($"该项目不支持管理员启动：{item.Name}");
            return;
        }

        var path = ToShellPath(item.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("没有可启动路径。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = ResolveWorkingDirectory(path)
            });
            SetStatus($"已请求管理员启动：{item.Name}");
            Hide();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("已取消管理员启动。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"管理员启动失败：{item.Name}", ex);
            SetStatus($"管理员启动失败：{item.Name}，{ex.Message}");
        }
    }

    private void OpenItemLocation(LaunchItem item)
    {
        if (item.Kind == LaunchItemKind.AppsFolder)
        {
            SetStatus($"该项目没有可打开的本地位置：{item.Name}");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Path) || IsNonFileUri(item.Path))
        {
            SetStatus($"该项目没有可打开的本地位置：{item.Name}");
            return;
        }

        var path = ToShellPath(item.Path);
        try
        {
            if (Directory.Exists(path))
            {
                OpenExplorer(path);
                SetStatus($"已打开位置：{item.Name}");
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                SetStatus($"已定位：{item.Name}");
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                OpenExplorer(directory);
                SetStatus($"已打开位置：{item.Name}");
                return;
            }

            SetStatus($"未找到所在位置：{item.Name}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开位置失败：{item.Name}", ex);
            SetStatus($"打开位置失败：{item.Name}，{ex.Message}");
        }
    }

    private static LaunchItem? GetMenuItemLaunchItem(object sender)
    {
        return (sender as MenuItem)?.CommandParameter as LaunchItem;
    }

    private static bool IsNonFileUri(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    private static string ToShellPath(string path)
    {
        return IsNonFileUri(path) ? path : path.Replace('/', '\\');
    }

    private static void OpenExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static string? ResolveWorkingDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 QuickShelf", null, (_, _) => ShowAndActivate());
        menu.Items.Add("刷新应用", null, async (_, _) => await Dispatcher.InvokeAsync(RefreshItemsAsync));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "QuickShelf",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowAndActivate();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        return File.Exists(iconPath)
            ? new Drawing.Icon(iconPath)
            : Drawing.SystemIcons.Application;
    }

    internal void ShowAndActivate()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HideStartupWindow()
    {
        Hide();
        Opacity = 1;
        ShowInTaskbar = true;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        SaveSettings();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveSettings();
        UnregisterHotKey();
        _notifyIcon?.Dispose();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        ApplyVisualSettings();
        RegisterConfiguredHotKey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            ToggleWindowByHotKey();
            handled = true;
        }

        if (msg == WmNcHitTest)
        {
            var hitTest = HitTestResizeBorder(lParam);
            if (hitTest != 0)
            {
                handled = true;
                return new IntPtr(hitTest);
            }
        }

        return IntPtr.Zero;
    }

    private void UnregisterHotKey()
    {
        UnregisterConfiguredHotKey();

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private void ApplyVisualSettings()
    {
        var opacity = Math.Clamp(_settings.GlassOpacity, 0.55, 1.0);
        var accentColor = ParseAccentColor(_settings.AccentColor);
        _settings.AccentColor = ColorToHex(accentColor);
        ApplyAccentColor(accentColor);
        UpdateWindowClip();

        var alpha = _settings.UseGlass ? (byte)(opacity * 255) : (byte)255;
        GlassRoot.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 248, 251, 255));
        WindowBackdrop.Apply(this, _settings.UseGlass, opacity);
    }

    private static bool TryApplyStartupSetting(bool enabled, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true);
            if (key is null)
            {
                message = "开机自启设置失败：无法打开启动项注册表。";
                return false;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    message = "开机自启设置失败：无法获取当前程序路径。";
                    return false;
                }

                key.SetValue(
                    StartupRegistryValueName,
                    $"\"{executablePath}\" {StartupArgument}",
                    RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(StartupRegistryValueName, false);
            }

            message = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("开机自启设置失败。", ex);
            message = "开机自启设置失败：" + ex.Message;
            return false;
        }
    }

    private static bool IsStartupEnabled()
    {
        return IsStartupCommandForCurrentExecutable(GetStartupRegistryValue());
    }

    private static string? GetStartupRegistryValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false);
            return key?.GetValue(StartupRegistryValueName) as string;
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取开机自启设置失败。", ex);
            return null;
        }
    }

    private static bool IsStartupCommandForCurrentExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return false;
        }

        var registeredPath = ExtractExecutablePath(command);
        if (string.IsNullOrWhiteSpace(registeredPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registeredPath),
                Path.GetFullPath(Environment.ProcessPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Warn("解析开机自启路径失败。", ex);
            return false;
        }
    }

    private static string? ExtractExecutablePath(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.Length == 0)
        {
            return null;
        }

        if (expanded[0] == '"')
        {
            var endQuote = expanded.IndexOf('"', 1);
            return endQuote > 1 ? expanded[1..endQuote] : null;
        }

        var startupArgumentIndex = expanded.IndexOf(" " + StartupArgument, StringComparison.OrdinalIgnoreCase);
        if (startupArgumentIndex > 0)
        {
            return expanded[..startupArgumentIndex].Trim();
        }

        return expanded.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private SearchIndex GetSearchIndex(LaunchItem item)
    {
        if (_searchIndexes.TryGetValue(item.Id, out var index))
        {
            return index;
        }

        var fields = BuildSearchFields(item)
            .Select(NormalizeSearchText)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        index = new SearchIndex(fields);
        _searchIndexes[item.Id] = index;
        return index;
    }

    private static IEnumerable<string> BuildSearchFields(LaunchItem item)
    {
        yield return item.Name;
        yield return item.Path;
        yield return item.Source;
        yield return Path.GetFileNameWithoutExtension(item.Path);

        foreach (var value in BuildPinyinFields(item.Name))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> BuildPinyinFields(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return
            [
                WordsHelper.GetPinyin(value),
                WordsHelper.GetPinyin(value, string.Empty),
                WordsHelper.GetFirstPinyin(value),
                WordsHelper.GetPinyinForName(value)
            ];
        }
        catch
        {
            // 拼音只是搜索增强，失败时保留原始名称匹配。
            return [];
        }
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var index = 0;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) &&
                ch != '-' &&
                ch != '_' &&
                ch != '.' &&
                ch != '/' &&
                ch != '\\' &&
                ch != ':' &&
                ch != '(' &&
                ch != ')' &&
                ch != '[' &&
                ch != ']')
            {
                buffer[index++] = ch;
            }
        }

        return new string(buffer, 0, index);
    }

    private static bool IsSubsequenceMatch(string candidate, string query)
    {
        if (query.Length < 2 || candidate.Length < query.Length)
        {
            return false;
        }

        var queryIndex = 0;
        foreach (var ch in candidate)
        {
            if (ch == query[queryIndex])
            {
                queryIndex++;
                if (queryIndex == query.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void UpdateFavoriteCount()
    {
        var visibleCount = GetVisibleFavorites().Count;
        FavoriteCountText.Text = _activeFavoriteGroup == AllFavoriteGroupsLabel || visibleCount == _favorites.Count
            ? $"{_favorites.Count} 项"
            : $"{visibleCount}/{_favorites.Count} 项";
    }

    private void UpdateCompactOptionsVisibility()
    {
        CompactAllAppsOptionsPanel.Visibility = _settings.CompactAllApps
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void ToggleWindowByHotKey()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            return;
        }

        ShowAndActivate();
    }

    private void UpdateWindowClip()
    {
        if (GlassRoot.ActualWidth <= 0 || GlassRoot.ActualHeight <= 0)
        {
            return;
        }

        GlassRoot.Clip = new RectangleGeometry(
            new Rect(0, 0, GlassRoot.ActualWidth, GlassRoot.ActualHeight),
            28,
            28);
    }

    private void RegisterConfiguredHotKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        UnregisterConfiguredHotKey();

        if (IsDoubleCtrlHotKey(_settings.HotKey))
        {
            if (InstallKeyboardHook())
            {
                SetStatus("已注册快捷键：Ctrl+Ctrl");
                return;
            }

            SetStatus("Ctrl+Ctrl 注册失败。");
            AppLog.Warn("Ctrl+Ctrl 注册失败。");
            return;
        }

        if (!TryParseHotKey(_settings.HotKey, out var modifiers, out var key))
        {
            _settings.HotKey = DefaultHotKey;
            _ = TryParseHotKey(DefaultHotKey, out modifiers, out key);
            ApplyHotKeyToControls(_settings.HotKey);
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(handle, HotKeyId, modifiers, virtualKey))
        {
            _hotKeyRegistered = true;
            SetStatus($"已注册快捷键：{_settings.HotKey}");
            return;
        }

        var message = $"快捷键 {_settings.HotKey} 注册失败，可能已被占用。";
        SetStatus(message);
        AppLog.Warn(message);
    }

    private void UnregisterConfiguredHotKey()
    {
        UninstallKeyboardHook();

        if (!_hotKeyRegistered)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(handle, HotKeyId);
        }

        _hotKeyRegistered = false;
    }

    private int HitTestResizeBorder(IntPtr lParam)
    {
        if (WindowState != WindowState.Normal)
        {
            return 0;
        }

        var point = PointFromScreen(GetPointFromLParam(lParam));
        var onLeft = point.X >= 0 && point.X <= ResizeBorder;
        var onRight = point.X <= ActualWidth && point.X >= ActualWidth - ResizeBorder;
        var onTop = point.Y >= 0 && point.Y <= ResizeBorder;
        var onBottom = point.Y <= ActualHeight && point.Y >= ActualHeight - ResizeBorder;

        if (onTop && onLeft)
        {
            return HtTopLeft;
        }

        if (onTop && onRight)
        {
            return HtTopRight;
        }

        if (onBottom && onLeft)
        {
            return HtBottomLeft;
        }

        if (onBottom && onRight)
        {
            return HtBottomRight;
        }

        if (onLeft)
        {
            return HtLeft;
        }

        if (onRight)
        {
            return HtRight;
        }

        if (onTop)
        {
            return HtTop;
        }

        return onBottom ? HtBottom : 0;
    }

    private static System.Windows.Point GetPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new System.Windows.Point(x, y);
    }

    private void ApplyHotKeyToControls(string? hotKey)
    {
        if (IsDoubleCtrlHotKey(hotKey))
        {
            HotKeyCaptureBox.Text = DoubleCtrlHotKey;
            return;
        }

        if (!TryParseHotKey(hotKey, out var modifiers, out var key))
        {
            _settings.HotKey = DefaultHotKey;
            _ = TryParseHotKey(DefaultHotKey, out modifiers, out key);
        }

        HotKeyCaptureBox.Text = BuildHotKeyString(modifiers, key);
    }

    private void UpdateHotKeySummary()
    {
        HotKeySummaryText.Text = $"当前：{_settings.HotKey}，点击输入框后直接按新组合键";
    }

    private static Key NormalizeHotKeyInput(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        key = key == Key.ImeProcessed ? e.ImeProcessedKey : key;
        return key == Key.DeadCharProcessed ? e.DeadCharProcessedKey : key;
    }

    private static uint GetKeyboardHotKeyModifiers()
    {
        var modifiers = 0u;
        var keyboardModifiers = Keyboard.Modifiers;

        if ((keyboardModifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= ModControl;
        }

        if ((keyboardModifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= ModAlt;
        }

        if ((keyboardModifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= ModShift;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.None
            or Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin
            or Key.Clear;
    }

    private static bool IsCtrlKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl;
    }

    private static bool IsCtrlVirtualKey(int virtualKey)
    {
        return virtualKey is VkControl or VkLControl or VkRControl;
    }

    private static bool IsDoubleCtrlHotKey(string? value)
    {
        return string.Equals(value?.Trim(), DoubleCtrlHotKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool InstallKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return true;
        }

        _keyboardHookProc ??= LowLevelKeyboardHookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module?.ModuleName is { Length: > 0 }
            ? GetModuleHandle(module.ModuleName)
            : IntPtr.Zero;

        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, moduleHandle, 0);
        return _keyboardHookHandle != IntPtr.Zero;
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_keyboardHookHandle);
        _keyboardHookHandle = IntPtr.Zero;
        _lastCtrlKeyDownTick = 0;
        _lastDoubleCtrlTriggerTick = 0;
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown) &&
            !HotKeyCaptureBox.IsKeyboardFocusWithin)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var now = Environment.TickCount64;

            if (IsCtrlVirtualKey(info.VkCode))
            {
                if (_lastCtrlKeyDownTick > 0 &&
                    now - _lastCtrlKeyDownTick <= DoubleCtrlIntervalMilliseconds &&
                    now - _lastDoubleCtrlTriggerTick > DoubleCtrlIntervalMilliseconds)
                {
                    _lastCtrlKeyDownTick = 0;
                    _lastDoubleCtrlTriggerTick = now;
                    Dispatcher.BeginInvoke(ToggleWindowByHotKey);
                }
                else
                {
                    _lastCtrlKeyDownTick = now;
                }
            }
            else
            {
                _lastCtrlKeyDownTick = 0;
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool TryParseHotKey(string? value, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var rawToken in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
            }
            else if (key == Key.None && TryParseHotKeyKey(token, out var parsedKey))
            {
                key = parsedKey;
            }
            else
            {
                return false;
            }
        }

        return modifiers != 0 && key != Key.None;
    }

    private static bool TryParseHotKeyKey(string value, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length == 1)
        {
            var ch = normalized[0];
            if (ch >= 'A' && ch <= 'Z' && Enum.TryParse(normalized, out key))
            {
                return true;
            }

            if (ch >= 'a' && ch <= 'z' && Enum.TryParse(normalized.ToUpperInvariant(), out key))
            {
                return true;
            }

            if (ch >= '0' && ch <= '9')
            {
                key = (Key)((int)Key.D0 + (ch - '0'));
                return true;
            }
        }

        return Enum.TryParse(normalized, true, out key) && key != Key.None;
    }

    private static string BuildHotKeyString(uint modifiers, Key key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatHotKeyKey(key));
        return string.Join("+", parts);
    }

    private static string FormatHotKeyKey(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        return key.ToString();
    }

    private static List<string> BuildHotKeyKeyOptions()
    {
        var keys = new List<string>();
        for (var ch = 'A'; ch <= 'Z'; ch++)
        {
            keys.Add(ch.ToString());
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            keys.Add(digit.ToString());
        }

        for (var index = 1; index <= 12; index++)
        {
            keys.Add($"F{index}");
        }

        return keys;
    }

    private int GetFavoriteDropIndex(System.Windows.DragEventArgs e, LaunchItem dragged)
    {
        var oldIndex = _favorites.IndexOf(dragged);
        var targetItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not LaunchItem target)
        {
            return _favorites.Count - 1;
        }

        var targetIndex = _favorites.IndexOf(target);
        if (targetIndex < 0)
        {
            return oldIndex;
        }

        var position = e.GetPosition(targetItem);
        if (position.X > targetItem.ActualWidth / 2)
        {
            targetIndex++;
        }

        if (oldIndex >= 0 && oldIndex < targetIndex)
        {
            targetIndex--;
        }

        return Math.Clamp(targetIndex, 0, _favorites.Count - 1);
    }

    private int GetFavoriteDropIndexFromPointer(System.Windows.DragEventArgs e, LaunchItem dragged)
    {
        var oldIndex = _favorites.IndexOf(dragged);
        if (oldIndex < 0)
        {
            return -1;
        }

        var pointer = e.GetPosition(FavoritesList);
        var rows = BuildFavoriteDropRows(dragged);

        if (rows.Count == 0)
        {
            return oldIndex;
        }

        if (pointer.Y <= rows[0].Top)
        {
            return NormalizeFavoriteInsertionIndex(0, oldIndex);
        }

        if (pointer.Y >= rows[^1].Bottom)
        {
            return NormalizeFavoriteInsertionIndex(_favorites.Count, oldIndex);
        }

        var targetRow = rows
            .Where(row => row.ContainsY(pointer.Y))
            .OrderBy(row => Math.Abs(row.CenterY - pointer.Y))
            .FirstOrDefault() ??
            rows.OrderBy(row => row.DistanceToY(pointer.Y)).First();

        var rowItems = targetRow.Items
            .OrderBy(candidate => candidate.Rect.Left)
            .ToList();

        foreach (var candidate in rowItems)
        {
            if (pointer.X < candidate.CenterX)
            {
                return NormalizeFavoriteInsertionIndex(candidate.Index, oldIndex);
            }
        }

        return NormalizeFavoriteInsertionIndex(rowItems[^1].Index + 1, oldIndex);
    }

    private List<FavoriteDropRow> BuildFavoriteDropRows(LaunchItem dragged)
    {
        var rows = new List<FavoriteDropRow>();

        for (var index = 0; index < _favorites.Count; index++)
        {
            var item = _favorites[index];
            if (item.Id == dragged.Id)
            {
                continue;
            }

            if (FavoritesList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = container.TransformToAncestor(FavoritesList).Transform(new System.Windows.Point(0, 0));
            var rect = new Rect(topLeft, new System.Windows.Size(container.ActualWidth, container.ActualHeight));
            var candidate = new FavoriteDropCandidate(index, rect);
            var row = rows.FirstOrDefault(existing => existing.Accepts(candidate));
            if (row is null)
            {
                row = new FavoriteDropRow();
                rows.Add(row);
            }

            row.Items.Add(candidate);
        }

        return rows
            .OrderBy(row => row.Top)
            .ThenBy(row => row.Items.Min(candidate => candidate.Rect.Left))
            .ToList();
    }

    private int NormalizeFavoriteInsertionIndex(int insertionIndex, int oldIndex)
    {
        if (insertionIndex > oldIndex)
        {
            insertionIndex--;
        }

        return Math.Clamp(insertionIndex, 0, _favorites.Count - 1);
    }

    private LaunchItem? GetFavoriteFromOriginalSource(DependencyObject? source)
    {
        return FindAncestor<ListBoxItem>(source)?.DataContext as LaunchItem;
    }

    private void ApplyAccentColor(System.Windows.Media.Color color)
    {
        Resources["BlueBrush"] = new SolidColorBrush(color);
        Resources["PanelBorderBrush"] = new SolidColorBrush(BlendWithWhite(color, 0.78));
    }

    private static System.Windows.Media.Color ParseAccentColor(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(value) is System.Windows.Media.Color color)
                {
                    return color;
                }
            }
            catch
            {
                // 使用默认主题色。
            }
        }

        return System.Windows.Media.Color.FromRgb(47, 124, 246);
    }

    private static string ColorToHex(System.Windows.Media.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static System.Windows.Media.Color BlendWithWhite(System.Windows.Media.Color color, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(color.R + (255 - color.R) * ratio),
            (byte)Math.Round(color.G + (255 - color.G) * ratio),
            (byte)Math.Round(color.B + (255 - color.B) * ratio));
    }

    private sealed record SearchIndex(string[] Fields);

    private sealed record FavoriteDropCandidate(int Index, Rect Rect)
    {
        public double CenterX => Rect.Left + Rect.Width / 2;

        public double CenterY => Rect.Top + Rect.Height / 2;
    }

    private sealed class FavoriteDropRow
    {
        public List<FavoriteDropCandidate> Items { get; } = [];

        public double Top => Items.Min(candidate => candidate.Rect.Top);

        public double Bottom => Items.Max(candidate => candidate.Rect.Bottom);

        public double Height => Bottom - Top;

        public double CenterY => Top + Height / 2;

        public bool ContainsY(double y)
        {
            return y >= Top && y <= Bottom;
        }

        public double DistanceToY(double y)
        {
            if (ContainsY(y))
            {
                return 0;
            }

            return y < Top ? Top - y : y - Bottom;
        }

        public bool Accepts(FavoriteDropCandidate candidate)
        {
            if (Items.Count == 0)
            {
                return true;
            }

            var rowHeight = Math.Max(Height, candidate.Rect.Height);
            return Math.Abs(CenterY - candidate.CenterY) <= Math.Max(24, rowHeight * 0.45);
        }
    }

    private sealed class FavoriteDragPreviewWindow : Window
    {
        private readonly ScaleTransform _scale = new(0.97, 0.97);

        public FavoriteDragPreviewWindow(FrameworkElement content)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Opacity = 0;

            content.RenderTransform = _scale;
            content.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            Content = content;
            SourceInitialized += (_, _) => EnableMousePassThrough();
        }

        private void EnableMousePassThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }

        public void MoveWithCursorOffset(double screenX, double screenY, double offsetX, double offsetY)
        {
            Left = screenX - offsetX;
            Top = screenY - offsetY;
        }

        public void PlayShowAnimation()
        {
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 0.94, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });

            _scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });

            _scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
