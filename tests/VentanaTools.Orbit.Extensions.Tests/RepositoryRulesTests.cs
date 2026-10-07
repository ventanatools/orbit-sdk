// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>Every shared fixture is pinned by SHA-256 over its LF bytes (raw bytes for byte-exact fixtures).</summary>
public sealed class FixturePinTests
{
    public static TheoryData<string> FixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Fixtures.All())
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void TheSharedFixture_MatchesItsPin(string path)
    {
        var pins = Pins();
        if (Fixtures.Update)
        {
            return;
        }

        Assert.True(pins.TryGetValue(path, out var pin), "The fixture " + path + " has no pin in FixturePins.txt.");
        Assert.Equal(pin.Mode, Mode(path));
        Assert.Equal(pin.Hash, Hash(path));
    }

    [Fact]
    public void EveryPinNamesAFixtureAndTextFixturesAreUtf8WithoutAByteOrderMark()
    {
        var all = Fixtures.All();
        if (Fixtures.Update)
        {
            var lines = all.Select(path => Hash(path) + " " + Mode(path) + " " + path);
            var target = Path.Combine(Fixtures.Root, "tests", typeof(FixturePinTests).Assembly.GetName().Name!, "FixturePins.txt");
            File.WriteAllText(target, string.Join('\n', lines) + "\n");
            return;
        }

        Assert.Equal(all, Pins().Keys.Order(StringComparer.Ordinal));
        foreach (var path in all.Where(path => Mode(path) == "text"))
        {
            var bytes = Fixtures.Bytes(path);
            Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), path);
            Assert.True(JsonTree.IsStrictUtf8(bytes), path);
        }
    }

    [Fact]
    public void OnlyTheRegistriesNameAProduct()
    {
        foreach (var path in Fixtures.All().Where(path => path is not ("hosts.json" or "reserved-publishers.json")))
        {
            var text = Encoding.Latin1.GetString(Fixtures.Bytes(path));
            if (path.EndsWith("utf16.json", StringComparison.Ordinal))
            {
                text = Encoding.Unicode.GetString(Fixtures.Bytes(path));
            }

            Assert.True(ProductNames.Find(text) is null, path + " names " + ProductNames.Find(text));
        }
    }

    /// <summary>Byte-exact fixtures, which line-ending normalization must not touch.</summary>
    private static string Mode(string path)
    {
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            return "raw";
        }

        var exact = File.ReadAllLines(Path.Combine(Fixtures.Directory, ".gitattributes"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && !parts[0].Contains('*', StringComparison.Ordinal))
            .Select(parts => parts[0]);
        return exact.Contains(path, StringComparer.Ordinal) ? "raw" : "text";
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Mode(path) == "raw" ? Fixtures.Bytes(path) : Fixtures.Text(path)));

    private static Dictionary<string, (string Hash, string Mode)> Pins()
    {
        using var stream = typeof(FixturePinTests).Assembly.GetManifestResourceStream("FixturePins.txt")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pins = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', 3);
            pins.Add(parts[2], (parts[0], parts[1]));
        }

        return pins;
    }
}

