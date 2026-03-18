using System.Diagnostics;

namespace Rivet.Tool.Compile;

/// <summary>
/// Bootstraps Node dependencies (typescript, typia) to ~/.rivet/
/// and compiles validators.ts via tsc + typia transformer.
/// </summary>
public static class TypiaCompiler
{
    private static readonly string RivetHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".rivet");

    public static async Task<bool> CompileAsync(string outputDir)
    {
        await EnsureNodeDepsAsync();
        GenerateTsConfig(outputDir);
        return await RunTscAsync(outputDir);
    }

    private static async Task EnsureNodeDepsAsync()
    {
        Directory.CreateDirectory(RivetHome);

        var packageJsonPath = Path.Combine(RivetHome, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            await File.WriteAllTextAsync(packageJsonPath, """
                {
                  "name": "rivet-compiler",
                  "private": true,
                  "dependencies": {
                    "typescript": "^5.0.0",
                    "ts-patch": "^3.0.0",
                    "typia": "^7.0.0"
                  }
                }
                """);
        }

        var nodeModulesDir = Path.Combine(RivetHome, "node_modules");
        if (!Directory.Exists(nodeModulesDir))
        {
            Console.Error.WriteLine("Installing typia + typescript + ts-patch...");
            var result = await RunProcessAsync("npm", "install", RivetHome);
            if (!result)
            {
                throw new InvalidOperationException("Failed to install Node dependencies. Is node/npm on PATH?");
            }

            // Patch tsc so it supports transformer plugins
            Console.Error.WriteLine("Patching tsc with ts-patch...");
            var patchResult = await RunProcessAsync(
                Path.Combine(nodeModulesDir, ".bin", "ts-patch"),
                "install",
                RivetHome);
            if (!patchResult)
            {
                throw new InvalidOperationException("Failed to patch tsc with ts-patch.");
            }
        }
    }

    private static void GenerateTsConfig(string outputDir)
    {
        var buildDir = Path.Combine(outputDir, "build");
        Directory.CreateDirectory(buildDir);

        var tsconfigPath = Path.Combine(buildDir, "tsconfig.json");

        // Resolve paths: parent for source, ~/.rivet/node_modules for deps
        var parentRelative = "..";
        var nodeModulesPath = Path.GetFullPath(Path.Combine(RivetHome, "node_modules"));

        var tsconfig = $$"""
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "ES2022",
                "moduleResolution": "bundler",
                "declaration": true,
                "outDir": ".",
                "rootDir": "{{parentRelative}}",
                "strict": true,
                "esModuleInterop": true,
                "skipLibCheck": true,
                "baseUrl": ".",
                "paths": {
                  "typia": ["{{nodeModulesPath}}/typia"],
                  "typia/*": ["{{nodeModulesPath}}/typia/*"]
                },
                "plugins": [
                  { "transform": "typia/lib/transform" }
                ]
              },
              "include": [
                "{{parentRelative}}/types.ts",
                "{{parentRelative}}/validators.ts"
              ]
            }
            """;

        File.WriteAllText(tsconfigPath, tsconfig);
    }

    private static async Task<bool> RunTscAsync(string outputDir)
    {
        var buildDir = Path.Combine(outputDir, "build");
        var tsconfigPath = Path.Combine(buildDir, "tsconfig.json");

        // Use the patched tsc directly from our managed node_modules
        var tscPath = Path.Combine(RivetHome, "node_modules", ".bin", "tsc");

        Console.Error.WriteLine("Compiling validators with typia...");

        var nodeModulesPath = Path.Combine(RivetHome, "node_modules");
        var result = await RunProcessAsync(
            tscPath,
            $"--project \"{tsconfigPath}\"",
            outputDir,
            new Dictionary<string, string> { ["NODE_PATH"] = nodeModulesPath });

        return result;
    }

    private static async Task<bool> RunProcessAsync(
        string command,
        string arguments,
        string workingDirectory,
        Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {command}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.Error.WriteLine(stdout);
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.WriteLine(stderr);
            }
            return false;
        }

        return true;
    }
}
