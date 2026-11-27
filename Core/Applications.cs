using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace Diskovy
{
    public sealed class InstalledApplication
    {
        public string Name { get; internal set; }
        public string Publisher { get; internal set; }
        public string Version { get; internal set; }
        public string Location { get; internal set; }
        public long? SizeBytes { get; internal set; }
        public bool IsCurrentUser { get; internal set; }
        public bool IsPackaged { get { return PackageFamily != null; } }
        public bool CanUninstallDirectly { get { return ProductCode.HasValue || VendorUninstaller != null; } }
        internal string PackageFamily { get; set; }
        internal Guid? ProductCode { get; set; }
        internal string VendorUninstaller { get; set; }
    }

    public static class ApplicationCatalog
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string AppsSettings = "ms-settings:appsfeatures";

        public static List<InstalledApplication> Read()
        {
            var applications = new List<InstalledApplication>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                if (Environment.Is64BitOperatingSystem)
                    ReadRegistryView(hive, RegistryView.Registry64, applications, seen);
                ReadRegistryView(hive, RegistryView.Registry32, applications, seen);
            }
            ReadPackages(applications);
            return applications.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Version, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ReadRegistryView(RegistryHive hive, RegistryView view,
            List<InstalledApplication> applications, HashSet<string> seen)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var entries = root.OpenSubKey(UninstallKey))
                {
                    if (entries == null) return;
                    foreach (string id in entries.GetSubKeyNames())
                    {
                        try
                        {
                            using (var key = entries.OpenSubKey(id))
                            {
                                if (key == null) continue;
                                var app = FromRegistryEntry(id, field => key.GetValue(field, null,
                                    RegistryValueOptions.DoNotExpandEnvironmentNames), hive == RegistryHive.CurrentUser);
                                if (app == null) continue;
                                // HKCU often shares the exact same uninstall keys across both views.
                                string identity = string.Join("\0", hive.ToString(), id, app.Name,
                                    app.Version, app.Publisher, app.Location);
                                if (seen.Add(identity)) applications.Add(app);
                            }
                        }
                        catch (Exception e)
                        {
                            if (!IsRegistryUnavailable(e)) throw;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!IsRegistryUnavailable(e)) throw;
            }
        }

        private static bool IsRegistryUnavailable(Exception e)
        {
            return e is SecurityException || e is UnauthorizedAccessException || e is IOException;
        }

        internal static InstalledApplication FromRegistryEntry(string id, Func<string, object> read, bool currentUser)
        {
            string name = ReadText(read, "DisplayName");
            string release = ReadText(read, "ReleaseType");
            if (name.Length == 0 || HasFlag(read, "SystemComponent") ||
                ReadText(read, "ParentKeyName").Length != 0 ||
                new[] { "Update", "Security Update", "Hotfix", "Update Rollup", "Service Pack" }
                    .Contains(release, StringComparer.OrdinalIgnoreCase)) return null;

            Guid product = Guid.Empty;
            bool isMsi = HasFlag(read, "WindowsInstaller");
            bool removable = !HasFlag(read, "NoRemove");
            bool registeredMsi = isMsi && removable &&
                Guid.TryParseExact(id, "B", out product) && IsRegisteredProduct(product, name);
            string vendor = !isMsi && removable && !currentUser ?
                VerifiedVendorUninstaller(ReadText(read, "UninstallString")) : null;
            return new InstalledApplication {
                Name = name,
                Publisher = ReadText(read, "Publisher"),
                Version = ReadText(read, "DisplayVersion"),
                Location = ReadText(read, "InstallLocation").Trim('"'),
                SizeBytes = SizeFromKilobytes(read("EstimatedSize")),
                IsCurrentUser = currentUser,
                ProductCode = registeredMsi ? (Guid?)product : null,
                VendorUninstaller = vendor
            };
        }

        private static readonly Regex VendorFileName = new Regex(
            @"^(?:unins[0-9]{3}|uninstall(?:er)?|uninst)\.exe\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string ParseVendorPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string path = command.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                path = path.Substring(1, path.Length - 2);
            if (path.Length < 4 || !((path[0] >= 'A' && path[0] <= 'Z') ||
                (path[0] >= 'a' && path[0] <= 'z')) || path[1] != ':' || path[2] != '\\') return null;
            string relative = path.Substring(3);
            if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                relative.IndexOfAny(new[] { ':', '*', '?', '/' }) >= 0) return null;
            // Keep a single literal path. Reject aliases and trailing-dot directory spellings.
            foreach (string part in relative.Split('\\'))
                if (part.Length == 0 || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal) || part.Contains("~")) return null;
            if (!VendorFileName.IsMatch(Path.GetFileName(path))) return null;
            return path;
        }

        internal static string VerifiedVendorUninstaller(string command)
        {
            string path = ParseVendorPath(command);
            if (path == null) return null;
            try
            {
                var roots = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
                bool underProgramFiles = roots.Select(Environment.GetFolderPath).Where(r => r.Length != 0)
                    .Any(r => path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith(Path.Combine(r, "WindowsApps") + "\\", StringComparison.OrdinalIgnoreCase));
                if (!underProgramFiles || !File.Exists(path)) return null;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !HasProtectedAcl(File.GetAccessControl(path), false)) return null;
                for (var folder = new FileInfo(path).Directory; folder != null; folder = folder.Parent)
                {
                    if ((folder.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        !HasProtectedAcl(folder.GetAccessControl(), folder.Parent == null)) return null;
                }
                return path;
            }
            catch (Exception e)
            {
                if (IsRegistryUnavailable(e) || e is NotSupportedException || e is ArgumentException) return null;
                throw;
            }
        }

        internal static bool HasProtectedAcl(FileSystemSecurity security, bool volumeRoot)
        {
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl == null || descriptor.Owner == null || !TrustedWriter(descriptor.Owner))
                return false;
            const FileSystemRights genericWriteOrAll = (FileSystemRights)0x50000000;
            FileSystemRights forbidden = genericWriteOrAll | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            // Windows normally lets users create unrelated directories at C:\.
            if (!volumeRoot) forbidden |= FileSystemRights.WriteData | FileSystemRights.AppendData;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((rule.FileSystemRights & forbidden) != 0 &&
                    !TrustedWriter((SecurityIdentifier)rule.IdentityReference)) return false;
            }
            return true;
        }

        private static bool TrustedWriter(SecurityIdentifier identifier)
        {
            return identifier.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                identifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                identifier.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
        }

        private static string ReadText(Func<string, object> read, string field)
        {
            object value = read(field);
            return value is string ? ((string)value).Trim() : string.Empty;
        }

        private static bool HasFlag(Func<string, object> read, string field)
        {
            return string.Equals(Convert.ToString(read(field), CultureInfo.InvariantCulture), "1",
                StringComparison.Ordinal);
        }

        internal static long? SizeFromKilobytes(object value)
        {
            long kilobytes;
            if (value is int && (int)value < -1)
                kilobytes = unchecked((uint)(int)value); // Registry DWORD is unsigned.
            else if (!long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out kilobytes)) return null;
            if (kilobytes <= 0 || kilobytes > long.MaxValue / 1024) return null;
            return kilobytes * 1024;
        }

        private static bool IsRegisteredProduct(Guid code, string expectedName)
        {
            // MsiGetProductInfo reads Installer metadata without triggering an MSI consistency check.
            var name = new StringBuilder(512);
            uint length = (uint)name.Capacity;
            string product = code.ToString("B").ToUpperInvariant();
            uint status = MsiGetProductInfo(product, "InstalledProductName", name, ref length);
            if (status == 234 && length < 32768)
            {
                name = new StringBuilder((int)++length);
                status = MsiGetProductInfo(product, "InstalledProductName", name, ref length);
            }
            return status == 0 && string.Equals(expectedName, name.ToString().Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW", ExactSpelling = true)]
        private static extern uint MsiGetProductInfo(string code, string property, StringBuilder output, ref uint length);

        private static void ReadPackages(List<InstalledApplication> applications)
        {
            dynamic shell = null, folder = null, items = null;
            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                shell = Activator.CreateInstance(type);
                folder = shell.NameSpace("shell:AppsFolder");
                if (folder == null) return;
                items = folder.Items();
                int count = items.Count;
                for (int index = 0; index < count; index++)
                {
                    dynamic item = null;
                    try
                    {
                        item = items.Item(index);
                        if (item == null) continue;
                        string family = item.ExtendedProperty("System.AppUserModel.PackageFamilyName") as string;
                        if (string.IsNullOrWhiteSpace(family) || families.Contains(family)) continue;
                        string name = item.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string fullName = item.ExtendedProperty("System.AppUserModel.PackageFullName") as string;
                        string location = item.ExtendedProperty("System.AppUserModel.PackageInstallPath") as string;
                        applications.Add(new InstalledApplication {
                            Name = name, Publisher = string.Empty, Version = PackageVersion(fullName),
                            Location = location ?? string.Empty, IsCurrentUser = true, PackageFamily = family
                        });
                        families.Add(family);
                    }
                    catch (Exception e)
                    {
                        if (!IsShellUnavailable(e)) throw;
                    }
                    finally { Release((object)item); }
                }
            }
            catch (Exception e)
            {
                if (!IsShellUnavailable(e)) throw;
            }
            finally
            {
                Release((object)items);
                Release((object)folder);
                Release((object)shell);
            }
        }

        private static bool IsShellUnavailable(Exception e)
        {
            return e is COMException || e is UnauthorizedAccessException ||
                e is RuntimeBinderException || e is NotSupportedException;
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private static string PackageVersion(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return string.Empty;
            // Windows package names disallow underscores. The second field is the version.
            string[] fields = fullName.Split('_');
            Version parsed;
            return fields.Length == 5 && Version.TryParse(fields[1], out parsed) ? parsed.ToString() : string.Empty;
        }

        internal static ProcessStartInfo GetUninstallTarget(InstalledApplication app)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (app.ProductCode.HasValue && IsRegisteredProduct(app.ProductCode.Value, app.Name))
            {
                return new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/x " + app.ProductCode.Value.ToString("B").ToUpperInvariant() + " /qf /norestart")
                    { UseShellExecute = false };
            }
            if (!app.IsCurrentUser && app.VendorUninstaller != null &&
                VerifiedVendorUninstaller(app.VendorUninstaller) != null)
                return new ProcessStartInfo(app.VendorUninstaller) {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.VendorUninstaller)
                };
            string uri = app.IsPackaged ? AppsSettings + "-app?" + Uri.EscapeDataString(app.PackageFamily) : AppsSettings;
            return new ProcessStartInfo(uri) { UseShellExecute = true };
        }

        // The caller confirms the selected name before calling. Registry command lines are never run.
        public static bool StartUninstall(InstalledApplication app)
        {
            var target = GetUninstallTarget(app);
            try
            {
                using (Process.Start(target)) { }
                return !target.UseShellExecute;
            }
            catch (Win32Exception e)
            {
                // Let Windows manage a vendor installer whose manifest requires elevation.
                if (!target.UseShellExecute && e.NativeErrorCode != 740) throw;
                OpenWindowsApps();
                return false;
            }
        }

        public static void OpenWindowsApps()
        {
            try { LaunchSettings(AppsSettings); }
            catch (Win32Exception)
            {
                using (Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "control.exe"), "/name Microsoft.ProgramsAndFeatures")
                    { UseShellExecute = false })) { }
            }
        }

        private static void LaunchSettings(string uri)
        {
            using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
        }
    }
}
