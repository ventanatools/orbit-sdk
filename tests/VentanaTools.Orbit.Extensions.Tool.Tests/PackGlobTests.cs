// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>The copy rule globs of contract §11.3.</summary>
public sealed class PackGlobTests
{
    /// <summary>The compatibility check's tree: folders whose names look like files, repeat at several depths or differ only in case.</summary>
    private static readonly string[] Tree =
    [
        "a.js", "b.txt", ".hidden", "LICENSE", "src/a1.js", "src/a2.js", "src/b.js", "src/ab.js", "src/lib/c.js", "src/lib/deep/d.js",
        "src/lib/deep/e.txt", "docs/readme.md", "docs/src/x.js", "node_modules/m/index.js", "x/y/z/w.js", "x/w.js", "lib/q.js",
        "a/b/a/b/f.js", "a/b.js", "a/a/b.js", "s.js/inner.txt", "b/a/b/a/g.js", "a/x/a/x/a/h.js", "Src/Lib/K.JS", "q/src", "q/src.js",
        "ab/b.js", "a/b/a/b/a/b/i.js", "b/b/b/j.js", "x/a/b/x/a/b/k.txt", "deep/a.js", "lib/deep/lib/l.js",
    ];

    [Theory]
    [InlineData("src/a?.js", "src/a1.js src/a2.js")]
    [InlineData("src/?.js", "src/b.js")]
    [InlineData("src/??.js", "src/a1.js src/a2.js")]
    [InlineData("src/???.js", "src/ab1.js")]
    [InlineData("?.js", "a.js")]
    [InlineData("**/?.js", "a.js lib/a/b.js src/b.js sxc/z.js")]
    [InlineData("lib/?/b.js", "lib/a/b.js")]
    [InlineData("s?c/*.js", "src/a1.js src/a2.js src/ab1.js src/b.js sxc/z.js")]
    [InlineData("S?C/A?.JS", "src/a1.js src/a2.js")]
    public void AQuestionMarkMatchesExactlyOneCharacterOfAName(string glob, string expected)
    {
        string[] files = ["a.js", "ab.js", "src/a1.js", "src/a2.js", "src/b.js", "src/ab1.js", "sxc/z.js", "lib/a/b.js"];
        Assert.Equal(expected, Select([glob], [], files));
    }

    [Theory]
    [InlineData("src", "")]
    [InlineData("**/src", "q/src")]
    [InlineData("src/", "src/a1.js src/b.js src/lib/c.js src/lib/deep/d.txt")]
    [InlineData("src/**", "src/a1.js src/b.js src/lib/c.js src/lib/deep/d.txt")]
    [InlineData("src/*", "src/a1.js src/b.js")]
    [InlineData("/src/*.js", "src/a1.js src/b.js")]
    [InlineData("./src/./*.js", "src/a1.js src/b.js")]
    [InlineData("src//b.js", "src/b.js")]
    [InlineData(@"src\lib\*.js", "src/lib/c.js")]
    [InlineData("SRC/LIB/*.JS", "src/lib/c.js")]
    [InlineData("x/**/w.js", "x/w.js x/y/z/w.js")]
    [InlineData("*.*", ".hidden LICENSE a.js b.txt")]
    [InlineData("**.txt", "b.txt s.js/inner.txt src/lib/deep/d.txt")]
    [InlineData("src/a**.js", "src/a1.js")]
    [InlineData("*.js/", "s.js/inner.txt")]
    [InlineData("**/src/", "docs/src/x.js src/a1.js src/b.js src/lib/c.js src/lib/deep/d.txt")]
    public void GlobsKeepTheirMeaning(string glob, string expected)
    {
        string[] files = ["a.js", "b.txt", ".hidden", "LICENSE", "src/a1.js", "src/b.js", "src/lib/c.js", "src/lib/deep/d.txt", "docs/src/x.js",
            "x/w.js", "x/y/z/w.js", "s.js/inner.txt", "q/src"];
        Assert.Equal(expected, Select([glob], [], files));
    }

    [Theory]
    [InlineData("src", "a.js")]
    [InlineData("src/", "a.js")]
    [InlineData("src/**", "a.js")]
    [InlineData("src/*", "a.js")]
    [InlineData("**/lib", "a.js src/a1.js src/b.js")]
    [InlineData("*.js", "src/a1.js src/b.js src/lib/c.js src/lib/deep/d.js")]
    [InlineData("src/a?.js", "a.js src/b.js src/lib/c.js src/lib/deep/d.js")]
    [InlineData("**/dee?/", "a.js src/a1.js src/b.js src/lib/c.js")]
    public void AnExcludeGlobLeavesOutFilesAndFoldersWithEverythingInThem(string exclude, string kept)
    {
        string[] files = ["a.js", "src/a1.js", "src/b.js", "src/lib/c.js", "src/lib/deep/d.js"];
        Assert.Equal(kept, Select(["**/*"], [exclude], files));
    }

