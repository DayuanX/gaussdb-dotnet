var target = CommandLineParser.Val(args, "target", "Default");
var apiKey = CommandLineParser.Val(args, "apiKey");
var noPush = CommandLineParser.BooleanVal(args, "noPush");
var version = Environment.GetEnvironmentVariable("VERSION");
var stable = CommandLineParser.BooleanVal(args, "stable") || !string.IsNullOrEmpty(version);
var runningOnGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

Console.WriteLine($$"""
Arguments:

target: {{target}}
stable: {{stable}}
noPush: {{noPush}}
args:
{{args.StringJoin("\n")}}

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
                var command = $"dotnet test --blame --collect:\"XPlat Code Coverage;Format=cobertura,opencover;ExcludeByAttribute=ExcludeFromCodeCoverage,Obsolete,GeneratedCode,CompilerGenerated\" {loggerOptions} -v=d {project}";
                await ExecuteCommandAsync(command, cancellationToken);
            }
        }));

    options.WithTaskConfigure("pack", task => task
        .WithDescription("dotnet pack")
        .WithDependency("build")
        .WithExecution(async cancellationToken =>
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
        }));
});

Console.WriteLine("Cleaning previous package artifacts if they exist.");
if (Directory.Exists("./artifacts/packages"))
    Directory.Delete("./artifacts/packages", true);

await process.ExecuteAsync(args, ApplicationHelper.ExitToken);

async Task ExecuteCommandAsync(string commandText, CancellationToken cancellationToken = default)
{
    Console.WriteLine($"Executing command: \n    {commandText}");
    Console.WriteLine();

    var result = await CommandExecutor.ExecuteCommandAndOutputAsync(commandText, cancellationToken: cancellationToken);
    result.EnsureSuccessExitCode();
    Console.WriteLine();
}
