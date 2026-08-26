using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Pim.UnitTests.Harness.Generators;
using Pim.UnitTests.Harness.Invariants;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class FilesPropertyTests
{
    [Fact]
    public void IndexingDedup_RandomCorpus_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.Generate(50, seed: seed);
            var chunks = new List<(Guid fileItemId, Guid versionId, int chunkIndex, string pointId)>();
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            foreach (var f in corpus)
            {
                var fileId = Guid.TryParse(f.Id, out var g) ? g : Guid.NewGuid();
                var versionId = Guid.NewGuid();
                int chunkCount = faker.Random.Int(1, 3);
                for (int ci = 0; ci < chunkCount; ci++)
                {
                    var pointId = $"{fileId:N}_{versionId:N}_{ci}";
                    chunks.Add((fileId, versionId, ci, pointId));
                }
            }
            var (pass, detail) = FilesInvariants.CheckIndexingDedup(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void IndexingDedup_DuplicatesCorpus_ShouldHoldAfterDedup()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.GenerateDuplicates(20, seed: seed);
            var chunks = new List<(Guid fileItemId, Guid versionId, int chunkIndex, string pointId)>();
            foreach (var f in corpus)
            {
                var fileId = Guid.NewGuid();
                var versionId = Guid.NewGuid();
                var pointId = $"{fileId:N}_{f.Path.GetHashCode():X8}_{f.Hash.Substring(0, 8)}";
                chunks.Add((fileId, versionId, 0, pointId));
            }
            // ensure distinct pointIds even with duplicate names
            var distinct = chunks.Select(c => c.pointId).Distinct().Count();
            Assert.Equal(chunks.Count, distinct);
            var (pass, detail) = FilesInvariants.CheckIndexingDedup(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void EmbeddingDimensions_RandomCorpus_ShouldBe384AndNormalized()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.Generate(20, seed: seed);
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var vectors = new List<float[]>();
            foreach (var _ in corpus)
            {
                var v = new float[384];
                for (int i = 0; i < 384; i++) v[i] = (float)(faker.Random.Double(-1, 1));
                // normalize to unit length (or keep zero occasionally)
                var sum = 0f;
                foreach (var f in v) sum += f * f;
                var norm = MathF.Sqrt(sum);
                if (norm > 1e-6f)
                {
                    for (int i = 0; i < 384; i++) v[i] /= norm;
                }
                else
                {
                    Array.Clear(v, 0, v.Length);
                }
                vectors.Add(v);
            }
            var (pass, detail) = FilesInvariants.CheckEmbeddingDimensions(vectors);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void EmbeddingDimensions_ZeroVector_ShouldPass()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var vectors = new List<float[]>();
            // mix of zero and normalized
            for (int i = 0; i < 10; i++)
            {
                if (i % 3 == 0)
                {
                    vectors.Add(new float[384]); // zero
                }
                else
                {
                    var v = new float[384];
                    var faker = new Bogus.Faker("zh_CN");
                    faker.Random = new Bogus.Randomizer(seed + i);
                    for (int j = 0; j < 384; j++) v[j] = (float)faker.Random.Double(-1, 1);
                    var sum = v.Sum(x => x * x);
                    var norm = MathF.Sqrt(sum);
                    if (norm > 1e-6f) for (int j = 0; j < 384; j++) v[j] /= norm;
                    vectors.Add(v);
                }
            }
            var (pass, detail) = FilesInvariants.CheckEmbeddingDimensions(vectors);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void DisabledPathNotBilled_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.Generate(30, seed: seed);
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var disabledPaths = new HashSet<string>(corpus.Take(5).Select(c => c.Path));
            var items = new List<(string path, bool isDisabled, int billedTokens, double billedCost)>();
            foreach (var f in corpus)
            {
                var isDisabled = disabledPaths.Contains(f.Path) || faker.Random.Bool(0.2f);
                var tokens = isDisabled ? 0 : faker.Random.Int(0, 1000);
                var cost = isDisabled ? 0.0 : faker.Random.Double(0, 10);
                items.Add((f.Path, isDisabled, tokens, cost));
            }
            var (pass, detail) = FilesInvariants.CheckDisabledPathNotBilled(items);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void ChunkHashConsistency_RandomText_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var chunks = new List<(string text, string textHash)>();
            for (int i = 0; i < 20; i++)
            {
                var text = faker.Lorem.Sentence(5) + $"_{seed}_{i}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                chunks.Add((text, hash));
            }
            var (pass, detail) = FilesInvariants.CheckChunkHashConsistency(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void IndexIdempotency_Reindex_ShouldBeIdempotent()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.Generate(20, seed: seed);
            var chunksBefore = new List<(string text, string textHash)>();
            foreach (var f in corpus)
            {
                var text = $"content-{f.Id}-{f.Name}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                chunksBefore.Add((text, hash));
            }
            var hashesBefore = new HashSet<string>(chunksBefore.Select(c => c.textHash));
            int countBefore = chunksBefore.Count;
            // reindex same corpus
            var chunksAfter = new List<(string text, string textHash)>(chunksBefore);
            var hashesAfter = new HashSet<string>(chunksAfter.Select(c => c.textHash));
            int countAfter = chunksAfter.Count;
            var (pass, detail) = FilesInvariants.CheckIndexIdempotency(countBefore, countAfter, hashesBefore, hashesAfter);
            Assert.True(pass, $"Seed {seed}: {detail}");
            var (pass2, detail2) = FilesInvariants.CheckChunkHashConsistency(chunksAfter);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
        }
    }

    [Fact]
    public void HugeAndEmptyFiles_ShouldPassHashAndDedup()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.GenerateHugeAndEmpty(20, seed: seed);
            var chunks = new List<(Guid fileItemId, Guid versionId, int chunkIndex, string pointId)>();
            var hashChecks = new List<(string text, string textHash)>();
            foreach (var f in corpus)
            {
                var fileId = Guid.NewGuid();
                var versionId = Guid.NewGuid();
                var pointId = $"{fileId:N}_{f.SizeBytes}_{f.Hash.Substring(0, 8)}";
                chunks.Add((fileId, versionId, 0, pointId));
                var text = f.SizeBytes == 0 ? string.Empty : $"huge-{f.Id}-{f.SizeBytes}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                hashChecks.Add((text, hash));
            }
            var (pass, detail) = FilesInvariants.CheckIndexingDedup(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
            var (pass2, detail2) = FilesInvariants.CheckChunkHashConsistency(hashChecks);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
        }
    }

    [Fact]
    public void NestedPaths_ShouldNotBreakIndexing()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.GenerateNestedPaths(20, seed: seed);
            var chunks = new List<(Guid fileItemId, Guid versionId, int chunkIndex, string pointId)>();
            foreach (var f in corpus)
            {
                Assert.True(f.Depth >= 5, $"Seed {seed}: expected deep nested path but got depth {f.Depth} for {f.Path}");
                var fileId = Guid.NewGuid();
                var versionId = Guid.NewGuid();
                var pointId = $"{fileId:N}_{f.Path.GetHashCode():X8}";
                chunks.Add((fileId, versionId, 0, pointId));
            }
            var (pass, detail) = FilesInvariants.CheckIndexingDedup(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
            // also check disabled path billing not violated for nested paths
            var items = chunks.Select((c, idx) => (corpus[idx].Path, false, 10, 0.01)).ToList();
            var (pass2, detail2) = FilesInvariants.CheckDisabledPathNotBilled(items);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
        }
    }

    [Fact]
    public void MixedCorpus_Comprehensive_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var a = FileCorpusGenerator.Generate(10, seed: seed);
            var b = FileCorpusGenerator.GenerateHugeAndEmpty(10, seed: seed + 1000);
            var c = FileCorpusGenerator.GenerateNestedPaths(10, seed: seed + 2000);
            var combined = a.Concat(b).Concat(c).ToList();
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            // dedup check
            var chunks = combined.Select(f =>
            {
                var fid = Guid.NewGuid();
                var vid = Guid.NewGuid();
                var pid = $"{fid:N}_{vid:N}_0";
                return (fid, vid, 0, pid);
            }).ToList();
            var (pass, detail) = FilesInvariants.CheckIndexingDedup(chunks);
            Assert.True(pass, $"Seed {seed}: {detail}");
            // embedding dimensions
            var vectors = new List<float[]>();
            foreach (var _ in combined)
            {
                var v = new float[384];
                for (int i = 0; i < 384; i++) v[i] = (float)faker.Random.Double(-1, 1);
                var sum = v.Sum(x => x * x);
                var norm = MathF.Sqrt(sum);
                if (norm > 1e-6f) for (int i = 0; i < 384; i++) v[i] /= norm;
                vectors.Add(v);
            }
            var (pass2, detail2) = FilesInvariants.CheckEmbeddingDimensions(vectors);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
            // hash consistency for combined
            var hashChecks = combined.Select(f =>
            {
                var text = $"file-{f.Name}-{f.Path}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                return (text, hash);
            }).ToList();
            var (pass3, detail3) = FilesInvariants.CheckChunkHashConsistency(hashChecks);
            Assert.True(pass3, $"Seed {seed}: {detail3}");
            // idempotency
            var hashes = new HashSet<string>(hashChecks.Select(h => h.hash));
            var (pass4, detail4) = FilesInvariants.CheckIndexIdempotency(hashes.Count, hashes.Count, hashes, new HashSet<string>(hashes));
            Assert.True(pass4, $"Seed {seed}: {detail4}");
        }
    }
}
