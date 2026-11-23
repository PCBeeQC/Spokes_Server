using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

/// <summary>
/// Single source of truth for staging real Spokes voice/video ES modules
/// (<c>voiceInterop.js</c>, <c>voicePip.js</c>, and <c>wwwroot/js/voice/*.js</c>)
/// into Node.js test sandboxes with zero regex import rewriting.
/// </summary>
public static class VoiceJsTestModuleLoader
{
    private static readonly Lazy<LoadedVoiceJsSources> CachedSources = new(LoadFromDisk);

    private sealed record LoadedVoiceJsSources(
        string VoiceInteropJs,
        string VoicePipJs,
        IReadOnlyDictionary<string, string> VoiceSubModules);

    private static LoadedVoiceJsSources LoadFromDisk()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var jsRoot = Path.Combine(repoRoot, "Spokes_Server", "wwwroot", "js");
        var voiceInteropPath = Path.Combine(jsRoot, "voiceInterop.js");
        var voicePipPath = Path.Combine(jsRoot, "voicePip.js");
        var voiceSubDir = Path.Combine(jsRoot, "voice");

        if (!File.Exists(voiceInteropPath))
            throw new FileNotFoundException($"voiceInterop.js not found at {voiceInteropPath}");
        if (!File.Exists(voicePipPath))
            throw new FileNotFoundException($"voicePip.js not found at {voicePipPath}");
        if (!Directory.Exists(voiceSubDir))
            throw new DirectoryNotFoundException($"wwwroot/js/voice directory not found at {voiceSubDir}");

        var subModules = Directory.GetFiles(voiceSubDir, "*.js")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                p => Path.GetFileName(p),
                p => File.ReadAllText(p, Encoding.UTF8),
                StringComparer.OrdinalIgnoreCase);

        if (subModules.Count == 0)
            throw new InvalidOperationException($"Expected modular JS files inside {voiceSubDir}, found none.");

        return new LoadedVoiceJsSources(
            File.ReadAllText(voiceInteropPath, Encoding.UTF8),
            File.ReadAllText(voicePipPath, Encoding.UTF8),
            subModules);
    }

    private const string NoiseSuppressionStub = """
        export class NoiseSuppressionManager {
            async getProcessor(type, options = {}) {
                if (!type || type === 'webrtc' || type === 'none') return null;
                return {
                    name: type,
                    autoGainEnabled: options?.autoGainEnabled !== false,
                    setAutoGainEnabled(enabled) {
                        this.autoGainEnabled = Boolean(enabled);
                    },
                    flushCaptureFifo() {
                        return true;
                    },
                    async init() {},
                    async restart() {},
                    async destroy() {}
                };
            }
        }
        """;

    private const string CaptureConditionerStub = """
        export async function ensureVoiceLevelerWorkletLoaded() { return false; }
        export function createReceiverVoiceLevelerNode() { return null; }
        export function createMasterSoftLimiterNode() { return null; }
        """;

    private const string MediaZoomGestureStub = """
        export class MediaZoomGestureController {
            destroy() {}
            reset() {}
            resetZoom() {}
            updateTransform() {}
            setRotation() {}
        }
        """;

    /// <summary>
    /// Stages all real voice ES modules into <paramref name="targetDir"/> for standalone single-client Node.js tests.
    /// </summary>
    public static async Task StageStandaloneAsync(string targetDir, string? customVoicePipJs = null)
    {
        var sources = CachedSources.Value;
        Directory.CreateDirectory(targetDir);
        var voiceSubDir = Path.Combine(targetDir, "voice");
        Directory.CreateDirectory(voiceSubDir);

        await File.WriteAllTextAsync(Path.Combine(targetDir, "package.json"), """{"type":"module"}""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "voiceInterop.js"), sources.VoiceInteropJs, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "voicePip.js"), customVoicePipJs ?? sources.VoicePipJs, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "noiseSuppressionManager.js"), NoiseSuppressionStub, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "voiceCaptureConditioner.js"), CaptureConditionerStub, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "mediaZoomGesture.js"), MediaZoomGestureStub, Encoding.UTF8);

        foreach (var (fileName, content) in sources.VoiceSubModules)
        {
            await File.WriteAllTextAsync(Path.Combine(voiceSubDir, fileName), content, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Stages an isolated per-client copy of the entire voice ES module graph inside
    /// <c>{clusterTempDir}/clients/{clientId}/</c>, lexically binding browser globals to
    /// <c>globalThis.__spokesClientEnvs[clientId]</c> so relative imports resolve natively per client.
    /// </summary>
    /// <returns>Relative import specifier (from <paramref name="clusterTempDir"/>) to the client's <c>voiceInterop.js</c>.</returns>
    public static async Task<string> StageIsolatedClientAsync(string clusterTempDir, string clientId)
    {
        var sources = CachedSources.Value;
        var clientDir = Path.Combine(clusterTempDir, "clients", clientId);
        var clientVoiceDir = Path.Combine(clientDir, "voice");
        Directory.CreateDirectory(clientVoiceDir);

        var envPreamble = $$"""
            const {
                window,
                document,
                navigator,
                localStorage,
                MediaStream,
                CustomEvent,
                MutationObserver,
                ResizeObserver,
                getComputedStyle,
                Audio,
                RTCRtpSender,
                requestAnimationFrame,
                cancelAnimationFrame
            } = globalThis.__spokesClientEnvs['{{clientId}}'];
            """;

        await File.WriteAllTextAsync(
            Path.Combine(clientDir, "voicePip.js"),
            envPreamble + "\n" + sources.VoicePipJs,
            Encoding.UTF8);

        await File.WriteAllTextAsync(
            Path.Combine(clientDir, "voiceInterop.js"),
            envPreamble + "\n" + sources.VoiceInteropJs,
            Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(clientDir, "noiseSuppressionManager.js"), NoiseSuppressionStub, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(clientDir, "voiceCaptureConditioner.js"), CaptureConditionerStub, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(clientDir, "mediaZoomGesture.js"), MediaZoomGestureStub, Encoding.UTF8);

        foreach (var (fileName, content) in sources.VoiceSubModules)
        {
            await File.WriteAllTextAsync(
                Path.Combine(clientVoiceDir, fileName),
                envPreamble + "\n" + content,
                Encoding.UTF8);
        }

        return $"./clients/{clientId}/voiceInterop.js";
    }
}