    [Theory]
    [InlineData("src/*.js", "src", true)]
    [InlineData("src/*.js", "src/lib", false)]
    [InlineData("src/*.js", "lib", false)]
    [InlineData("uxp/*.js", "uxp", true)]
    [InlineData("main.js", "uxp", false)]
    [InlineData("**/*.js", "docs", true)]
    [InlineData("src/", "src/lib/deep", true)]
    [InlineData("s?c/lib/*", "src", true)]
    [InlineData("s?c/lib/*", "src/lib", true)]
    [InlineData("s?c/lib/*", "src/lib/deep", false)]
    public void AnIncludeGlobReachesIntoTheFoldersItCouldSelectSomethingIn(string glob, string folder, bool reaches) =>
        Assert.Equal(reaches, PackGlob.Parse(glob).CouldMatchBelow(folder.Split('/')));

    [Theory]
    [InlineData("node_modules/**", "node_modules", true)]
    [InlineData("node_modules/", "node_modules/sdk", true)]
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("node_modules/*", "node_modules", false)]
    [InlineData("node_modules/*", "node_modules/sdk", true)]
    [InlineData("**/*.js", "s.js", true)]
    [InlineData("src/**/*.js", "src", false)]
    [InlineData("vendor/??", "vendor/ab", true)]
    public void AnExcludeGlobLeavesOutAFolderItMatchesOrEndsBelow(string glob, string folder, bool leavesOut) =>
        Assert.Equal(leavesOut, PackGlob.Parse(glob).LeavesOutFolder(folder.Split('/')));

    [Theory]
    [InlineData("a/../*.js")]
    [InlineData("vendor/../vendor/*.tgz")]
    [InlineData("../*.txt")]
    [InlineData(@"src\..\*.js")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./.")]
    public void AGlobThatLeavesItsFolderOrNamesNothingIsRefused(string glob) => Assert.False(PackGlob.TryParse(glob, out _));

    [Theory]
    [InlineData("..a/*.js")]
    [InlineData("a../*.js")]
    [InlineData("./")]
    [InlineData("**")]
    public void GlobsThatOnlyLookLikeThoseAreRead(string glob) => Assert.True(PackGlob.TryParse(glob, out _));

    /// <summary>
    /// Earlier tool versions matched copy rules with Microsoft.Extensions.FileSystemGlobbing. Every
    /// glob it read selects the same files now, except a glob of only <c>.</c> segments, which it
    /// read as selecting nothing and which is now refused.
    /// </summary>
    [Fact]
    public void GlobsSelectWhatFileSystemGlobbingSelected()
    {
        var random = new Random(20261006);
        string[] parts = ["**", "*", "a", "b", "src", "lib", "*.js", "**.js", "*.*", "**.*", ".", "a*", "*b", "a**b", "**b", "***", "x", "deep", "Src"];
        var globs = new SortedSet<string>(StringComparer.Ordinal);
        while (globs.Count < 4_000)
        {
            var glob = string.Join('/', Enumerable.Range(0, random.Next(1, 6)).Select(_ => parts[random.Next(parts.Length)]));
            globs.Add(random.Next(5) switch { 0 => "/" + glob, 1 => glob + "/", _ => glob });
        }

        var compared = 0;
        foreach (var glob in globs.Where(glob => glob.Split('/').Any(segment => segment is not ("" or "."))))
        {
            Assert.True(Select([glob], []) == FileSystemGlobbing([glob], []), "include " + glob);
            Assert.True(Select(["**/*"], [glob]) == FileSystemGlobbing(["**/*"], [glob]), "exclude " + glob);
            compared++;
        }

        Assert.True(compared > 3_500, compared.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The files of <paramref name="files"/> a copy rule with these globs selects, as the walk decides it.</summary>
    private static string Select(string[] include, string[] exclude, string[]? files = null)
    {
        var includes = include.Select(PackGlob.Parse).ToList();
        var excludes = exclude.Select(PackGlob.Parse).ToList();
        var selected = new List<string>();
        foreach (var file in files ?? Tree)
        {
            var names = file.Split('/');
            var reached = true;
            for (var depth = 1; depth < names.Length && reached; depth++)
            {
                var folder = names[..depth];
                reached = !excludes.Exists(glob => glob.LeavesOutFolder(folder)) && includes.Exists(glob => glob.CouldMatchBelow(folder));
            }

            if (reached && includes.Exists(glob => glob.Matches(names)) && !excludes.Exists(glob => glob.Matches(names)))
            {
                selected.Add(file);
            }
        }

        return string.Join(' ', selected.Order(StringComparer.Ordinal));
    }

    private static string FileSystemGlobbing(string[] include, string[] exclude)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(include);
        matcher.AddExcludePatterns(exclude);
        var root = Path.Combine(Path.GetTempPath(), "ventana-glob-tree");
        var result = matcher.Execute(new InMemoryDirectoryInfo(root, Tree.Select(file => Path.Combine(root, file))));
        return string.Join(' ', result.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }
}
