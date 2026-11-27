using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Diskovy
{
    internal sealed class Preferences
    {
        internal static readonly int[] Thresholds = { 1, 10, 50, 100, 250, 500, 1024, 5120 };
        internal static readonly int[] Limits = { 100, 500, 1000, 5000 };
        internal string Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        internal int MinMiB = 100;
        internal int Limit = 500;
        private readonly string file;

        internal Preferences(string path)
        {
            file = path;
            if (!File.Exists(file)) return;
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] pair = line.Split('=');
                    int value;
                    if (pair.Length != 2) continue;
                    if (pair[0] == "language" && (pair[1] == "ru" || pair[1] == "en")) Language = pair[1];
                    if (!int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out value)) continue;
                    if (pair[0] == "thresholdMiB" && Thresholds.Contains(value)) MinMiB = value;
                    if (pair[0] == "results" && Limits.Contains(value)) Limit = value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[] { "language=" + Language,
                    "thresholdMiB=" + MinMiB.ToString(CultureInfo.InvariantCulture),
                    "results=" + Limit.ToString(CultureInfo.InvariantCulture) }, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    internal sealed class Locale
    {
        private readonly Preferences settings;
        internal Locale(Preferences preferences) { settings = preferences; }
        internal bool Russian { get { return settings.Language == "ru"; } }
        internal CultureInfo Culture { get { return CultureInfo.GetCultureInfo(Russian ? "ru-RU" : "en-US"); } }
        internal string T(string en, string ru) { return Russian ? ru : en; }
        internal string Count(long number) { return number.ToString("N0", Culture); }
        internal string Size(long bytes)
        {
            string[] units = Russian ? new[] { "Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ", "ЭиБ" }
                : new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "N0" : "0.#", Culture) + " " + units[unit];
        }
    }

    internal sealed class FileRow
    {
        private readonly Locale locale;
        public LargeFile File { get; private set; }
        internal FileRow(LargeFile file, Locale locale) { File = file; this.locale = locale; }
        public string Name { get { return File.Name; } }
        public string Directory { get { return File.Directory; } }
        public long Bytes { get { return File.Bytes; } }
        public string SizeText { get { return locale.Size(Bytes); } }
        public DateTime LastModified { get { return File.LastWriteUtc.ToLocalTime(); } }
        public string DateText { get { return LastModified.ToString("g", locale.Culture); } }
    }

    internal sealed class AppRow
    {
        private readonly Locale locale;
        public InstalledApplication App { get; private set; }
        internal AppRow(InstalledApplication app, Locale locale) { App = app; this.locale = locale; }
        public string Name { get { return App.Name; } }
        public string Publisher { get { return string.IsNullOrEmpty(App.Publisher) ? "—" : App.Publisher; } }
        public string Version { get { return string.IsNullOrEmpty(App.Version) ? "—" : App.Version; } }
        public long? Bytes { get { return App.SizeBytes; } }
        public string SizeText { get { return Bytes.HasValue ? locale.Size(Bytes.Value) : locale.T("Unknown", "Неизвестно"); } }
    }
}
