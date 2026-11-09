using System.Text.Json;
using PSXDownloadHelper.Modern;
using Xunit;

namespace PSXDownloadHelper.Modern.Tests;

public sealed class RuleStoreTests
{
    [Fact]
    public void CdnStoreCreatesEmptyConfigurationAndRoundTripsRules()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "cdn-rules.json");
        var store = new CdnRuleStore(path);

        Assert.True(File.Exists(path));
        Assert.Empty(store.Load());

        store.Save([
            new CdnRule
            {
                Name = "  Assets CDN  ",
                MatchHost = "CDN.EXAMPLE.COM",
                TargetHost = "EDGE.EXAMPLE.NET",
                MatchPathPrefix = "assets",
                TargetPathPrefix = "mirror",
                Priority = 25
            }
        ]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal("Assets CDN", loaded.Name);
        Assert.Equal("cdn.example.com", loaded.MatchHost);
        Assert.Equal("/assets", loaded.MatchPathPrefix);
        Assert.Equal("/mirror", loaded.TargetPathPrefix);
    }

    [Fact]
    public void CdnRewriteUsesHighestPriorityAndPreservesQueryAndPort()
    {
        using var temp = new TemporaryDirectory();
        var store = new CdnRuleStore(Path.Combine(temp.Path, "cdn.json"));
        store.Save([
            Cdn("Fallback", "cdn.example.com", "low.example.net", "/", "/low", 10),
            Cdn("Assets", "cdn.example.com", "fast.example.net", "/assets", "/v2", 100)
        ]);

        var rewritten = store.Rewrite(
            new Uri("http://cdn.example.com:8080/assets/game.pkg?region=eu"), out var name);

        Assert.Equal("High", name);
        Assert.Equal("fast.example.net", rewritten.Host);
        Assert.Equal(8080, rewritten.Port);
        Assert.Equal("/v2/game.pkg", rewritten.AbsolutePath);
        Assert.Equal("?region=eu", rewritten.Query);
    }

    [Theory]
    [InlineData("http://cdn.example.com/assets")]
    [InlineData("http://cdn.example.com/assets/game.pkg")]
    public void CdnPathPrefixMatchesExactPathAndDescendants(string value)
    {
        var rule = Cdn("Assets", "cdn.example.com", "edge.example.net", "/assets", "/mirror", 1)
            .NormalizeAndValidate();

        Assert.True(rule.IsMatch(new Uri(value)));
    }

    [Theory]
    [InlineData("http://cdn.example.com/assets-old")]
    [InlineData("http://cdn.example.com/asset")]
    [InlineData("http://other.example.com/assets")]
    public void CdnPathAndHostMatchingDoesNotUseLooseSuffixes(string value)
    {
        var rule = Cdn("Assets", "cdn.example.com", "edge.example.net", "/assets", "/mirror", 1)
            .NormalizeAndValidate();

        Assert.False(rule.IsMatch(new Uri(value)));
    }

    [Fact]
    public void CdnWildcardMatchesSubdomainsButNotApexOrLookalikes()
    {
        var rule = Cdn("Wildcard", "*.example.com", "edge.example.net", "/", "/", 1)
            .NormalizeAndValidate();

        Assert.True(rule.IsMatch(new Uri("http://cdn.example.com/file.pkg")));
        Assert.False(rule.IsMatch(new Uri("http://example.com/file.pkg")));
        Assert.False(rule.IsMatch(new Uri("http://notexample.com/file.pkg")));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/assets/../secret")]
    [InlineData(@"\assets\secret")]
    public void CdnRejectsTraversalAndBackslashes(string prefix)
    {
        var rule = Cdn("Invalid", "cdn.example.com", "edge.example.net", prefix, "/target", 1);

        Assert.Throws<InvalidDataException>(() => rule.NormalizeAndValidate());
    }

    [Fact]
    public void CdnSaveRejectsDuplicateHostAndPathRules()
    {
        using var temp = new TemporaryDirectory();
        var store = new CdnRuleStore(Path.Combine(temp.Path, "cdn.json"));

        var error = Assert.Throws<InvalidDataException>(() => store.Save([
            Cdn("First", "CDN.EXAMPLE.COM", "one.example.net", "/assets", "/", 1),
            Cdn("Second", "cdn.example.com", "two.example.net", "/assets/", "/", 2)
        ]));

        Assert.Contains("Duplicate CDN rule", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineStoreCreatesConfigurationAndRoundTripsHeaders()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "offline.json");
        var store = new OfflineUrlStore(path);

        Assert.True(File.Exists(path));
        store.Save([
            Offline("Mirror", "http://download.example.com/game.pkg?region=eu",
                "http://mirror.example.net/game.pkg?region=eu", 10, "download.example.com", "session=abc")
        ]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal("download.example.com", loaded.HostHeader);
        Assert.Equal("session=abc", loaded.Cookie);
    }

    [Fact]
    public void OfflineRewriteUsesHighestPriorityExactUrl()
    {
        using var temp = new TemporaryDirectory();
        var store = new OfflineUrlStore(Path.Combine(temp.Path, "offline.json"));
        store.Save([
            Offline("Exact mirror", "http://download.example.com/game.pkg?region=eu",
                "http://high.example.net/files/game.pkg?mirror=1", 100),
            Offline("Unrelated higher priority", "http://download.example.com/other.pkg",
                "http://disabled.example.net/other.pkg", 1000, enabled: false)
        ]);

        var matched = store.TryRewrite(
            new Uri("http://DOWNLOAD.example.com/game.pkg?region=eu"),
            out var rule, out var target);

        Assert.True(matched);
        Assert.Equal("Exact mirror", rule?.Name);
        Assert.Equal("high.example.net", target.Host);
        Assert.Equal("/files/game.pkg", target.AbsolutePath);
        Assert.Equal("?mirror=1", target.Query);
    }

    [Theory]
    [InlineData("http://download.example.com/Game.pkg?region=eu")]
    [InlineData("http://download.example.com/game.pkg?region=us")]
    [InlineData("http://download.example.com/game.pkg")]
    [InlineData("https://download.example.com/game.pkg?region=eu")]
    [InlineData("http://download.example.com:8080/game.pkg?region=eu")]
    public void OfflineMappingRequiresExactSchemePortPathAndQuery(string value)
    {
        var rule = Offline("Exact", "http://download.example.com/game.pkg?region=eu",
            "http://mirror.example.net/game.pkg", 1).NormalizeAndValidate();

        Assert.False(rule.IsMatch(new Uri(value)));
    }

    [Fact]
    public void OfflineDisabledRuleIsNotApplied()
    {
        using var temp = new TemporaryDirectory();
        var store = new OfflineUrlStore(Path.Combine(temp.Path, "offline.json"));
        store.Save([
            Offline("Disabled", "http://download.example.com/game.pkg",
                "http://mirror.example.net/game.pkg", 100, enabled: false)
        ]);

        var matched = store.TryRewrite(new Uri("http://download.example.com/game.pkg"),
            out var rule, out var target);

        Assert.False(matched);
        Assert.Null(rule);
        Assert.Equal("download.example.com", target.Host);
    }

    [Fact]
    public void OfflineDuplicateSourceUrlsAreRejectedIgnoringHostCase()
    {
        using var temp = new TemporaryDirectory();
        var store = new OfflineUrlStore(Path.Combine(temp.Path, "offline.json"));

        var error = Assert.Throws<InvalidDataException>(() => store.Save([
            Offline("First", "http://download.example.com/game.pkg", "http://one.example.net/a", 1),
            Offline("Second", "http://DOWNLOAD.example.com/game.pkg", "http://two.example.net/a", 2)
        ]));

        Assert.Contains("Duplicate source URL", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://download.example.com/game.pkg", "http://mirror.example.net/game.pkg", "", "")]
    [InlineData("http://user:pass@download.example.com/game.pkg", "http://mirror.example.net/game.pkg", "", "")]
    [InlineData("http://download.example.com/game.pkg#fragment", "http://mirror.example.net/game.pkg", "", "")]
    [InlineData("http://download.example.com/game.pkg", "http://mirror.example.net/game.pkg#fragment", "", "")]
    [InlineData("http://download.example.com/game.pkg", "http://mirror.example.net/game.pkg", "mirror.example.net\r\nX-Evil: yes", "")]
    [InlineData("http://download.example.com/game.pkg", "http://mirror.example.net/game.pkg", "", "session=abc\r\nX-Evil: yes")]
    public void OfflineValidationRejectsUnsafeUrlsAndHeaders(
        string source, string target, string hostHeader, string cookie)
    {
        var rule = Offline("Invalid", source, target, 1, hostHeader, cookie);

        Assert.Throws<InvalidDataException>(() => rule.NormalizeAndValidate());
    }

    [Fact]
    public void OfflineLoadSkipsInvalidEntriesWithoutLosingValidOnes()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "offline.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            Offline("Valid", "http://download.example.com/a.pkg", "http://mirror.example.net/a.pkg", 1),
            Offline("Invalid", "https://download.example.com/b.pkg", "http://mirror.example.net/b.pkg", 2)
        }));

        var store = new OfflineUrlStore(path);

        Assert.Equal("Valid", Assert.Single(store.Load()).Name);
    }

    private static CdnRule Cdn(
        string name, string matchHost, string targetHost,
        string matchPrefix, string targetPrefix, int priority) => new()
    {
        Name = name,
        MatchHost = matchHost,
        TargetHost = targetHost,
        MatchPathPrefix = matchPrefix,
        TargetPathPrefix = targetPrefix,
        Priority = priority
    };

    private static OfflineUrlRule Offline(
        string name, string source, string target, int priority,
        string hostHeader = "", string cookie = "", bool enabled = true) => new()
    {
        Name = name,
        SourceUrl = source,
        TargetUrl = target,
        Priority = priority,
        HostHeader = hostHeader,
        Cookie = cookie,
        Enabled = enabled
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "PSXDownloadHelper.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
