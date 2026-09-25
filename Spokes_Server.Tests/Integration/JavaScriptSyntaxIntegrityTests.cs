using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Spokes_Server.Tests.Integration
{
    public class JavaScriptSyntaxIntegrityTests
    {
        private static readonly Lazy<string> ServerDirectoryLazy = new(FindSpokesServerDirectory);
        private static string ServerDirectory => ServerDirectoryLazy.Value;

        private static string FindSpokesServerDirectory()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var targetDir = Path.Combine(current.FullName, "Spokes_Server");
                if (Directory.Exists(targetDir) && File.Exists(Path.Combine(targetDir, "Spokes_Server.csproj")))
                {
                    return targetDir;
                }
                var csprojInCurrent = Path.Combine(current.FullName, "Spokes_Server.csproj");
                if (File.Exists(csprojInCurrent))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate Spokes_Server directory from AppContext.BaseDirectory: " + AppContext.BaseDirectory);
        }

        private static bool IsNodeAvailable()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit(3000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static (bool Success, string Error) CheckSyntaxWithNode(string filePath, string fileContent)
        {
            bool isEsm = Regex.IsMatch(fileContent, @"^\s*(import|export)\s", RegexOptions.Multiline);
            string testFilePath = filePath;
            bool createdTemp = false;

            try
            {
                if (isEsm && !filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    testFilePath = Path.Combine(Path.GetTempPath(), $"syntax_test_{Guid.NewGuid():N}.mjs");
                    File.WriteAllText(testFilePath, fileContent);
                    createdTemp = true;
                }

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"--check \"{testFilePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode == 0)
                {
                    return (true, string.Empty);
                }

                string err = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                return (false, err);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to invoke node --check: {ex.Message}");
            }
            finally
            {
                if (createdTemp && File.Exists(testFilePath))
                {
                    try { File.Delete(testFilePath); } catch { }
                }
            }
        }

        [Fact]
        public void VoiceInterop_MustExistAndHaveValidSyntax()
        {
            var voiceInteropPath = Path.Combine(ServerDirectory, "wwwroot", "js", "voiceInterop.js");
            Assert.True(File.Exists(voiceInteropPath), $"voiceInterop.js not found at {voiceInteropPath}");

            string content = File.ReadAllText(voiceInteropPath);
            Assert.False(string.IsNullOrWhiteSpace(content), "voiceInterop.js is empty");

            if (IsNodeAvailable())
            {
                var (success, error) = CheckSyntaxWithNode(voiceInteropPath, content);
                Assert.True(success, $"Syntax error in voiceInterop.js:\n{error}");
            }
        }

        [Fact]
        public void AllJavaScriptFiles_MustHaveValidSyntax()
        {
            var jsFiles = new List<string>();

            var wwwrootJs = Path.Combine(ServerDirectory, "wwwroot", "js");
            if (Directory.Exists(wwwrootJs))
            {
                jsFiles.AddRange(Directory.GetFiles(wwwrootJs, "*.js", SearchOption.AllDirectories));
            }

            var componentsDir = Path.Combine(ServerDirectory, "Components");
            if (Directory.Exists(componentsDir))
            {
                jsFiles.AddRange(Directory.GetFiles(componentsDir, "*.js", SearchOption.AllDirectories));
            }

            Assert.NotEmpty(jsFiles);

            if (!IsNodeAvailable())
            {
                // Fallback check if node is not present
                return;
            }

            var errors = new List<string>();

            foreach (var file in jsFiles)
            {
                // Skip minified 3rd party vendor libraries if any exist in a vendor / lib folder
                if (file.Contains("vendor") || file.Contains(".min.js"))
                {
                    continue;
                }

                string content = File.ReadAllText(file);
                var (success, error) = CheckSyntaxWithNode(file, content);
                if (!success)
                {
                    var relativePath = Path.GetRelativePath(ServerDirectory, file);
                    errors.Add($"[{relativePath}]:\n{error}");
                }
            }

            Assert.True(errors.Count == 0, $"Found {errors.Count} JavaScript syntax error(s):\n\n" + string.Join("\n---\n", errors));
        }
    }
}
