using System.Xml;
using System.Xml.Linq;
using DwgTranslator.Contracts;

namespace DwgTranslator.Infrastructure.Windows;

public sealed record CadManualLoadBundle(string BundleDirectory, string AssemblyPath);

public static class CadManualLoadBundlePolicy
{
    public static Result<CadManualLoadBundle> Resolve(
        string? bundleDirectory,
        string expectedAppName,
        string expectedAssemblyFileName)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory) || !Path.IsPathFullyQualified(bundleDirectory) ||
            string.IsNullOrWhiteSpace(expectedAppName) || string.IsNullOrWhiteSpace(expectedAssemblyFileName))
            return Failure("CAD_MANUAL_LOAD_BUNDLE_INVALID");

        string bundle;
        try { bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return Failure("CAD_MANUAL_LOAD_BUNDLE_INVALID"); }

        var manifest = Path.Combine(bundle, "PackageContents.xml");
        var expectedAssembly = Path.Combine(bundle, "Contents", "Windows", expectedAssemblyFileName);
        if (!Directory.Exists(bundle) || !File.Exists(manifest) || !File.Exists(expectedAssembly) ||
            IsReparse(bundle) || IsReparse(Path.Combine(bundle, "Contents")) ||
            IsReparse(Path.Combine(bundle, "Contents", "Windows")) || IsReparse(expectedAssembly))
            return Failure("CAD_MANUAL_LOAD_BUNDLE_INVALID");

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(manifest, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        { return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID"); }

        var components = document.Descendants().Where(element => element.Name.LocalName == "ComponentEntry").ToArray();
        if (components.Length != 1)
            return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID");
        var component = components[0];
        if (!string.Equals((string?)component.Attribute("AppName"), expectedAppName, StringComparison.Ordinal) ||
            !string.Equals((string?)component.Attribute("LoadOnAutoCADStartup"), "False", StringComparison.Ordinal))
            return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID");

        var moduleName = (string?)component.Attribute("ModuleName");
        if (string.IsNullOrWhiteSpace(moduleName) || Path.IsPathFullyQualified(moduleName))
            return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID");
        string declaredAssembly;
        try { declaredAssembly = Path.GetFullPath(Path.Combine(bundle, moduleName.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID"); }
        if (!PathsEqual(declaredAssembly, expectedAssembly))
            return Failure("CAD_MANUAL_LOAD_MANIFEST_INVALID");

        var otherAdapter = expectedAssemblyFileName == "DwgTranslator.AutoCAD.ReadOnly.dll"
            ? "DwgTranslator.AutoCAD.Write.dll"
            : "DwgTranslator.AutoCAD.ReadOnly.dll";
        if (File.Exists(Path.Combine(bundle, "Contents", "Windows", otherAdapter)))
            return Failure("CAD_MANUAL_LOAD_BUNDLE_MIXED");

        return Results.Success(new CadManualLoadBundle(bundle, expectedAssembly));
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static Result<CadManualLoadBundle> Failure(string code) =>
        Results.Failure<CadManualLoadBundle>(new ContractError(
            code,
            ErrorCategory.Security,
            "The CAD adapter bundle is not eligible for explicit manual loading.",
            false));
}
