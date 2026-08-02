using System.Diagnostics;
using System.Text;

namespace FishingVisionAssistant.App;

/// <summary>
/// Запускает ML CLI в отдельном процессе и передаёт его журнал в интерфейс.
/// </summary>
public sealed class MlTrainingRunner
{
    private Process? _process;

    public async Task<int> RunAsync(
        string pythonPath,
        string scriptPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Action<string> writeLog,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(writeLog);

        var startInfo = new ProcessStartInfo(pythonPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) => WriteOutput(eventArgs.Data, writeLog);
        process.ErrorDataReceived += (_, eventArgs) => WriteOutput(eventArgs.Data, writeLog);
        _process = process;
        using var cancellationRegistration = cancellationToken.Register(Stop);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить ML-процесс.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }
    }

    public void Stop()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс мог завершиться между проверкой состояния и остановкой.
        }
    }

    private static void WriteOutput(string? text, Action<string> writeLog)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            writeLog(text);
        }
    }
}
