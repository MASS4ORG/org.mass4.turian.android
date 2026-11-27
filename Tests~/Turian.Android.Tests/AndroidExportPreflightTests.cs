using System.Reflection;

namespace Turian.Tests;

/// <summary>Checks signing before export setup and local development profile selection.</summary>
[Collection(SerialTests.Name)]
public sealed class AndroidExportPreflightTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-android-preflight-").FullName;
    readonly AppSettings settings;

    /// <summary>Creates source settings without compiling or importing assets.</summary>
    public AndroidExportPreflightTests()
    {
        settings = new AppSettings { ProjectAbsoluteDir = root };
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        ProjectSettingsFiles.CreateProjectAsset(settings, [], []);
        ProjectSettingsFiles.Create(settings, new PlayerSettings { ApplicationIdentifier = "org.example.app" });
        ProjectSettingsFiles.Create(settings, new AndroidPlayerSettings());
    }

    /// <summary>Removes the fixture project.</summary>
    public void Dispose() => Directory.Delete(root, recursive: true);

    ProjectBuildProfile Profile => new("Android Client", string.Empty,
        new BuildProfile { Platform = "android", Configuration = BuildConfiguration.Release });

    /// <summary>A signing failure precedes project generation, restore and content staging.</summary>
    [Fact]
    public async Task ReleaseFailsBeforeGeneratingBuildInputs()
    {
        var exporter = new ExportUserCode(settings, NullLogger.Instance, profile: Profile);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(exporter.ExecuteAsync);

        Assert.Contains("needs a keystore", error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, ".Cache")));
        Assert.False(Directory.Exists(Path.Combine(root, "Output")));
    }

    /// <summary>Local deployment uses development signing without changing the authored release profile.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalDeploymentSelectsTheSigningPolicy(bool development)
    {
        var profile = Profile;
        var exporter = new ExportUserCode(settings, NullLogger.Instance, profile: profile,
            deployment: new BuildDeployment(null) { Development = development });
        var effective = (BuildProfile)typeof(ExportUserCode)
            .GetProperty("SettingsOverrides", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(exporter)!;
        ProjectSettingsLoader.LoadFromSources(settings, effective);
        var context = new BuildPlatformContext(new BuildAppSettings().Load(settings) as BuildAppSettings
            ?? throw new InvalidOperationException(), NullLogger.Instance, string.Empty, root, "Release", effective);

        Assert.Equal(development, effective.Development);
        Assert.False(profile.Profile.Development);
        var problem = new AndroidBuildPlatform().ValidateExport(context);
        if (development) { Assert.Null(problem); }
        else { Assert.Contains("needs a keystore", problem); }
    }

    /// <summary>Missing toolchain components fail before local development starts compiling.</summary>
    [Fact]
    public void IncompleteToolsFailBeforeGeneratingBuildInputs()
    {
        using var tools = AndroidFakeTools.Use(root);
        var expected = new AndroidBuildPlatform().CheckToolchain();
        Assert.False(expected.IsReady);
        var exporter = new ExportUserCode(settings, NullLogger.Instance, profile: Profile,
            deployment: new BuildDeployment(null) { Development = true });

        var error = Assert.Throws<InvalidOperationException>(exporter.Validate);

        Assert.Equal(expected.Message, error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, ".Cache")));
        Assert.False(Directory.Exists(Path.Combine(root, "Output")));
    }

    /// <summary>Validation loads profile-specific settings before checking platform requirements.</summary>
    [Fact]
    public void ValidationUsesProfileOverrides()
    {
        var overridePath = ProjectSettingsFiles.Create(settings,
            new PlayerSettings { ApplicationIdentifier = "invalid" });
        var profile = Profile;
        profile.Profile.Settings.Add(new DataAssetReference<ProjectSettingsAsset>(Asset.Load(overridePath + ".meta")!.Id));
        var exporter = new ExportUserCode(settings, NullLogger.Instance, profile: profile);

        Assert.Contains("valid Android application id", Assert.Throws<InvalidOperationException>(exporter.Validate).Message);
    }
}
