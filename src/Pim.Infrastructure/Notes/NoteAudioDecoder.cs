using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Converts one immutable Ogg Opus source file into a bounded, sample-exact 16 kHz mono
/// WAV slice. The provider never receives the compressed source and no shell is involved.
/// FFmpeg diagnostics stay in memory and are never copied into task errors or logs.
/// </summary>
internal sealed class NoteAudioDecoder(NoteMaterialUploadOptions options)
{
    internal async Task<byte[]> DecodeOpusSliceAsync(string sourcePath, long offsetSamples,
        long samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executable = DecoderPath();
        var decodeTimeout = DecodeTimeout();
        var expectedBytes = checked((int)samples * 2);
        var start = BuildStartInfo(executable, sourcePath, offsetSamples, samples);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("process start returned null");
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            throw new DomainException(50303,
                "服务器 Ogg Opus 解码器不可用；WAV 录音不受影响，请联系管理员检查 FFmpeg");
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(decodeTimeout);
            var stderr = DrainAsync(process.StandardError.BaseStream, timeout.Token);
            try
            {
                var pcm = await ReadBoundedAsync(process.StandardOutput.BaseStream,
                    expectedBytes, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    throw new DomainException(40001,
                        "Ogg Opus 录音无法安全解码，未发送至转写服务；请确认录音完整后重试");
                return Wav(pcm);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                throw new DomainException(40001,
                    "Ogg Opus 录音解码超时，未发送至转写服务；请确认录音完整后重试");
            }
            catch (IOException)
            {
                Kill(process);
                throw new DomainException(40001,
                    "Ogg Opus 录音无法安全解码，未发送至转写服务；请确认录音完整后重试");
            }
            catch
            {
                Kill(process);
                throw;
            }
            finally
            {
                try { await stderr; }
                catch { }
            }
        }
    }

    private string DecoderPath()
    {
        var configured = options.FfmpegPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured) || configured.Length > 512 ||
            configured.Any(char.IsControl))
            throw Unavailable();
        return configured;
    }

    private TimeSpan DecodeTimeout() => options.AudioDecodeTimeoutSeconds is >= 10 and <= 600
        ? TimeSpan.FromSeconds(options.AudioDecodeTimeoutSeconds)
        : throw Unavailable();

    private static DomainException Unavailable() => new(50303,
        "服务器 Ogg Opus 解码器不可用；WAV 录音不受影响，请联系管理员检查 FFmpeg");

    private static ProcessStartInfo BuildStartInfo(string executable, string sourcePath,
        long offsetSamples, long samples)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror",
                     "-threads", "1", "-max_alloc", "67108864",
                     "-probesize", "1048576", "-analyzeduration", "2000000",
                     "-protocol_whitelist", "file,pipe", "-f", "ogg", "-i", sourcePath,
                     "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar",
                     NoteAsrPlan.SampleRate.ToString(CultureInfo.InvariantCulture), "-af",
                     $"aresample={NoteAsrPlan.SampleRate},atrim=start_sample=" +
                     $"{offsetSamples.ToString(CultureInfo.InvariantCulture)}:end_sample=" +
                     $"{checked(offsetSamples + samples).ToString(CultureInfo.InvariantCulture)}",
                     "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"
                 })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int expectedBytes,
        CancellationToken ct)
    {
        using var output = new MemoryStream(expectedBytes);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > expectedBytes)
                throw new DomainException(40001,
                    "Ogg Opus 录音解码后的样本数与固定材料不一致，未发送至转写服务");
            output.Write(buffer, 0, read);
        }
        if (output.Length != expectedBytes)
            throw new DomainException(40001,
                "Ogg Opus 录音解码后的样本数与固定材料不一致，未发送至转写服务");
        return output.ToArray();
    }

    private static async Task DrainAsync(Stream source, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (await source.ReadAsync(buffer, ct) != 0) { }
    }

    private static byte[] Wav(byte[] pcm)
    {
        var bytes = new byte[checked(44 + pcm.Length)];
        NoteAudioFormats.WriteWavHeader(bytes, pcm.Length);
        pcm.CopyTo(bytes, 44);
        return bytes;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or
            NotSupportedException or AggregateException) { }
    }
}
