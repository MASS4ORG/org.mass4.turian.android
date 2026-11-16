using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Turian.Editor.Core;

/// <summary>Checks the export tools against the engine release's shared Android pins.</summary>
internal static class AndroidToolchainQualification
{
    internal static readonly IReadOnlyDictionary<string, string> Pins = ReadPins();

    static IReadOnlyDictionary<string, string> ReadPins()
    {
        using var stream = typeof(AndroidToolchainQualification).Assembly
            .GetManifestResourceStream("Turian.Android.ToolchainPins")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n')
            .Where(static line => line.StartsWith("ANDROID_", StringComparison.Ordinal))
            .Select(static line => line.Split('=', 2))
            .ToDictionary(static parts => parts[0], static parts => parts[1].Trim().Trim('"'));
    }

    internal static BuildPlatformStatus Check(string? sdk, string? jdk, string expectedSdk,
        Func<string>? findDotnet = null, Func<string, string, string[], string>? query = null)
    {
        try
        {
            var dotnet = findDotnet?.Invoke() ?? DotnetLocator.Find(requiredSdkMajor: 11);
            query ??= Query;
            var directory = Directory.CreateTempSubdirectory("turian-toolchain-");
            try
            {
                File.WriteAllText(Path.Combine(directory.FullName, "global.json"), JsonSerializer.Serialize(new
                {
                    sdk = new { version = expectedSdk, rollForward = "latestPatch", allowPrerelease = false },
                }));
                return Inspect(sdk, jdk, expectedSdk, query(dotnet, directory.FullName, ["--version"]),
                    query(dotnet, directory.FullName, ["workload", "--version"]),
                    query(dotnet, directory.FullName, ["workload", "list"]));
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
                                      or Win32Exception or TimeoutException)
        {
            return new(false, error.Message);
        }
    }

    internal static BuildPlatformStatus Inspect(string? sdk, string? jdk, string expectedSdk,
        string sdkVersion, string workloadVersion, string workloads)
    {
        List<string> failures = [];
        CheckVersions(expectedSdk, sdkVersion, workloadVersion, workloads, failures);
        CheckSdk(sdk, failures);
        CheckJdk(jdk, failures);
        return failures.Count > 0 ? new(false, string.Join(" ", failures))
            : new(true, $".NET SDK {expectedSdk}; Android workload set {Pins["ANDROID_WORKLOAD_SET"]}; "
                + $"{Pins["ANDROID_PLATFORM_PACKAGE"]}; build-tools {Pins["ANDROID_BUILD_TOOLS_VERSION"]}; "
                + $"JDK 17. Android SDK at {sdk}, JDK at {jdk}.");
    }

    static void CheckVersions(string expectedSdk, string sdkVersion, string workloadVersion, string workloads,
        List<string> failures)
    {
        if (sdkVersion.Trim() != expectedSdk)
        {
            failures.Add($".NET SDK {sdkVersion.Trim()} differs from release pin {expectedSdk}.");
        }
        var workload = Pins["ANDROID_WORKLOAD_SET"];
        if (workloadVersion.Trim() != workload)
        {
            failures.Add($"Android workload set {workloadVersion.Trim()} differs from release pin {workload}; "
                + $"run dotnet workload install android --version {workload}.");
        }
        if (!workloads.Split('\n').Any(static line => line.TrimStart().StartsWith("android ", StringComparison.Ordinal)))
        {
            failures.Add($"The Android workload is missing; run dotnet workload install android --version {workload}.");
        }

    }

    static void CheckSdk(string? sdk, List<string> failures)
    {
        var platform = Pins["ANDROID_PLATFORM_PACKAGE"].Split(';')[1];
        var buildTools = Pins["ANDROID_BUILD_TOOLS_VERSION"];
        if (sdk is null)
        {
            failures.Add("Android SDK missing; set its folder in Settings, Build, Android.");
        }
        else
        {
            Require(sdk, Path.Combine("platforms", platform, "android.jar"), failures);
            Require(sdk, Path.Combine("platform-tools", Executable("adb")), failures);
            foreach (var tool in new[] { "aapt2", "zipalign", OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner" })
            {
                Require(sdk, Path.Combine("build-tools", buildTools,
                    tool == "apksigner.bat" ? tool : Executable(tool)), failures);
            }
        }

    }

    static void CheckJdk(string? jdk, List<string> failures)
    {
        var expectedJdk = int.Parse(Pins["ANDROID_JDK_PACKAGE"].Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
        var jdkVersion = jdk is null ? null : AndroidToolchain.JdkVersion(jdk);
        if (jdkVersion != expectedJdk || jdk is null || !File.Exists(Path.Combine(jdk, "bin", Executable("javac"))))
        {
            failures.Add($"JDK {jdkVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown/missing"} "
                + $"differs from release pin {expectedJdk}; set the JDK folder in Settings, Build, Android.");
        }

    }

    static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    static void Require(string sdk, string relative, List<string> failures)
    {
        if (!File.Exists(Path.Combine(sdk, relative)))
        {
            failures.Add($"Android SDK missing {relative}; install the release-pinned platform, build-tools and platform-tools.");
        }
    }

    internal static string Query(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish within 15 seconds.");
        }
        var result = output.GetAwaiter().GetResult().Trim();
        var diagnostic = error.GetAwaiter().GetResult().Trim();
        return process.ExitCode == 0 ? result
            : throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed: {result} {diagnostic}");
    }
}