/// <summary>No public type of the packable libraries is a positional record or has a primary constructor (contract §9).</summary>
public sealed class PublicShapeTests
{
    public static TheoryData<string> PublicTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(ExtensionManifest).Assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PublicTypes))]
    public void NoPublicTypeIsAPositionalRecordOrHasAPrimaryConstructor(string name)
    {
        var type = typeof(ExtensionManifest).Assembly.GetType(name)!;
        Assert.Null(type.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance));
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            Assert.False(parameters.Length > 0 && parameters.All(parameter => properties.Contains(parameter.Name!)),
                name + " has a public constructor whose parameters mirror its properties.");
        }

        Assert.DoesNotContain(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => Regex.IsMatch(field.Name, "^<.+>P$"));
    }

    [Theory]
    [InlineData(typeof(FacePicture))]
    [InlineData(typeof(FaceLine))]
    public void TheFacePartHierarchiesAreClosed(Type root)
    {
        // New variants arrive with capabilities (contract §9.1), so no other assembly may derive one.
        Assert.True(root.IsAbstract);
        foreach (var type in root.Assembly.GetExportedTypes().Where(root.IsAssignableFrom))
        {
            Assert.True(type == root || type.IsSealed, type + " is not sealed.");
            foreach (var constructor in type.IsSealed ? [] : type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(constructor.IsPrivate || constructor.IsAssembly || constructor.IsFamilyAndAssembly,
                    type + " has a constructor another assembly can call: " + constructor);
            }
        }
    }

    [Fact]
    public void CodeOutsideTheLibraryCannotDeriveAFacePart()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(FaceLine).Assembly.Location))
            .ToArray();
        Assert.Empty(Errors("var line = (FaceLine)\"4:59\"; var picture = FacePicture.Glyph(\"\\uE916\"); System.Console.WriteLine(line == new TextLine { Text = \"4:59\" });"));
        Assert.Contains("CS8864", Errors("sealed record MyLine : FaceLine { public MyLine() : base((FaceLine)\"x\"!) { } }"));
        Assert.Contains("CS0122", Errors("sealed class MyLine : FaceLine { public override bool Equals(FaceLine? other) => false; public override int GetHashCode() => 0; }"));
        Assert.Contains("CS0122", Errors("sealed class MyPicture : FacePicture { public override bool Equals(FacePicture? other) => false; public override int GetHashCode() => 0; }"));

        string[] Errors(string source)
        {
            var tree = CSharpSyntaxTree.ParseText("using VentanaTools.Orbit.Extensions;\n" + source, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create("Outside", [tree], references, new CSharpCompilationOptions(
                OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
            return compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Id).ToArray();
        }
    }

    [Fact]
    public void FacePartsCompareByValue()
    {
        FaceLine? converted = "4:59";
        Assert.Equal(converted, new TextLine { Text = "4:59" });
        Assert.True(converted == new TextLine { Text = "4:59" });
        Assert.True(converted != new TextLine { Text = "5:00" });
        Assert.Equal(new TextLine { Text = "4:59" }.GetHashCode(), converted!.GetHashCode());
        Assert.Equal(FacePicture.Glyph("\uE916"), new GlyphPicture { Glyph = "\uE916" });
        Assert.NotEqual(FacePicture.Glyph("\uE916"), FacePicture.None);
        Assert.Equal(FacePicture.None, new NoPicture());
        Assert.Equal(new Face { Line1 = "a", Picture = FacePicture.Glyph("\uE916"), GoodFor = TimeSpan.FromSeconds(1) },
            new Face { Line1 = "a", Picture = FacePicture.Glyph("\uE916"), GoodFor = TimeSpan.FromSeconds(1) });
        Assert.Equal("TextLine { Text = 4:59 }", converted.ToString());
    }

    [Fact]
    public void TheNodePackageCarriesTheRepositorysLicenceAndNotice()
    {
        // Apache-2.0 section 4: the licence and the NOTICE travel with every redistribution, the npm tarball included.
        var node = Directory.EnumerateDirectories(Path.Combine(Fixtures.Root, "node")).Single(folder => File.Exists(Path.Combine(folder, "package.json")));
        foreach (var name in new[] { "LICENSE", "NOTICE" })
        {
            Assert.Equal(Lf(File.ReadAllBytes(Path.Combine(Fixtures.Root, name))), Lf(File.ReadAllBytes(Path.Combine(node, name))));
            Assert.Contains("\"" + name + "\"", File.ReadAllText(Path.Combine(node, "package.json")), StringComparison.Ordinal);
        }

        static byte[] Lf(byte[] bytes) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShapeRuleCatchesPositionalRecordsAndPrimaryConstructors()
    {
        Assert.NotNull(typeof(PositionalSample).GetMethod("Deconstruct"));
        Assert.Contains(typeof(PrimarySample).GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => Regex.IsMatch(field.Name, "^<.+>P$"));
    }

    [Fact]
    public void TheLibraryIsPortableAndAotCompatible()
    {
        var assembly = typeof(ExtensionManifest).Assembly;
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == "netstandard", reference.Name));
        Assert.Contains(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), attribute => attribute.Key == "IsTrimmable" && attribute.Value == "True");
        var family = assembly.GetName().Name!;
        Assert.Equal([family, family + ".Packaging", family + ".Wire"],
            assembly.GetExportedTypes().Select(type => type.Namespace!).Distinct().Order(StringComparer.Ordinal));
    }

    private sealed record PositionalSample(int Value);

    private sealed class PrimarySample(int value)
    {
        public int Twice() => value * 2;
    }
}

