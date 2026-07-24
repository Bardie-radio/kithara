using Kithara.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kithara.Tests;

public class LocalBlobStorageTests
{
    [Fact]
    public async Task Put_round_trip_under_tunes_slug_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "kithara-blobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateStorage(root);
            var key = BlobKeyLayout.AssignKey("magpie");
            await using var payload = new MemoryStream("hello-pcm"u8.ToArray());

            var size = await storage.PutAsync(key, payload, "audio/pcm");
            Assert.Equal(9, size);
            Assert.True(await storage.ExistsAsync(key));

            var opened = await storage.OpenReadAsync(key);
            Assert.NotNull(opened);
            await using (opened.Stream)
            {
                Assert.Equal("audio/pcm", opened.ContentType);
                using var reader = new StreamReader(opened.Stream);
                Assert.Equal("hello-pcm", await reader.ReadToEndAsync());
            }

            await storage.DeleteAsync(key);
            Assert.False(await storage.ExistsAsync(key));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("tunes/magpie")]
    [InlineData("other/magpie/object")]
    [InlineData("tunes/../magpie/object")]
    public void EnsureValidKey_rejects_escape_and_short_keys(string key)
    {
        Assert.Throws<ArgumentException>(() => BlobKeyLayout.EnsureValidKey(key));
    }

    [Fact]
    public void EnsureKeyOwnedBy_rejects_other_module_prefix()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => BlobKeyLayout.EnsureKeyOwnedBy("tunes/starling/obj", "magpie"));
    }

    [Fact]
    public void ResolvePath_rejects_traversal_even_if_segments_look_valid()
    {
        var root = Path.Combine(Path.GetTempPath(), "kithara-blobs-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Valid shape but ResolvePath still binds under root; traversal already blocked by EnsureValidKey.
            Assert.Throws<ArgumentException>(() => BlobKeyLayout.ResolvePath(root, "tunes/magpie/../../etc/passwd"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalBlobStorage CreateStorage(string root) =>
        new(
            Options.Create(new BlobStorageOptions { Path = root }),
            NullLogger<LocalBlobStorage>.Instance);
}
