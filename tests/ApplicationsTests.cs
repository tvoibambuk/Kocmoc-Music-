using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using Diskovy;

internal static class ApplicationsTests
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static InstalledApplication ReadFixture(Dictionary<string, object> fields, string id)
    {
        return ApplicationCatalog.FromRegistryEntry(id, field => {
            object value;
            return fields.TryGetValue(field, out value) ? value : null;
        }, true);
    }

    private static bool ProtectedAcl(string sddl, bool root)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return ApplicationCatalog.HasProtectedAcl(security, root);
    }

    public static int Main()
    {
        var fields = new Dictionary<string, object> {
            { "DisplayName", "  Example  " }, { "DisplayVersion", "1.2" },
            { "Publisher", "Example Studio" }, { "EstimatedSize", 1024 },
            { "InstallLocation", "\"C:\\Example\"" },
            { "UninstallString", "untrusted.exe /silent" }
        };
        var app = ReadFixture(fields, "Example");
        Check(app.Name == "Example" && app.Publisher == "Example Studio" && app.Version == "1.2",
            "Read app metadata");
        Check(app.Location == @"C:\Example" && app.IsCurrentUser, "Read path and scope");
        Check(app.SizeBytes == 1048576, "Convert estimated KiB to bytes");
        Check(!app.CanUninstallDirectly && ApplicationCatalog.GetUninstallTarget(app).FileName ==
            "ms-settings:appsfeatures", "Delegate arbitrary uninstallers to Windows");
        foreach (object unknown in new object[] { null, "", "bad", -1, 0, long.MaxValue })
            Check(ApplicationCatalog.SizeFromKilobytes(unknown) == null, "Preserve unknown size");
        Check(ApplicationCatalog.SizeFromKilobytes(-2) == ((long)uint.MaxValue - 1) * 1024,
            "Unsigned DWORD");
        Check(ApplicationCatalog.SizeFromKilobytes("2048") == 2097152L, "String estimate");
        foreach (var field in new Dictionary<string, object> {
            { "SystemComponent", 1 }, { "ParentKeyName", "Suite" }, { "ReleaseType", "security update" }
        })
        {
            fields.Add(field.Key, field.Value);
            Check(ReadFixture(fields, "id") == null, "Filter " + field.Key);
            fields.Remove(field.Key);
        }
        fields["WindowsInstaller"] = 1;
        Check(!ReadFixture(fields, "{injected} /passive").CanUninstallDirectly, "Strict product code");
        string missing = Guid.NewGuid().ToString("B");
        Check(!ReadFixture(fields, missing).CanUninstallDirectly, "Validate Windows Installer registration");
        fields["NoRemove"] = 1;
        Check(!ReadFixture(fields, missing).CanUninstallDirectly, "Honor NoRemove");
        fields.Remove("DisplayName");
        Check(ReadFixture(fields, "entry") == null, "Skip unnamed entries");

        var package = new InstalledApplication { Name = "Example package", PackageFamily = "Sample.App_abc123" };
        var target = ApplicationCatalog.GetUninstallTarget(package);
        Check(package.IsPackaged && target.UseShellExecute && target.FileName ==
            "ms-settings:appsfeatures-app?Sample.App_abc123", "Open the selected package's Settings page");

        string examplePath = @"C:\Program Files\Example\unins000.exe";
        Check(ApplicationCatalog.ParseVendorPath("\"" + examplePath + "\"") == examplePath, "Quoted vendor path");
        Check(ApplicationCatalog.ParseVendorPath(examplePath) == examplePath, "Unquoted vendor path");
        foreach (string input in new[] { "uninstall.exe", @"C:uninstall.exe", @"\\server\share\uninstall.exe",
            examplePath + " /quiet", "\"" + examplePath + "\" /S", examplePath + ":uninstall.exe",
            @"C:\Program Files\..\Elsewhere\uninstall.exe", @"C:\PROGRA~1\Example\uninstall.exe",
            @"C:\Program Files\Example\cmd.exe", @"C:/Program Files/Example/uninstall.exe" })
            Check(ApplicationCatalog.ParseVendorPath(input) == null, "Reject ambiguous vendor target");
        string userPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uninstall.exe");
        Check(ApplicationCatalog.VerifiedVendorUninstaller(userPath) == null, "Reject user-writable locations");
        Check(ProtectedAcl("O:BAD:(A;;FA;;;SY)(A;;FR;;;BU)", false), "Allow system-managed, read-only user access");
        Check(!ProtectedAcl("O:BAD:(A;;FA;;;SY)(A;;FW;;;BU)", false), "Reject ordinary users with write access");
        Check(!ProtectedAcl("O:BUD:(A;;FR;;;BU)", false), "Reject ordinary user as file owner");
        Check(ProtectedAcl("O:BAD:(A;;0x4;;;AU)", true), "Allow sibling directory creation on drive root");
        Check(!ProtectedAcl("O:BAD:(A;;0x4;;;AU)", false), "Reject child creation inside vendor directory");
        Check(!ProtectedAcl("O:BAD:(A;;0x40;;;AU)", true), "Reject root ACL allowing children to be removed");

        var installed = ApplicationCatalog.Read();
        Check(installed.Count > 0 && installed.All(a => !string.IsNullOrWhiteSpace(a.Name)), "Live inventory");
        var firstMsi = installed.FirstOrDefault(a => a.ProductCode.HasValue);
        if (firstMsi != null)
        {
            var msi = ApplicationCatalog.GetUninstallTarget(firstMsi);
            Check(msi.FileName == Path.Combine(Environment.SystemDirectory, "msiexec.exe") &&
                !msi.UseShellExecute && msi.Arguments == "/x " +
                firstMsi.ProductCode.Value.ToString("B").ToUpperInvariant() + " /qf /norestart",
                "Interactive, fully qualified Windows Installer command");
        }
        var vendors = installed.Where(a => a.VendorUninstaller != null).ToList();
        foreach (var vendor in vendors)
        {
            var plan = ApplicationCatalog.GetUninstallTarget(vendor);
            Check(!plan.UseShellExecute && string.IsNullOrEmpty(plan.Arguments) &&
                plan.FileName == vendor.VendorUninstaller && !vendor.IsCurrentUser, "Direct protected vendor uninstaller");
        }
        if (vendors.Count != 0)
        {
            fields.Clear();
            fields.Add("DisplayName", "Test");
            fields.Add("UninstallString", vendors[0].VendorUninstaller);
            Check(!ReadFixture(fields, "entry").CanUninstallDirectly, "Reject vendor commands in per-user registrations");
            fields.Add("NoRemove", 1);
            Check(!ApplicationCatalog.FromRegistryEntry("entry", key =>
                fields.ContainsKey(key) ? fields[key] : null, false).CanUninstallDirectly, "Honor machine-wide NoRemove");
        }
        Console.WriteLine("Application tests passed ({0} checks). Inventory: {1} entries, {2} packages, {3} direct MSI, {4} direct vendor uninstallers.",
            checks, installed.Count, installed.Count(a => a.IsPackaged), installed.Count(a => a.ProductCode.HasValue), vendors.Count);
        return 0;
    }
}
