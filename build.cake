//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////
#tool nuget:?package=Newtonsoft.Json&version=13.0.1
#addin nuget:?package=Cake.Http&version=1.3.0

using Path = System.IO.Path;
using IO = System.IO;
using System.Text.RegularExpressions;
using Cake.Common.Tools;
using Newtonsoft.Json.Linq;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var package = Argument("package", string.Empty);

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////
var localPackagesDir = "../LocalPackages";
var buildDir = @".\build";
var unpackFolder = Path.Combine(buildDir, "temp");
var unpackFolderFullPath = Path.GetFullPath(unpackFolder);
var artifactsDir = @".\artifacts";
var file = "AzCmdlets.msi";
var nugetVersion = string.Empty;
var nugetPackageFile = string.Empty;

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////
Setup(context =>
{

});

Teardown(context =>
{
    Information("Finished running tasks.");
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectory(unpackFolder);
    CleanDirectory(buildDir);
    CleanDirectory(artifactsDir);
});

Task("Restore-Source-Package")
    .IsDependentOn("Clean")
    .Does(() =>
{
    var releaseUrl = $"https://api.github.com/repos/Azure/azure-powershell/releases";
    Information($"Getting {releaseUrl}");
    var releaseJson = HttpGet(releaseUrl);
    JArray releases = JArray.Parse(releaseJson);
    var release =  (from r in releases.Children()
		            let assets = r["assets"]
		            where assets.Any(x => x.Value<string>("name").EndsWith("msi") && x.Value<string>("name").StartsWith("Az-Cmdlets"))
                    && (package == string.Empty || package == "latest" ? true : r["name"].ToString() == package)
		            && r["prerelease"].Value<bool>() == false
		            select new { Name = r["name"], Url = assets.First()["browser_download_url"]}).First();
    var packageDownloadUrl = release.Url.ToString();
    var releaseName = release.Name.ToString();
    var azReleasePrefix = "Az ";
    nugetVersion = releaseName.Substring(azReleasePrefix.Length);
    var outputPath = File($"{buildDir}/{file}");
    Information($"Downloading {packageDownloadUrl}");
    DownloadFile(packageDownloadUrl, outputPath);
});

Task("Unpack-Source-Package")
    .IsDependentOn("Restore-Source-Package")
    .IsDependentOn("Clean")
    .Does(() =>
{
    var sourcePackage = file;

    Information($"Unpacking {sourcePackage}");

    var processArgumentBuilder = new ProcessArgumentBuilder();
    processArgumentBuilder.Append($"/a {sourcePackage}");
    processArgumentBuilder.Append("/qn");
    processArgumentBuilder.Append($"TARGETDIR={unpackFolderFullPath}");
    var processSettings = new ProcessSettings { Arguments = processArgumentBuilder, WorkingDirectory = buildDir };
    StartProcess("msiexec.exe", processSettings);
    Information($"Unpacked {sourcePackage} to {unpackFolderFullPath}");
});

Task("Pack")
    .IsDependentOn("Unpack-Source-Package")
    .Does(() =>
{
    var fileWithoutExtension = Path.GetFileNameWithoutExtension(file);
    Information($"Building Octopus.Dependencies.AzureCmdlets v{nugetVersion}");
    NuGetPack("Octopus.Dependencies.AzureCmdlets.nuspec", new NuGetPackSettings {
        BasePath = Path.Combine(unpackFolder),
        OutputDirectory = artifactsDir,
        ArgumentCustomization = args => args.Append($"-Properties \"version={nugetVersion}\"")
    });
});

Task("Publish")
    .WithCriteria(BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Pack")
    .Does(() =>
{
    NuGetPush($"{artifactsDir}/Octopus.Dependencies.AzureCmdlets.{nugetVersion}.nupkg", new NuGetPushSettings {
        Source = "https://f.feedz.io/octopus-deploy/dependencies/nuget",
        ApiKey = EnvironmentVariable("FeedzIoApiKey")
    });
});

Task("CopyToLocalPackages")
    .WithCriteria(BuildSystem.IsLocalBuild)
    .IsDependentOn("Pack")
    .Does(() =>
{
    CreateDirectory(localPackagesDir);
    CopyFileToDirectory(Path.Combine(artifactsDir, $"Octopus.Dependencies.AzureCmdlets.{nugetVersion}.nupkg"), localPackagesDir);
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("FullChain")
    .IsDependentOn("Clean")
    .IsDependentOn("Restore-Source-Package")
    .IsDependentOn("Unpack-Source-Package")
    .IsDependentOn("Pack")
    .IsDependentOn("Publish")
    .IsDependentOn("CopyToLocalPackages");

Task("Default").IsDependentOn("FullChain");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);