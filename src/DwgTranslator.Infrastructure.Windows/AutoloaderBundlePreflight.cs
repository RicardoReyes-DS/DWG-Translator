using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace DwgTranslator.Infrastructure.Windows;

public sealed record AutoloaderPreflightResult(bool IsValid, IReadOnlyList<string> Errors);

public static class AutoloaderBundlePreflight
{
    public static AutoloaderPreflightResult Validate(
        string packageContentsPath,
        string assemblyOutputDirectory,
        string expectedAppName,
        string expectedVersion)
    {
        var errors = new List<string>();
        if (!Path.IsPathFullyQualified(packageContentsPath) || !File.Exists(packageContentsPath))
            return Invalid("AUTOLOADER_MANIFEST_MISSING");
        if (!Path.IsPathFullyQualified(assemblyOutputDirectory) || !Directory.Exists(assemblyOutputDirectory))
            return Invalid("AUTOLOADER_OUTPUT_MISSING");

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(packageContentsPath, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return Invalid("AUTOLOADER_MANIFEST_INVALID");
        }

        var package = document.Root;
        var component = package?.Descendants("ComponentEntry").SingleOrDefault();
        var runtime = package?.Descendants("RuntimeRequirements").SingleOrDefault();
        if (package?.Name.LocalName != "ApplicationPackage" || component is null || runtime is null)
            errors.Add("AUTOLOADER_MANIFEST_SHAPE_INVALID");
        if (!string.Equals((string?)package?.Attribute("AppVersion"), expectedVersion, StringComparison.Ordinal))
            errors.Add("AUTOLOADER_PACKAGE_VERSION_MISMATCH");
        if (!string.Equals((string?)component?.Attribute("AppName"), expectedAppName, StringComparison.Ordinal))
            errors.Add("AUTOLOADER_APP_NAME_MISMATCH");
        if (!string.Equals((string?)component?.Attribute("LoadOnAutoCADStartup"), "False", StringComparison.Ordinal))
            errors.Add("AUTOLOADER_STARTUP_LOAD_NOT_DISABLED");
        if (!string.Equals((string?)runtime?.Attribute("OS"), "Win64", StringComparison.Ordinal) ||
            !string.Equals((string?)runtime?.Attribute("SeriesMin"), "R25.1", StringComparison.Ordinal) ||
            !string.Equals((string?)runtime?.Attribute("SeriesMax"), "R25.1", StringComparison.Ordinal))
            errors.Add("AUTOLOADER_RUNTIME_REQUIREMENTS_INVALID");

        var moduleName = ((string?)component?.Attribute("ModuleName"))?.Replace('/', Path.DirectorySeparatorChar);
        var moduleFile = string.IsNullOrWhiteSpace(moduleName) ? string.Empty : Path.GetFileName(moduleName);
        var modulePath = Path.Combine(assemblyOutputDirectory, moduleFile);
        if (string.IsNullOrWhiteSpace(moduleFile) || !File.Exists(modulePath))
            errors.Add("AUTOLOADER_MODULE_MISSING");
        else
            ValidateAssemblyClosure(modulePath, assemblyOutputDirectory, expectedVersion, errors);

        if (Directory.EnumerateFiles(assemblyOutputDirectory, "*.dll").Any(path =>
                Path.GetFileName(path) is "AcCoreMgd.dll" or "AcDbMgd.dll" or "AcMgd.dll"))
            errors.Add("AUTOLOADER_AUTODESK_DLL_COPY_DETECTED");

        return new(errors.Count == 0, errors);
    }

    private static void ValidateAssemblyClosure(string modulePath, string outputDirectory, string expectedVersion, List<string> errors)
    {
        try
        {
            using var stream = File.OpenRead(modulePath);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                errors.Add("AUTOLOADER_MODULE_NOT_X64");
            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            if (!string.Equals(definition.Version.ToString(3), expectedVersion, StringComparison.Ordinal))
                errors.Add("AUTOLOADER_ASSEMBLY_VERSION_MISMATCH");
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (name.StartsWith("DwgTranslator.", StringComparison.Ordinal) &&
                    !File.Exists(Path.Combine(outputDirectory, name + ".dll")))
                    errors.Add("AUTOLOADER_DEPENDENCY_MISSING:" + name);
            }
        }
        catch (BadImageFormatException)
        {
            errors.Add("AUTOLOADER_MODULE_INVALID_PE");
        }
    }

    private static AutoloaderPreflightResult Invalid(string error) => new(false, new[] { error });
}
