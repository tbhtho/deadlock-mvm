using System.Diagnostics;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>Launches the Deadlock executable with Movie Mode arguments.</summary>
public sealed class GameLauncher : IGameLauncher
{
    public LaunchResult Launch(LaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || !File.Exists(request.ExecutablePath))
            return LaunchResult.Fail($"Executable not found: {request.ExecutablePath}");

        var arguments = request.BaseArguments.Concat(request.ExtraArguments).ToArray();
        var commandLine = CommandLine.Join(arguments);

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetDirectoryName(request.ExecutablePath)
            : request.WorkingDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            return LaunchResult.Ok(process.Id, commandLine);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail($"Failed to launch Deadlock: {ex.Message}");
        }
    }
}
