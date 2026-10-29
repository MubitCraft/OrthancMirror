using System.Diagnostics;
using Microsoft.Extensions.Options;
using OrthancWadoProxy.Configuration;

namespace OrthancWadoProxy.Services;

public sealed class WsiDicomizerClient(IOptions<WsiDicomizerOptions> options)
{
    private readonly WsiDicomizerOptions settings = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.ExecutablePath) &&
        Path.IsPathFullyQualified(settings.ExecutablePath) &&
        File.Exists(settings.ExecutablePath);

    public void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new WsiPipelineUnavailableException(
                "WSI conversion is unavailable. Configure WsiDicomizer:ExecutablePath with the full path to OrthancWSIDicomizer; ensure the path is valid and the executable exists.");
        }

        if (settings.Threads < 0 || settings.TimeoutHours <= 0)
        {
            throw new WsiPipelineUnavailableException("WSI DICOM-izer thread and timeout settings are invalid.");
        }
    }

    public async Task ConvertAsync(string sourcePath, string outputDirectory, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.ExecutablePath,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add($"--folder={outputDirectory}");

        if (settings.GeneratePyramid)
        {
            startInfo.ArgumentList.Add("--pyramid=1");
            if (settings.SmoothPyramid)
            {
                startInfo.ArgumentList.Add("--smooth=1");
            }
        }

        if (settings.Threads > 0)
        {
            startInfo.ArgumentList.Add($"--threads={settings.Threads}");
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new WsiConversionException("OrthancWSIDicomizer could not be started.");
        }

        var stdoutDrain = DrainAsync(process.StandardOutput);
        var stderrDrain = DrainAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(settings.TimeoutHours));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutDrain, stderrDrain);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("OrthancWSIDicomizer exceeded its configured processing timeout.");
        }

        await Task.WhenAll(stdoutDrain, stderrDrain);
        if (process.ExitCode != 0)
        {
            throw new WsiConversionException(
                $"OrthancWSIDicomizer failed with exit code {process.ExitCode}; verify the source format and installed WSI build.");
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None) is > 0)
        {
        }
    }
}
