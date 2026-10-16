namespace ALog.Tests;

/// <summary>
/// Checks the SQL password generator and the image pin without starting Docker.
/// The live fixture still owns the container. These tests only read its helpers.
/// </summary>
public sealed class SqlPasswordSourceTests
{
    /// <summary>
    /// Thirty-two draws stay in the connection-string alphabet and differ from each other.
    /// The prefix covers SQL Server's four character classes. The tail is 24 hex characters.
    /// A separator in any draw fails the test, as does a repeated password.
    /// </summary>
    /// <remarks>
    /// Reads OS randomness. Does not log a password and does not start a container.
    /// </remarks>
    [Fact]
    public void CreateSqlPassword_UsesFourClasses_AndNoSeparators()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var draw = 0; draw < 32; draw++)
        {
            var password = SqlLiveIntegrationTests.CreateSqlPassword();
            Assert.Equal(28, password.Length);
            Assert.Matches("^Aa1![0-9A-F]{24}$", password);
            var added = seen.Add(password);
            Assert.True(added);
        }

        Assert.Equal(32, seen.Count);
    }

    /// <summary>
    /// The image constant is one digest pin: a name, one digest separator, and 64 hex characters.
    /// A tag beside the digest, or the moving SQL Server tag, fails the test.
    /// </summary>
    /// <remarks>
    /// Does not contact a registry. The digest text is the constant under test, not a second copy.
    /// </remarks>
    [Fact]
    public void SqlServerImage_IsASingleDigestPin()
    {
        var image = SqlLiveIntegrationTests.SqlServerImage;
        var marker = "@" + "sha256:";
        var movingTag = "2022-" + "latest";
        Assert.False(string.IsNullOrWhiteSpace(image));
        Assert.DoesNotContain(movingTag, image, StringComparison.Ordinal);

        var count = 0;
        var start = 0;
        while (start < image.Length && count < 3)
        {
            var found = image.IndexOf(marker, start, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count++;
            start = found + marker.Length;
        }

        Assert.Equal(1, count);
        var split = image.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(split > 0);
        var name = image[..split];
        var digest = image[(split + marker.Length)..];
        Assert.DoesNotContain(':', name);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }
}
