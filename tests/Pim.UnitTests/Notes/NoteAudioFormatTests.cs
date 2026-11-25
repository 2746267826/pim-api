using System.Buffers.Binary;
using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAudioFormatTests
{
    [Fact]
    public void Legacy_chunks_without_format_remain_wav()
    {
        var chunk = Chunk("""{"segment":1,"index":1,"samples":1600}""");

        Assert.Equal(NoteAudioFormat.Wav, NoteAudioFormats.Parse(chunk));
        Assert.Equal(".wav", NoteAudioFormats.Extension(NoteAudioFormat.Wav));
        Assert.Equal("audio/wav", NoteAudioFormats.MimeType(NoteAudioFormat.Wav));
        Assert.True(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Wav, 44 + 1600 * 2, 1600));
        Assert.False(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Wav, 44 + 1600 * 2 + 1, 1600));
    }

    [Theory]
    [InlineData("""{"format":"wav"}""", "wav")]
    [InlineData("""{"mimeType":"audio/x-wav"}""", "wav")]
    [InlineData("""{"mimeType":"audio/wav; codecs=1"}""", "wav")]
    [InlineData("""{"format":"opus"}""", "opus")]
    [InlineData("""{"format":"OPUS"}""", "opus")]
    [InlineData("""{"mimeType":"audio/ogg"}""", "opus")]
    [InlineData("""{"mimeType":"audio/ogg; codecs=opus"}""", "opus")]
    [InlineData("""{"mimeType":"audio/opus"}""", "opus")]
    [InlineData("""{"format":"opus","mimeType":"audio/ogg"}""", "opus")]
    public void Explicit_wav_and_opus_formats_are_recognized(string json, string expected)
    {
        Assert.Equal(expected, NoteAudioFormats.Parse(Chunk(json)).ToString().ToLowerInvariant());
    }

    [Theory]
    [InlineData("""{"format":"mp3"}""")]
    [InlineData("""{"format":"ogg"}""")]
    [InlineData("""{"mimeType":"audio/mpeg"}""")]
    [InlineData("""{"format":"wav","mimeType":"audio/ogg"}""")]
    [InlineData("""{"format":"opus","mimeType":"audio/wav"}""")]
    [InlineData("""{"format":42}""")]
    [InlineData("""{"mimeType":true}""")]
    public void Unknown_or_conflicting_formats_are_rejected(string json)
    {
        var error = Assert.Throws<DomainException>(() => NoteAudioFormats.Parse(Chunk(json)));
        Assert.Equal(40001, error.ErrorCode);
    }

    [Fact]
    public void Opus_size_and_extension_boundaries_are_explicit()
    {
        Assert.Equal(".opus", NoteAudioFormats.Extension(NoteAudioFormat.Opus));
        Assert.Equal("audio/ogg", NoteAudioFormats.MimeType(NoteAudioFormat.Opus));
        Assert.True(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Opus, 1, 1));
        Assert.True(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Opus,
            NoteAudioFormats.MaxOpusFileBytes, 1));
        Assert.False(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Opus,
            NoteAudioFormats.MaxOpusFileBytes + 1, 1));
        Assert.False(NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Opus, 480, 0));
    }

    [Fact]
    public void Existing_twenty_thousand_file_cap_is_not_lowered()
    {
        Assert.Equal(20_000, new NoteMaterialUploadOptions().MaxFiles);
    }

    [Fact]
    public void Tiny_fixture_is_a_complete_mono_opus_stream_with_exact_16k_samples()
    {
        var bytes = TinyOpus();
        Assert.Equal(480, bytes.Length);

        Validate(bytes, 1600);
        var hasValidSize = NoteAudioFormats.HasValidDeclaredSize(NoteAudioFormat.Opus,
            bytes.Length, 1600);
        Assert.True(hasValidSize);
    }

    [Fact]
    public void Ogg_validation_rejects_truncation_stereo_missing_eos_and_wrong_sample_count()
    {
        AssertRejected([], 1600);
        AssertRejected(TinyOpus()[..^1], 1600);
        AssertRejected(new byte[128], 1600);

        var stereo = TinyOpus();
        var body = 27 + stereo[26];
        stereo[body + 9] = 2;
        AssertRejected(stereo, 1600);

        var noEos = TinyOpus();
        var lastPage = noEos.AsSpan().LastIndexOf("OggS"u8);
        Assert.True(lastPage >= 0);
        noEos[lastPage + 5] = (byte)(noEos[lastPage + 5] & 0xFB);
        AssertRejected(noEos, 1600);

        AssertRejected(TinyOpus(), 1599);
        AssertRejected(TinyOpus(), 1601);
    }

    [Fact]
    public void Ogg_validation_accepts_mono_mapping_family_zero_and_one()
    {
        Validate(OggOpusTestData.Valid(1600), 1600);
        Validate(OggOpusTestData.Valid(1600, family: 1), 1600);
    }

    [Fact]
    public void Ogg_validation_rejects_crc_sequence_serial_tags_mapping_empty_and_trailing_pages()
    {
        var crc = OggOpusTestData.Valid(1600);
        crc[^1] ^= 1;
        AssertRejected(crc, 1600);

        const uint serial = 0x12345678;
        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial, 2, 0, OggOpusTestData.OpusTags()),
            OggOpusTestData.Page(0x04, serial, 3, 5112, OggOpusTestData.AudioPacket())
        ]), 1600);

        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial + 1, 1, 0, OggOpusTestData.OpusTags()),
            OggOpusTestData.Page(0x04, serial, 2, 5112, OggOpusTestData.AudioPacket())
        ]), 1600);

        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial, 1, 0, "NotOpusTags"u8.ToArray()),
            OggOpusTestData.Page(0x04, serial, 2, 5112, OggOpusTestData.AudioPacket())
        ]), 1600);

        var truncatedTags = OggOpusTestData.OpusTags();
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedTags.AsSpan(8), uint.MaxValue);
        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial, 1, 0, truncatedTags),
            OggOpusTestData.Page(0x04, serial, 2, 5112, OggOpusTestData.AudioPacket())
        ]), 1600);

        AssertRejected(OggOpusTestData.Valid(1600, family: 2), 1600);

        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0x04, serial, 1, 0, OggOpusTestData.OpusTags())
        ]), 1600);

        AssertRejected(OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial, 1, 0, OggOpusTestData.OpusTags()),
            OggOpusTestData.Page(0x04, serial, 2, 5112, OggOpusTestData.AudioPacket()),
            OggOpusTestData.Page(0, serial, 3, 5112, OggOpusTestData.AudioPacket())
        ]), 1600);
    }

    [Fact]
    public void Fifteen_second_chunk_is_one_server_step_without_hardcoding_the_client_chunk_size()
    {
        Assert.Equal(60 * 16_000, NoteAsrPlan.SamplesPerCall);

        var step = Assert.Single(NoteAsrPlan.Build(Version((240_000, "opus"))));
        Assert.Equal(0, step.SourceOffset);
        Assert.Equal(0, step.RecordingOffset);
        Assert.Equal(0, step.TimelineOffset);
        Assert.Equal(240_000, step.Samples);
        Assert.Equal(".opus", Path.GetExtension(step.Path));
    }

    [Fact]
    public void Sample_slicing_rounds_at_exact_boundaries_and_keeps_chunk_offsets_separate()
    {
        var exact = Assert.Single(NoteAsrPlan.Build(Version((960_000, "wav"))));
        Assert.Equal(960_000, exact.Samples);

        var over = NoteAsrPlan.Build(Version((960_001, "wav")));
        Assert.Equal(2, over.Count);
        Assert.Equal(960_000, over[0].Samples);
        Assert.Equal(0, over[0].SourceOffset);
        Assert.Equal(1, over[1].Samples);
        Assert.Equal(960_000, over[1].SourceOffset);

        var fifteen = NoteAsrPlan.Build(Version((240_000, "opus"), (240_000, "opus")),
            samplesPerCall: 240_000);
        Assert.Equal(2, fifteen.Count);
        Assert.Equal(0, fifteen[0].SourceOffset);
        Assert.Equal(0, fifteen[0].RecordingOffset);
        Assert.Equal(0, fifteen[1].SourceOffset);
        Assert.Equal(240_000, fifteen[1].RecordingOffset);
        Assert.Equal(240_000, fifteen[1].TimelineOffset);
        Assert.All(fifteen, step => Assert.EndsWith(".opus", step.Path));
    }

    [Fact]
    public void One_sample_over_a_fifteen_second_slice_produces_a_visible_remainder()
    {
        var steps = NoteAsrPlan.Build(Version((240_001, "opus")), samplesPerCall: 240_000);
        Assert.Equal(2, steps.Count);
        Assert.Equal(240_000, steps[0].Samples);
        Assert.Equal(1, steps[1].Samples);
        Assert.Equal(240_000, steps[1].SourceOffset);
    }

    private static void Validate(byte[] bytes, long samples)
    {
        NoteOggOpus.Validate(bytes, samples);
    }

    private static void AssertRejected(byte[] bytes, long samples)
    {
        var error = Assert.Throws<DomainException>(() => Validate(bytes, samples));
        Assert.Equal(40001, error.ErrorCode);
    }

    private static byte[] TinyOpus() => OggOpusTestData.TinyOpus();

    private static JsonElement Chunk(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static NoteMaterialVersionDto Version(params (long Samples, string? Format)[] chunks)
    {
        var recordingId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var chunkJson = chunks.Select((chunk, index) =>
        {
            var value = new Dictionary<string, object?>
            {
                ["segment"] = 1, ["index"] = index + 1, ["samples"] = chunk.Samples
            };
            if (chunk.Format is not null)
            {
                value["format"] = chunk.Format;
                value["mimeType"] = chunk.Format == "opus" ? "audio/ogg" : "audio/wav";
            }
            return value;
        }).ToArray();
        var inputJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            recordings = new[]
            {
                new
                {
                    id = recordingId, noteId, schemaVersion = 1, currentSegment = 1,
                    phase = "Completed", chunks = chunkJson
                }
            }
        });
        var files = chunks.Select((chunk, index) => new NoteMaterialUploadFileDto(index,
            NoteAudioFormats.Path(recordingId, 1, index + 1,
                chunk.Format == "opus" ? NoteAudioFormat.Opus : NoteAudioFormat.Wav),
            "AudioChunk", new string('a', 64), 100, 1, [0])).ToArray();
        var upload = new NoteMaterialUploadDto(versionId, noteId, null, null, "Uploaded",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false,
            new(NoteMaterialUploadOptions.PartSizeBytes, NoteMaterialUploadOptions.MaxMetadataCharacters,
                1, 1, 20_000, 10, 2_000, 168), files);
        return new(upload, inputJson, "{}");
    }
}