/// <summary>The product-name rule (contract §2.1), enforced with the exact rule of contract §2.8.</summary>
public sealed class ProductNameConfinementTests
{
    [Fact]
    public void NoCSharpSourceUnderSrcNamesAProduct()
    {
        var failures = new List<string>();
        foreach (var path in TrackedFiles("src").Where(path => path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            failures.AddRange(CheckCSharp(path, File.ReadAllText(Path.Combine(Fixtures.Root, path))));
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void NoOtherFileUnderSrcNamesAProductOutsideTheAllowedPlaces()
    {
        var failures = new List<string>();
        foreach (var path in TrackedFiles("src").Where(path => !path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            if (Path.GetFileName(path) == "README.md")
            {
                continue; // Package readmes are display text.
            }

            var text = File.ReadAllText(Path.Combine(Fixtures.Root, path));
            var stripped = StripAllowed(text, allowToolCommand: path.EndsWith(".csproj", StringComparison.Ordinal));
            if (ProductNames.Find(stripped) is { } product)
            {
                failures.Add(path + " names " + product);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void TheNodeSdkNamesAProductOnlyInItsAllowedPlaces()
    {
        var folder = "node/" + Segment().ToLowerInvariant() + "-extensions/";
        var failures = new List<string>();
        foreach (var path in TrackedFiles("node").Where(path => path.StartsWith(folder, StringComparison.Ordinal)))
        {
            var relative = path[folder.Length..];
            // NOTICE is the repository's NOTICE, which names the products (TheNodePackageCarriesTheRepositorysLicenceAndNotice).
            if (relative is "lib/hosts.json" or "README.md" or "NOTICE")
            {
                continue;
            }

            var text = File.ReadAllText(Path.Combine(Fixtures.Root, path));
            var stripped = Regex.Replace(text, "@ventanatools/" + Regex.Escape(Segment().ToLowerInvariant()) + "-extensions(/[A-Za-z0-9._/-]*)?", string.Empty);
            if (ProductNames.Find(stripped) is { } product)
            {
                failures.Add(path + " names " + product);
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("namespace X; class C { string s = \"orbit\"; }", true)]
    [InlineData("namespace X; class C { } // Orbit", true)]
    [InlineData("namespace X; class OrbitHost { }", true)]
    [InlineData("namespace X; class C { char c = 'o'; string s = $\"{c}rbit\"; }", false)]
    [InlineData("namespace X; class C { string s = $\"Pinwheel {1}\"; }", true)]
    [InlineData("namespace X; class C { string s = \"\"\"lollipop\"\"\"; }", true)]
    [InlineData("namespace X; class C { byte[] b = \"x.orbitextension\"u8.ToArray(); }", true)]
    [InlineData("namespace X; /// <summary>An Orbit thing.</summary> class C { }", true)]
    [InlineData("namespace X; class C { string s = @\"ORBIT\"; }", true)]
    [InlineData("#region Orbit\nnamespace X; class C { }\n#endregion", true)]
    [InlineData("using VentanaTools.Orbit.Extensions;\nnamespace VentanaTools.Orbit.Extensions.Wire; class C { global::VentanaTools.Orbit.Extensions.Wire.C? c; }", false)]
    [InlineData("using Alias = VentanaTools.Orbit.Extensions.Wire;\nnamespace X; class C { }", false)]
    [InlineData("namespace X; class C { int Orbit = 1; }", true)]
    [InlineData("namespace X; class C { object o = VentanaTools.Orbit.Other; }", true)]
    public void TheRuleFindsProductNamesInEveryKindOfToken(string source, bool fails)
    {
        var text = source.Replace("VentanaTools.Orbit.Extensions", Family(), StringComparison.Ordinal);
        Assert.Equal(fails, CheckCSharp("sample.cs", text).Count > 0);
    }

    [Fact]
    public void TheRuleReadsTheProductNamesFromTheRegistry()
    {
        var names = ProductNames.All();
        Assert.Contains(HostRegistry.Known[0].Id, names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(HostRegistry.Known[0].DisplayName, names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(HostRegistry.Known[0].PackageExtension!.TrimStart('.'), names, StringComparer.OrdinalIgnoreCase);
        Assert.All(HostRegistry.ReservedIds, id => Assert.Contains(id, names, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(Segment(), names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("VentanaTools", Family().Split('.')[0]);
        Assert.Equal("Extensions", Family().Split('.')[2]);
    }

    internal static List<string> CheckCSharp(string path, string text)
    {
        var failures = new List<string>();
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse));
        var root = tree.GetRoot();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                if (ProductNames.Contains(token.ValueText) && !IsFamilySegment(token))
                {
                    failures.Add(path + ": identifier " + token.ValueText);
                }

                continue;
            }

            if (IsLiteral(token) && ProductNames.Contains(token.Text))
            {
                failures.Add(path + ": literal at " + token.GetLocation().GetLineSpan().StartLinePosition);
            }

            if (token.Parent is XmlTextSyntax or XmlTextAttributeSyntax && ProductNames.Contains(token.Text))
            {
                failures.Add(path + ": documentation at " + token.GetLocation().GetLineSpan().StartLinePosition);
            }
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                continue;
            }

            var triviaText = trivia.HasStructure ? string.Empty : trivia.ToString();
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                triviaText = trivia.ToFullString();
            }

            if (ProductNames.Contains(StripFamily(triviaText)))
            {
                failures.Add(path + ": comment or directive at " + trivia.GetLocation().GetLineSpan().StartLinePosition);
            }
        }

        return failures;
    }

    private static bool IsLiteral(SyntaxToken token) => token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
        or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.InterpolatedRawStringEndToken
        or SyntaxKind.XmlTextLiteralToken or SyntaxKind.XmlEntityLiteralToken;

    /// <summary>The <c>Orbit</c> of <c>VentanaTools.Orbit.Extensions</c> in a namespace, using directive, alias or qualified name.</summary>
    private static bool IsFamilySegment(SyntaxToken token)
    {
        var parts = Family().Split('.');
        if (!string.Equals(token.ValueText, parts[1], StringComparison.Ordinal))
        {
            return false;
        }

        var before = token.GetPreviousToken();
        var first = before.GetPreviousToken();
        var after = token.GetNextToken();
        var last = after.GetNextToken();
        return before.IsKind(SyntaxKind.DotToken) && first.ValueText == parts[0]
            && after.IsKind(SyntaxKind.DotToken) && last.ValueText == parts[2]
            && token.Parent is IdentifierNameSyntax { Parent: QualifiedNameSyntax or MemberAccessExpressionSyntax };
    }

    private static string StripFamily(string text) => text.Replace(Family(), string.Empty, StringComparison.Ordinal);

    private static string StripAllowed(string text, bool allowToolCommand)
    {
        var segment = Segment();
        var stripped = Regex.Replace(text, Regex.Escape(Family()) + "[A-Za-z0-9.]*", string.Empty);
        stripped = Regex.Replace(stripped, "@ventanatools/" + Regex.Escape(segment.ToLowerInvariant()) + "-extensions(/[A-Za-z0-9._/-]*)?", string.Empty);
        stripped = Regex.Replace(stripped, "ventanatools-" + Regex.Escape(segment.ToLowerInvariant()) + "-extensions-[0-9A-Za-z.+-]*\\.tgz", string.Empty);
        stripped = Regex.Replace(stripped, "<Description>[^<]*</Description>", string.Empty);
        if (allowToolCommand)
        {
            stripped = Regex.Replace(stripped, "<ToolCommandName>[^<]*</ToolCommandName>", string.Empty);
        }

        return stripped;
    }

    /// <summary>The package family name, from the author package's assembly name; never a literal.</summary>
    private static string Family() => typeof(ExtensionManifest).Assembly.GetName().Name!;

    private static string Segment() => Family().Split('.')[1];

    private static List<string> TrackedFiles(string folder)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", ["-C", Fixtures.Root, "ls-files", "-z", "--", folder])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode == 0)
            {
                return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => File.Exists(Path.Combine(Fixtures.Root, path))).ToList();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git: fall back to the files on disk.
        }

        var root = Path.Combine(Fixtures.Root, folder);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Fixtures.Root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "node_modules"))
            .ToList();
    }
}
