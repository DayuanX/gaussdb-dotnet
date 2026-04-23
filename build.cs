var hasExplicitTarget = Array.Exists(args, arg => arg == "--target" || arg.StartsWith("--target=", StringComparison.Ordinal));
var effectiveArgs = args;
if (!hasExplicitTarget)
{
    effectiveArgs = new string[args.Length + 1];
    effectiveArgs[0] = "--target=publish";
    Array.Copy(args, 0, effectiveArgs, 1, args.Length);
}

var target = CommandLineParser.Val(effectiveArgs, "target", "publish");
var apiKey = CommandLineParser.Val(effectiveArgs, "apiKey");
var noPush = CommandLineParser.BooleanVal(effectiveArgs, "noPush");
var version = Environment.GetEnvironmentVariable("VERSION");
var stable = CommandLineParser.BooleanVal(effectiveArgs, "stable") || !string.IsNullOrEmpty(version);
var runningOnGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
const string BaselineGitHubActionsTestFilter =
    "FullyQualifiedName!~HuaweiCloud.GaussDB.Tests.Replication&" +
    "FullyQualifiedName!~HuaweiCloud.GaussDB.Tests.SecurityTests&" +
    "FullyQualifiedName!~Open_physical_failure&" +
    "FullyQualifiedName!~BaseColumnName_with_column_aliases";
var githubActionsTestFilter = Environment.GetEnvironmentVariable("GAUSSDB_TEST_FILTER");
if (string.IsNullOrEmpty(githubActionsTestFilter) && runningOnGithubActions)
    githubActionsTestFilter = BaselineGitHubActionsTestFilter;

Console.WriteLine($$"""
Arguments:

target: {{target}}
stable: {{stable}}
noPush: {{noPush}}
args:
{{effectiveArgs.StringJoin("\n")}}

""");

var solutionPath = "./GaussDB.slnx";
string[] srcProjects = [
    "./src/GaussDB/GaussDB.csproj"
];
string[] testProjects = [
    "./test/GaussDB.Tests/GaussDB.Tests.csproj",
    "./test/GaussDB.DependencyInjection.Tests/GaussDB.DependencyInjection.Tests.csproj"
];

var process = DotNetPackageBuildProcess.Create(options =>
{
    options.SolutionPath = solutionPath;
    options.SrcProjects = srcProjects;
    options.TestProjects = testProjects;
    options.ArtifactsPath = "./artifacts/packages";

    options.WithTaskConfigure("build", task => task
        .WithDescription("build")
        .WithExecution(cancellationToken => ExecuteCommandAsync($"dotnet build {solutionPath}", cancellationToken)));

    options.WithTaskConfigure("test", task => task
        .WithDescription("dotnet test")
        .WithDependency("build")
        .WithExecution(async cancellationToken =>
        {
            foreach (var project in testProjects)
            {
                var loggerOptions = runningOnGithubActions
                    ? "--logger GitHubActions"
                    : "--logger \"console;verbosity=d\"";
                var filterOptions = string.Empty;
                if (!string.IsNullOrEmpty(githubActionsTestFilter) &&
                    project.EndsWith("GaussDB.Tests.csproj", StringComparison.Ordinal))
                {
                    filterOptions = $" --filter \"{githubActionsTestFilter}\"";
                }

                var command =
                    $"dotnet test --blame --collect:\"XPlat Code Coverage;Format=cobertura,opencover;ExcludeByAttribute=ExcludeFromCodeCoverage,Obsolete,GeneratedCode,CompilerGenerated\" {loggerOptions}{filterOptions} -v=d {project}";
                await ExecuteCommandAsync(command, cancellationToken);
            }
        }));

    options.WithTaskConfigure("publish", task => task
        .WithDescription("dotnet pack")
        .WithDependency("build")
        .WithExecution(PackAndMaybePushAsync));
});

Console.WriteLine("Cleaning previous package artifacts if they exist.");
if (Directory.Exists("./artifacts/packages"))
    Directory.Delete("./artifacts/packages", true);

await process.ExecuteAsync(effectiveArgs, ApplicationHelper.ExitToken);

async Task PackAndMaybePushAsync(CancellationToken cancellationToken)
{
    // The script owns package cleanup and publishing so local and CI runs stay aligned.
    if (Directory.Exists("./artifacts/packages"))
        Directory.Delete("./artifacts/packages", true);

    var packOptions = " -o ./artifacts/packages";
    if (stable)
    {
        if (!string.IsNullOrEmpty(version))
            packOptions += $" -p VersionPrefix={version}";
    }
    else
    {
        var suffix = $"preview-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        packOptions += $" --version-suffix {suffix}";
    }

    foreach (var project in srcProjects)
        await ExecuteCommandAsync($"dotnet pack {project} {packOptions}", cancellationToken);

    if (noPush)
    {
        Console.WriteLine("Skip push there's noPush specified");
        return;
    }

    if (string.IsNullOrEmpty(apiKey))
    {
        apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Skip push since there's no apiKey found");
            return;
        }
    }

    foreach (var file in Directory.GetFiles("./artifacts/packages/", "*.nupkg"))
    {
        await RetryHelper.TryInvokeAsync(
            () => ExecuteCommandAsync($"dotnet nuget push {file} -s https://api.nuget.org/v3/index.json -k {apiKey} --skip-duplicate", cancellationToken),
            cancellationToken: cancellationToken);
    }
}

async Task ExecuteCommandAsync(string commandText, CancellationToken cancellationToken = default)
{
    Console.WriteLine($"Executing command: \n    {commandText}");
    Console.WriteLine();

    var result = await CommandExecutor.ExecuteCommandAndOutputAsync(commandText, cancellationToken: cancellationToken);
    result.EnsureSuccessExitCode();
    Console.WriteLine();
}
