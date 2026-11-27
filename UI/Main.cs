using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;
using Icon = System.Drawing.Icon;

namespace Diskovy
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var application = new WpfApp();
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Velora Disk", "settings.ini");
            var screen = new MainScreen(new Preferences(settingsPath));
            application.Run(screen.Window);
        }
    }

    internal enum Page { Files, Apps, Settings }

    internal sealed class MainScreen
    {
        internal Window Window { get; private set; }
        private readonly Preferences preferences;
        private readonly Locale locale;
        private readonly DataGrid filesGrid, appsGrid;
        private readonly ComboBox scope, language, threshold, limit;
        private readonly TextBox fileFilter, appFilter;
        private readonly DispatcherTimer timer;
        private readonly string[] driveRoots;
        private string customFolder;
        private Page page;
        private bool translating, closed, appsLoaded, loadingApps;
        private int runThreshold;
        private ScanResult lastScan;
        private ScanProgress progress;
        private CancellationTokenSource cancellation;
        private List<FileRow> files = new List<FileRow>();
        private List<AppRow> applications = new List<AppRow>();
        private string fileNoticeEn, fileNoticeRu, appNoticeEn, appNoticeRu, settingsNoticeEn, settingsNoticeRu;

        internal MainScreen(Preferences preferences)
        {
            this.preferences = preferences;
            locale = new Locale(preferences);
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Diskovy.Main.xaml"))
                Window = (Window)XamlReader.Load(source);
            filesGrid = Get<DataGrid>("FilesGrid");
            appsGrid = Get<DataGrid>("AppsGrid");
            scope = Get<ComboBox>("Scope");
            language = Get<ComboBox>("LanguageBox");
            threshold = Get<ComboBox>("ThresholdBox");
            limit = Get<ComboBox>("LimitBox");
            fileFilter = Get<TextBox>("FileFilter");
            appFilter = Get<TextBox>("AppFilter");
            driveRoots = DiskScanner.FixedRoots();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += delegate { UpdateScanPresentation(); };
            SetBrandIcon();
            SetColumns();
            ConnectEvents();
            ApplyLanguage();
        }

        private TControl Get<TControl>(string name) where TControl : FrameworkElement
        { return (TControl)Window.FindName(name); }
        private void Text(string name, string text) { Get<TextBlock>(name).Text = text ?? ""; }
        private void Label(string name, string text) { Get<Button>(name).Content = text; }
        private string T(string english, string russian) { return locale.T(english, russian); }

        private void SetBrandIcon()
        {
            using (Icon icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
            {
                if (icon == null) return;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Window.Icon = source;
                Get<System.Windows.Controls.Image>("BrandIcon").Source = source;
            }
        }

        private void SetColumns()
        {
            Column(filesGrid, "Name", "Name", 2.2, 95);
            Column(filesGrid, "SizeText", "Bytes", 92, 65, true);
            Column(filesGrid, "Directory", "Directory", 2.5, 105);
            Column(filesGrid, "DateText", "LastModified", 1.45, 90);
            Column(appsGrid, "Name", "Name", 2.7, 110);
            Column(appsGrid, "SizeText", "Bytes", 105, 75, true);
            Column(appsGrid, "Publisher", "Publisher", 1.8, 105);
            Column(appsGrid, "Version", "Version", 1.15, 70);
        }

        private void Column(DataGrid grid, string binding, string sort, double width, double minimum, bool fixedWidth = false)
        {
            grid.Columns.Add(new DataGridTextColumn {
                Binding = new Binding(binding) { Mode = BindingMode.OneWay }, SortMemberPath = sort,
                Width = new DataGridLength(width, fixedWidth ? DataGridLengthUnitType.Pixel : DataGridLengthUnitType.Star),
                MinWidth = minimum, ElementStyle = (Style)Window.Resources["CellText"]
            });
        }

        private void ConnectEvents()
        {
            Get<Button>("NavFiles").Click += delegate { SwitchTo(Page.Files); };
            Get<Button>("NavApps").Click += delegate { SwitchTo(Page.Apps); };
            Get<Button>("NavSettings").Click += delegate { SwitchTo(Page.Settings); };
            Get<Button>("ScanButton").Click += delegate { ScanOrCancel(); };
            Get<Button>("ChooseFolder").Click += delegate { PickFolder(); };
            Get<Button>("LocateButton").Click += delegate { LocateFile(); };
            Get<Button>("RecycleButton").Click += delegate { RecycleFile(); };
            Get<Button>("RefreshApps").Click += delegate { LoadApps(); };
            Get<Button>("WindowsAppsButton").Click += delegate { OpenWindowsApps(); };
            Get<Button>("UninstallButton").Click += delegate { Uninstall(); };
            filesGrid.SelectionChanged += delegate { RefreshFileActions(); };
            appsGrid.SelectionChanged += delegate { RefreshAppActions(); };
            fileFilter.TextChanged += delegate { Filter(filesGrid); };
            appFilter.TextChanged += delegate { Filter(appsGrid); };
            language.SelectionChanged += delegate {
                if (translating || language.SelectedIndex < 0) return;
                preferences.Language = language.SelectedIndex == 1 ? "ru" : "en";
                ApplyLanguage(); SavePreferences();
            };
            threshold.SelectionChanged += delegate {
                if (translating || threshold.SelectedIndex < 0) return;
                preferences.MinMiB = Preferences.Thresholds[threshold.SelectedIndex];
                UpdateScanPresentation(); SavePreferences();
            };
            limit.SelectionChanged += delegate {
                if (translating || limit.SelectedIndex < 0) return;
                preferences.Limit = Preferences.Limits[limit.SelectedIndex]; SavePreferences();
            };
            Window.Closed += delegate {
                closed = true;
                timer.Stop();
                if (cancellation != null) cancellation.Cancel();
            };
        }

        private void SavePreferences()
        {
            try
            {
                preferences.Save();
                settingsNoticeEn = "Settings saved."; settingsNoticeRu = "Настройки сохранены.";
            }
            catch (Exception error)
            {
                settingsNoticeEn = "Could not save settings. " + error.Message;
                settingsNoticeRu = "Не удалось сохранить настройки. " + error.Message;
            }
            UpdateStatus();
        }

        private void ApplyLanguage()
        {
            translating = true;
            try
            {
                Window.Language = XmlLanguage.GetLanguage(locale.Culture.Name);
                TranslateTree(Window);
                UpdateScopeOptions();
                language.SelectedIndex = locale.Russian ? 1 : 0;
                threshold.ItemsSource = Preferences.Thresholds.Select(v => locale.Size((long)v << 20)).ToList();
                threshold.SelectedIndex = Array.IndexOf(Preferences.Thresholds, preferences.MinMiB);
                limit.ItemsSource = Preferences.Limits.Select(v => locale.Count(v)).ToList();
                limit.SelectedIndex = Array.IndexOf(Preferences.Limits, preferences.Limit);
                string[] fileHeaders = { T("File name", "Имя файла"), T("Size", "Размер"), T("Folder", "Папка"), T("Modified", "Изменён") };
                string[] appHeaders = { T("Application", "Программа"), T("Reported size", "Размер"), T("Publisher", "Издатель"), T("Version", "Версия") };
                for (int i = 0; i < 4; i++) { filesGrid.Columns[i].Header = fileHeaders[i]; appsGrid.Columns[i].Header = appHeaders[i]; }
                Accessibility("Scope", T("Scan location", "Область поиска"));
                Accessibility("FileFilter", T("Filter by file name or path", "Фильтр по имени или пути файла"));
                Accessibility("AppFilter", T("Filter by application or publisher", "Фильтр по названию или издателю программы"));
                Accessibility("LanguageBox", T("Interface language", "Язык интерфейса"));
                Accessibility("ThresholdBox", T("Minimum file size", "Минимальный размер файла"));
                Accessibility("LimitBox", T("Maximum results", "Лимит результатов"));
                Accessibility("NavFiles", T("Large files", "Крупные файлы"));
                Accessibility("NavApps", T("Applications", "Программы"));
                Accessibility("NavSettings", T("Settings", "Настройки"));
                if (filesGrid.ItemsSource != null) RebindLocalized(filesGrid, files);
                if (appsGrid.ItemsSource != null) RebindLocalized(appsGrid, applications);
            }
            finally { translating = false; }
            SwitchTo(page);
        }

        private void Accessibility(string name, string value)
        {
            FrameworkElement control = Get<FrameworkElement>(name);
            AutomationProperties.SetName(control, value);
            control.ToolTip = value;
        }

        private void TranslateTree(DependencyObject root)
        {
            FrameworkElement element = root as FrameworkElement;
            string source = element == null ? null : element.Tag as string;
            if (source != null)
            {
                string[] versions = source.Split('|');
                if (versions.Length == 2)
                {
                    var label = element as TextBlock;
                    var button = element as Button;
                    if (label != null) label.Text = T(versions[0], versions[1]);
                    if (button != null) button.Content = T(versions[0], versions[1]);
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject) TranslateTree((DependencyObject)child);
        }

        private void UpdateScopeOptions()
        {
            int selected = Math.Max(0, scope.SelectedIndex);
            var locations = new List<string> { T("This computer · all fixed drives", "Весь компьютер · все локальные диски") };
            locations.AddRange(driveRoots.Select(root => T("Drive ", "Диск ") + root));
            if (customFolder != null) locations.Add(customFolder);
            scope.ItemsSource = locations;
            scope.SelectedIndex = Math.Min(selected, locations.Count - 1);
        }

        private void PickFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = T("Choose the folder to scan", "Выберите папку для поиска");
                dialog.ShowNewFolderButton = false;
                if (customFolder != null) dialog.SelectedPath = customFolder;
                if (dialog.ShowDialog(new WindowOwner(Window)) != Forms.DialogResult.OK) return;
                SelectFolder(dialog.SelectedPath);
            }
        }

        internal void SelectFolder(string folder)
        {
            customFolder = folder;
            UpdateScopeOptions();
            scope.SelectedIndex = scope.Items.Count - 1;
        }

        private sealed class WindowOwner : Forms.IWin32Window
        {
            internal WindowOwner(Window window) { Handle = new WindowInteropHelper(window).Handle; }
            public IntPtr Handle { get; private set; }
        }

        internal void SwitchTo(Page destination)
        {
            page = destination;
            Get<Grid>("FilesPage").Visibility = page == Page.Files ? Visibility.Visible : Visibility.Collapsed;
            Get<Grid>("AppsPage").Visibility = page == Page.Apps ? Visibility.Visible : Visibility.Collapsed;
            Get<ScrollViewer>("SettingsPage").Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            SetNav("NavFiles", page == Page.Files); SetNav("NavApps", page == Page.Apps); SetNav("NavSettings", page == Page.Settings);
            if (page == Page.Files)
            {
                Text("TitleText", T("Large files", "Крупные файлы"));
                Text("Subtitle", T("Find what takes up space on your computer.", "Посмотрите, какие файлы занимают больше всего места."));
            }
            else if (page == Page.Apps)
            {
                Text("TitleText", T("Applications", "Программы"));
                Text("Subtitle", T("Review installed software and its reported size.", "Посмотрите список установленных программ и их размер по данным Windows."));
                if (!appsLoaded && !loadingApps) LoadApps();
            }
            else
            {
                Text("TitleText", T("Settings", "Настройки"));
                Text("Subtitle", T("Choose a language and set the limits for your scans.", "Выберите язык и задайте параметры поиска."));
            }
            UpdateScanPresentation(); UpdateAppsPresentation(); UpdateStatus();
        }

        private void SetNav(string name, bool selected)
        {
            Get<Button>(name).Background = selected ? new SolidColorBrush(ColorFromRgb(54, 89, 70)) : Brushes.Transparent;
        }
        private static System.Windows.Media.Color ColorFromRgb(byte r, byte g, byte b)
        { return System.Windows.Media.Color.FromRgb(r, g, b); }

        private async void ScanOrCancel()
        {
            if (cancellation != null)
            {
                cancellation.Cancel(); UpdateScanPresentation(); return;
            }
            int selected = scope.SelectedIndex;
            string[] roots = selected <= 0 ? driveRoots : new[] { selected <= driveRoots.Length ? driveRoots[selected - 1] : customFolder };
            if (roots.Length == 0)
            {
                Alert(T("Choose a folder to scan.", "Выберите папку для поиска.")); return;
            }
            var operation = new CancellationTokenSource();
            cancellation = operation;
            lastScan = null; Volatile.Write(ref progress, null);
            fileNoticeEn = fileNoticeRu = null;
            runThreshold = preferences.MinMiB;
            long minimum = (long)runThreshold << 20;
            int maxResults = preferences.Limit;
            files = new List<FileRow>();
            fileFilter.Clear();
            SetRows(filesGrid, files, "Bytes", ListSortDirection.Descending);
            UpdateScanPresentation(); timer.Start();
            try
            {
                var result = await Task.Run(() => DiskScanner.Scan(roots, minimum, maxResults,
                    operation.Token, p => Volatile.Write(ref progress, p)));
                if (closed) return;
                ShowResults(result, runThreshold);
            }
            catch (Exception error)
            {
                fileNoticeEn = "Scan failed. " + error.Message;
                fileNoticeRu = "Не удалось завершить поиск. " + error.Message;
            }
            finally
            {
                cancellation = null; operation.Dispose(); timer.Stop(); Volatile.Write(ref progress, null);
                if (!closed) UpdateScanPresentation();
            }
        }

        internal void ShowResults(ScanResult result, int minimumMiB)
        {
            runThreshold = minimumMiB;
            lastScan = result;
            files = result.Files.Select(f => new FileRow(f, locale)).ToList();
            SetRows(filesGrid, files, "Bytes", ListSortDirection.Descending);
            UpdateScanPresentation();
        }

        private void SetRows<TItem>(DataGrid grid, List<TItem> rows, string defaultOrder, ListSortDirection direction)
        {
            var view = new ListCollectionView(rows);
            view.Culture = locale.Culture;
            view.Filter = grid == filesGrid ? (Predicate<object>)MatchesFile : MatchesApp;
            view.SortDescriptions.Add(new SortDescription(defaultOrder, direction));
            grid.ItemsSource = view;
            foreach (var column in grid.Columns) column.SortDirection = column.SortMemberPath == defaultOrder ? (ListSortDirection?)direction : null;
        }

        private void RebindLocalized<TItem>(DataGrid grid, List<TItem> rows)
        {
            var view = (ListCollectionView)grid.ItemsSource;
            SortDescription ordering = view.SortDescriptions.Count > 0 ? view.SortDescriptions[0] :
                new SortDescription("Name", ListSortDirection.Ascending);
            object selection = grid.SelectedItem;
            SetRows(grid, rows, ordering.PropertyName, ordering.Direction);
            grid.SelectedItem = selection;
        }

        private static bool Contains(string value, string query)
        { return value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        private bool MatchesFile(object item)
        { return Contains(((FileRow)item).File.Path, fileFilter.Text.Trim()); }
        private bool MatchesApp(object item)
        {
            var row = (AppRow)item;
            string query = appFilter.Text.Trim();
            return Contains(row.Name, query) || Contains(row.Publisher, query) || Contains(row.Version, query);
        }
        private void Filter(DataGrid grid)
        {
            if (grid.ItemsSource != null) ((ListCollectionView)grid.ItemsSource).Refresh();
            if (grid == filesGrid) UpdateScanPresentation(); else UpdateAppsPresentation();
        }

        private void UpdateScanPresentation()
        {
            bool active = cancellation != null;
            bool stopping = active && cancellation.IsCancellationRequested;
            ScanProgress current = Volatile.Read(ref progress);
            ScanTotals totals = active ? (ScanTotals)current : lastScan;
            Text("Metric1", totals == null ? "—" : locale.Count(totals.FilesSeen));
            Text("Metric2", totals == null ? "—" : locale.Size(totals.BytesSeen));
            Text("Metric3", totals == null ? "—" : locale.Count(totals.Matches));
            Label("ScanButton", stopping ? T("Stopping…", "Остановка…") :
                active ? T("Stop scan", "Остановить") : T("Start scan", "Начать поиск"));
            Get<Button>("ScanButton").IsEnabled = !stopping;
            Get<Button>("ChooseFolder").IsEnabled = !active;
            scope.IsEnabled = !active;
            ProgressBar bar = Get<ProgressBar>("ScanBar");
            bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            bar.IsIndeterminate = active;
            int displayed = filesGrid.Items.Count;
            long thresholdBytes = (long)(active || lastScan != null ? runThreshold : preferences.MinMiB) << 20;
            Text("FileListTitle", T("Files ≥ ", "Файлы ≥ ") + locale.Size(thresholdBytes));
            TextBlock empty = Get<TextBlock>("FilesEmpty");
            empty.Visibility = displayed == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (active) empty.Text = T("Scanning… Results will appear when the scan ends.", "Идёт поиск. Список появится после завершения.");
            else if (lastScan == null) empty.Text = T("Choose a location and start a scan.", "Выберите диск или папку и начните поиск.");
            else if (files.Count == 0) empty.Text = lastScan.Cancelled
                ? T("Scan stopped before any matches were found.", "Поиск остановлен. Подходящих файлов пока нет.")
                : T("No files of this size were found in the scanned locations.", "В проверенных папках нет файлов такого размера.");
            else empty.Text = T("No files match the filter.", "Нет файлов, подходящих под фильтр.");
            RefreshFileActions(); UpdateStatus();
        }

        private void RefreshFileActions()
        {
            var row = filesGrid.SelectedItem as FileRow;
            bool selected = row != null && cancellation == null;
            bool eligible = selected && row.File.CanRecycle;
            Get<Button>("LocateButton").IsEnabled = selected;
            Button recycle = Get<Button>("RecycleButton");
            recycle.IsEnabled = eligible;
            string protectedText = T("This location or file is protected.", "Этот файл или его расположение защищены.");
            ToolTipService.SetShowOnDisabled(recycle, true);
            recycle.ToolTip = selected && !eligible ? protectedText : T("Move the selected file to the Windows Recycle Bin", "Переместить выбранный файл в корзину Windows");
            string detail = selected ? (eligible ? row.File.Path : protectedText + " " + row.File.Path) :
                lastScan == null ? "" : T("Shown: ", "Показано: ") + locale.Count(filesGrid.Items.Count) + " / " + locale.Count(lastScan.Matches);
            Text("FileSelection", detail);
            Get<TextBlock>("FileSelection").ToolTip = detail;
        }

        private void LocateFile()
        {
            var row = filesGrid.SelectedItem as FileRow;
            if (row == null) return;
            try { FileActions.Locate(row.File.Path); }
            catch (Exception e) { Alert(T("Could not locate this file.", "Не удалось показать файл в папке.") + "\n\n" + e.Message); }
        }

        private void RecycleFile()
        {
            var row = filesGrid.SelectedItem as FileRow;
            if (row == null || !row.File.CanRecycle) return;
            string prompt = T("Move this file to the Windows Recycle Bin?", "Переместить файл в корзину Windows?") +
                "\n\n" + row.File.Path + "\n" + row.SizeText;
            if (!Confirm(prompt)) return;
            try
            {
                FileActions.Recycle(row.File);
                files.Remove(row);
                ((ListCollectionView)filesGrid.ItemsSource).Refresh();
                fileNoticeEn = "Moved to the Recycle Bin: " + row.Name;
                fileNoticeRu = "Перемещено в корзину: " + row.Name;
                UpdateScanPresentation();
            }
            catch (Exception e) { Alert(T("Could not move this file to the Recycle Bin.", "Не удалось переместить файл в корзину.") + "\n\n" + e.Message); }
        }

        private async void LoadApps()
        {
            if (loadingApps) return;
            loadingApps = true;
            appNoticeEn = appNoticeRu = null;
            UpdateAppsPresentation();
            try
            {
                var result = await Task.Run(() => ApplicationCatalog.Read());
                if (closed) return;
                applications = result.Select(a => new AppRow(a, locale)).ToList();
                appsLoaded = true;
                SetRows(appsGrid, applications, "Name", ListSortDirection.Ascending);
            }
            catch (Exception e)
            {
                appNoticeEn = "Could not read the application list. " + e.Message;
                appNoticeRu = "Не удалось получить список программ. " + e.Message;
            }
            finally { loadingApps = false; if (!closed) UpdateAppsPresentation(); }
        }

        private void UpdateAppsPresentation()
        {
            Get<Button>("RefreshApps").IsEnabled = !loadingApps;
            int count = applications.Count(a => a.Bytes.HasValue);
            string summary = loadingApps ? T("Reading installed applications…", "Получение списка программ…") :
                appsLoaded ? T("Installed: ", "Установлено: ") + locale.Count(applications.Count) + T("  ·  With reported size: ", "  ·  Известен размер: ") + locale.Count(count) :
                T("Application inventory", "Список программ");
            Text("AppsSummary", summary);
            TextBlock empty = Get<TextBlock>("AppsEmpty");
            empty.Visibility = appsGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = loadingApps ? T("Loading…", "Загрузка…") : !appsLoaded ? T("Refresh to load the application list.", "Нажмите «Обновить», чтобы загрузить список.") :
                applications.Count == 0 ? T("Windows returned no applications.", "Windows не вернула ни одной программы.") :
                T("No applications match the filter.", "Нет программ, подходящих под фильтр.");
            RefreshAppActions(); UpdateStatus();
        }

        private void RefreshAppActions()
        {
            var row = appsGrid.SelectedItem as AppRow;
            bool direct = row != null && row.App.CanUninstallDirectly;
            Get<Button>("UninstallButton").IsEnabled = row != null && !loadingApps;
            Label("UninstallButton", row == null || direct ? T("Uninstall…", "Удалить…") : T("Uninstall in Windows…", "Удалить в Windows…"));
            string detail = "";
            if (row != null)
            {
                detail = row.App.IsCurrentUser ? T("Current user", "Текущий пользователь") : T("All users", "Все пользователи");
                if (!string.IsNullOrWhiteSpace(row.App.Location)) detail += "  ·  " + row.App.Location;
            }
            else if (appsLoaded) detail = T("Shown: ", "Показано: ") + locale.Count(appsGrid.Items.Count);
            Text("AppSelection", detail); Get<TextBlock>("AppSelection").ToolTip = detail;
        }

        private void OpenWindowsApps()
        {
            try { ApplicationCatalog.OpenWindowsApps(); }
            catch (Exception e) { Alert(T("Could not open Windows application settings.", "Не удалось открыть список программ Windows.") + "\n\n" + e.Message); }
        }

        private void Uninstall()
        {
            var row = appsGrid.SelectedItem as AppRow;
            if (row == null) return;
            string question = row.App.CanUninstallDirectly ? T("Open the uninstaller for this application?", "Открыть программу удаления для этого приложения?") :
                T("Open Windows settings? Find this application by name and choose Uninstall there.",
                    "Открыть параметры Windows? Найдите приложение по названию и выберите «Удалить».");
            if (!Confirm(question + "\n\n" + row.Name + "\n" + row.Publisher + "  ·  " + row.Version)) return;
            try
            {
                bool direct = ApplicationCatalog.StartUninstall(row.App);
                appNoticeEn = direct ? "Uninstaller opened. Refresh this list after it finishes." : "In Windows, search for “" + row.Name + "”. Refresh this list after uninstalling.";
                appNoticeRu = direct ? "Открыта программа удаления. Обновите список после завершения." : "В параметрах Windows найдите «" + row.Name + "». После удаления обновите список.";
                UpdateStatus();
            }
            catch (Exception e) { Alert(T("Could not open the uninstaller.", "Не удалось открыть программу удаления.") + "\n\n" + e.Message); }
        }

        private void UpdateStatus()
        {
            string message;
            if (page == Page.Settings) message = T(settingsNoticeEn, settingsNoticeRu);
            else if (page == Page.Apps) message = T(appNoticeEn, appNoticeRu) ?? T("Sizes are provided by installed applications.", "Размер указывают сами установленные программы.");
            else if (cancellation != null)
            {
                ScanProgress current = Volatile.Read(ref progress);
                message = cancellation.IsCancellationRequested ? T("Stopping the scan…", "Останавливаем поиск…") :
                    T("Scanning ", "Проверка ") + (current == null ? "…" : current.CurrentDirectory);
            }
            else if (fileNoticeEn != null) message = T(fileNoticeEn, fileNoticeRu);
            else if (lastScan != null)
            {
                message = lastScan.Cancelled ? T("Scan stopped.", "Поиск остановлен.") : T("Scan complete.", "Поиск завершён.");
                if (lastScan.Truncated) message += T(" Largest results retained: ", " В списке самые крупные: ") + locale.Count(lastScan.Files.Count) + ".";
                message += T(" Unreadable: ", " Недоступно: ") + locale.Count(lastScan.Skipped) +
                    T(" · Reparse points skipped: ", " · Пропущено точек повторной обработки: ") + locale.Count(lastScan.ReparseSkipped);
            }
            else message = T("Ready to scan.", "Готово к поиску.");
            Text("Status", message); Get<TextBlock>("Status").ToolTip = message;
        }

        private bool Confirm(string message)
        {
            // Standard Windows message-box button labels follow the OS display language.
            return MessageBox.Show(Window, message, "Velora Disk", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        private void Alert(string message)
        { MessageBox.Show(Window, message, "Velora Disk", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
