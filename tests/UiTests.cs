using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace Diskovy
{
    // Compile with the application sources using /main:Diskovy.UiTests.
    internal static class UiTests
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string temp = Path.Combine(Path.GetTempPath(), "VeloraUi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            int result = 1;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async delegate
            {
                try { await Check(temp, args); result = 0; Console.WriteLine("UI smoke tests passed."); }
                catch (Exception e) { Console.Error.WriteLine(e); }
                finally { app.Shutdown(); }
            }));
            try { app.Run(); }
            finally { Directory.Delete(temp, true); }
            return result;
        }

        private static async Task Check(string temp, string[] args)
        {
            string settingsPath = Path.Combine(temp, "settings.ini");
            var prefs = new Preferences(settingsPath);
            prefs.Language = "en";
            var screen = new MainScreen(prefs);
            Window window = screen.Window;
            Func<string, FrameworkElement> field = name => (FrameworkElement)window.FindName(name);
            Func<string, string> text = name => ((TextBlock)field(name)).Text;
            Action<string> click = name => ((Button)field(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(text("TitleText") == "Large files", "English startup");
            click("NavSettings"); Assert(text("TitleText") == "Settings", "Settings navigation");
            ((ComboBox)field("LanguageBox")).SelectedIndex = 1;
            ((ComboBox)field("ThresholdBox")).SelectedIndex = 0;
            ((ComboBox)field("LimitBox")).SelectedIndex = 0;
            var saved = new Preferences(settingsPath);
            Assert(text("TitleText") == "Настройки" && saved.Language == "ru" && saved.MinMiB == 1 && saved.Limit == 100, "Russian setting persistence");
            string folder = Path.Combine(temp, "samples");
            Directory.CreateDirectory(folder);
            using (var file = File.Create(Path.Combine(folder, "small.dat"))) file.SetLength(55);
            using (var file = File.Create(Path.Combine(folder, "первый.bin"))) file.SetLength(1L << 20);
            using (var file = File.Create(Path.Combine(folder, "second.bin"))) file.SetLength(2L << 20);
            click("NavFiles"); screen.SelectFolder(folder); click("ScanButton");
            await Until(() => ((Button)field("ScanButton")).Content.Equals("Начать поиск"));
            var grid = (DataGrid)field("FilesGrid");
            Assert(grid.Items.Count == 2 && ((FileRow)grid.Items[0]).Name == "second.bin", "Size order and inclusive threshold");
            Assert(text("Metric1") == "3" && text("Metric3") == "2", "Russian scan totals");
            ((TextBox)field("FileFilter")).Text = "перв";
            Assert(grid.Items.Count == 1, "Unicode filter");
            grid.SelectedIndex = 0;
            Assert(field("LocateButton").IsEnabled, "Row selection action");
            ((TextBox)field("FileFilter")).Text = "no matching test value";
            Assert(grid.Items.Count == 0 && field("FilesEmpty").Visibility == Visibility.Visible, "No-match empty state");
            ((TextBox)field("FileFilter")).Clear();
            click("NavSettings"); ((ComboBox)field("LanguageBox")).SelectedIndex = 0;
            click("NavFiles");
            Assert(((FileRow)grid.Items[0]).SizeText == "2 MiB", "English result units after switching");
            click("NavApps"); await Until(() => field("RefreshApps").IsEnabled);
            var apps = (DataGrid)field("AppsGrid");
            Assert(text("AppsSummary").StartsWith("Installed:"), "Application view loaded");
            ((TextBox)field("AppFilter")).Text = Guid.NewGuid().ToString("N");
            Assert(apps.Items.Count == 0 && field("AppsEmpty").Visibility == Visibility.Visible, "Empty application filter");
            if (args.Length > 0)
            {
                click("NavSettings"); ((ComboBox)field("LanguageBox")).SelectedIndex = 1;
                click("NavFiles");
                string demoFolder = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Медиатека");
                screen.SelectFolder(demoFolder);
                string[] names = { "Архив видеопроекта.zip", "Съёмка в горах.mp4", "Резервная копия.7z",
                    "Лекция 04.mp4", "Виртуальная машина.vhdx", "Фотографии за лето.zip", "Концерт.wav", "Материалы курса.zip" };
                string[] folders = { "Проекты", "Видео", "Архивы", "Видео", "Проекты", "Фотографии", "Музыка", "Архивы" };
                int[] sizes = { 12780, 9160, 5400, 3014, 2048, 1704, 890, 438 };
                var examples = new List<LargeFile>();
                for (int i = 0; i < names.Length; i++)
                {
                    long bytes = (long)sizes[i] << 20;
                    DateTime modified = new DateTime(2026, 11, 24, 16, 32, 0, DateTimeKind.Local).AddDays(-i * 2);
                    long timestamp = modified.ToFileTimeUtc();
                    var file = new Native.FindData { Name = names[i], SizeHigh = (uint)(bytes >> 32), SizeLow = (uint)bytes,
                        Written = new Native.Timestamp { High = (uint)(timestamp >> 32), Low = (uint)timestamp } };
                    examples.Add(new LargeFile(Path.Combine(demoFolder, folders[i]), file));
                }
                screen.ShowResults(new ScanResult { Files = examples, Matches = examples.Count, FilesSeen = 32418,
                    BytesSeen = (long)(61.4 * 1024 * 1024 * 1024), ReparseSkipped = 2 }, 100);
                grid.SelectedIndex = 1;
                // Render the window content into a bitmap in memory without displaying or capturing the desktop.
                var layout = (FrameworkElement)window.Content;
                const int width = 1100, height = 716;
                layout.Measure(new Size(width, height)); layout.Arrange(new Rect(0, 0, width, height)); layout.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                layout.Measure(new Size(width, height)); layout.Arrange(new Rect(0, 0, width, height)); layout.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(layout);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(args[0])) encoder.Save(file);
            }
            window.Close();
        }
        private static async Task Until(Func<bool> predicate)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert(predicate(), "Time limit for a UI action");
        }
        private static void Assert(bool pass, string context)
        { if (!pass) throw new InvalidOperationException(context); }
    }
}
