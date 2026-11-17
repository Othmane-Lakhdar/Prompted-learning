using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PromptedLearning.Core;

internal static class MsBuildDefinitions
{
    internal static async Task<string> FingerprintAsync(string preprocessed, MsBuildRequest request, CancellationToken token)
    {
        // Only MSBuild's full-line import locators are normalized. Imported file contents
        // retain their exact bytes; arbitrary XML literals are never globally rewritten.
        var xml = XDocument.Parse(preprocessed, LoadOptions.None);
        var imports = new List<object>();
        var project = Path.GetFullPath(request.ProjectPath);
        var intermediate = Path.GetFullPath(request.IntermediateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var vs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(request.ExecutablePath)!, "../../.."));
        var roots = new[] {
            (Path: vs, Key: "visual-studio"),
            (Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), Key: "dotnet"),
            (Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET"), Key: "windows-dotnet-framework"),
            (Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits"), Key: "windows-kits")
        };
        foreach (var comment in xml.DescendantNodes().OfType<XComment>())
        foreach (var line in comment.Value.Split('\n').Select(s => s.Trim()))
        {
            if (!Path.IsPathFullyQualified(line)) continue;
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(line);
            if (!File.Exists(full)) throw new NotSupportedException("Unresolved preprocessing import locator: " + full);
            string logical, hash;
            if (string.Equals(full, project, StringComparison.OrdinalIgnoreCase))
            {
                logical = "project/" + Path.GetFileName(project); hash = await HashFileAsync(full, token);
            }
            else if (IsWithin(full, intermediate))
            {
                var name = Path.GetFileName(project) + ".nuget.g.";
                if (full != Path.Combine(intermediate, name + "props") && full != Path.Combine(intermediate, name + "targets"))
                    throw new NotSupportedException("Unrecognized generated build import: " + full);
                logical = "restore/" + Path.GetFileName(full);
                hash = GeneratedRestoreFingerprint(await File.ReadAllTextAsync(full, token), full.EndsWith(".props", StringComparison.Ordinal));
            }
            else
            {
                var root = roots.FirstOrDefault(r => IsWithin(full, r.Path));
                if (root.Path is null) throw new NotSupportedException("M1 pilot rejects local/custom build imports: " + full);
                logical = root.Key + "/" + Path.GetRelativePath(root.Path, full).Replace('\\', '/');
                hash = await HashFileAsync(full, token);
            }
            imports.Add(new { logical_path = logical, content_hash = hash });
        }
        if (imports.Count < 2) throw new InvalidDataException("Preprocessing supplied no auditable import closure.");
        return CanonicalJson.Sha256(CanonicalJson.Serialize(imports));
    }

    private static string GeneratedRestoreFingerprint(string text, bool props)
    {
        const string ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var actual = XDocument.Parse(text);
        var userPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages").TrimEnd('\\');
        var sharedPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Shared", "NuGetPackages").TrimEnd('\\');
        if (props)
        {
            var folders = actual.Descendants(XName.Get("NuGetPackageFolders", ns)).SingleOrDefault();
            if (folders?.Value != userPackages + "\\;" + sharedPackages) throw new NotSupportedException("Unexpected NuGet package roots in frozen pilot restore.");
            folders.Value = "<user-packages>;<shared-packages>";
            var sourceRoots = actual.Descendants(XName.Get("SourceRoot", ns)).ToArray();
            if (sourceRoots.Length != 2 || (string?)sourceRoots[0].Attribute("Include") != userPackages + "\\" || (string?)sourceRoots[1].Attribute("Include") != sharedPackages + "\\")
                throw new NotSupportedException("Unexpected restore source roots.");
            sourceRoots[0].SetAttributeValue("Include", "<user-packages>"); sourceRoots[1].SetAttributeValue("Include", "<shared-packages>");
        }
        var expected = XDocument.Parse(props ? """
            <Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
                <RestoreSuccess Condition=" '$(RestoreSuccess)' == '' ">True</RestoreSuccess>
                <RestoreTool Condition=" '$(RestoreTool)' == '' ">NuGet</RestoreTool>
                <ProjectAssetsFile Condition=" '$(ProjectAssetsFile)' == '' ">$(MSBuildThisFileDirectory)project.assets.json</ProjectAssetsFile>
                <NuGetPackageRoot Condition=" '$(NuGetPackageRoot)' == '' ">$(UserProfile)\.nuget\packages\</NuGetPackageRoot>
                <NuGetPackageFolders Condition=" '$(NuGetPackageFolders)' == '' ">&lt;user-packages&gt;;&lt;shared-packages&gt;</NuGetPackageFolders>
                <NuGetProjectStyle Condition=" '$(NuGetProjectStyle)' == '' ">PackageReference</NuGetProjectStyle>
                <NuGetToolVersion Condition=" '$(NuGetToolVersion)' == '' ">6.14.1</NuGetToolVersion>
              </PropertyGroup>
              <ItemGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
                <SourceRoot Include="&lt;user-packages&gt;" />
                <SourceRoot Include="&lt;shared-packages&gt;" />
              </ItemGroup>
            </Project>
            """ : "<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />");
        if (!XNode.DeepEquals(actual.Root, expected.Root)) throw new NotSupportedException("Generated NuGet import is outside the exact no-package pilot shape.");
        return CanonicalJson.Sha256(Encoding.UTF8.GetBytes(actual.Root!.ToString(SaveOptions.DisableFormatting)));
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
