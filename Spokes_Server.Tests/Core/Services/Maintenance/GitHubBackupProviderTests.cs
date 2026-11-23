using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class GitHubBackupProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly CompanyProfileRepository _profiles;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;

    public GitHubBackupProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_GitHubTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var configDict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDir,
            ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _profiles = new CompanyProfileRepository(_persistence, _config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private static IHttpClientFactory CreateMockHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken ct) => responder(req, ct));

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        return factoryMock.Object;
    }

    [Theory]
    [InlineData(null, "", "Projects/proj1.json", "Projects/proj1.json")]
    [InlineData("", "", "Projects/proj1.json", "Projects/proj1.json")]
    [InlineData("/", "", "/Projects/proj1.json", "Projects/proj1.json")]
    [InlineData("spokes-backups", "spokes-backups", "Projects/proj1.json", "spokes-backups/Projects/proj1.json")]
    [InlineData("/spokes-backups/", "spokes-backups", "\\Projects\\proj1.json", "spokes-backups/Projects/proj1.json")]
    [InlineData("backups//prod/", "backups/prod", "GlobalProfile.json", "backups/prod/GlobalProfile.json")]
    public void PathNormalization_ProducesCleanRepositoryPaths(string? inputFolder, string expectedFolder, string relPath, string expectedRepoPath)
    {
        Assert.Equal(expectedFolder, GitHubBackupProvider.NormalizeTargetFolder(inputFolder));
        Assert.Equal(expectedRepoPath, GitHubBackupProvider.CombineRepoPath(inputFolder, relPath));
    }

    [Theory]
    [InlineData("octocat/hello-world", true, "octocat", "hello-world")]
    [InlineData(" /octocat/hello-world/ ", true, "octocat", "hello-world")]
    [InlineData("invalid-repo", false, "", "")]
    [InlineData("a/b/c", false, "", "")]
    [InlineData("", false, "", "")]
    public void TryParseRepository_ValidatesOwnerAndRepo(string? input, bool expectedValid, string expectedOwner, string expectedRepo)
    {
        var valid = GitHubBackupProvider.TryParseRepository(input, out var owner, out var repo);
        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedRepo, repo);
    }

    [Fact]
    public async Task ComputeGitBlobSha1Async_MatchesStandardGitBlobHash()
    {
        var sampleFile = Path.Combine(_testDir, "test_blob.txt");
        // Known Git blob SHA1 for "what is up, doc?" -> bd9dbf5aae1a3862dd1526723246b20206e5fc37
        await File.WriteAllBytesAsync(sampleFile, Encoding.ASCII.GetBytes("what is up, doc?"));

        var sha = await GitHubBackupProvider.ComputeGitBlobSha1Async(sampleFile);
        Assert.Equal("bd9dbf5aae1a3862dd1526723246b20206e5fc37", sha);
    }

    [Fact]
    public async Task VerifyTokenAsync_ReturnsUsernameOnValidToken()
    {
        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            Assert.Equal("https://api.github.com/user", req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"login\":\"spokes-admin\"}", Encoding.UTF8, "application/json")
            });
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config);
        var res = await provider.VerifyTokenAsync("ghp_test123");

        Assert.True(res.Success);
        Assert.Equal("spokes-admin", res.Username);
    }

    [Fact]
    public async Task ListRepositoriesAndFolders_ParsesGitHubResponsesAndHandlesEmptyRepo()
    {
        bool returnEmptyRepo = false;

        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/user/repos"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"full_name\":\"acme/spokes-backups\",\"private\":true,\"default_branch\":\"main\"}]",
                        Encoding.UTF8, "application/json")
                });
            }
            if (url.EndsWith("/repos/acme/spokes-backups"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}",
                        Encoding.UTF8, "application/json")
                });
            }
            if (url.Contains("/git/trees/main?recursive=1"))
            {
                if (returnEmptyRepo)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"tree\":[{\"path\":\"backups\",\"type\":\"tree\"},{\"path\":\"backups/daily\",\"type\":\"tree\"},{\"path\":\"README.md\",\"type\":\"blob\"}]}",
                        Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var profile = _profiles.Get();
        profile.Backup.GitHub.PersonalAccessToken = "ghp_valid";
        _profiles.Save(profile);

        var provider = new GitHubBackupProvider(_profiles, factory, _config);

        var repos = await provider.ListRepositoriesAsync();
        Assert.Single(repos);
        Assert.Equal("acme/spokes-backups", repos[0].FullName);
        Assert.True(repos[0].Private);

        var folders = await provider.ListRepositoryFoldersAsync("acme/spokes-backups");
        Assert.Equal(2, folders.Count);
        Assert.Contains("backups", folders);
        Assert.Contains("backups/daily", folders);

        returnEmptyRepo = true;
        var emptyFolders = await provider.ListRepositoryFoldersAsync("acme/spokes-backups");
        Assert.Empty(emptyFolders);
    }

    [Fact]
    public async Task UploadAsync_SkipsUnchangedBlobs_UploadsModifiedFiles_AndDeletesRemovedFilesInSingleCommit()
    {
        // Arrange local Data directory with:
        // 1. unchanged.json (already matching remote SHA -> skipped)
        // 2. modified.json (UTF-8 text -> sent inline via "content" in POST /git/trees without calling POST /git/blobs)
        // 3. binary.bin (binary bytes containing null byte -> uploaded via POST /git/blobs and referenced by "sha")
        // And 1 remote file "spokes-backups/deleted.json" that no longer exists locally (should get sha: null in tree)
        var unchangedPath = Path.Combine(_testDir, "unchanged.json");
        var modifiedPath = Path.Combine(_testDir, "modified.json");
        var binaryPath = Path.Combine(_testDir, "binary.bin");
        var ignoredBackupArchive = Path.Combine(_testDir, "Backups", "Spokes_Backup_2026.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredBackupArchive)!);

        await File.WriteAllTextAsync(unchangedPath, "{\"status\":\"same\"}");
        await File.WriteAllTextAsync(modifiedPath, "{\"status\":\"updated\"}");
        await File.WriteAllBytesAsync(binaryPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF });
        await File.WriteAllTextAsync(ignoredBackupArchive, "should_be_ignored");

        var unchangedSha = await GitHubBackupProvider.ComputeGitBlobSha1Async(unchangedPath);
        var profile = _profiles.Get();
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.PersonalAccessToken = "ghp_secret";
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.GitHub.TargetFolder = "spokes-backups";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        var profilePath = Directory.GetFiles(_testDir, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.EndsWith("unchanged.json", StringComparison.OrdinalIgnoreCase) &&
                                 !f.EndsWith("modified.json", StringComparison.OrdinalIgnoreCase));
        var profileRelPath = profilePath != null ? Path.GetRelativePath(_testDir, profilePath).Replace('\\', '/') : "company_profile.json";
        var profileSha = profilePath != null
            ? await GitHubBackupProvider.ComputeLocalUploadSha1Async(profilePath, profileRelPath)
            : "none";

        int blobsUploaded = 0;
        string? capturedTreeRequestJson = null;
        bool refUpdated = false;

        var factory = CreateMockHttpClientFactory(async (req, ct) =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/ref/heads/main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":{\"sha\":\"head-sha-001\"}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/trees/head-sha-001?recursive=1")
            {
                var treeJson = JsonSerializer.Serialize(new
                {
                    tree = new[]
                    {
                        new { path = "spokes-backups/unchanged.json", type = "blob", sha = unchangedSha },
                        new { path = $"spokes-backups/{profileRelPath}", type = "blob", sha = profileSha },
                        new { path = "spokes-backups/modified.json", type = "blob", sha = "old-sha-999" },
                        new { path = "spokes-backups/deleted.json", type = "blob", sha = "deleted-sha-888" },
                        new { path = "other-folder/keep.txt", type = "blob", sha = "keep-sha-777" }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(treeJson, Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/blobs")
            {
                blobsUploaded++;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"new-blob-sha-222\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/trees")
            {
                capturedTreeRequestJson = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"new-tree-sha-333\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/commits")
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"new-commit-sha-444\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Patch && url == "https://api.github.com/repos/acme/backups/git/refs/heads/main")
            {
                refUpdated = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ref\":\"refs/heads/main\"}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config)
        {
            BlobPostPacingDelay = TimeSpan.Zero
        };
        var progressReports = new List<BackupUploadProgress>();
        var progress = new Progress<BackupUploadProgress>(progressReports.Add);

        // Act
        var uploadResult = await provider.UploadAsync(ignoredBackupArchive, "Spokes_Backup_2026.zip", progress);

        // Assert
        Assert.True(uploadResult.Success, uploadResult.ErrorMessage);
        Assert.Equal("new-commit-sha-444", uploadResult.DestinationId);
        Assert.Equal(1, blobsUploaded); // Only binary.bin used POST /git/blobs; modified.json was embedded inline in POST /git/trees!
        Assert.True(refUpdated);
        Assert.NotNull(capturedTreeRequestJson);
        Assert.Contains("\"path\":\"spokes-backups/modified.json\"", capturedTreeRequestJson);
        Assert.Contains("\"content\":\"{\\u0022status\\u0022:\\u0022updated\\u0022}\"", capturedTreeRequestJson);
        Assert.Contains("\"path\":\"spokes-backups/binary.bin\"", capturedTreeRequestJson);
        Assert.Contains("\"sha\":\"new-blob-sha-222\"", capturedTreeRequestJson);
        // Deleted local file in target folder is removed with sha:null
        Assert.Contains("\"path\":\"spokes-backups/deleted.json\"", capturedTreeRequestJson);
        Assert.Contains("\"sha\":null", capturedTreeRequestJson);
        // File outside target folder is untouched
        Assert.DoesNotContain("other-folder/keep.txt", capturedTreeRequestJson);
    }

    [Fact]
    public async Task UploadAsync_RetriesOnSecondaryRateLimit403_AndSucceeds()
    {
        var file1 = Path.Combine(_testDir, "doc1.json");
        await File.WriteAllTextAsync(file1, "{\"hello\":\"world\"}");

        var profile = _profiles.Get();
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.PersonalAccessToken = "ghp_secret";
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.GitHub.TargetFolder = "Spokes_Data";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        int treePostAttempts = 0;

        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/ref/heads/main")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":{\"sha\":\"head-sha-001\"}}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/trees/head-sha-001?recursive=1")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tree\":[]}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/trees")
            {
                treePostAttempts++;
                if (treePostAttempts == 1)
                {
                    var rateLimitRes = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent(
                            "{\"message\":\"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.\"}",
                            Encoding.UTF8, "application/json")
                    };
                    rateLimitRes.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                    return Task.FromResult(rateLimitRes);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"tree-after-retry\"}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/commits")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"commit-after-retry\"}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Patch && url == "https://api.github.com/repos/acme/backups/git/refs/heads/main")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ref\":\"refs/heads/main\"}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config)
        {
            DefaultSecondaryRateLimitDelay = TimeSpan.FromMilliseconds(10)
        };

        var result = await provider.UploadAsync("dummy.zip", "dummy.zip");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("commit-after-retry", result.DestinationId);
        Assert.Equal(2, treePostAttempts);
    }

    [Fact]
    public async Task UploadAsync_BatchesLargeFileSets_AndCheckpointsCompletedTreeOnFailure()
    {
        for (int i = 1; i <= 4; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_testDir, $"item{i}.json"), $"{{\"id\":{i}}}");
        }

        var profile = _profiles.Get();
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.PersonalAccessToken = "ghp_secret";
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.GitHub.TargetFolder = "Spokes_Data";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        int treePosts = 0;
        var committedTrees = new List<string>();
        var patchedRefs = new List<string>();

        var factory = CreateMockHttpClientFactory(async (req, ct) =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/ref/heads/main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":{\"sha\":\"head-sha-001\"}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/trees/head-sha-001?recursive=1")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tree\":[]}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/trees")
            {
                treePosts++;
                if (treePosts == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent("{\"sha\":\"batch-1-tree-sha\"}", Encoding.UTF8, "application/json")
                    };
                }

                // Batch 2 fails permanently
                return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"message\":\"Tree batch 2 error\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/commits")
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                committedTrees.Add(doc.RootElement.GetProperty("tree").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"checkpoint-commit-sha\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Patch && url == "https://api.github.com/repos/acme/backups/git/refs/heads/main")
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                patchedRefs.Add(doc.RootElement.GetProperty("sha").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ref\":\"refs/heads/main\"}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config)
        {
            MaxEntriesPerTreeBatch = 2,
            CommitCheckpointIntervalBatches = 5,
            MaxRateLimitRetries = 0
        };

        var result = await provider.UploadAsync("dummy.zip", "dummy.zip");

        Assert.False(result.Success);
        // Verify Batch 1's tree was checkpoint-committed and advanced on main before returning failure!
        Assert.Contains("batch-1-tree-sha", committedTrees);
        Assert.Contains("checkpoint-commit-sha", patchedRefs);
    }

    [Fact]
    public async Task UploadAsync_ResolvesSubtreeWhenRootRecursiveTreeIsTruncated()
    {
        var existingFile = Path.Combine(_testDir, "existing.json");
        await File.WriteAllTextAsync(existingFile, "{\"already\":\"synced\"}");
        var existingSha = await GitHubBackupProvider.ComputeGitBlobSha1Async(existingFile);

        var profile = _profiles.Get();
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.PersonalAccessToken = "ghp_secret";
        profile.Backup.GitHub.Repository = "PCBeeQC/Logiciel_PCBee";
        profile.Backup.GitHub.TargetFolder = "Spokes_Data";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        var profilePath = Directory.GetFiles(_testDir, "company.json", SearchOption.AllDirectories).First();
        var profileRelPath = Path.GetRelativePath(_testDir, profilePath).Replace('\\', '/');
        var profileSha = await GitHubBackupProvider.ComputeLocalUploadSha1Async(profilePath, profileRelPath);

        bool subtreeQueried = false;
        int treesCreated = 0;

        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/PCBeeQC/Logiciel_PCBee")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/PCBeeQC/Logiciel_PCBee/git/ref/heads/main")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":{\"sha\":\"head-sha-001\"}}", Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/PCBeeQC/Logiciel_PCBee/git/trees/head-sha-001?recursive=1")
            {
                // Simulate a truncated root tree that contains the "Spokes_Data" directory entry but truncated its children
                var truncatedJson = JsonSerializer.Serialize(new
                {
                    truncated = true,
                    tree = new[]
                    {
                        new { path = "Spokes_Data", type = "tree", sha = "spokes-data-subtree-sha" }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(truncatedJson, Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/PCBeeQC/Logiciel_PCBee/git/trees/spokes-data-subtree-sha?recursive=1")
            {
                subtreeQueried = true;
                var subtreeJson = JsonSerializer.Serialize(new
                {
                    truncated = false,
                    tree = new[]
                    {
                        new { path = "existing.json", type = "blob", sha = existingSha },
                        new { path = profileRelPath, type = "blob", sha = profileSha }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(subtreeJson, Encoding.UTF8, "application/json")
                });
            }
            if (req.Method == HttpMethod.Post && url.EndsWith("/git/trees"))
            {
                treesCreated++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config);
        var result = await provider.UploadAsync("dummy.zip", "dummy.zip");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(subtreeQueried);
        Assert.Equal(0, treesCreated); // All files matched via the subtree lookup, so 0 mutations were needed!
    }

    [Fact]
    public async Task TestConnectionAsync_VerifiesTokenAndPushPermissions()
    {
        var encryptionService = new Spokes_Server.Core.Services.Security.EncryptionService(_config);
        var profile = _profiles.Get();
        profile.Backup.GitHub.PersonalAccessToken = encryptionService.Encrypt("ghp_test");
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.GitHub.TargetFolder = "spokes-backups";
        _profiles.Save(profile);

        bool allowPush = true;
        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "https://api.github.com/user")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"login\":\"octocat\"}", Encoding.UTF8, "application/json")
                });
            }
            if (url == "https://api.github.com/repos/acme/backups")
            {
                var body = $"{{\"default_branch\":\"main\",\"permissions\":{{\"push\":{(allowPush ? "true" : "false")}}}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config);

        var okResult = await provider.TestConnectionAsync();
        Assert.True(okResult.Success);
        Assert.Contains("@octocat", okResult.Message);

        allowPush = false;
        var noPushResult = await provider.TestConnectionAsync();
        Assert.False(noPushResult.Success);
        Assert.Contains("write/push permissions", noPushResult.Message);
    }

    [Fact]
    public async Task IsEnabled_AndPersistence_RoundTripsAcrossReloadAndNotifiesListeners()
    {
        var mockProvider = new Mock<IBackupDestinationProvider>();
        mockProvider.SetupGet(p => p.DestinationType).Returns(BackupDestinationType.GitHub);
        mockProvider
            .Setup(p => p.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<BackupUploadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupUploadResult(true, "commit-123"));

        var coordinator = new BackupDestinationCoordinator(new[] { mockProvider.Object }, _profiles);

        var profile = _profiles.Get();
        Assert.False(profile.Backup.GitHub.IsEnabled);

        Spokes_Server.Core.Models.Core.CompanyProfile? notifiedProfile = null;
        int loadedNotifications = 0;
        _profiles.OnSaved += p => notifiedProfile = p;
        _profiles.OnLoaded += () => loadedNotifications++;

        // When disabled, coordinator does not dispatch GitHub upload
        var skippedResults = await coordinator.DispatchUploadAsync("dummy.zip", "dummy.zip");
        Assert.Empty(skippedResults);

        // Simulate ConnectGitHub flow: token + username + IsEnabled = true + repository selection
        profile.Backup.GitHub.PersonalAccessToken = "ghp_connected";
        profile.Backup.GitHub.AccountUsername = "spokes-admin";
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.GitHub.TargetFolder = "spokes-backups";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        Assert.NotNull(notifiedProfile);
        Assert.True(notifiedProfile!.Backup.GitHub.IsEnabled);

        // Reload from disk and verify new instance preserves IsEnabled and decrypted settings
        _profiles.LoadFromDisk();
        Assert.Equal(1, loadedNotifications);

        var reloaded = _profiles.Get();
        Assert.True(reloaded.Backup.GitHub.IsEnabled);
        Assert.Equal("ghp_connected", reloaded.Backup.GitHub.PersonalAccessToken);
        Assert.Equal("spokes-admin", reloaded.Backup.GitHub.AccountUsername);
        Assert.Equal("acme/backups", reloaded.Backup.GitHub.Repository);
        Assert.Equal("spokes-backups", reloaded.Backup.GitHub.TargetFolder);

        var dispatchedResults = await coordinator.DispatchUploadAsync("dummy.zip", "dummy.zip");
        Assert.True(dispatchedResults.ContainsKey(BackupDestinationType.GitHub));
        Assert.True(dispatchedResults[BackupDestinationType.GitHub].Success);
    }

    [Fact]
    public async Task AllBackupCredentials_AreEncryptedAtRestWithMasterPassword_AndMigratedFromLegacyPlaintext()
    {
        var configWithMasterPwd = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest_MasterPassword_2026!"
            })
            .Build();

        var repo = new CompanyProfileRepository(_persistence, configWithMasterPwd);
        var profile = repo.Get();

        profile.Backup.GitHub.PersonalAccessToken = "ghp_super_secret_pat_999";
        profile.Backup.GoogleDrive.RefreshToken = "1//0g_gdrive_refresh_secret";
        profile.Backup.S3.AccessKey = "AKIA_S3_ACCESS_KEY";
        profile.Backup.S3.SecretKey = "s3_super_secret_key_value";
        profile.Backup.WebDav.Username = "webdav_admin";
        profile.Backup.WebDav.Password = "webdav_secret_pwd";
        profile.Backup.Sftp.Username = "sftp_root";
        profile.Backup.Sftp.Password = "sftp_secret_pwd";

        repo.Save(profile);
        await _persistence.FlushAllAsync();

        var companyJsonPath = Path.Combine(_testDir, "Settings", "company.json");
        Assert.True(File.Exists(companyJsonPath));

        var rawDiskJson = await File.ReadAllTextAsync(companyJsonPath);

        // Verify NONE of the credentials appear in plain text on disk, and canonical v2: payloads are written
        Assert.DoesNotContain("ghp_super_secret_pat_999", rawDiskJson);
        Assert.DoesNotContain("1//0g_gdrive_refresh_secret", rawDiskJson);
        Assert.DoesNotContain("AKIA_S3_ACCESS_KEY", rawDiskJson);
        Assert.DoesNotContain("s3_super_secret_key_value", rawDiskJson);
        Assert.DoesNotContain("webdav_admin", rawDiskJson);
        Assert.DoesNotContain("webdav_secret_pwd", rawDiskJson);
        Assert.DoesNotContain("sftp_root", rawDiskJson);
        Assert.DoesNotContain("sftp_secret_pwd", rawDiskJson);
        Assert.Contains("\"v2:", rawDiskJson);

        // Verify saving unrelated changes reuses cached ciphertexts so company.json does not churn
        profile.CompanyName = "Acme Robotics";
        repo.Save(profile);
        await _persistence.FlushAllAsync();
        var secondDiskJson = await File.ReadAllTextAsync(companyJsonPath);
        using (var doc1 = JsonDocument.Parse(rawDiskJson))
        using (var doc2 = JsonDocument.Parse(secondDiskJson))
        {
            Assert.Equal(
                doc1.RootElement.GetProperty("Backup").GetProperty("GitHub").GetProperty("PersonalAccessToken").GetString(),
                doc2.RootElement.GetProperty("Backup").GetProperty("GitHub").GetProperty("PersonalAccessToken").GetString());
        }

        // Verify reloading from disk decrypts all credentials transparently into memory
        repo.LoadFromDisk();
        var reloaded = repo.Get();
        Assert.Equal("ghp_super_secret_pat_999", reloaded.Backup.GitHub.PersonalAccessToken);
        Assert.Equal("1//0g_gdrive_refresh_secret", reloaded.Backup.GoogleDrive.RefreshToken);
        Assert.Equal("AKIA_S3_ACCESS_KEY", reloaded.Backup.S3.AccessKey);
        Assert.Equal("s3_super_secret_key_value", reloaded.Backup.S3.SecretKey);
        Assert.Equal("webdav_admin", reloaded.Backup.WebDav.Username);
        Assert.Equal("webdav_secret_pwd", reloaded.Backup.WebDav.Password);
        Assert.Equal("sftp_root", reloaded.Backup.Sftp.Username);
        Assert.Equal("sftp_secret_pwd", reloaded.Backup.Sftp.Password);

        // Simulate legacy plaintext token on disk and verify LoadFromDisk migrates it automatically
        var legacyJson = System.Text.RegularExpressions.Regex.Replace(
            rawDiskJson,
            "\"PersonalAccessToken\"\\s*:\\s*\"[^\"]*\"",
            "\"PersonalAccessToken\": \"ghp_legacy_plaintext_token\"");
        await File.WriteAllTextAsync(companyJsonPath, legacyJson);

        repo.LoadFromDisk();
        await _persistence.FlushAllAsync();

        var migratedLoaded = repo.Get();
        Assert.Equal("ghp_legacy_plaintext_token", migratedLoaded.Backup.GitHub.PersonalAccessToken);

        var migratedDiskJson = await File.ReadAllTextAsync(companyJsonPath);
        Assert.DoesNotContain("ghp_legacy_plaintext_token", migratedDiskJson);
    }

    [Fact]
    public async Task UploadAsync_CompanySettingsJson_RedactsAllBackupCredentialsAndMasterPasswordWithoutMutatingDisk()
    {
        var archivePath = Path.Combine(_testDir, "Backups", "Spokes_Backup_Redact.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        await File.WriteAllTextAsync(archivePath, "zip-placeholder");

        var profile = _profiles.Get();
        profile.CompanyName = "Acme Corp";
        profile.PublicChannelMasterPassword = "legacy-channel-master-secret";
        profile.Backup.GitHub.IsEnabled = true;
        profile.Backup.GitHub.PersonalAccessToken = "ghp_redact_pat_12345";
        profile.Backup.GitHub.Repository = "acme/backups";
        profile.Backup.S3.AccessKey = "AKIA_REDACT_KEY";
        profile.Backup.S3.SecretKey = "s3_redact_secret";
        profile.Backup.GoogleDrive.RefreshToken = "1//gdrive_redact_token";
        profile.Backup.WebDav.Username = "webdav_redact_user";
        profile.Backup.WebDav.Password = "webdav_redact_pass";
        profile.Backup.Sftp.Username = "sftp_redact_user";
        profile.Backup.Sftp.Password = "sftp_redact_pass";
        _profiles.Save(profile);
        await _persistence.FlushAllAsync();

        var companyJsonPath = Directory.GetFiles(_testDir, "company.json", SearchOption.AllDirectories).First();
        var originalDiskJson = await File.ReadAllTextAsync(companyJsonPath);
        Assert.Contains("v2:", originalDiskJson);

        string? capturedTreeJson = null;
        var factory = CreateMockHttpClientFactory(async (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"default_branch\":\"main\",\"permissions\":{\"push\":true}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/ref/heads/main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"object\":{\"sha\":\"head-sha-1\"}}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && url == "https://api.github.com/repos/acme/backups/git/trees/head-sha-1?recursive=1")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tree\":[]}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/trees")
            {
                capturedTreeJson = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"new-tree-sha-1\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/backups/git/commits")
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sha\":\"new-commit-sha-1\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Patch && url == "https://api.github.com/repos/acme/backups/git/refs/heads/main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ref\":\"refs/heads/main\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new GitHubBackupProvider(_profiles, factory, _config)
        {
            BlobPostPacingDelay = TimeSpan.Zero
        };
        var result = await provider.UploadAsync(archivePath, "Spokes_Backup_Redact.zip");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(capturedTreeJson);

        using var treeDoc = JsonDocument.Parse(capturedTreeJson!);
        var settingsEntry = treeDoc.RootElement.GetProperty("tree").EnumerateArray()
            .First(e => string.Equals(e.GetProperty("path").GetString(), "Settings/company.json", StringComparison.OrdinalIgnoreCase));
        var uploadedContent = settingsEntry.GetProperty("content").GetString()!;

        Assert.Contains("Acme Corp", uploadedContent);
        Assert.DoesNotContain("v2:", uploadedContent);
        Assert.DoesNotContain("ghp_redact_pat_12345", uploadedContent);
        Assert.DoesNotContain("AKIA_REDACT_KEY", uploadedContent);
        Assert.DoesNotContain("s3_redact_secret", uploadedContent);
        Assert.DoesNotContain("gdrive_redact_token", uploadedContent);
        Assert.DoesNotContain("webdav_redact", uploadedContent);
        Assert.DoesNotContain("sftp_redact", uploadedContent);
        Assert.DoesNotContain("legacy-channel-master-secret", uploadedContent);

        using var uploadedCompanyDoc = JsonDocument.Parse(uploadedContent);
        Assert.Equal(string.Empty, uploadedCompanyDoc.RootElement.GetProperty("PublicChannelMasterPassword").GetString());
        Assert.Equal(string.Empty, uploadedCompanyDoc.RootElement.GetProperty("Chat").GetProperty("PublicChannelMasterPassword").GetString());
        var backupNode = uploadedCompanyDoc.RootElement.GetProperty("Backup");
        Assert.Equal(string.Empty, backupNode.GetProperty("GitHub").GetProperty("PersonalAccessToken").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("S3").GetProperty("AccessKey").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("S3").GetProperty("SecretKey").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("GoogleDrive").GetProperty("RefreshToken").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("WebDav").GetProperty("Username").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("WebDav").GetProperty("Password").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("Sftp").GetProperty("Username").GetString());
        Assert.Equal(string.Empty, backupNode.GetProperty("Sftp").GetProperty("Password").GetString());

        // Local company.json on disk must remain untouched with its encrypted secrets
        Assert.Equal(originalDiskJson, await File.ReadAllTextAsync(companyJsonPath));
    }
}
