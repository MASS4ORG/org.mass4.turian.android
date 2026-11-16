namespace Turian.Tests;

/// <summary>Release qualification rejects incomplete installations and tool versions outside the shared pins.</summary>
public sealed class AndroidToolchainQualificationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-pinned-tools-").FullName;
    readonly string sdk;
    readonly string jdk;
    static IReadOnlyDictionary<string, string> Pins => AndroidToolchainQualification.Pins;
    const string SdkVersion = "11.0.100";

    /// <summary>Creates a complete SDK and release-pinned JDK without starting any external tools.</summary>
    public AndroidToolchainQualificationTests()
    {
        sdk = Path.Combine(root, "sdk");
        jdk = AndroidFakeTools.Jdk(Path.Combine(root, "jdk"), "17.0.12");
        Write(Path.Combine("platforms", Pins["ANDROID_PLATFORM_PACKAGE"].Split(';')[1], "android.jar"));
        Write(Path.Combine("platform-tools", Tool("adb")));
        foreach (var tool in new[] { "aapt2", "zipalign", "apksigner" })
        {
            Write(Path.Combine("build-tools", Pins["ANDROID_BUILD_TOOLS_VERSION"], Tool(tool)));
        }
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(root, recursive: true);

    static string Tool(string tool) => OperatingSystem.IsWindows()
        ? tool + (tool == "apksigner" ? ".bat" : ".exe") : tool;

    void Write(string relative)
    {
        var path = Path.Combine(sdk, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    BuildPlatformStatus Inspect(string? actualSdk = SdkVersion, string? workload = null, string workloads = "android 37.2.29") =>
        AndroidToolchainQualification.Inspect(sdk, jdk, SdkVersion, actualSdk!,
            workload ?? Pins["ANDROID_WORKLOAD_SET"], workloads);

    /// <summary>A complete installation reports the actual qualified identities.</summary>
    [Fact]
    public void CompletePinnedInstallationIsReady()
    {
        var result = Inspect();
        Assert.True(result.IsReady, result.Message);
        Assert.Contains(SdkVersion, result.Message);
        Assert.Contains(Pins["ANDROID_BUILD_TOOLS_VERSION"], result.Message);
        Assert.Contains(sdk, result.Message);
        Assert.Contains(jdk, result.Message);
    }

    /// <summary>The earlier broad 37.x workload check cannot hide an SDK or workload-set mismatch.</summary>
    [Theory]
    [InlineData("11.0.101", "11.0.100", ".NET SDK 11.0.101 differs")]
    [InlineData("11.0.100-rc.3", "11.0.100", ".NET SDK 11.0.100-rc.3 differs")]
    [InlineData("11.0.100", "11.0.101", "workload set 11.0.101 differs")]
    public void VersionMismatchesNameBothVersions(string sdkVersion, string workload, string problem)
    {
        var result = Inspect(sdkVersion, workload);
        Assert.False(result.IsReady);
        Assert.Contains(problem, result.Message);
        Assert.Contains(SdkVersion, result.Message);
    }

    /// <summary>An installed workload set alone does not establish that Android was installed.</summary>
    [Fact]
    public void MissingAndroidWorkloadHasInstallationAdvice()
    {
        var result = Inspect(workloads: "ios 27.2.11039");
        Assert.False(result.IsReady);
        Assert.Contains("Android workload is missing", result.Message);
        Assert.Contains("dotnet workload install android --version", result.Message);
    }

    /// <summary>Every required SDK component is checked, including files inside an otherwise present directory.</summary>
    [Theory]
    [InlineData("platform")]
    [InlineData("adb")]
    [InlineData("aapt2")]
    [InlineData("zipalign")]
    [InlineData("apksigner")]
    public void MissingSdkComponentIsNotReady(string component)
    {
        var relative = component switch
        {
            "platform" => Path.Combine("platforms", Pins["ANDROID_PLATFORM_PACKAGE"].Split(';')[1], "android.jar"),
            "adb" => Path.Combine("platform-tools", Tool("adb")),
            _ => Path.Combine("build-tools", Pins["ANDROID_BUILD_TOOLS_VERSION"], Tool(component)),
        };
        File.Delete(Path.Combine(sdk, relative));
        var result = Inspect();
        Assert.False(result.IsReady);
        Assert.Contains(relative, result.Message);
    }

    /// <summary>An older or newer unqualified JDK is identified instead of silently accepted.</summary>
    [Theory]
    [InlineData("11.0.2")]
    [InlineData("21.0.4")]
    [InlineData(null)]
    public void JdkVersionMustMatchTheReleasePin(string? version)
    {
        File.Delete(Path.Combine(jdk, "release"));
        AndroidFakeTools.Jdk(jdk, version);
        var result = Inspect();
        Assert.False(result.IsReady);
        Assert.Contains("differs from release pin 17", result.Message);
    }

    /// <summary>A version file without a compiler does not make a JDK usable.</summary>
    [Fact]
    public void MissingJavaCompilerIsNotReady()
    {
        File.Delete(Path.Combine(jdk, "bin", Tool("javac")));
        Assert.False(Inspect().IsReady);
    }

    /// <summary>Absent SDK and JDK are reported together, with the settings location.</summary>
    [Fact]
    public void MissingSdkAndJdkAreBothReported()
    {
        var result = AndroidToolchainQualification.Inspect(null, null, SdkVersion, SdkVersion,
            Pins["ANDROID_WORKLOAD_SET"], "android 37.2.29");
        Assert.False(result.IsReady);
        Assert.Contains("Android SDK missing", result.Message);
        Assert.Contains("JDK unknown/missing", result.Message);
        Assert.Contains("Settings, Build, Android", result.Message);
    }
    /// <summary>SDK queries use a disposable release-pinned project and remove it after qualification.</summary>
    [Fact]
    public void QueriesRunWithAnIsolatedStableSdkPin()
    {
        string? queriedDirectory = null;
        var result = AndroidToolchainQualification.Check(sdk, jdk, SdkVersion, () => "dotnet",
            (_, directory, arguments) =>
            {
                queriedDirectory = directory;
                using var global = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "global.json")));
                var policy = global.RootElement.GetProperty("sdk");
                Assert.Equal(SdkVersion, policy.GetProperty("version").GetString());
                Assert.False(policy.GetProperty("allowPrerelease").GetBoolean());
                return arguments switch
                {
                    ["--version"] => SdkVersion,
                    ["workload", "--version"] => Pins["ANDROID_WORKLOAD_SET"],
                    _ => "android 37.2.29",
                };
            });
        Assert.True(result.IsReady, result.Message);
        Assert.NotNull(queriedDirectory);
        Assert.False(Directory.Exists(queriedDirectory));
    }

    /// <summary>Discovery and query failures produce actionable missing status and leave no temporary project.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedQueriesAreReportedAndCleanedUp(bool discovery)
    {
        string? queriedDirectory = null;
        var result = AndroidToolchainQualification.Check(sdk, jdk, SdkVersion,
            () => discovery ? throw new InvalidOperationException("No matching SDK") : "dotnet",
            (_, directory, _) =>
            {
                queriedDirectory = directory;
                throw new IOException("Cannot read workload metadata");
            });
        Assert.False(result.IsReady);
        Assert.Contains(discovery ? "No matching SDK" : "Cannot read workload metadata", result.Message);
        Assert.True(queriedDirectory is null || !Directory.Exists(queriedDirectory));
    }

    /// <summary>The bounded SDK process reader returns output and reports unsuccessful commands.</summary>
    [Fact]
    public void SdkQueriesReturnOutputAndRejectFailures()
    {
        var dotnet = DotnetLocator.Find();
        Assert.NotEmpty(AndroidToolchainQualification.Query(dotnet, root, "--version"));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AndroidToolchainQualification.Query(dotnet, root, "--invalid-turian-toolchain-option"));
        Assert.Contains("failed", error.Message);
    }
}