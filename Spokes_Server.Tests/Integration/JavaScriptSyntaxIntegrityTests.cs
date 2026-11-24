using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Spokes_Server.Tests.Integration;

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
                try { File.Delete(testFilePath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
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

    [Fact]
    public void AllJavaScriptAndCSharpModules_MustNotHardcodeVersionQueryStrings()
    {
        var filesToScan = new List<string>();
        var wwwrootJs = Path.Combine(ServerDirectory, "wwwroot", "js");
        if (Directory.Exists(wwwrootJs))
        {
            filesToScan.AddRange(Directory.GetFiles(wwwrootJs, "*.js", SearchOption.AllDirectories));
        }

        var wwwrootNoiseLib = Path.Combine(ServerDirectory, "wwwroot", "lib", "noise-suppression");
        if (Directory.Exists(wwwrootNoiseLib))
        {
            filesToScan.AddRange(Directory.GetFiles(wwwrootNoiseLib, "*.js", SearchOption.TopDirectoryOnly));
        }

        var componentsDir = Path.Combine(ServerDirectory, "Components");
        if (Directory.Exists(componentsDir))
        {
            filesToScan.AddRange(Directory.GetFiles(componentsDir, "*.razor.js", SearchOption.AllDirectories));
        }

        var coreDir = Path.Combine(ServerDirectory, "Core");
        if (Directory.Exists(coreDir))
        {
            filesToScan.AddRange(Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories));
        }

        Assert.NotEmpty(filesToScan);

        var hardcodedVersionRegex = new Regex(@"\.js\?v=[0-9a-zA-Z_-]+", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in filesToScan)
        {
            var content = File.ReadAllText(file);
            var match = hardcodedVersionRegex.Match(content);
            if (match.Success)
            {
                var relPath = Path.GetRelativePath(ServerDirectory, file);
                violations.Add($"{relPath}: found hardcoded cache-buster '{match.Value}' (use clean paths + AssetExtensions / ImportMap)");
            }
        }

        Assert.True(violations.Count == 0, "Hardcoded .js?v= strings are forbidden:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void MediaLoaderJs_PrioritizesVisibleNewestChatImages_EnforcesConcurrency_AndAbortsOnDisconnect()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var mediaLoaderPath = Path.Combine(ServerDirectory, "wwwroot", "js", "mediaLoader.js");
        Assert.True(File.Exists(mediaLoaderPath), $"mediaLoader.js not found at {mediaLoaderPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_medialoader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_media_loader.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const mediaLoaderSource = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(mediaLoaderPath)}}, 'utf8');

                function runScenario(userAgent, expectedConcurrency) {
                    const startedUrls = [];
                    const removedSrcUrls = [];

                    class FakeClassList {
                        constructor() { this._set = new Set(); }
                        add(c) { this._set.add(c); }
                        remove(c) { this._set.delete(c); }
                        contains(c) { return this._set.has(c); }
                    }

                    class FakeContainer {
                        constructor() { this.classList = new FakeClassList(); }
                    }

                    class FakeHTMLImageElement {
                        constructor(attrs, rect) {
                            this._attrs = new Map(Object.entries(attrs));
                            this._rect = rect;
                            this._listeners = new Map();
                            this._src = '';
                            this.dataset = {};
                            this.complete = false;
                            this.naturalWidth = 0;
                            this.isConnected = true;
                            this.container = new FakeContainer();
                            this.tagName = 'IMG';
                        }
                        getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
                        hasAttribute(k) { return this._attrs.has(k); }
                        removeAttribute(k) {
                            if (k === 'src' && this._src) {
                                removedSrcUrls.push(this._src);
                                this._src = '';
                            }
                            this._attrs.delete(k);
                        }
                        set src(val) {
                            this._src = val;
                            this._attrs.set('src', val);
                            startedUrls.push(val);
                        }
                        get src() { return this._src; }
                        addEventListener(evt, fn) {
                            if (!this._listeners.has(evt)) this._listeners.set(evt, new Set());
                            this._listeners.get(evt).add(fn);
                        }
                        removeEventListener(evt, fn) {
                            this._listeners.get(evt)?.delete(fn);
                        }
                        emit(evt) {
                            if (evt === 'load') {
                                this.complete = true;
                                this.naturalWidth = 800;
                            }
                            for (const fn of Array.from(this._listeners.get(evt) || [])) {
                                fn();
                            }
                        }
                        getBoundingClientRect() { return this._rect; }
                        closest() { return this.container; }
                    }

                    const context = {
                        console,
                        setTimeout,
                        clearTimeout,
                        queueMicrotask,
                        HTMLImageElement: FakeHTMLImageElement,
                        IntersectionObserver: class {
                            observe() {}
                            unobserve() {}
                            disconnect() {}
                        },
                        MutationObserver: class {
                            observe() {}
                            disconnect() {}
                        },
                        innerWidth: 1280,
                        innerHeight: 800,
                        matchMedia: () => ({ matches: false }),
                        navigator: { userAgent },
                        document: {
                            readyState: 'complete',
                            documentElement: { clientWidth: 1280, clientHeight: 800 },
                            body: { nodeType: 1, tagName: 'BODY', querySelectorAll: () => [] },
                            addEventListener() {}
                        }
                    };
                    context.window = context;

                    vm.runInNewContext(mediaLoaderSource, context);
                    const loader = context.window.spokesMediaLoader;
                    assert.ok(loader, 'window.spokesMediaLoader should be registered');
                    assert.equal(loader.getStats().maxConcurrency, expectedConcurrency);

                    const visibleRect = { top: 100, bottom: 300, left: 50, right: 250, width: 200, height: 200 };
                    const albumOld = new FakeHTMLImageElement({ 'data-media-src': '/img/album-0', 'data-media-priority': 'album-preview', 'data-media-order': '0' }, visibleRect);
                    const chatOld = new FakeHTMLImageElement({ 'data-media-src': '/img/chat-3', 'data-media-priority': 'chat', 'data-media-order': '3' }, visibleRect);
                    const chatNewest = new FakeHTMLImageElement({ 'data-media-src': '/img/chat-0', 'data-media-priority': 'chat', 'data-media-order': '0' }, visibleRect);
                    const chatSecond = new FakeHTMLImageElement({ 'data-media-src': '/img/chat-1', 'data-media-priority': 'chat', 'data-media-order': '1' }, visibleRect);
                    const chatThird = new FakeHTMLImageElement({ 'data-media-src': '/img/chat-2', 'data-media-priority': 'chat', 'data-media-order': '2' }, visibleRect);

                    // Register intentionally out-of-order (album & oldest chat first)
                    loader.observe(albumOld);
                    loader.observe(chatOld);
                    loader.observe(chatNewest);
                    loader.observe(chatSecond);
                    loader.observe(chatThird);

                    return new Promise((resolve) => {
                        setTimeout(() => {
                            // Bounded concurrency & newest-first chat priority
                            assert.equal(startedUrls.length, expectedConcurrency, `Expected ${expectedConcurrency} concurrent loads, got ${startedUrls.join(',')}`);
                            assert.equal(startedUrls[0], '/img/chat-0');
                            assert.equal(startedUrls[1], '/img/chat-1');
                            if (expectedConcurrency === 3) {
                                assert.equal(startedUrls[2], '/img/chat-2');
                            }

                            // Complete newest chat image -> container marked loaded & next priority starts
                            chatNewest.emit('load');
                            assert.equal(chatNewest.dataset.loaded, 'true');
                            assert.equal(chatNewest.container.classList.contains('is-loaded'), true);

                            // Simulate channel switch disconnecting in-flight chatSecond -> HTTP stream aborted via removeAttribute('src')
                            chatSecond.isConnected = false;
                            loader.cancelDetached();
                            assert.ok(removedSrcUrls.includes('/img/chat-1'), 'Detached in-flight image must have src removed to abort HTTP stream');

                            setTimeout(() => {
                                // Remaining chat images load before album-preview
                                const chat3Idx = startedUrls.indexOf('/img/chat-3');
                                const albumIdx = startedUrls.indexOf('/img/album-0');
                                assert.ok(chat3Idx >= 0, 'chat-3 should have started');
                                if (albumIdx >= 0) {
                                    assert.ok(chat3Idx < albumIdx, 'chat images must start ahead of album-preview');
                                }
                                resolve();
                            }, 10);
                        }, 10);
                    });
                }

                await runScenario('Mozilla/5.0 (Windows NT 10.0; Win64; x64)', 3);
                await runScenario('Mozilla/5.0 (Linux; Android 14)', 2);
                console.log('MEDIA_LOADER_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"mediaLoader.js Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("MEDIA_LOADER_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void MediaLoaderJs_TwoTierCanonicalCache_VaultPersistence_PurgeLifecycle_AndFetchErrorHandling()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var mediaLoaderPath = Path.Combine(ServerDirectory, "wwwroot", "js", "mediaLoader.js");
        Assert.True(File.Exists(mediaLoaderPath), $"mediaLoader.js not found at {mediaLoaderPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_medialoader_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_media_cache.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const mediaLoaderSource = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(mediaLoaderPath)}}, 'utf8');

                // Shared persistent storage across simulated app restarts (IndexedDB + localStorage)
                const sharedDbs = new Map(); // dbName -> Map(storeName -> Map(key -> record))
                const sharedDbVersions = new Map(); // dbName -> version number
                const sharedStorePutCounts = new Map(); // storeName -> count
                const sharedLocalStorage = new Map();

                function createMockIndexedDb(idbOpts = {}) {
                    function makeReq(exec) {
                        const req = { result: undefined, error: null, onsuccess: null, onerror: null, onupgradeneeded: null, onblocked: null };
                        queueMicrotask(() => {
                            try {
                                exec(req);
                            } catch (e) {
                                req.error = e;
                                if (req.onerror) req.onerror({ target: req });
                            }
                        });
                        return req;
                    }

                    return {
                        databases: async () => Array.from(sharedDbs.keys()).map(name => ({ name, version: sharedDbVersions.get(name) || 1 })),
                        deleteDatabase: (name) => makeReq((req) => {
                            sharedDbs.delete(name);
                            sharedDbVersions.delete(name);
                            if (req.onsuccess) req.onsuccess({ target: req });
                        }),
                        open: (name, ver = 1) => {
                            if (idbOpts.stallOpen) {
                                // Simulates a blocked/hung IndexedDB open that never resolves
                                return { result: undefined, error: null, onsuccess: null, onerror: null, onupgradeneeded: null, onblocked: null };
                            }
                            return makeReq((req) => {
                                let needsUpgrade = false;
                                if (!sharedDbs.has(name)) {
                                    sharedDbs.set(name, new Map());
                                    sharedDbVersions.set(name, ver);
                                    needsUpgrade = true;
                                } else if ((sharedDbVersions.get(name) || 1) < ver) {
                                    sharedDbVersions.set(name, ver);
                                    needsUpgrade = true;
                                }
                                const stores = sharedDbs.get(name);
                                const db = {
                                    objectStoreNames: { contains: (s) => stores.has(s) },
                                    createObjectStore: (s) => {
                                        if (!stores.has(s)) stores.set(s, new Map());
                                        return { createIndex: () => {} };
                                    },
                                    deleteObjectStore: (s) => {
                                        stores.delete(s);
                                    },
                                    close: () => {},
                                    transaction: (storeNames) => {
                                        const tx = { oncomplete: null, onerror: null, onabort: null };
                                        let pendingOps = 0;
                                        let completeScheduled = false;
                                        const scheduleFinish = () => {
                                            if (completeScheduled) return;
                                            completeScheduled = true;
                                            queueMicrotask(() => {
                                                completeScheduled = false;
                                                if (pendingOps === 0 && tx.oncomplete) tx.oncomplete();
                                            });
                                        };
                                        const beginOp = () => { pendingOps++; };
                                        const endOp = () => {
                                            pendingOps = Math.max(0, pendingOps - 1);
                                            if (pendingOps === 0) scheduleFinish();
                                        };
                                        const defaultStore = Array.isArray(storeNames) ? storeNames[0] : storeNames;
                                        tx.objectStore = (name) => {
                                            const targetName = name || defaultStore;
                                            if (!stores.has(targetName)) stores.set(targetName, new Map());
                                            const map = stores.get(targetName);
                                            const makeCursor = (entries, idx, cursorReq) => {
                                                if (idx >= entries.length) {
                                                    cursorReq.result = null;
                                                    if (cursorReq.onsuccess) cursorReq.onsuccess({ target: cursorReq });
                                                    endOp();
                                                    return;
                                                }
                                                const [k, v] = entries[idx];
                                                cursorReq.result = {
                                                    key: k,
                                                    primaryKey: k,
                                                    value: v,
                                                    delete: () => { map.delete(k); },
                                                    continue: () => queueMicrotask(() => makeCursor(entries, idx + 1, cursorReq))
                                                };
                                                if (cursorReq.onsuccess) cursorReq.onsuccess({ target: cursorReq });
                                            };
                                            return {
                                                get: (key) => {
                                                    beginOp();
                                                    return makeReq((r) => {
                                                        r.result = map.get(key);
                                                        if (r.onsuccess) r.onsuccess({ target: r });
                                                        endOp();
                                                    });
                                                },
                                                put: (rec) => {
                                                    beginOp();
                                                    return makeReq((r) => {
                                                        sharedStorePutCounts.set(targetName, (sharedStorePutCounts.get(targetName) || 0) + 1);
                                                        map.set(rec.key, rec);
                                                        r.result = rec.key;
                                                        if (r.onsuccess) r.onsuccess({ target: r });
                                                        endOp();
                                                    });
                                                },
                                                delete: (key) => {
                                                    beginOp();
                                                    return makeReq((r) => {
                                                        map.delete(key);
                                                        if (r.onsuccess) r.onsuccess({ target: r });
                                                        endOp();
                                                    });
                                                },
                                                openCursor: () => {
                                                    beginOp();
                                                    const r = { result: null, onsuccess: null, onerror: null };
                                                    queueMicrotask(() => makeCursor(Array.from(map.entries()), 0, r));
                                                    return r;
                                                },
                                                index: (indexName) => ({
                                                    openCursor: (range) => {
                                                        beginOp();
                                                        const r = { result: null, onsuccess: null, onerror: null };
                                                        queueMicrotask(() => {
                                                            const prop = indexName === 'by_context' ? 'contextKey'
                                                                : indexName === 'by_encrypted' ? 'encrypted'
                                                                : 'lastAccessedAt';
                                                            let entries = Array.from(map.entries());
                                                            if (range && range._only !== undefined) {
                                                                entries = entries.filter(([, v]) => v[prop] === range._only);
                                                            } else {
                                                                entries.sort((a, b) => (a[1][prop] || 0) - (b[1][prop] || 0));
                                                            }
                                                            makeCursor(entries, 0, r);
                                                        });
                                                        return r;
                                                    }
                                                })
                                            };
                                        };
                                        return tx;
                                    }
                                };
                                req.result = db;
                                if (needsUpgrade && req.onupgradeneeded) {
                                    req.onupgradeneeded({ target: req });
                                }
                                if (req.onsuccess) req.onsuccess({ target: req });
                            });
                        }
                    };
                }

                function createEnv(sessionState, fetchHandler, envOpts = {}) {
                    let blobSeq = 0;
                    const revokedUrls = [];
                    const fetchLog = [];
                    const tokenRecoveryCalls = [];
                    const perfEvents = [];

                    class FakeClassList {
                        constructor() { this._set = new Set(); }
                        add(...cs) { cs.forEach(c => this._set.add(c)); }
                        remove(...cs) { cs.forEach(c => this._set.delete(c)); }
                        contains(c) { return this._set.has(c); }
                    }

                    class FakeContainer {
                        constructor() { this.classList = new FakeClassList(); }
                    }

                    class FakeHTMLImageElement {
                        constructor(attrs, rect) {
                            this._attrs = new Map(Object.entries(attrs));
                            this._rect = rect || { top: 100, bottom: 300, left: 50, right: 250, width: 200, height: 200 };
                            this._listeners = new Map();
                            this._src = '';
                            this.dataset = {};
                            this.complete = false;
                            this.naturalWidth = 0;
                            this.isConnected = true;
                            this.container = new FakeContainer();
                            this.tagName = 'IMG';
                        }
                        getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
                        hasAttribute(k) { return this._attrs.has(k); }
                        setAttribute(k, v) { this._attrs.set(k, v); }
                        removeAttribute(k) {
                            if (k === 'src') this._src = '';
                            this._attrs.delete(k);
                        }
                        set src(val) {
                            this._src = val;
                            this._attrs.set('src', val);
                            if (val && val.startsWith('blob:')) {
                                this.complete = true;
                                this.naturalWidth = 800;
                                queueMicrotask(() => this.dispatchEvent({ type: 'load' }));
                            }
                        }
                        get src() { return this._src; }
                        addEventListener(evt, fn) {
                            if (!this._listeners.has(evt)) this._listeners.set(evt, new Set());
                            this._listeners.get(evt).add(fn);
                        }
                        removeEventListener(evt, fn) { this._listeners.get(evt)?.delete(fn); }
                        dispatchEvent(ev) {
                            const type = typeof ev === 'string' ? ev : ev.type;
                            for (const fn of Array.from(this._listeners.get(type) || [])) {
                                fn(ev);
                            }
                        }
                        getBoundingClientRect() { return this._rect; }
                        closest() { return this.container; }
                    }

                    class MockURL extends URL {
                        static createObjectURL(blob) { return `blob:spokes-${++blobSeq}-${blob.size}`; }
                        static revokeObjectURL(u) { revokedUrls.push(u); }
                    }

                    const context = {
                        console,
                        setTimeout,
                        clearTimeout,
                        queueMicrotask,
                        AbortController,
                        atob,
                        Blob,
                        Uint8Array,
                        Event: class { constructor(type) { this.type = type; } },
                        IDBKeyRange: { only: (v) => ({ _only: v }) },
                        indexedDB: createMockIndexedDb(envOpts),
                        isCapacitorNative: () => Boolean(envOpts.isNative),
                        localStorage: {
                            getItem: (k) => sharedLocalStorage.has(k) ? sharedLocalStorage.get(k) : null,
                            setItem: (k, v) => sharedLocalStorage.set(k, String(v)),
                            removeItem: (k) => sharedLocalStorage.delete(k)
                        },
                        URL: MockURL,
                        location: { href: 'http://192.168.1.50:5000/chat/ch1', origin: 'http://192.168.1.50:5000', pathname: '/chat/ch1' },
                        HTMLImageElement: FakeHTMLImageElement,
                        IntersectionObserver: class {
                            observe() {}
                            unobserve() {}
                            disconnect() {}
                        },
                        MutationObserver: class {
                            observe() {}
                            disconnect() {}
                        },
                        innerWidth: 1280,
                        innerHeight: 800,
                        matchMedia: () => ({ matches: false }),
                        navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)' },
                        __spokesSession: Object.assign({}, sessionState),
                        _imageRecoveryCoordinator: {
                            invokeMethodAsync: async (method) => { tokenRecoveryCalls.push(method); }
                        },
                        spokesPerf: {
                            recordMediaEvent: (evt) => perfEvents.push(evt)
                        },
                        fetch: async (url, opts) => {
                            fetchLog.push({ url, opts });
                            return fetchHandler(url, opts);
                        },
                        document: {
                            readyState: 'complete',
                            documentElement: { clientWidth: 1280, clientHeight: 800 },
                            body: { nodeType: 1, tagName: 'BODY', querySelectorAll: () => [] },
                            addEventListener() {}
                        }
                    };
                    context.window = context;
                    vm.runInNewContext(mediaLoaderSource, context);
                    return {
                        context,
                        loader: context.window.spokesMediaLoader,
                        FakeHTMLImageElement,
                        fetchLog,
                        revokedUrls,
                        tokenRecoveryCalls,
                        perfEvents
                    };
                }

                const wait = (ms = 25) => new Promise(r => setTimeout(r, ms));
                const makeOkResponse = (bytesLen, scope) => ({
                    ok: true,
                    status: 200,
                    headers: {
                        get: (name) => {
                            const n = name.toLowerCase();
                            if (n === 'content-type') return 'image/jpeg';
                            if (n === 'x-spokes-media-scope') return scope;
                            if (n === 'cache-control') return scope === 'encrypted' ? 'private, no-store' : 'private, max-age=1209600';
                            return null;
                        }
                    },
                    blob: async () => ({ size: bytesLen, type: 'image/jpeg' })
                });

                // 1. Cold fetch populates Tier 1 RAM LRU + Tier 2 IndexedDB (both standard and encrypted while vault is saved)
                const env1 = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'user1111aaaa2222', hasVaultKey: true },
                    async (url) => url.includes('/ch-enc/') ? makeOkResponse(4096, 'encrypted') : makeOkResponse(2048, 'standard')
                );

                const stdImg1 = new env1.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-pub/a.jpg?t=tok1&s=sm', 'data-media-priority': 'chat', 'data-media-order': '0' });
                const encImg1 = new env1.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-enc/b.jpg?t=tok1', 'data-media-priority': 'chat', 'data-media-order': '1', 'data-media-encrypted': 'true' });
                env1.loader.observe(stdImg1);
                env1.loader.observe(encImg1);
                await wait(40);

                assert.equal(env1.fetchLog.length, 2, 'Cold load should fetch each distinct canonical image once');
                assert.equal(env1.fetchLog[0].opts.credentials, 'include');
                assert.ok(stdImg1.src.startsWith('blob:'));
                assert.ok(encImg1.src.startsWith('blob:'));
                assert.equal(stdImg1.container.classList.contains('is-loaded'), true);

                // 2. Re-opening same channel with a ROTATED ?t= token hits Tier 1 RAM LRU synchronously (0 network calls, is-instant)
                const stdImg2 = new env1.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-pub/a.jpg?t=ROTATED_TOKEN_2&s=sm', 'data-media-priority': 'chat', 'data-media-order': '0' });
                env1.loader.observe(stdImg2);
                assert.equal(stdImg2.src, stdImg1.src, 'Tier 1 RAM LRU must serve existing ObjectURL synchronously despite rotated ?t=');
                assert.equal(stdImg2.container.classList.contains('is-instant'), true);
                assert.equal(env1.fetchLog.length, 2, 'Tier 1 hit must make 0 network requests');

                // 3. Simulated app restart (fresh VM, same IndexedDB) with hasVaultKey=true -> BOTH standard and encrypted hit Tier 2 IndexedDB (0 network calls)
                const env2 = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'user1111aaaa2222', hasVaultKey: true },
                    async () => { throw new Error('Network should NOT be called on Tier 2 IndexedDB hit'); }
                );
                await wait(25);
                const stdReload = new env2.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-pub/a.jpg?t=TOK_AFTER_RESTART&s=sm', 'data-media-priority': 'chat', 'data-media-order': '0' });
                const encReload = new env2.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-enc/b.jpg?t=TOK_AFTER_RESTART', 'data-media-priority': 'chat', 'data-media-order': '1', 'data-media-encrypted': 'true' });
                env2.loader.observe(stdReload);
                env2.loader.observe(encReload);
                await wait(40);
                assert.equal(env2.fetchLog.length, 0, 'Tier 2 IndexedDB must serve standard and unlocked encrypted images with 0 network calls');
                assert.ok(stdReload.src.startsWith('blob:'));
                assert.ok(encReload.src.startsWith('blob:'));
                assert.equal(stdReload.container.classList.contains('is-instant'), true);
                assert.equal(encReload.container.classList.contains('is-instant'), true);

                // Check getCacheStatsAsync reports persisted entries & bytes
                const stats = await env2.loader.getCacheStatsAsync();
                assert.equal(stats.diskEntries, 2);
                assert.equal(stats.diskBytes, 2048 + 4096);

                // 4. Remote device vault password removal while closed (next boot has hasVaultKey=false) -> purges encrypted entries ONLY
                const env3 = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'user1111aaaa2222', hasVaultKey: false },
                    async () => makeOkResponse(4096, 'encrypted')
                );
                await wait(35);
                const statsAfterVaultRemoved = await env3.loader.getCacheStatsAsync();
                assert.equal(statsAfterVaultRemoved.diskEntries, 1, 'Cold boot with hasVaultKey=false must purge encrypted IndexedDB records while preserving standard ones');
                assert.equal(statsAfterVaultRemoved.diskBytes, 2048);

                // 5. While vault is NOT persisted, fetching an encrypted image caches in Tier 1 RAM only (never written to IndexedDB)
                const encTemp = new env3.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-enc/c.jpg?t=temp1', 'data-media-priority': 'chat', 'data-media-order': '0', 'data-media-encrypted': 'true' });
                env3.loader.observe(encTemp);
                await wait(35);
                assert.equal(env3.fetchLog.length, 1);
                const statsTemp = await env3.loader.getCacheStatsAsync();
                assert.equal(statsTemp.diskEntries, 1, 'Encrypted image must not persist to IndexedDB when vaultPersisted is false');

                // 6. clearEncryptedCache() revokes encrypted Tier 1 ObjectURLs while preserving standard entries
                await env3.loader.clearEncryptedCache();
                assert.ok(env3.revokedUrls.length >= 1, 'clearEncryptedCache must revoke encrypted ObjectURL');
                assert.equal(env3.loader.getStats().memoryEntries, 0);

                // 7. evictContext('chat', 'ch-pub') removes ch-pub from IndexedDB
                await env3.loader.evictContext('chat', 'ch-pub');
                const statsAfterEvict = await env3.loader.getCacheStatsAsync();
                assert.equal(statsAfterEvict.diskEntries, 0);

                // 8. Account switch deletes previous user's IndexedDB
                const envUserA = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'useraaaa11112222', hasVaultKey: false },
                    async () => makeOkResponse(1024, 'standard')
                );
                envUserA.loader.observe(new envUserA.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/chat/c1/x.jpg?t=t1', 'data-media-priority': 'chat', 'data-media-order': '0' }));
                await wait(35);
                assert.ok(sharedDbs.has('spokes-media-v1::useraaaa11112222'));

                createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userbbbb33334444', hasVaultKey: false },
                    async () => makeOkResponse(1024, 'standard')
                );
                await wait(35);
                assert.equal(sharedDbs.has('spokes-media-v1::useraaaa11112222'), false, 'Account switch must delete previous user IndexedDB');

                // 9. 410 Gone triggers TriggerTokenRecovery directly (no HEAD probe); 403 fast-fails with error event; detach aborts fetch
                let abortedSignal = false;
                const envErr = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userbbbb33334444', hasVaultKey: false },
                    (url, opts) => {
                        if (url.includes('expired.jpg')) return Promise.resolve({ ok: false, status: 410 });
                        if (url.includes('forbidden.jpg')) return Promise.resolve({ ok: false, status: 403 });
                        return new Promise((resolve) => {
                            opts.signal.addEventListener('abort', () => {
                                abortedSignal = true;
                                const err = new Error('Aborted');
                                err.name = 'AbortError';
                                resolve(Promise.reject(err));
                            });
                        });
                    }
                );
                const expiredImg = new envErr.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/chat/c1/expired.jpg?t=old', 'data-media-priority': 'chat', 'data-media-order': '0' });
                const forbiddenImg = new envErr.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/chat/c1/forbidden.jpg?t=bad', 'data-media-priority': 'lightbox', 'data-media-order': '0' });
                let lightboxErrorEvents = 0;
                forbiddenImg.addEventListener('error', () => { lightboxErrorEvents++; });
                const hangImg = new envErr.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/chat/c1/slow.jpg?t=slow', 'data-media-priority': 'chat', 'data-media-order': '1' });

                envErr.loader.observe(expiredImg);
                envErr.loader.observe(forbiddenImg);
                envErr.loader.observe(hangImg);
                await wait(35);

                assert.deepEqual(envErr.tokenRecoveryCalls, ['TriggerTokenRecovery']);
                assert.equal(forbiddenImg.container.classList.contains('is-error'), true);
                assert.equal(lightboxErrorEvents, 1, '403 fetch failure must dispatch DOM error event for lightbox spinner cleanup');
                assert.equal(envErr.fetchLog.filter(x => x.url.includes('forbidden.jpg')).length, 1, '403 must not retry');

                hangImg.isConnected = false;
                envErr.loader.cancelDetached();
                await wait(15);
                assert.equal(abortedSignal, true, 'Detaching an in-flight image must abort its AbortController signal');

                // 10. Two-store v2 upgrade deletes legacy 'media' store; idbTouch updates 'meta' ONLY and NEVER rewrites 'blobs'
                const legacyDbName = 'spokes-media-v1::userv2upgrade111';
                sharedDbs.set(legacyDbName, new Map([['media', new Map([['old', { key: 'old' }]])]]));
                sharedDbVersions.set(legacyDbName, 1);
                const envV2 = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userv2upgrade111', hasVaultKey: true },
                    async () => makeOkResponse(3072, 'standard')
                );
                const v2Img = new envV2.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-v2/pic.jpg?t=t1', 'data-media-priority': 'chat', 'data-media-order': '0' });
                envV2.loader.observe(v2Img);
                await wait(35);
                assert.equal(sharedDbs.get(legacyDbName).has('media'), false, 'v2 upgrade must remove legacy single media store');
                assert.equal(sharedDbs.get(legacyDbName).has('blobs'), true, 'v2 upgrade must create blobs store');
                assert.equal(sharedDbs.get(legacyDbName).has('meta'), true, 'v2 upgrade must create meta store');
                // Age meta.lastAccessedAt > 60s so next read triggers idbTouch
                const metaRec = sharedDbs.get(legacyDbName).get('meta').get('/spokesapi/files/thumb/chat/ch-v2/pic.jpg');
                metaRec.lastAccessedAt = Date.now() - 120000;
                const blobPutsBeforeTouch = sharedStorePutCounts.get('blobs') || 0;
                const envV2Restart = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userv2upgrade111', hasVaultKey: true },
                    async () => { throw new Error('Should hit IDB on restart'); }
                );
                const v2ImgRestart = new envV2Restart.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-v2/pic.jpg?t=t2', 'data-media-priority': 'chat', 'data-media-order': '0' });
                envV2Restart.loader.observe(v2ImgRestart);
                await wait(35);
                assert.equal(v2ImgRestart.container.classList.contains('is-instant'), true);
                assert.equal(sharedStorePutCounts.get('blobs') || 0, blobPutsBeforeTouch, 'idbTouch must NEVER rewrite blobs store');

                // 11. seedMedia & seedMediaBase64 paint frame-1 synchronously with 0 network calls and honor clearEncryptedCache()
                const envSeed = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userseed11112222', hasVaultKey: true },
                    async () => { throw new Error('Seeded upload must NOT hit network'); }
                );
                const b64 = Buffer.from([0xff, 0xd8, 0xff, 0xd9]).toString('base64');
                assert.equal(envSeed.loader.seedMediaBase64('/spokesapi/files/thumb/chat/ch-seed/new.jpg', b64, 'image/jpeg', { encrypted: false }), true);
                const seededImg = new envSeed.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-seed/new.jpg?t=fresh', 'data-media-priority': 'chat', 'data-media-order': '0' });
                envSeed.loader.observe(seededImg);
                assert.equal(seededImg.container.classList.contains('is-instant'), true, 'Seeded upload must paint synchronously on frame 1');
                assert.equal(envSeed.fetchLog.length, 0);

                assert.equal(envSeed.loader.seedMediaBase64('/spokesapi/files/thumb/chat/ch-enc/secret.jpg', b64, 'image/jpeg', { encrypted: true }), true);
                await envSeed.loader.clearEncryptedCache();
                assert.equal(envSeed.loader.getStats().memoryEntries, 1, 'clearEncryptedCache must evict encrypted seeded entry while keeping standard entry');

                // 12. retryBroken() must NEVER clobber in-flight loading entries whose <img> has complete=true, naturalWidth=0 (unset src)
                let slowFetchCount = 0;
                const envRetry = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userretry1112222', hasVaultKey: false },
                    async () => {
                        slowFetchCount++;
                        await wait(80);
                        return makeOkResponse(1024, 'standard');
                    }
                );
                const inFlightImg = new envRetry.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-r/slow.jpg?t=1', 'data-media-priority': 'chat', 'data-media-order': '0' });
                inFlightImg.complete = true; // Matches HTML spec for <img> with no src attribute yet
                inFlightImg.naturalWidth = 0;
                envRetry.loader.observe(inFlightImg);
                await wait(20);
                assert.equal(slowFetchCount, 1);
                envRetry.loader.retryBroken();
                await wait(100);
                assert.equal(slowFetchCount, 1, 'retryBroken must not re-queue or duplicate an in-flight fetch');
                assert.equal(inFlightImg.container.classList.contains('is-loaded'), true);

                // 13. Hung IndexedDB open bypasses to network within IDB_LOOKUP_TIMEOUT_MS instead of stalling on skeleton
                const envStall = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: 'userstall1112222', hasVaultKey: false },
                    async () => makeOkResponse(1024, 'standard'),
                    { stallOpen: true }
                );
                const stallImg = new envStall.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-s/pic.jpg?t=1', 'data-media-priority': 'chat', 'data-media-order': '0' });
                envStall.loader.observe(stallImg);
                await wait(320);
                assert.equal(envStall.fetchLog.length, 1, 'Stalled IndexedDB lookup must fast-bypass to network fetch');
                assert.equal(stallImg.container.classList.contains('is-loaded'), true);

                // 14. Native expired-cookie boot preserves IndexedDB until reconcileSession() runs after silent refresh,
                //     and purges when ensureCookiesFlushed confirms no refresh token exists.
                const nativeUserScope = 'usernativekeep11';
                const nativeDbName = 'spokes-media-v1::' + nativeUserScope;
                const envNativeSeed = createEnv(
                    { authenticated: true, expiresAt: Date.now() + 60000, userScope: nativeUserScope, hasVaultKey: true },
                    async () => makeOkResponse(2048, 'standard'),
                    { isNative: true }
                );
                envNativeSeed.loader.observe(new envNativeSeed.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-nat/pic.jpg?t=t0', 'data-media-priority': 'chat', 'data-media-order': '0' }));
                await wait(35);
                assert.ok(sharedDbs.has(nativeDbName));

                const envNativeKeep = createEnv(
                    { authenticated: false, expiresAt: 0, userScope: null, hasVaultKey: false },
                    async () => { throw new Error('Should hit preserved IDB after silent native refresh'); },
                    { isNative: true }
                );
                await wait(25);
                assert.ok(sharedDbs.has(nativeDbName), 'Native unauthenticated boot must not wipe IndexedDB before ensureCookiesFlushed refreshes');
                envNativeKeep.context.__spokesSession.authenticated = true;
                envNativeKeep.context.__spokesSession.userScope = nativeUserScope;
                envNativeKeep.context.__spokesSession.hasVaultKey = true;
                envNativeKeep.loader.reconcileSession();
                const preservedImg = new envNativeKeep.FakeHTMLImageElement({ 'data-media-src': '/spokesapi/files/thumb/chat/ch-nat/pic.jpg?t=t3', 'data-media-priority': 'chat', 'data-media-order': '0' });
                envNativeKeep.loader.observe(preservedImg);
                await wait(35);
                assert.equal(preservedImg.container.classList.contains('is-instant'), true, 'reconcileSession after silent refresh must restore warm IndexedDB hits');

                // Confirming unauthenticated on native explicitly purges IndexedDB
                envNativeKeep.context.__spokesSession.authenticated = false;
                envNativeKeep.context.__spokesSession.userScope = null;
                envNativeKeep.loader.reconcileSession({ purgeIfUnauthenticated: true });
                await wait(25);
                assert.equal(sharedDbs.has(nativeDbName), false, 'reconcileSession({ purgeIfUnauthenticated: true }) must purge IndexedDB');

                console.log('MEDIA_CACHE_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            Assert.True(proc.ExitCode == 0, $"mediaLoader.js cache Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("MEDIA_CACHE_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void CapacitorBackButton_AndSpokesPerf_NeverMinimizeWhenChatChannelOpen_AndBuildBaselineSummary()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var capacitorInitPath = Path.Combine(ServerDirectory, "wwwroot", "js", "capacitor-init.js");
        var spokesBridgePath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokesbridge.js");
        Assert.True(File.Exists(capacitorInitPath));
        Assert.True(File.Exists(spokesBridgePath));

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_nav_perf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_nav_perf.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const capSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(capacitorInitPath)}}, 'utf8');
                const bridgeSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesBridgePath)}}, 'utf8');

                let backHandler = null;
                let minimizedCount = 0;
                let inAppBackClicks = 0;
                let historyBackCalls = 0;
                let dialogCloseClicks = 0;
                const logs = [];

                let mockPathname = '/chat';
                let mockInAppBackVisible = true;
                let mockChannelOpenDom = true;
                let mockDialogOpen = false;

                const fakeInAppBack = {
                    get offsetParent() { return mockInAppBackVisible ? {} : null; },
                    click() { inAppBackClicks++; }
                };
                const fakeDialog = {
                    querySelector(sel) {
                        if (sel.includes('aria-label="close"')) return { click() { dialogCloseClicks++; } };
                        return null;
                    }
                };

                const context = {
                    console: { log: (...a) => logs.push(a.join(' ')), warn() {}, error() {}, trace() {} },
                    setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask,
                    requestAnimationFrame: (cb) => { cb(); return 1; },
                    performance: { timeOrigin: Date.now() - 1000, now: () => 1000 },
                    PerformanceObserver: { supportedEntryTypes: [] },
                    CustomEvent: class { constructor(t, o) { this.type = t; this.detail = o?.detail; } },
                    navigator: { userAgent: 'Mozilla/5.0 Capacitor Android', connection: { effectiveType: '4g', downlink: 10, rtt: 50 } },
                    innerWidth: 393, innerHeight: 852,
                    location: { get pathname() { return mockPathname; }, search: '?client=mobile', href: 'https://test.local/chat?client=mobile', origin: 'https://test.local' },
                    history: { length: 3, back() { historyBackCalls++; } },
                    localStorage: { getItem() { return null; }, setItem() {} },
                    Capacitor: {
                        isNativePlatform: () => true,
                        Plugins: {
                            App: {
                                async addListener(evt, fn) {
                                    if (evt === 'backButton') backHandler = fn;
                                    return { remove() {} };
                                },
                                minimizeApp() { minimizedCount++; }
                            }
                        }
                    },
                    document: {
                        readyState: 'complete',
                        addEventListener() {},
                        getElementById(id) { return id === 'chat-mobile-back-btn' ? fakeInAppBack : null; },
                        querySelector(sel) {
                            if (sel === '.mud-main-content.chat-channel-open') return mockChannelOpenDom ? {} : null;
                            return null;
                        },
                        querySelectorAll(sel) {
                            if (sel === '.mud-dialog-container') return mockDialogOpen ? [fakeDialog] : [];
                            return [];
                        }
                    },
                    dispatchEvent() {},
                    addEventListener() {}
                };
                context.window = context;

                vm.runInNewContext(bridgeSrc, context);
                vm.runInNewContext(capSrc, context);

                await context.window.initCapacitorEnv();
                assert.ok(typeof backHandler === 'function', 'backButton listener must be registered');

                // Case 1: URL still /chat (e.g. slow cold channel open), but channel is open in DOM -> MUST click in-app back, NEVER minimizeApp
                mockPathname = '/chat';
                mockInAppBackVisible = true;
                mockChannelOpenDom = true;
                backHandler();
                assert.equal(inAppBackClicks, 1, 'Expected in-app back button click when chat channel is visibly open');
                assert.equal(minimizedCount, 0, 'Must NEVER minimize app when chat channel is open');
                assert.equal(historyBackCalls, 0, 'Must NOT rely on brittle window.history.back() inside chat channel');

                // Case 2: Open MediaViewerDialog / MudDialog -> MUST close dialog first
                mockDialogOpen = true;
                backHandler();
                assert.equal(dialogCloseClicks, 1);
                assert.equal(inAppBackClicks, 1);
                assert.equal(minimizedCount, 0);
                mockDialogOpen = false;

                // Case 3: Truly on root /chat list (no open channel) -> minimizes app cleanly
                mockPathname = '/chat';
                mockInAppBackVisible = false;
                mockChannelOpenDom = false;
                backHandler();
                assert.equal(minimizedCount, 1, 'Expected minimizeApp on root /chat list');

                // Case 4: window.spokesPerf media telemetry & baseline summary/export (chat profiler off by default)
                const perf = context.window.spokesPerf;
                assert.ok(perf, 'window.spokesPerf must exist');
                assert.equal(perf.isChatDiagnosticsEnabled(), false, 'Chat transition profiler must default to OFF');
                assert.equal(typeof perf.enableChatDiagnostics, 'function');
                assert.equal(typeof perf.disableChatDiagnostics, 'function');
                assert.equal(typeof perf.help, 'function');
                perf.onStartupSettled({ clickToUsableMs: 740, shellTtiMs: 690, settledRoute: 'ChatList' });
                perf.recordViewportFill({ channelId: 'general', step: 1, durationMs: 42, hasMore: true });
                perf.recordMediaEvent({ type: 'registered', seq: 1, priority: 'chat', order: 0, visible: true, view: '/chat/general', concurrencyCap: 2 });
                perf.recordMediaEvent({ type: 'loaded', seq: 1, priority: 'chat', order: 0, visible: true, view: '/chat/general', concurrencyCap: 2, queueWaitMs: 0, loadMs: 120, ttfbMs: 55, downloadMs: 65, transferSize: 40960, cacheHit: false, selfHealed: false });
                const summary = perf.summary();
                assert.equal(summary.startup.clickToUsableMs, 740);
                assert.equal(summary.stage2ViewportFill.total, 1);
                assert.equal(summary.media.counts.loaded, 1);
                assert.equal(summary.media.loadMs.p50, 120);
                assert.equal(summary.backNavigation.androidBackActions['chat-in-app-back'], 1);
                assert.equal(summary.backNavigation.androidBackActions['minimize-app'], 1);
                const exported = JSON.parse(perf.exportBaseline());
                assert.equal(exported.media.counts.loaded, 1);

                console.log('NAV_PERF_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Nav/Perf Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("NAV_PERF_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SpokesBridgeBackStack_AndCapacitorBack_DismissHeaderlessDialogsOverlaysAndWebPopStateInLifoOrder()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var capacitorInitPath = Path.Combine(ServerDirectory, "wwwroot", "js", "capacitor-init.js");
        var spokesBridgePath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokesbridge.js");
        var mediaZoomGesturePath = Path.Combine(ServerDirectory, "wwwroot", "js", "mediaZoomGesture.js");
        var mediaViewerJsPath = Path.Combine(ServerDirectory, "Components", "Dialogs", "Common", "MediaViewerDialog.razor.js");
        var actionSheetJsPath = Path.Combine(ServerDirectory, "Components", "Pages", "Chat", "MobileActionSheet.razor.js");

        Assert.True(File.Exists(capacitorInitPath));
        Assert.True(File.Exists(spokesBridgePath));
        Assert.True(File.Exists(mediaZoomGesturePath));
        Assert.True(File.Exists(mediaViewerJsPath));
        Assert.True(File.Exists(actionSheetJsPath));

        // Verify superseded parallel history helpers have been completely deleted (AGENTS.md §2F)
        var zoomSrc = File.ReadAllText(mediaZoomGesturePath);
        Assert.DoesNotContain("pushOverlayHistoryState", zoomSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("popOverlayHistoryState", zoomSrc, StringComparison.Ordinal);
        var mediaViewerSrc = File.ReadAllText(mediaViewerJsPath);
        Assert.DoesNotContain("initLightboxState", mediaViewerSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanupLightboxState", mediaViewerSrc, StringComparison.Ordinal);
        var actionSheetSrc = File.ReadAllText(actionSheetJsPath);
        Assert.DoesNotContain("initHistoryState", actionSheetSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanupHistoryState", actionSheetSrc, StringComparison.Ordinal);

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_backstack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_backstack.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const capSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(capacitorInitPath)}}, 'utf8');
                const bridgeSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesBridgePath)}}, 'utf8');

                // =========================================================================
                // PART 1: Android Capacitor Native Back Interception (Zero history mutation)
                // =========================================================================
                {
                    let backHandler = null;
                    let minimizedCount = 0;
                    let inAppBackClicks = 0;
                    let historyPushCalls = 0;
                    let historyBackCalls = 0;
                    let popupDismissClicks = 0;
                    let headerlessDialogBackdropClicks = 0;

                    let mockPathname = '/chat/general';
                    let mockInAppBackVisible = true;
                    let mockChannelOpenDom = true;
                    let mockHeaderlessDialogDom = false;
                    let mockPopupOpenDom = false;

                    const fakeInAppBack = {
                        get offsetParent() { return mockInAppBackVisible ? {} : null; },
                        click() { inAppBackClicks++; }
                    };
                    const fakePopupBackdrop = {
                        getAttribute(name) { return name === 'data-spokes-back-dismiss' ? 'menu' : null; },
                        click() { popupDismissClicks++; }
                    };
                    // Simulates MediaViewerDialog DOM (NoHeader=true, CloseButton=false -> NO header close button)
                    const fakeHeaderlessDialog = {
                        querySelector(sel) {
                            if (sel.includes('.mud-overlay-dialog')) return { click() { headerlessDialogBackdropClicks++; } };
                            return null;
                        }
                    };

                    const context = {
                        console: { log() {}, warn() {}, error() {}, trace() {} },
                        setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask,
                        requestAnimationFrame: (cb) => { cb(); return 1; },
                        performance: { timeOrigin: Date.now() - 1000, now: () => 1000 },
                        PerformanceObserver: { supportedEntryTypes: [] },
                        CustomEvent: class { constructor(t, o) { this.type = t; this.detail = o?.detail; } },
                        navigator: { userAgent: 'Mozilla/5.0 Capacitor Android', connection: { effectiveType: '4g', downlink: 10, rtt: 50 } },
                        innerWidth: 393, innerHeight: 852,
                        location: {
                            get pathname() { return mockPathname; },
                            search: '?client=mobile',
                            get href() { return 'https://test.local' + mockPathname + '?client=mobile'; },
                            origin: 'https://test.local'
                        },
                        history: {
                            state: null,
                            length: 2,
                            pushState(s) { this.state = s; historyPushCalls++; },
                            back() { historyBackCalls++; }
                        },
                        localStorage: { getItem() { return null; }, setItem() {} },
                        Capacitor: {
                            isNativePlatform: () => true,
                            getPlatform: () => 'android',
                            Plugins: {
                                App: {
                                    async addListener(evt, fn) {
                                        if (evt === 'backButton') backHandler = fn;
                                        return { remove() {} };
                                    },
                                    minimizeApp() { minimizedCount++; }
                                }
                            }
                        },
                        document: {
                            readyState: 'complete',
                            addEventListener() {},
                            getElementById(id) { return id === 'chat-mobile-back-btn' ? fakeInAppBack : null; },
                            querySelector(sel) {
                                if (sel === '.mud-main-content.chat-channel-open') return mockChannelOpenDom ? {} : null;
                                return null;
                            },
                            querySelectorAll(sel) {
                                if (sel === '[data-spokes-back-dismiss]') return mockPopupOpenDom ? [fakePopupBackdrop] : [];
                                if (sel === '.mud-dialog-container') return mockHeaderlessDialogDom ? [fakeHeaderlessDialog] : [];
                                return [];
                            }
                        },
                        dispatchEvent() {},
                        addEventListener() {}
                    };
                    context.window = context;

                    vm.runInNewContext(bridgeSrc, context);
                    vm.runInNewContext(capSrc, context);
                    await context.window.initCapacitorEnv();

                    const bs = context.window.SpokesBridge.backStack;
                    assert.equal(bs.isNativeBackInterceptorActive(), true, 'Android Capacitor must activate native back interception');

                    // 1. MediaViewerDialog + stacked ShareModal + rapid double-swipe protection
                    const dismissed = [];
                    let mediaPendingResolve = null;
                    const mockProviderDotNetRef = {
                        invokeMethodAsync(method, id) {
                            dismissed.push(`${method}:${id}`);
                            if (id === 'media-1') {
                                return new Promise((resolve) => { mediaPendingResolve = resolve; });
                            }
                            if (id === 'share-1') {
                                bs.remove('dialog:share-1', { popHistory: false });
                                return Promise.resolve(true);
                            }
                            return Promise.resolve(true);
                        }
                    };

                    mockHeaderlessDialogDom = true;
                    bs.registerServerEntry('dialog:media-1', mockProviderDotNetRef, 'HandleBackForDialogAsync', {
                        arg: 'media-1', allowDismiss: true, useHistoryGuard: true, source: 'dialog', keepUntilRemoved: true
                    });
                    bs.registerServerEntry('dialog:share-1', mockProviderDotNetRef, 'HandleBackForDialogAsync', {
                        arg: 'share-1', allowDismiss: true, useHistoryGuard: false, source: 'dialog', keepUntilRemoved: true
                    });

                    // Android MUST NOT pollute window.history
                    assert.equal(historyPushCalls, 0, 'Android Capacitor must NOT call history.pushState for overlays');

                    // Back #1: closes ShareModal only (topmost), NOT MediaViewerDialog, NOT chat channel
                    backHandler();
                    assert.deepEqual(dismissed, ['HandleBackForDialogAsync:share-1']);
                    assert.equal(inAppBackClicks, 0);
                    assert.equal(bs.peekKey(), 'dialog:media-1');

                    // Back #2: initiates closing MediaViewerDialog (async)
                    backHandler();
                    assert.deepEqual(dismissed, ['HandleBackForDialogAsync:share-1', 'HandleBackForDialogAsync:media-1']);
                    assert.equal(inAppBackClicks, 0);

                    // Rapid duplicate Back #2b while MediaViewerDialog close is still in flight over SignalR -> MUST be absorbed!
                    backHandler();
                    assert.equal(dismissed.length, 2, 'Rapid double-swipe during async close must not re-invoke');
                    assert.equal(inAppBackClicks, 0, 'Rapid double-swipe must NEVER leak through to chat channel back');

                    // Complete MediaViewerDialog close
                    bs.remove('dialog:media-1', { popHistory: false });
                    mediaPendingResolve(true);
                    mockHeaderlessDialogDom = false;
                    await Promise.resolve();
                    assert.equal(bs.size(), 0);
                    assert.equal(historyBackCalls, 0, 'Android Capacitor must NOT call history.back() on overlay close');

                    // 2. Non-dismissible blocking modal (e.g. SecureChatSetupWizardDialog) -> consumes Back, NEVER falls through
                    bs.registerServerEntry('dialog:blocking-1', mockProviderDotNetRef, 'HandleBackForDialogAsync', {
                        arg: 'blocking-1', allowDismiss: false, useHistoryGuard: false, source: 'dialog', keepUntilRemoved: true
                    });
                    backHandler();
                    assert.equal(dismissed.length, 2, 'Blocking modal must not invoke dismissal');
                    assert.equal(inAppBackClicks, 0, 'Blocking modal must NEVER fall through to chat channel back');
                    assert.equal(bs.peekKey(), 'dialog:blocking-1');
                    bs.remove('dialog:blocking-1', { popHistory: false });

                    // 3. Voice fullscreen overlay -> closes fullscreen without exiting chat channel
                    let voiceFullscreenClosed = 0;
                    bs.push('voiceFullscreen', () => { voiceFullscreenClosed++; }, { useHistoryGuard: true, source: 'voice-fullscreen' });
                    assert.equal(historyPushCalls, 0);
                    backHandler();
                    assert.equal(voiceFullscreenClosed, 1);
                    assert.equal(inAppBackClicks, 0);
                    assert.equal(bs.size(), 0);

                    // 4. Lightweight popup/menu click-away backdrop -> closes popup before exiting channel
                    mockPopupOpenDom = true;
                    backHandler();
                    assert.equal(popupDismissClicks, 1);
                    assert.equal(inAppBackClicks, 0);
                    mockPopupOpenDom = false;

                    // 5. Defensive fallback: unmanaged headerless .mud-dialog-container in DOM without backStack entry -> NEVER falls through to chat back
                    mockHeaderlessDialogDom = true;
                    backHandler();
                    assert.equal(headerlessDialogBackdropClicks, 1);
                    assert.equal(inAppBackClicks, 0, 'Unmanaged headerless dialog must NEVER fall through to #chat-mobile-back-btn');
                    mockHeaderlessDialogDom = false;

                    // 6. Now with all overlays closed -> Back exits chat channel cleanly
                    backHandler();
                    assert.equal(inAppBackClicks, 1, 'Back with no overlays open must exit chat channel');
                    assert.equal(minimizedCount, 0);
                }

                // =========================================================================
                // PART 2: Web / iOS Browser Back (popstate history guard + Share nav safety)
                // =========================================================================
                {
                    const popListeners = [];
                    const historyStack = [{ state: null, url: 'https://test.local/chat/general' }];
                    let historyIndex = 0;
                    let mockHref = 'https://test.local/chat/general';

                    const webContext = {
                        console: { log() {}, warn() {}, error() {}, trace() {} },
                        setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask,
                        navigator: { userAgent: 'Mozilla/5.0 iPhone Mobile Safari' },
                        location: {
                            get href() { return mockHref; },
                            get pathname() { return new URL(mockHref).pathname; },
                            search: '',
                            origin: 'https://test.local'
                        },
                        history: {
                            get state() { return historyStack[historyIndex].state; },
                            get length() { return historyStack.length; },
                            pushState(state) {
                                historyStack.splice(historyIndex + 1);
                                historyStack.push({ state, url: mockHref });
                                historyIndex = historyStack.length - 1;
                            },
                            back() {
                                if (historyIndex > 0) {
                                    historyIndex--;
                                    for (const fn of [...popListeners]) fn({ state: historyStack[historyIndex].state });
                                }
                            }
                        },
                        localStorage: { getItem() { return null; }, setItem() {} },
                        document: { readyState: 'complete', addEventListener() {} },
                        addEventListener(evt, fn) { if (evt === 'popstate') popListeners.push(fn); },
                        dispatchEvent() {}
                    };
                    webContext.window = webContext;

                    vm.runInNewContext(bridgeSrc, webContext);
                    const bs = webContext.window.SpokesBridge.backStack;
                    assert.equal(bs.isNativeBackInterceptorActive(), false);

                    // Stacked overlay: MediaViewerDialog (useHistoryGuard=true) + ShareModal (useHistoryGuard=false)
                    const closedOrder = [];
                    bs.push('dialog:media-w', () => { closedOrder.push('media-w'); bs.remove('dialog:media-w', { popHistory: false }); }, {
                        useHistoryGuard: true, source: 'dialog', keepUntilRemoved: true
                    });
                    assert.equal(historyStack.length, 2);
                    assert.equal(webContext.history.state.__spokesBackKey, 'dialog:media-w');

                    bs.push('dialog:share-w', () => { closedOrder.push('share-w'); bs.remove('dialog:share-w', { popHistory: false }); }, {
                        useHistoryGuard: false, source: 'dialog', keepUntilRemoved: true
                    });

                    // Browser swipe #1: pops sentinel, dismisses TOPMOST ShareModal, re-arms MediaViewerDialog's sentinel!
                    webContext.history.back();
                    assert.deepEqual(closedOrder, ['share-w']);
                    assert.equal(bs.peekKey(), 'dialog:media-w');
                    assert.equal(webContext.history.state.__spokesBackKey, 'dialog:media-w', 'Underlying guarded overlay must be re-armed');

                    // Browser swipe #2: closes MediaViewerDialog!
                    webContext.history.back();
                    assert.deepEqual(closedOrder, ['share-w', 'media-w']);
                    assert.equal(bs.size(), 0);
                    assert.equal(historyIndex, 0);

                    // UI Close of stacked guarded overlay must NOT close underlying overlay via synthetic popstate
                    const closedOrder2 = [];
                    bs.push('outer', () => closedOrder2.push('outer'), { useHistoryGuard: true });
                    bs.push('inner', () => closedOrder2.push('inner'), { useHistoryGuard: true });
                    bs.remove('inner', { popHistory: true });
                    assert.deepEqual(closedOrder2, [], 'Synthetic popHistory for inner overlay must NOT trigger outer overlay onBack');
                    assert.equal(bs.peekKey(), 'outer');
                    bs.remove('outer', { popHistory: true });
                    assert.equal(bs.size(), 0);
                    assert.equal(historyIndex, 0);

                    // Share -> Nav.NavigateTo race protection: if location.href changed, remove(popHistory: true) must NOT call history.back()
                    bs.push('dialog:share-nav', () => {}, { useHistoryGuard: true });
                    const indexAfterPush = historyIndex;
                    mockHref = 'https://test.local/chat/other-channel';
                    bs.remove('dialog:share-nav', { popHistory: true });
                    assert.equal(historyIndex, indexAfterPush, 'Must NOT call history.back() after URL already changed');
                }

                console.log('BACKSTACK_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"BackStack Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("BACKSTACK_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SpokesAppJs_WindowFocus_HonorsExplicitDesktopFocusAndHostViewActiveChanges()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var spokesAppPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-app.js");
        Assert.True(File.Exists(spokesAppPath), $"spokes-app.js not found at {spokesAppPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_focus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_focus_test.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const spokesAppSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesAppPath)}}, 'utf8');

                const focusCalls = [];
                const winListeners = new Map();
                const docListeners = new Map();
                let electronFocusCallback = null;
                let docHasFocus = false;
                let reobserveCalls = 0;

                const context = {
                    console,
                    setTimeout,
                    clearTimeout,
                    fetch: () => Promise.resolve({ ok: true }),
                    navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpokesDesktop/1.0' },
                    document: {
                        visibilityState: 'visible',
                        hasFocus: () => docHasFocus,
                        addEventListener: (evt, fn) => {
                            if (!docListeners.has(evt)) docListeners.set(evt, new Set());
                            docListeners.get(evt).add(fn);
                        },
                        removeEventListener: (evt, fn) => docListeners.get(evt)?.delete(fn)
                    },
                    addEventListener: (evt, fn) => {
                        if (!winListeners.has(evt)) winListeners.set(evt, new Set());
                        winListeners.get(evt).add(fn);
                    },
                    removeEventListener: (evt, fn) => winListeners.get(evt)?.delete(fn),
                    dispatchEvent: (ev) => {
                        for (const fn of Array.from(winListeners.get(ev.type) || [])) fn(ev);
                    },
                    SpokesBridge: {
                        isDesktop: true,
                        onWindowFocusChanged: (cb) => {
                            electronFocusCallback = cb;
                            return () => { electronFocusCallback = null; };
                        }
                    },
                    _spokesReobserveReadReceipts: () => { reobserveCalls++; }
                };
                context.window = context;

                vm.runInNewContext(spokesAppSrc, context);

                const dotNetRef = {
                    invokeMethodAsync: (method, val) => {
                        if (method === 'OnWindowFocusChanged') focusCalls.push(val);
                        return Promise.resolve();
                    }
                };

                // Initial register when WebContentsView doesn't have DOM focus yet
                context.window.registerWindowFocus(dotNetRef);
                assert.equal(context.window.isSpokesWindowFocused(), false);
                assert.deepEqual(focusCalls, [false]);

                // Electron reports mainWindow focused even though child WebContentsView document.hasFocus() is still false
                assert.ok(typeof electronFocusCallback === 'function', 'Electron focus callback must be subscribed');
                electronFocusCallback(true);
                assert.equal(context.window.isSpokesWindowFocused(), true, 'Explicit Electron focus must count as focused when host view is active');
                assert.deepEqual(focusCalls, [false, true]);
                assert.equal(reobserveCalls, 1);

                // Desktop side-rail switches away to another server (dispatches spokes:host-view-active-changed)
                context.window._spokesHostViewActive = false;
                context.window.dispatchEvent({ type: 'spokes:host-view-active-changed', detail: { active: false } });
                assert.equal(context.window.isSpokesWindowFocused(), false, 'Inactive host view must report unfocused');
                assert.deepEqual(focusCalls, [false, true, false]);

                // Switching back to this server view restores focus
                context.window._spokesHostViewActive = true;
                context.window.dispatchEvent({ type: 'spokes:host-view-active-changed', detail: { active: true } });
                assert.equal(context.window.isSpokesWindowFocused(), true, 'Reactivated host view must restore focus');
                assert.deepEqual(focusCalls, [false, true, false, true]);

                // Electron reports window unfocused
                electronFocusCallback(false);
                assert.equal(context.window.isSpokesWindowFocused(), false, 'Electron blur must report unfocused');
                assert.deepEqual(focusCalls, [false, true, false, true, false]);

                // Cleanup unsubscribes host-view listener
                context.window.unregisterWindowFocus();
                assert.equal(winListeners.get('spokes:host-view-active-changed')?.size ?? 0, 0);

                console.log('FOCUS_JS_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Focus Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("FOCUS_JS_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SpokesAppJs_AndroidBackgroundResume_SequenceOrderedAckDrivenAndNeverDeadlocks()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var spokesAppPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-app.js");
        var capacitorInitPath = Path.Combine(ServerDirectory, "wwwroot", "js", "capacitor-init.js");
        Assert.True(File.Exists(spokesAppPath), $"spokes-app.js not found at {spokesAppPath}");
        Assert.True(File.Exists(capacitorInitPath), $"capacitor-init.js not found at {capacitorInitPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_android_presence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_android_presence_test.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const spokesAppSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesAppPath)}}, 'utf8');
                const capInitSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(capacitorInitPath)}}, 'utf8');

                const rpcCalls = [];
                const fetchCalls = [];
                const winListeners = new Map();
                const docListeners = new Map();
                const timers = new Map();
                let nextTimerId = 1;
                let appStateCb = null;
                let verifyAliveCalls = 0;
                let failNextFocusRpc = false;

                const fakeSetTimeout = (fn, ms) => {
                    const id = nextTimerId++;
                    timers.set(id, { fn, ms });
                    return id;
                };
                const fakeClearTimeout = (id) => {
                    timers.delete(id);
                };

                const context = {
                    console,
                    AbortController,
                    Blob,
                    CustomEvent: class CustomEvent { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
                    setTimeout: fakeSetTimeout,
                    clearTimeout: fakeClearTimeout,
                    fetch: (url, opts) => {
                        const entry = { url, body: opts?.body ? JSON.parse(opts.body) : null, aborted: false };
                        if (opts?.signal) {
                            opts.signal.addEventListener('abort', () => { entry.aborted = true; });
                        }
                        fetchCalls.push(entry);
                        return new Promise(() => {}); // Simulate slow/stalled background HTTP request
                    },
                    navigator: {
                        userAgent: 'Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Mobile Safari/537.36 Capacitor'
                    },
                    location: { origin: 'https://spokes.example.com', search: '?client=mobile', pathname: '/chat' },
                    document: {
                        readyState: 'complete',
                        visibilityState: 'visible',
                        hasFocus: () => true,
                        addEventListener: (evt, fn) => {
                            if (!docListeners.has(evt)) docListeners.set(evt, new Set());
                            docListeners.get(evt).add(fn);
                        },
                        removeEventListener: (evt, fn) => docListeners.get(evt)?.delete(fn)
                    },
                    addEventListener: (evt, fn) => {
                        if (!winListeners.has(evt)) winListeners.set(evt, new Set());
                        winListeners.get(evt).add(fn);
                    },
                    removeEventListener: (evt, fn) => winListeners.get(evt)?.delete(fn),
                    dispatchEvent: (ev) => {
                        for (const fn of Array.from(winListeners.get(ev.type) || [])) fn(ev);
                    },
                    Capacitor: {
                        isNativePlatform: () => true,
                        Plugins: {
                            App: {
                                addListener: (name, cb) => {
                                    if (name === 'appStateChange') appStateCb = cb;
                                    return { remove: () => { appStateCb = null; } };
                                }
                            }
                        }
                    },
                    spokesResume: {
                        onHostViewActivated: () => {},
                        onHostViewDeactivated: () => {},
                        onConnectionDown: (reason) => { throw new Error('Unexpected onConnectionDown: ' + reason); },
                        verifyCircuitAlive: (p, forceProbe, reason) => { verifyAliveCalls++; }
                    },
                    _currentSubscriptionId: 'sub-android-1',
                    _currentPresenceToken: 'tok-1',
                    __spokesBlazorSocket: { readyState: 1 },
                    _blazorDisconnected: false
                };
                context.window = context;

                vm.runInNewContext(capInitSrc, context);
                vm.runInNewContext(spokesAppSrc, context);

                const dotNetRef = {
                    invokeMethodAsync: (method, ...args) => {
                        rpcCalls.push({ method, args });
                        if (method === 'OnWindowFocusChanged' && failNextFocusRpc) {
                            failNextFocusRpc = false;
                            return Promise.reject(new Error('transient transport glitch'));
                        }
                        return Promise.resolve();
                    }
                };

                context.window.registerWindowFocus(dotNetRef);
                context.window.registerActivityTracker(dotNetRef);
                await Promise.resolve();
                await Promise.resolve();

                // Initial focus registered with seq=1
                assert.equal(rpcCalls.length, 1);
                assert.equal(rpcCalls[0].method, 'OnWindowFocusChanged');
                assert.equal(rpcCalls[0].args[0], true);
                assert.equal(rpcCalls[0].args[1], 1);
                assert.equal(timers.size, 1, 'Idle timer should be armed while focused');

                // 1. Android onStop() backgrounds the app with native shell epoch=1
                context.document.visibilityState = 'hidden';
                context.window.spokesSetServerViewActive(false, { nativeShellLifecycle: true, epoch: 1 });
                await Promise.resolve();
                await Promise.resolve();

                assert.equal(context.window.isSpokesWindowFocused(), false);
                assert.equal(timers.size, 0, 'Mobile idle timer must be paused while backgrounded');
                assert.equal(fetchCalls.length, 1);
                assert.equal(fetchCalls[0].url, '/spokesapi/presence/unfocus');
                assert.equal(fetchCalls[0].body.sequence, 2);
                assert.equal(fetchCalls[0].aborted, false);
                assert.equal(rpcCalls[1].method, 'OnWindowFocusChanged');
                assert.equal(rpcCalls[1].args[0], false);
                assert.equal(rpcCalls[1].args[1], 2);

                // 2. Simulate > 5 minutes hidden (320,000ms) while WebSocket stayed open!
                // Previously this deadlocked on hiddenDuration >= 180000.
                context.window._lastHiddenDurationMs = 320000;

                // 3. Android onResume() runs BEFORE document.visibilityState flips to 'visible' (lags 'hidden')
                context.window.spokesSetServerViewActive(true, { nativeShellLifecycle: true, epoch: 2 });
                await Promise.resolve();
                await Promise.resolve();

                assert.equal(context.window.isSpokesWindowFocused(), true, 'Explicit native foreground must count as focused even while visibilityState lags hidden');
                assert.equal(fetchCalls[0].aborted, true, 'Stalled background /unfocus HTTP fetch must be aborted on resume');
                assert.equal(rpcCalls.length, 3, 'Resume after >3m must immediately invoke OnWindowFocusChanged(true) without deadlocking');
                assert.equal(rpcCalls[2].method, 'OnWindowFocusChanged');
                assert.equal(rpcCalls[2].args[0], true);
                assert.equal(rpcCalls[2].args[1], 3);
                assert.equal(context.window._lastHiddenDurationMs, 0, 'Confirmed focus ACK must clear _lastHiddenDurationMs');
                assert.ok(verifyAliveCalls >= 1, 'Foreground resume must piggyback verifyCircuitAlive');

                // 4. Stale buffered Capacitor WebMessagePort appStateChange({ isActive: false }) wakes up late -> MUST BE IGNORED
                assert.ok(typeof appStateCb === 'function');
                appStateCb({ isActive: false });
                await Promise.resolve();
                assert.equal(context.window.isSpokesWindowFocused(), true, 'Stale WebMessagePort appStateChange(false) must not clobber nativeShellLifecycle state');
                assert.equal(rpcCalls.length, 3, 'Ignored stale appStateChange must not fire unfocus');

                // 5. Stale lower epoch native call (epoch=1 < 2) -> MUST BE IGNORED
                context.window.spokesSetServerViewActive(false, { nativeShellLifecycle: true, epoch: 1 });
                assert.equal(context.window.isSpokesWindowFocused(), true);
                assert.equal(rpcCalls.length, 3);

                // 6. Disconnected socket does NOT poison confirmed focus; spokes:blazor-connected immediately reconciles
                context.document.visibilityState = 'visible';
                context.window._blazorDisconnected = true;
                context.window.dispatchEvent(new context.CustomEvent('spokes:blazor-disconnected'));
                context.window._forceUpdateFocusState();
                assert.equal(rpcCalls.length, 3, 'Must not send focus while disconnected');

                context.window._blazorDisconnected = false;
                context.window.dispatchEvent(new context.CustomEvent('spokes:blazor-connected'));
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(rpcCalls.length, 4, 'spokes:blazor-connected must immediately re-sync focus');
                assert.equal(rpcCalls[3].args[0], true);
                assert.equal(rpcCalls[3].args[1], 4);

                // 7. pagehide followed by foreground resume clears _spokesPageUnloading and sends newer seq
                for (const fn of Array.from(winListeners.get('pagehide') || [])) fn({});
                assert.equal(context.window._spokesPageUnloading, true);
                const offlinePost = fetchCalls[fetchCalls.length - 1];
                assert.equal(offlinePost.url, '/spokesapi/presence/offline');
                assert.equal(offlinePost.body.sequence, 5);

                context.window.spokesSetServerViewActive(true, { nativeShellLifecycle: true, epoch: 3 });
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(context.window._spokesPageUnloading, false);
                assert.equal(rpcCalls.length, 5);
                assert.equal(rpcCalls[4].args[0], true);
                assert.equal(rpcCalls[4].args[1], 6);

                // 8. Transient RPC rejection does NOT poison confirmed state and retries cleanly
                context.window.dispatchEvent(new context.CustomEvent('spokes:blazor-disconnected'));
                failNextFocusRpc = true;
                context.window.dispatchEvent(new context.CustomEvent('spokes:blazor-connected'));
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(rpcCalls.length, 6);
                // Run scheduled 250ms retry timer
                const retryEntry = Array.from(timers.entries()).find(([, v]) => v.ms === 250);
                assert.ok(retryEntry, 'Failed focus RPC must schedule bounded retry');
                timers.delete(retryEntry[0]);
                retryEntry[1].fn();
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(rpcCalls.length, 7);
                assert.equal(rpcCalls[6].args[0], true);
                assert.equal(rpcCalls[6].args[1], 8);

                context.window.unregisterWindowFocus();
                context.window.unregisterActivityTracker();
                console.log('ANDROID_PRESENCE_JS_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Android Presence Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("ANDROID_PRESENCE_JS_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SpokesBootJs_LongBackgroundResume_DoesNotForceRetentionExpiredAndEmitsConnectedEvent()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var spokesBootPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-boot.js");
        Assert.True(File.Exists(spokesBootPath), $"spokes-boot.js not found at {spokesBootPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_boot_resume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_boot_resume_test.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const bootSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesBootPath)}}, 'utf8');
                assert.equal(bootSrc.includes('MAX_BACKGROUND_RETENTION_MS'), false, 'Stale 3-minute MAX_BACKGROUND_RETENTION_MS must be deleted');
                assert.equal(bootSrc.includes('background-retention-expired'), false, 'Stale background-retention-expired branch must be deleted');

                const docListeners = new Map();
                const dispatchedEvents = [];
                let reconnectCalls = 0;
                let pingCalls = 0;
                let nowMs = 1_000_000;

                const FakeDate = class extends Date {
                    static now() { return nowMs; }
                };

                const context = {
                    console,
                    Date: FakeDate,
                    URL,
                    AbortController,
                    CustomEvent: class CustomEvent { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
                    Event: class Event { constructor(type) { this.type = type; } },
                    MutationObserver: class { observe() {} disconnect() {} },
                    setTimeout,
                    clearTimeout,
                    requestAnimationFrame: (cb) => cb(),
                    performance: { timeOrigin: 1_000_000, getEntriesByType: () => [] },
                    sessionStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                    localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                    navigator: {
                        onLine: true,
                        userAgent: 'Mozilla/5.0 (Linux; Android 15) Capacitor'
                    },
                    location: {
                        protocol: 'https:',
                        host: 'spokes.example.com',
                        origin: 'https://spokes.example.com',
                        href: 'https://spokes.example.com/chat?client=mobile',
                        pathname: '/chat',
                        search: '?client=mobile',
                        replace: () => {}
                    },
                    document: {
                        readyState: 'loading',
                        visibilityState: 'visible',
                        cookie: '',
                        documentElement: { appendChild: () => {} },
                        body: { children: [], appendChild: () => {} },
                        getElementById: () => null,
                        querySelectorAll: () => [],
                        addEventListener: (evt, fn) => {
                            if (!docListeners.has(evt)) docListeners.set(evt, new Set());
                            docListeners.get(evt).add(fn);
                        },
                        removeEventListener: (evt, fn) => docListeners.get(evt)?.delete(fn)
                    },
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    dispatchEvent: (ev) => { dispatchedEvents.push(ev.type); },
                    fetch: () => Promise.resolve({ ok: true, headers: { get: () => 'application/json' }, json: () => Promise.resolve({}) }),
                    WebSocket: class FakeWs {
                        constructor() { this.readyState = 1; }
                        addEventListener() {}
                        close() { this.readyState = 3; }
                    },
                    Capacitor: { isNativePlatform: () => true },
                    Blazor: {
                        start: () => Promise.resolve(),
                        addEventListener: () => {},
                        reconnect: () => {
                            reconnectCalls++;
                            // Simulate SignalR already Connected: resolves true WITHOUT calling reconnectionHandler.onConnectionUp()
                            return Promise.resolve(true);
                        }
                    },
                    __spokesBootConfig: {
                        isStaticLoginRoute: false,
                        session: { authenticated: true, expiresAt: nowMs + 86_400_000 * 80 }
                    },
                    spokesDotNetRef: {
                        invokeMethodAsync: (method) => {
                            if (method === 'PingCircuit') pingCalls++;
                            return Promise.resolve(true);
                        }
                    }
                };
                context.window = context;

                vm.runInNewContext(bootSrc, context);
                context.window._blazorCircuitEverOpened = true;
                context.window._spokesHostViewActive = true;
                context.window._spokesHostViewExplicitlyActivated = true;

                // 1. Background for 6 minutes (360,000ms) -> return to foreground with live socket
                context.document.visibilityState = 'hidden';
                for (const fn of Array.from(docListeners.get('visibilitychange') || [])) fn();
                nowMs += 360_000;
                context.document.visibilityState = 'visible';
                for (const fn of Array.from(docListeners.get('visibilitychange') || [])) fn();

                assert.equal(context.window._blazorDisconnected, undefined, '6-minute background with live socket must NOT force synthetic disconnect');
                assert.equal(reconnectCalls, 0, 'Live socket must NOT invoke Blazor.reconnect() on 6-minute foreground resume');

                // Wait past 250ms fallback probe to verify liveness check clears _lastHiddenDurationMs
                await new Promise(r => setTimeout(r, 320));
                assert.equal(pingCalls, 1, 'Expected fallback liveness probe after long background when no focus RPC ran');
                assert.equal(context.window._lastHiddenDurationMs, 0, 'Successful liveness probe must clear _lastHiddenDurationMs');

                // 2. If onConnectionDown fires and Blazor.reconnect() resolves true without onConnectionUp handler,
                // spokes-boot.js must still emit spokes:blazor-disconnected + spokes:blazor-connected and clear hidden duration.
                context.window._lastHiddenDurationMs = 240_000;
                context.window.spokesResume.onConnectionDown('test-drop');
                await new Promise(r => setTimeout(r, 40));

                assert.equal(reconnectCalls, 1);
                assert.equal(context.window._blazorDisconnected, false);
                assert.equal(context.window._lastHiddenDurationMs, 0);
                assert.ok(dispatchedEvents.includes('spokes:blazor-disconnected'));
                assert.ok(dispatchedEvents.includes('spokes:blazor-connected'));

                console.log('BOOT_RESUME_JS_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Boot resume Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("BOOT_RESUME_JS_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Wave5Js_SharedHoverToolbar_LoafPhaseBreakdown_AndUploadCallbackDeduplication()
    {
        var chatJsPath = Path.Combine(ServerDirectory, "Components", "Pages", "Chat", "Chat.razor.js");
        var profilerJsPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-chat-profiler.js");
        var uploadJsPath = Path.Combine(ServerDirectory, "wwwroot", "js", "upload.js");

        Assert.True(File.Exists(chatJsPath), $"Missing {chatJsPath}");
        Assert.True(File.Exists(profilerJsPath), $"Missing {profilerJsPath}");
        Assert.True(File.Exists(uploadJsPath), $"Missing {uploadJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"SpokesWave5JsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var runnerPath = Path.Combine(tempDir, "run.mjs");
            var chatJsUri = new Uri(chatJsPath).AbsoluteUri;
            var profilerEsc = profilerJsPath.Replace("\\", "\\\\");
            var uploadEsc = uploadJsPath.Replace("\\", "\\\\");

            var script = $$"""
                import fs from 'node:fs';
                import vm from 'node:vm';

                function assert(cond, msg) {
                    if (!cond) throw new Error('ASSERT FAILED: ' + msg);
                }

                // ── 1) Test Chat.razor.js shared desktop hover toolbar & keyboard sidebar delegation ──
                class FakeEl {
                    constructor(tag, attrs = {}, classes = []) {
                        this.tagName = tag.toUpperCase();
                        this.attrs = { ...attrs };
                        this.classSet = new Set(classes);
                        this.children = [];
                        this.parentNode = null;
                        this.listeners = {};
                        this.dataset = {};
                        this.isConnected = true;
                        this.clientHeight = 500;
                        this.scrollHeight = 800;
                        this.scrollTop = 0;
                        this.clicked = 0;
                    }
                    get firstElementChild() { return this.children[0] || null; }
                    get parentElement() { return this.parentNode; }
                    getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
                    setAttribute(k, v) { this.attrs[k] = String(v); }
                    appendChild(child) {
                        if (child.parentNode) child.parentNode.removeChild(child);
                        child.parentNode = this;
                        this.children.push(child);
                        return child;
                    }
                    removeChild(child) {
                        const i = this.children.indexOf(child);
                        if (i >= 0) this.children.splice(i, 1);
                        if (child.parentNode === this) child.parentNode = null;
                        return child;
                    }
                    addEventListener(type, fn) {
                        (this.listeners[type] ||= []).push(fn);
                    }
                    removeEventListener(type, fn) {
                        if (!this.listeners[type]) return;
                        this.listeners[type] = this.listeners[type].filter(f => f !== fn);
                    }
                    dispatch(type, ev) {
                        for (const fn of (this.listeners[type] || [])) fn(ev);
                    }
                    click() { this.clicked++; }
                    contains(node) {
                        let cur = node;
                        while (cur) { if (cur === this) return true; cur = cur.parentNode; }
                        return false;
                    }
                    matches(sel) {
                        if (sel === '.mud-list-item-clickable[role="button"]') {
                            return this.classSet.has('mud-list-item-clickable') && this.attrs['role'] === 'button';
                        }
                        if (sel === '.message-row[data-message-id]') {
                            return this.classSet.has('message-row') && 'data-message-id' in this.attrs;
                        }
                        if (sel === '.message-actions button[data-msg-action]') {
                            return this.tagName === 'BUTTON' && 'data-msg-action' in this.attrs && this.closest('.message-actions');
                        }
                        if (sel === '.message-actions') return this.classSet.has('message-actions');
                        if (sel === '.message-actions:not([data-spokes-shared-actions])') {
                            return this.classSet.has('message-actions') && !('data-spokes-shared-actions' in this.attrs);
                        }
                        if (sel === '.message-actions-stack') return this.classSet.has('message-actions-stack');
                        if (sel === '.message-actions-slot') return this.classSet.has('message-actions-slot');
                        return false;
                    }
                    closest(sel) {
                        let cur = this;
                        while (cur) {
                            if (cur.matches(sel)) return cur;
                            cur = cur.parentNode;
                        }
                        return null;
                    }
                    querySelector(sel) {
                        for (const c of this.children) {
                            if (c.matches(sel)) return c;
                            const nested = c.querySelector(sel);
                            if (nested) return nested;
                        }
                        return null;
                    }
                    querySelectorAll(sel) {
                        const out = [];
                        for (const c of this.children) {
                            if (c.matches(sel)) out.push(c);
                            out.push(...c.querySelectorAll(sel));
                        }
                        return out;
                    }
                    cloneNode() {
                        const copy = new FakeEl(this.tagName, this.attrs, [...this.classSet]);
                        for (const c of this.children) copy.appendChild(c.cloneNode(true));
                        return copy;
                    }
                }

                const pageContainer = new FakeEl('div', {}, ['chat-page-container']);
                const sidebarRow = new FakeEl('div', { role: 'button', tabindex: '0' }, ['mud-list-item', 'mud-list-item-clickable']);
                pageContainer.appendChild(sidebarRow);

                const messagesContainer = new FakeEl('div', { id: 'chat-messages-container', 'data-channel-id': 'ch1' });
                const row1 = new FakeEl('div', { 'data-message-id': 'm1' }, ['message-row']);
                const stack1 = new FakeEl('div', {}, ['message-actions-stack']);
                const slot1 = new FakeEl('div', {}, ['message-actions-slot']);
                stack1.appendChild(slot1);
                row1.appendChild(stack1);
                const row2 = new FakeEl('div', { 'data-message-id': 'm2' }, ['message-row']);
                const stack2 = new FakeEl('div', {}, ['message-actions-stack']);
                const slot2 = new FakeEl('div', {}, ['message-actions-slot']);
                stack2.appendChild(slot2);
                row2.appendChild(stack2);
                messagesContainer.appendChild(row1);
                messagesContainer.appendChild(row2);

                // Prototype rendered by Chat.razor as a hidden light-DOM div (no .content DocumentFragment)
                const tplDiv = new FakeEl('div', { id: 'spokes-desktop-message-actions-template', hidden: '', 'aria-hidden': 'true', 'data-template-key': '1|👍|Add|Reply' }, ['d-none']);
                const tplActions = new FakeEl('div', { 'data-spokes-shared-actions': '1' }, ['message-actions']);
                const reactBtn = new FakeEl('button', { 'data-msg-action': 'react', 'data-emoji': '👍' }, ['message-action-btn']);
                const moreBtn = new FakeEl('button', { 'data-msg-action': 'more' }, ['message-action-btn']);
                tplActions.appendChild(reactBtn);
                tplActions.appendChild(moreBtn);
                tplDiv.appendChild(tplActions);

                // Also verify a browser-realistic HTMLTemplateElement whose .content is an empty DocumentFragment
                // (what Blazor produces if <template> is ever rendered interactively via appendChild).
                const tplWithEmptyFragment = new FakeEl('template', { id: 'spokes-desktop-message-actions-template', 'data-template-key': '1|❤️|Add|Reply' });
                tplWithEmptyFragment.content = { firstElementChild: null };
                tplWithEmptyFragment.appendChild(tplActions.cloneNode(true));

                let activeTemplateEl = tplDiv;

                globalThis.Element = FakeEl;
                globalThis.window = {
                    _blazorDisconnected: false,
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    getComputedStyle: () => ({ transform: 'none' }),
                    spokesUpload: { initDropZone: () => {} }
                };
                globalThis.document = {
                    visibilityState: 'visible',
                    hasFocus: () => true,
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    getElementById: (id) => {
                        if (id === 'chat-messages-container') return messagesContainer;
                        if (id === 'spokes-desktop-message-actions-template') return activeTemplateEl;
                        return null;
                    },
                    querySelector: (sel) => sel === '.chat-page-container' ? pageContainer : null
                };
                globalThis.requestAnimationFrame = () => {};
                globalThis.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
                globalThis.MutationObserver = class { observe() {} disconnect() {} };

                const chatMod = await import('{{chatJsUri}}');
                const invoked = [];
                const dotNetHelper = {
                    invokeMethodAsync: (method, ...args) => {
                        invoked.push({ helper: 1, method, args });
                        return Promise.resolve();
                    }
                };

                chatMod.initChatChannelView('chat-messages-container', null, dotNetHelper, 1024, false, 'ch1');

                // Hover row1 via a Text-node-like target (no .closest, only parentElement) -> mounts shared node into slot1
                messagesContainer.dispatch('pointerover', { target: { parentElement: row1 } });
                assert(slot1.children.length === 1, 'Expected shared toolbar mounted in row1 slot from hidden div prototype');
                const sharedRef = slot1.children[0];

                // Hover row2 -> moves the EXACT same single DOM node into slot2
                messagesContainer.dispatch('mouseover', { target: row2 });
                assert(slot1.children.length === 0, 'Expected shared toolbar moved out of row1 slot');
                assert(slot2.children.length === 1 && slot2.children[0] === sharedRef, 'Expected same shared node reused on row2 slot');

                // Click react button -> invokes HandleDesktopMessageActionJS with m2, react, 👍 and keeps toolbar
                const mountedReact = slot2.querySelector('.message-actions button[data-msg-action]');
                messagesContainer.dispatch('click', { target: mountedReact });
                assert(invoked.length === 1 && invoked[0].method === 'HandleDesktopMessageActionJS' && invoked[0].args[0] === 'm2' && invoked[0].args[1] === 'react', 'Expected delegated react invoke');

                // Click more button -> invokes HandleDesktopMessageActionJS without premature flicker detach
                const mountedMore = slot2.children[0].children[1];
                messagesContainer.dispatch('click', { target: mountedMore });
                assert(invoked.length === 2 && invoked[1].args[0] === 'm2' && invoked[1].args[1] === 'more', 'Expected delegated more invoke');
                assert(slot2.children.length === 1, 'Expected shared toolbar to stay visible in slot until Blazor swaps slot for popup');

                // Simulate Blazor atomically replacing slot2 with Blazor-owned popup toolbar (.message-actions.force-visible)
                stack2.removeChild(slot2);
                const blazorPopup = new FakeEl('div', {}, ['message-actions', 'force-visible']);
                stack2.appendChild(blazorPopup);
                messagesContainer.dispatch('pointerover', { target: row2 });
                assert(stack2.children.length === 1 && stack2.children[0] === blazorPopup, 'Expected hover on active popup row not to duplicate toolbar');

                // Hovering row1 recovers the shared node cleanly even though its previous slot2 was disconnected by Blazor
                messagesContainer.dispatch('pointerover', { target: row1 });
                assert(slot1.children.length === 1 && slot1.children[0] === sharedRef, 'Expected shared node recovered cleanly from disconnected slot');

                // Browser-realistic <template> with empty .content DocumentFragment still falls back to firstElementChild
                activeTemplateEl = tplWithEmptyFragment;
                messagesContainer.dispatch('focusin', { target: row1 });
                assert(slot1.children.length === 1 && slot1.children[0] !== sharedRef, 'Expected template refresh and fallback to firstElementChild when template.content is empty');

                // Re-initializing same container refreshes dotNetHelper reference
                const dotNetHelper2 = {
                    invokeMethodAsync: (method, ...args) => {
                        invoked.push({ helper: 2, method, args });
                        return Promise.resolve();
                    }
                };
                chatMod.initChatChannelView('chat-messages-container', null, dotNetHelper2, 1024, false, 'ch1');
                messagesContainer.dispatch('pointerover', { target: row1 });
                const refreshedReact = slot1.querySelector('.message-actions button[data-msg-action]');
                messagesContainer.dispatch('click', { target: refreshedReact });
                assert(invoked.length === 3 && invoked[2].helper === 2 && invoked[2].args[0] === 'm1', 'Expected refreshed dotNetHelper to receive delegated clicks');

                // Delegated keyboard Enter/Space on sidebar row triggers click()
                let prevented = false;
                pageContainer.dispatch('keydown', { key: 'Enter', target: sidebarRow, preventDefault: () => { prevented = true; } });
                assert(prevented && sidebarRow.clicked === 1, 'Expected Enter key to activate sidebar row');

                chatMod.dispose();
                assert(slot1.children.length === 0, 'Expected dispose() to detach shared toolbar');
                messagesContainer.dispatch('pointerover', { target: row1 });
                assert(slot1.children.length === 0, 'Expected dispose() to remove delegated hover listeners');

                // ── 2) Test spokes-chat-profiler.js LoAF phase breakdown when scripts is empty ──
                const profilerSrc = fs.readFileSync('{{profilerEsc}}', 'utf8');
                let loafCb = null;
                const profWin = {
                    performance: { now: () => 1000, timeOrigin: 1000000 },
                    PerformanceObserver: class {
                        static supportedEntryTypes = ['long-animation-frame'];
                        constructor(cb) { loafCb = cb; }
                        observe() {}
                        disconnect() {}
                    },
                    MutationObserver: class { observe() {} disconnect() {} },
                    requestAnimationFrame: () => {},
                    setInterval: () => 1,
                    clearInterval: () => {},
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    document: {
                        body: {},
                        querySelector: () => null,
                        getElementById: () => null,
                        addEventListener: () => {},
                        removeEventListener: () => {}
                    },
                    location: { pathname: '/chat/general' },
                    spokesDotNetRef: { invokeMethodAsync: () => Promise.resolve(1) }
                };
                profWin.window = profWin;
                vm.runInNewContext(profilerSrc, profWin);
                const profiler = profWin.__spokesChatProfilerFactory({
                    now: () => 1000,
                    pushCapped: () => {},
                    getNetworkSummary: () => ({}),
                    formatNetworkSummary: () => '',
                    flushMediaBatch: () => {},
                    setPendingMediaTapAnchorMs: () => {},
                    setProfilerActive: () => {},
                    setMediaHook: () => {},
                    getSession: () => ({ longTasks: [] }),
                    isMobileViewport: () => false
                });
                await profiler.enable(profWin.spokesDotNetRef);
                profiler.startTransition('SelectChannel', 'Tap Channel');
                assert(typeof loafCb === 'function', 'Expected LoAF PerformanceObserver callback registered');
                loafCb({
                    getEntries: () => [{
                        startTime: 200,
                        duration: 402,
                        blockingDuration: 351,
                        renderStart: 560,
                        styleAndLayoutStart: 580,
                        scripts: []
                    }]
                });
                assert(profilerSrc.includes('work=${workMs}ms') && profilerSrc.includes('render=${renderMs}ms') && profilerSrc.includes('layout=${layoutMs}ms'), 'Expected LoAF phase formatting');
                await profiler.disable();

                // ── 3) Test upload.js callback deduplication at 100% and no Saving... callback ──
                const uploadSrc = fs.readFileSync('{{uploadEsc}}', 'utf8');
                const uploadCalls = [];
                let xhrHandlers = {};
                let uploadProgressHandler = null;
                class FakeXHR {
                    constructor() {
                        this.upload = { addEventListener: (ev, fn) => { if (ev === 'progress') uploadProgressHandler = fn; } };
                        this.status = 200;
                        this.responseText = JSON.stringify([{ filePath: '/a.jpg', fileName: 'a.jpg', contentType: 'image/jpeg', fileSizeBytes: 1000 }]);
                    }
                    addEventListener(ev, fn) { xhrHandlers[ev] = fn; }
                    open() {}
                    send() {
                        uploadProgressHandler({ lengthComputable: true, loaded: 1000, total: 1000 });
                        xhrHandlers.load();
                    }
                    getResponseHeader() { return ''; }
                }
                const upWin = {
                    XMLHttpRequest: FakeXHR,
                    performance: { now: () => 100 },
                    console: { log: () => {}, warn: () => {}, error: () => {} },
                    document: { addEventListener: () => {} }
                };
                upWin.window = upWin;
                vm.runInNewContext(uploadSrc, upWin);
                const uploadDotNet = {
                    invokeMethodAsync: (method, ...args) => {
                        uploadCalls.push({ method, args });
                        return Promise.resolve();
                    }
                };
                await new Promise((resolve, reject) => {
                    upWin.spokesUpload.performUpload({}, '/spokesapi/files/chat/general', uploadDotNet, resolve, reject, [], []);
                });
                assert(uploadCalls.filter(c => c.method === 'OnProgressUpdateCallback').length === 0, 'Expected zero OnProgressUpdateCallback at instant 100% transfer');
                assert(uploadCalls.filter(c => c.method === 'OnProcessingCallback').length === 1, 'Expected single Processing... callback and no dead Saving... callback');
                assert(uploadCalls[0].args[0] === 'Processing...', 'Expected Processing... stage');

                console.log('WAVE5_JS_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Wave 5 Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("WAVE5_JS_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SpokesTouch_SoundManager_And_ChatComposer_TouchReliabilityBehavior()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var spokesAppPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-app.js");
        var spokesBridgePath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokesbridge.js");
        var soundManagerPath = Path.Combine(ServerDirectory, "wwwroot", "js", "soundManager.js");
        var chatComposerPath = Path.Combine(ServerDirectory, "Components", "Pages", "Chat", "ChatComposer.razor.js");

        Assert.True(File.Exists(spokesAppPath), $"spokes-app.js not found at {spokesAppPath}");
        Assert.True(File.Exists(spokesBridgePath), $"spokesbridge.js not found at {spokesBridgePath}");
        Assert.True(File.Exists(soundManagerPath), $"soundManager.js not found at {soundManagerPath}");
        Assert.True(File.Exists(chatComposerPath), $"ChatComposer.razor.js not found at {chatComposerPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_touch_reliability_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_touch_reliability.mjs");
            var chatComposerUri = new Uri(chatComposerPath).AbsoluteUri;
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const spokesAppSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesAppPath)}}, 'utf8');
                const spokesBridgeSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesBridgePath)}}, 'utf8');
                const soundManagerSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(soundManagerPath)}}, 'utf8');

                // ── 1) Test spokes-app.js (window.spokesTouch) + spokesbridge.js (spokesPerf.tapDiagnostics) ──
                function createTouchEnv({ hasTouch = true, coarse = true } = {}) {
                    let fakeNow = 1000;
                    const docListeners = []; // { type, fn, capture }
                    const winListeners = [];
                    const timers = new Map();
                    let nextTimerId = 1;

                    class FakeClassList {
                        constructor(classes = []) { this._set = new Set(classes); }
                        contains(c) { return this._set.has(c); }
                        add(c) { this._set.add(c); }
                        remove(c) { this._set.delete(c); }
                    }

                    class FakeElement {
                        constructor(tag, { id = '', classes = [], attrs = {}, rect = { left: 10, top: 10, right: 90, bottom: 60, width: 80, height: 50 }, parent = null, disabled = false } = {}) {
                            this.nodeType = 1;
                            this.tagName = tag.toUpperCase();
                            this.id = id;
                            this.className = classes.join(' ');
                            this.classList = new FakeClassList(classes);
                            this._attrs = new Map(Object.entries(attrs));
                            this._rect = rect;
                            this.parentElement = parent;
                            this.children = [];
                            if (parent && Array.isArray(parent.children)) parent.children.push(this);
                            this.disabled = disabled;
                            this.isConnected = true;
                            this.scrollTop = 0;
                            this.scrollLeft = 0;
                            this.clickCount = 0;
                            this._ripples = [];
                            this._targetListeners = new Map();
                        }
                        getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
                        hasAttribute(k) { return this._attrs.has(k); }
                        getBoundingClientRect() { return this._rect; }
                        addEventListener(type, fn) {
                            if (!this._targetListeners.has(type)) this._targetListeners.set(type, []);
                            this._targetListeners.get(type).push(fn);
                        }
                        contains(other) {
                            let cur = other;
                            while (cur) {
                                if (cur === this) return true;
                                cur = cur.parentElement;
                            }
                            return false;
                        }
                        matches(selectorList) {
                            const selectors = selectorList.split(',').map(s => s.trim()).filter(Boolean);
                            for (const sel of selectors) {
                                if (sel.startsWith('#') && this.id === sel.slice(1)) return true;
                                if (sel.startsWith('.') && this.classList.contains(sel.slice(1))) return true;
                                if (sel === 'a[href]' && this.tagName === 'A' && this.hasAttribute('href')) return true;
                                if (sel.startsWith('[')) {
                                    const m = /^\[([^=\]]+)(?:="([^"]*)")?\]$/.exec(sel);
                                    if (m) {
                                        const attr = m[1];
                                        const val = m[2];
                                        if (val === undefined && this.hasAttribute(attr)) return true;
                                        if (val !== undefined && this.getAttribute(attr) === val) return true;
                                    }
                                }
                                if (/^[a-z]+$/i.test(sel) && this.tagName === sel.toUpperCase()) return true;
                            }
                            return false;
                        }
                        closest(selectorList) {
                            let cur = this;
                            while (cur) {
                                if (typeof cur.matches === 'function' && cur.matches(selectorList)) return cur;
                                cur = cur.parentElement;
                            }
                            return null;
                        }
                        querySelectorAll(sel) {
                            if (sel === '.mud-ripple-effect') return this._ripples.slice();
                            return [];
                        }
                        addFakeRipple() {
                            const r = { removed: false, remove: () => { r.removed = true; this._ripples = this._ripples.filter(x => x !== r); } };
                            this._ripples.push(r);
                            return r;
                        }
                        click() {
                            const ev = makeEvent('click', this, { clientX: this._rect.left + 10, clientY: this._rect.top + 10 });
                            dispatchDocEvent(ev);
                            if (!ev._stopped) {
                                this.clickCount++;
                            }
                        }
                    }

                    function makeEvent(type, target, extra = {}) {
                        const ev = Object.assign({
                            type,
                            target,
                            defaultPrevented: false,
                            _stopped: false,
                            _propagationStopped: false,
                            preventDefault() { this.defaultPrevented = true; },
                            stopImmediatePropagation() { this._stopped = true; this._propagationStopped = true; },
                            stopPropagation() { this._propagationStopped = true; }
                        }, extra);
                        return ev;
                    }

                    function dispatchDocEvent(ev) {
                        // 1. Capture listeners on document
                        for (const l of docListeners.slice()) {
                            if (l.type === ev.type && l.capture) {
                                l.fn(ev);
                                if (ev._stopped) return ev;
                            }
                        }
                        // 2. Target-phase listeners on target element
                        const tListeners = ev.target && ev.target._targetListeners ? (ev.target._targetListeners.get(ev.type) || []) : [];
                        for (const fn of tListeners.slice()) {
                            fn(ev);
                            if (ev._stopped) return ev;
                        }
                        if (ev._propagationStopped) return ev;
                        // 3. Bubble listeners on document
                        for (const l of docListeners.slice()) {
                            if (l.type === ev.type && !l.capture) {
                                l.fn(ev);
                                if (ev._stopped) return ev;
                            }
                        }
                        return ev;
                    }

                    const body = new FakeElement('body');
                    const docEl = new FakeElement('html');
                    let hitElement = null;

                    const ctx = {
                        console: { log: () => {}, warn: () => {}, error: () => {}, debug: () => {} },
                        performance: { now: () => fakeNow },
                        Date: class FakeDate extends Date { static now() { return fakeNow; } },
                        setTimeout: (fn, delay) => {
                            const id = nextTimerId++;
                            timers.set(id, { fn, at: fakeNow + (delay || 0) });
                            return id;
                        },
                        clearTimeout: (id) => { timers.delete(id); },
                        setInterval: () => 1,
                        clearInterval: () => {},
                        navigator: { maxTouchPoints: hasTouch ? 5 : 0, userAgent: hasTouch ? 'Mozilla/5.0 (Linux; Android 14)' : 'Mozilla/5.0 (Windows NT 10.0)' },
                        matchMedia: (q) => ({ matches: coarse && q.includes('coarse') }),
                        getSelection: () => ({ toString: () => '' }),
                        location: { pathname: '/chat/general', href: 'http://localhost/chat/general', origin: 'http://localhost' },
                        scrollX: 0,
                        scrollY: 0,
                        innerWidth: hasTouch ? 412 : 1440,
                        innerHeight: hasTouch ? 915 : 900,
                        addEventListener: (type, fn, opts) => winListeners.push({ type, fn, capture: typeof opts === 'boolean' ? opts : Boolean(opts && opts.capture) }),
                        removeEventListener: (type, fn, opts) => {
                            const cap = typeof opts === 'boolean' ? opts : Boolean(opts && opts.capture);
                            const idx = winListeners.findIndex(l => l.type === type && l.fn === fn && l.capture === cap);
                            if (idx >= 0) winListeners.splice(idx, 1);
                        },
                        document: {
                            readyState: 'complete',
                            body,
                            documentElement: docEl,
                            elementFromPoint: () => hitElement,
                            addEventListener: (type, fn, opts) => docListeners.push({ type, fn, capture: typeof opts === 'boolean' ? opts : Boolean(opts && opts.capture) }),
                            removeEventListener: (type, fn, opts) => {
                                const cap = typeof opts === 'boolean' ? opts : Boolean(opts && opts.capture);
                                const idx = docListeners.findIndex(l => l.type === type && l.fn === fn && l.capture === cap);
                                if (idx >= 0) docListeners.splice(idx, 1);
                            },
                            querySelector: () => null,
                            querySelectorAll: () => [],
                            getElementById: () => null
                        }
                    };
                    if (hasTouch) ctx.ontouchstart = null;
                    ctx.window = ctx;

                    vm.runInNewContext(spokesBridgeSrc, ctx);
                    vm.runInNewContext(spokesAppSrc, ctx);

                    function advanceTime(ms) {
                        const target = fakeNow + ms;
                        while (true) {
                            let earliest = null;
                            let earliestId = null;
                            for (const [id, t] of timers.entries()) {
                                if (t.at <= target && (!earliest || t.at < earliest.at)) {
                                    earliest = t;
                                    earliestId = id;
                                }
                            }
                            if (!earliest) break;
                            timers.delete(earliestId);
                            fakeNow = earliest.at;
                            earliest.fn();
                        }
                        fakeNow = target;
                    }

                    return {
                        ctx,
                        body,
                        FakeElement,
                        docListeners,
                        setHitElement: (el) => { hitElement = el; },
                        makeEvent,
                        dispatchDocEvent,
                        advanceTime,
                        setNow: (t) => { fakeNow = t; }
                    };
                }

                // 1a. Desktop fine-pointer non-touch must NOT auto-install
                const desktopEnv = createTouchEnv({ hasTouch: false, coarse: false });
                assert.equal(desktopEnv.ctx.window.spokesTouch.isInstalled(), false, 'spokesTouch must not auto-install on desktop non-touch');

                // 1b. Mobile touch environment auto-installs
                const env = createTouchEnv({ hasTouch: true, coarse: true });
                const touch = env.ctx.window.spokesTouch;
                assert.equal(touch.isInstalled(), true, 'spokesTouch must auto-install on touch devices');

                const scroller = new env.FakeElement('div', { id: 'scroller', parent: env.body, rect: { left: 0, top: 60, right: 400, bottom: 900, width: 400, height: 840 } });
                const fixedBtn = new env.FakeElement('button', { id: 'fixed-btn', classes: ['mud-button-root', 'mud-ripple'], parent: env.body, rect: { left: 10, top: 10, right: 58, bottom: 58, width: 48, height: 48 } });
                const listBtn = new env.FakeElement('button', { id: 'list-btn', classes: ['mud-button-root', 'mud-ripple'], parent: scroller, rect: { left: 10, top: 100, right: 390, bottom: 156, width: 380, height: 56 } });
                const blazorDiv = new env.FakeElement('div', { id: 'blazor-row', parent: scroller, rect: { left: 10, top: 170, right: 390, bottom: 226, width: 380, height: 56 } });
                blazorDiv._blazorEvents_1 = { handlers: { click: { id: 99 } } };
                const blazorChild = new env.FakeElement('span', { id: 'blazor-span', parent: blazorDiv, rect: { left: 16, top: 180, right: 200, bottom: 210, width: 184, height: 30 } });

                // Native click within grace window cancels rescue
                env.setHitElement(fixedBtn);
                env.dispatchDocEvent(env.makeEvent('touchstart', fixedBtn, { touches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(30);
                env.dispatchDocEvent(env.makeEvent('touchend', fixedBtn, { touches: [], changedTouches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(4);
                fixedBtn.click(); // native click arrives in 4ms
                env.advanceTime(100);
                assert.equal(fixedBtn.clickCount, 1, 'Native tap must click exactly once');
                assert.equal(touch.getDiagnostics().nativeClicks, 1);
                assert.equal(touch.getDiagnostics().rescuedTaps, 0);

                // Dropped click after 16px thumb roll (pointercancel, scrollDelta=0) -> rescued once
                env.setHitElement(fixedBtn);
                env.dispatchDocEvent(env.makeEvent('touchstart', fixedBtn, { touches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(15);
                env.dispatchDocEvent(env.makeEvent('touchmove', fixedBtn, { touches: [{ clientX: 30, clientY: 46 }] }));
                env.dispatchDocEvent(env.makeEvent('pointercancel', fixedBtn));
                env.advanceTime(25);
                env.dispatchDocEvent(env.makeEvent('touchend', fixedBtn, { touches: [], changedTouches: [{ clientX: 30, clientY: 46 }] }));
                env.advanceTime(100);
                assert.equal(fixedBtn.clickCount, 2, 'Dropped thumb-roll tap must be rescued exactly once');
                assert.equal(touch.getDiagnostics().rescuedTaps, 1);

                // Duplicate-click shield suppresses tardy native click arriving within 350ms of rescue
                const tardyEv = env.makeEvent('click', fixedBtn, { clientX: 30, clientY: 46 });
                env.dispatchDocEvent(tardyEv);
                assert.equal(tardyEv.defaultPrevented, true, 'Tardy native click within 350ms must be prevented');
                assert.equal(tardyEv._stopped, true, 'Tardy native click must have immediate propagation stopped');
                assert.equal(touch.getDiagnostics().suppressedDuplicates, 1);

                // Blazor _blazorEvents_ non-semantic div rescued when child span tapped
                env.advanceTime(400);
                env.setHitElement(blazorChild);
                env.dispatchDocEvent(env.makeEvent('touchstart', blazorChild, { touches: [{ clientX: 50, clientY: 195 }] }));
                env.advanceTime(20);
                env.dispatchDocEvent(env.makeEvent('touchmove', blazorChild, { touches: [{ clientX: 64, clientY: 195 }] }));
                env.dispatchDocEvent(env.makeEvent('touchend', blazorChild, { touches: [], changedTouches: [{ clientX: 64, clientY: 195 }] }));
                env.advanceTime(100);
                assert.equal(blazorDiv.clickCount, 1, 'Custom Blazor @onclick div must be rescued');
                assert.equal(touch.getDiagnostics().rescuedTaps, 2);

                // Real scroll aborts ripple and does NOT rescue
                env.advanceTime(400);
                env.setHitElement(listBtn);
                const ripple = listBtn.addFakeRipple();
                env.dispatchDocEvent(env.makeEvent('touchstart', listBtn, { touches: [{ clientX: 100, clientY: 120 }] }));
                env.advanceTime(20);
                scroller.scrollTop = 30;
                env.dispatchDocEvent(env.makeEvent('touchmove', listBtn, { touches: [{ clientX: 100, clientY: 100 }] }));
                env.dispatchDocEvent(env.makeEvent('pointercancel', listBtn));
                assert.equal(ripple.removed, true, 'Real scroll must immediately clear misleading MudBlazor ripple');
                env.dispatchDocEvent(env.makeEvent('touchend', listBtn, { touches: [], changedTouches: [{ clientX: 100, clientY: 100 }] }));
                env.advanceTime(100);
                assert.equal(listBtn.clickCount, 0, 'Real scroll must never rescue a click');
                assert.ok(touch.getDiagnostics().abortedRipples >= 1);

                // Fast-fling guard: tapping inside actively scrolling container (stepPx >= 6 within 60ms) must NOT rescue;
                // tapping a fixed button outside that scroller STILL rescues!
                env.advanceTime(400);
                scroller.scrollTop = 100;
                env.dispatchDocEvent(env.makeEvent('scroll', scroller));
                env.advanceTime(16);
                scroller.scrollTop = 135; // 35px/frame fast fling
                env.dispatchDocEvent(env.makeEvent('scroll', scroller));
                env.advanceTime(10); // 10ms later (< 60ms)
                env.setHitElement(listBtn);
                env.dispatchDocEvent(env.makeEvent('touchstart', listBtn, { touches: [{ clientX: 100, clientY: 120 }] }));
                env.advanceTime(30);
                env.dispatchDocEvent(env.makeEvent('touchend', listBtn, { touches: [], changedTouches: [{ clientX: 100, clientY: 120 }] }));
                env.advanceTime(100);
                assert.equal(listBtn.clickCount, 0, 'Fast-fling stop tap inside moving list must NOT rescue click');

                // Fixed button outside fast-flinging scroller STILL rescues
                scroller.scrollTop = 170;
                env.dispatchDocEvent(env.makeEvent('scroll', scroller));
                env.advanceTime(16);
                scroller.scrollTop = 205;
                env.dispatchDocEvent(env.makeEvent('scroll', scroller));
                env.advanceTime(10);
                env.setHitElement(fixedBtn);
                env.dispatchDocEvent(env.makeEvent('touchstart', fixedBtn, { touches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(30);
                env.dispatchDocEvent(env.makeEvent('touchend', fixedBtn, { touches: [], changedTouches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(100);
                assert.equal(fixedBtn.clickCount, 3, 'Fixed button outside flinging scroller must still rescue');

                // Long press (> 350ms) must NOT rescue
                env.advanceTime(400);
                env.dispatchDocEvent(env.makeEvent('touchstart', fixedBtn, { touches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(420);
                env.dispatchDocEvent(env.makeEvent('touchend', fixedBtn, { touches: [], changedTouches: [{ clientX: 30, clientY: 30 }] }));
                env.advanceTime(100);
                assert.equal(fixedBtn.clickCount, 3, 'Long-press must never rescue');

                // Custom target calling e.preventDefault() on touchend (ChatComposer send / popover) must NOT rescue
                const customBtn = new env.FakeElement('button', { id: 'custom-send', parent: env.body, rect: { left: 200, top: 10, right: 248, bottom: 58, width: 48, height: 48 } });
                customBtn.addEventListener('touchend', (e) => e.preventDefault());
                env.setHitElement(customBtn);
                env.dispatchDocEvent(env.makeEvent('touchstart', customBtn, { touches: [{ clientX: 220, clientY: 30 }] }));
                env.advanceTime(25);
                env.dispatchDocEvent(env.makeEvent('touchend', customBtn, { touches: [], changedTouches: [{ clientX: 220, clientY: 30 }] }));
                env.advanceTime(100);
                assert.equal(customBtn.clickCount, 0, 'Target calling preventDefault on touchend must not be rescued');

                // Excluded controls (disabled, input, data-spokes-no-tap-rescue, lightbox-image-area) must NOT rescue
                const disabledBtn = new env.FakeElement('button', { id: 'dis', disabled: true, parent: env.body });
                const textInput = new env.FakeElement('input', { id: 'inp', parent: env.body });
                const optOutWrap = new env.FakeElement('div', { attrs: { 'data-spokes-no-tap-rescue': '' }, parent: env.body });
                const optOutBtn = new env.FakeElement('button', { id: 'opt', parent: optOutWrap });
                const lightboxWrap = new env.FakeElement('div', { id: 'lightbox-image-area', parent: env.body });
                const lightboxBtn = new env.FakeElement('button', { id: 'lb', parent: lightboxWrap });
                for (const el of [disabledBtn, textInput, optOutBtn, lightboxBtn]) {
                    env.setHitElement(el);
                    env.dispatchDocEvent(env.makeEvent('touchstart', el, { touches: [{ clientX: 20, clientY: 20 }] }));
                    env.advanceTime(20);
                    env.dispatchDocEvent(env.makeEvent('touchend', el, { touches: [], changedTouches: [{ clientX: 20, clientY: 20 }] }));
                    env.advanceTime(100);
                    assert.equal(el.clickCount, 0, `Excluded element ${el.id} must never be rescued`);
                }

                // spokesPerf.tapDiagnostics() delegates to spokesTouch.getDiagnostics()
                const perfTapDiag = env.ctx.window.spokesPerf.tapDiagnostics();
                assert.ok(perfTapDiag && perfTapDiag.rescuedTaps === 3, 'spokesPerf.tapDiagnostics() must expose spokesTouch diagnostics');

                // destroy() detaches cleanly
                touch.destroy();
                assert.equal(touch.isInstalled(), false);

                // ── 2) Test soundManager.js listener hygiene ──
                function runSoundManagerScenario(isNative) {
                    const activeListeners = [];
                    let resumeCalls = 0;
                    let bufferCreations = 0;
                    class FakeAudioContext {
                        constructor() { this.state = 'suspended'; this.destination = {}; }
                        resume() { resumeCalls++; this.state = 'running'; return Promise.resolve(); }
                        createBuffer() { bufferCreations++; return {}; }
                        createBufferSource() { return { buffer: null, connect() {}, start() {} }; }
                    }
                    const smCtx = {
                        console: { log: () => {}, warn: () => {}, error: () => {} },
                        localStorage: { getItem: () => '', setItem: () => {}, removeItem: () => {} },
                        AudioContext: FakeAudioContext,
                        isCapacitorNative: () => isNative,
                        addEventListener: () => {},
                        removeEventListener: () => {},
                        document: {
                            addEventListener: (type, fn, opts) => activeListeners.push({ type, fn, capture: Boolean(opts && opts.capture) }),
                            removeEventListener: (type, fn, opts) => {
                                const cap = Boolean(opts && opts.capture);
                                const idx = activeListeners.findIndex(l => l.type === type && l.fn === fn && l.capture === cap);
                                if (idx >= 0) activeListeners.splice(idx, 1);
                            }
                        }
                    };
                    smCtx.window = smCtx;
                    vm.runInNewContext(soundManagerSrc, smCtx);
                    return { activeListeners, getResumeCalls: () => resumeCalls, getBufferCreations: () => bufferCreations };
                }

                const nativeSm = runSoundManagerScenario(true);
                assert.equal(nativeSm.activeListeners.length, 0, 'Native mobile SoundManager must attach 0 global gesture listeners');

                const webSm = runSoundManagerScenario(false);
                assert.deepEqual(webSm.activeListeners.map(l => l.type).sort(), ['keydown', 'pointerdown'], 'Web SoundManager must only attach pointerdown & keydown');
                // Fire first pointerdown -> unlocks audio and detaches all unlock listeners immediately
                webSm.activeListeners.find(l => l.type === 'pointerdown').fn();
                await Promise.resolve();
                assert.equal(webSm.activeListeners.length, 0, 'Web SoundManager must remove unlock listeners once AudioContext is running');
                assert.equal(webSm.getResumeCalls(), 1);

                // ── 3) Test ChatComposer.razor.js iOS-only visualViewport gating ──
                async function runComposerViewportTest(userAgent, platform = '', maxTouchPoints = 5, cacheBust = '') {
                    const vvListeners = [];
                    const textareaListeners = [];
                    let intervalCount = 0;
                    const fakeTextarea = {
                        value: '',
                        addEventListener: (type, fn) => textareaListeners.push({ type, fn }),
                        focus: () => {},
                        dispatchEvent: () => {}
                    };
                    const fakeContainer = { querySelector: () => fakeTextarea };

                    globalThis.window = globalThis;
                    Object.defineProperty(globalThis, 'navigator', {
                        configurable: true,
                        value: { userAgent, platform, maxTouchPoints }
                    });
                    globalThis.innerWidth = 412;
                    globalThis.innerHeight = 915;
                    globalThis.scrollTo = () => {};
                    globalThis.visualViewport = {
                        height: 915,
                        addEventListener: (type, fn) => vvListeners.push({ type, fn }),
                        removeEventListener: () => {}
                    };
                    globalThis.document = {
                        documentElement: {
                            classList: { add: () => {}, remove: () => {} },
                            style: { setProperty: () => {} },
                            scrollTop: 0
                        },
                        body: { scrollTop: 0 },
                        querySelector: () => null,
                        getElementById: (id) => id === 'composer-1' ? fakeContainer : null
                    };
                    const origSetInterval = globalThis.setInterval;
                    const origClearInterval = globalThis.clearInterval;
                    globalThis.setInterval = () => { intervalCount++; return intervalCount; };
                    globalThis.clearInterval = () => {};

                    try {
                        const mod = await import('{{chatComposerUri}}' + cacheBust);
                        mod.initComposer('composer-1', false, false, null);
                        const focusListener = textareaListeners.find(l => l.type === 'focus');
                        if (focusListener) focusListener.fn();
                        return { vvListeners, hasFocusListener: Boolean(focusListener), intervalCount };
                    } finally {
                        globalThis.setInterval = origSetInterval;
                        globalThis.clearInterval = origClearInterval;
                    }
                }

                const androidComposer = await runComposerViewportTest('Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/128.0.0.0 Mobile Safari/537.36', 'Linux armv8l', 5, '?ua=android');
                assert.equal(androidComposer.vvListeners.length, 0, 'Android must NOT attach visualViewport resize/scroll listeners in ChatComposer');
                assert.equal(androidComposer.hasFocusListener, false, 'Android must NOT attach iOS 20ms focus polling loop');
                assert.equal(androidComposer.intervalCount, 0);

                const iosComposer = await runComposerViewportTest('Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1', 'iPhone', 5, '?ua=ios');
                assert.deepEqual(iosComposer.vvListeners.map(l => l.type).sort(), ['resize', 'scroll'], 'iOS must keep visualViewport resize/scroll listeners');
                assert.equal(iosComposer.hasFocusListener, true, 'iOS must keep focus viewport polling fallback');

                console.log('TOUCH_RELIABILITY_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Touch reliability Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("TOUCH_RELIABILITY_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ChatJs_MobileLongPress_HighlightStateMachineAndHandoffBehavior()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var chatJsPath = Path.Combine(ServerDirectory, "Components", "Pages", "Chat", "Chat.razor.js");
        Assert.True(File.Exists(chatJsPath), $"Missing {chatJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"SpokesLongPressJsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_long_press.mjs");
            var chatJsUri = new Uri(chatJsPath).AbsoluteUri;
            var script = $$"""
                import assert from 'node:assert/strict';

                let nowMs = 0;
                let nextTimerId = 1;
                const timers = new Map();
                let nextRafId = 1;
                const rafs = new Map();

                globalThis.setTimeout = (fn, delay = 0) => {
                    const id = nextTimerId++;
                    timers.set(id, { at: nowMs + Math.max(0, delay), fn });
                    return id;
                };
                globalThis.clearTimeout = (id) => { timers.delete(id); };
                globalThis.requestAnimationFrame = (fn) => {
                    const id = nextRafId++;
                    rafs.set(id, fn);
                    return id;
                };
                globalThis.cancelAnimationFrame = (id) => { rafs.delete(id); };

                function advanceMs(ms) {
                    const target = nowMs + ms;
                    while (true) {
                        let nextId = null;
                        let nextEntry = null;
                        for (const [id, entry] of timers.entries()) {
                            if (entry.at <= target && (!nextEntry || entry.at < nextEntry.at)) {
                                nextId = id;
                                nextEntry = entry;
                            }
                        }
                        if (!nextEntry) break;
                        timers.delete(nextId);
                        nowMs = nextEntry.at;
                        nextEntry.fn();
                    }
                    nowMs = target;
                }

                function flushRaf() {
                    const pending = Array.from(rafs.entries());
                    rafs.clear();
                    for (const [, fn] of pending) fn(nowMs);
                }

                class FakeEl {
                    constructor(tag, attrs = {}, classes = []) {
                        this.nodeType = 1;
                        this.tagName = tag.toUpperCase();
                        this.attrs = { ...attrs };
                        this.classSet = new Set(classes);
                        this.classList = {
                            contains: (c) => this.classSet.has(c),
                            add: (c) => this.classSet.add(c),
                            remove: (c) => this.classSet.delete(c)
                        };
                        this.children = [];
                        this.parentNode = null;
                        this.parentElement = null;
                        this.listeners = new Map();
                        this.isConnected = true;
                    }
                    appendChild(child) {
                        child.parentNode = this;
                        child.parentElement = this;
                        this.children.push(child);
                        return child;
                    }
                    getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
                    setAttribute(k, v) { this.attrs[k] = String(v); }
                    removeAttribute(k) { delete this.attrs[k]; }
                    addEventListener(type, fn) {
                        if (!this.listeners.has(type)) this.listeners.set(type, new Set());
                        this.listeners.get(type).add(fn);
                    }
                    removeEventListener(type, fn) {
                        this.listeners.get(type)?.delete(fn);
                    }
                    listenerCount(type) { return this.listeners.get(type)?.size || 0; }
                    dispatch(type, ev = {}) {
                        for (const fn of Array.from(this.listeners.get(type) || [])) fn(ev);
                    }
                    contains(node) {
                        let cur = node;
                        while (cur) { if (cur === this) return true; cur = cur.parentNode; }
                        return false;
                    }
                    matches(sel) {
                        return sel.split(',').map(s => s.trim()).some(part => {
                            if (part === '.message-row[data-message-id]') {
                                return this.classSet.has('message-row') && 'data-message-id' in this.attrs;
                            }
                            if (part === 'input' || part === 'textarea' || part === 'select') {
                                return this.tagName === part.toUpperCase();
                            }
                            if (part === '[contenteditable="true"]') {
                                return this.attrs['contenteditable'] === 'true';
                            }
                            return false;
                        });
                    }
                    closest(sel) {
                        let cur = this;
                        while (cur) {
                            if (typeof cur.matches === 'function' && cur.matches(sel)) return cur;
                            cur = cur.parentElement;
                        }
                        return null;
                    }
                }

                const winListeners = new Map();
                const docListeners = new Map();
                const haptics = [];
                globalThis.Element = FakeEl;
                globalThis.window = {
                    _blazorDisconnected: false,
                    triggerHaptic: (kind) => haptics.push(kind),
                    getSelection: () => ({ removeAllRanges: () => {} }),
                    addEventListener: (t, fn) => {
                        if (!winListeners.has(t)) winListeners.set(t, new Set());
                        winListeners.get(t).add(fn);
                    },
                    removeEventListener: (t, fn) => { winListeners.get(t)?.delete(fn); },
                    dispatch: (t, ev = {}) => { for (const fn of Array.from(winListeners.get(t) || [])) fn(ev); }
                };
                globalThis.document = {
                    hidden: false,
                    addEventListener: (t, fn) => {
                        if (!docListeners.has(t)) docListeners.set(t, new Set());
                        docListeners.get(t).add(fn);
                    },
                    removeEventListener: (t, fn) => { docListeners.get(t)?.delete(fn); },
                    dispatch: (t, ev = {}) => { for (const fn of Array.from(docListeners.get(t) || [])) fn(ev); },
                    getElementById: () => null,
                    querySelector: () => null
                };

                const container = new FakeEl('div', { id: 'chat-messages-container' });
                const row1 = new FakeEl('div', { 'data-message-id': 'msg-1' }, ['message-row']);
                const msgText = new FakeEl('p');
                const textNode = { nodeType: 3, parentElement: msgText };
                row1.appendChild(msgText);
                const editTextarea = new FakeEl('textarea');
                row1.appendChild(editTextarea);
                container.appendChild(row1);

                const invoked = [];
                let pendingResolve = null;
                let pendingReject = null;
                let invokeMode = 'manual';
                const dotNetHelper = {
                    invokeMethodAsync: (method, ...args) => {
                        invoked.push({ method, args });
                        if (invokeMode === 'manual') {
                            return new Promise((res, rej) => { pendingResolve = res; pendingReject = rej; });
                        }
                        if (invokeMode === 'reject') {
                            return Promise.reject(new Error('circuit lost'));
                        }
                        return Promise.resolve();
                    }
                };

                const chatMod = await import('{{chatJsUri}}');
                chatMod.initLongPress(container, dotNetHelper);
                assert.equal(container.listenerCount('touchstart'), 1);

                // 1) Quick tap (<90ms): never highlights, never invokes OnMobileLongPress
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 50, clientY: 50 }] });
                advanceMs(60);
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Quick tap before 90ms must not flash highlight');
                container.dispatch('touchend', {});
                advanceMs(500);
                assert.equal(row1.getAttribute('data-long-pressing'), null);
                assert.equal(invoked.length, 0);

                // 2) Scroll/drag (>10px) after 90ms: highlights while still, clears immediately on drag, cancels trigger
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 50, clientY: 50 }] });
                advanceMs(95);
                assert.equal(row1.getAttribute('data-long-pressing'), 'true', 'Holding past 90ms should highlight message row');
                container.dispatch('touchmove', { touches: [{ clientX: 50, clientY: 65 }] });
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Dragging >10px must immediately clear press highlight');
                advanceMs(400);
                assert.equal(invoked.length, 0, 'Dragging >10px must cancel long-press');

                // 3) Container scroll cancels active hold immediately
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 50, clientY: 50 }] });
                advanceMs(100);
                assert.equal(row1.getAttribute('data-long-pressing'), 'true');
                container.dispatch('scroll', {});
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Scroll must immediately clear press highlight');
                advanceMs(400);
                assert.equal(invoked.length, 0);

                // 4) Full 400ms hold with <=10px thumb wobble + text node target: seamless zero-blink handoff to Blazor
                container.dispatch('touchstart', { target: textNode, touches: [{ clientX: 100, clientY: 100 }] });
                advanceMs(95);
                assert.equal(row1.getAttribute('data-long-pressing'), 'true', 'Text node target must resolve parent .message-row');
                container.dispatch('touchmove', { touches: [{ clientX: 105, clientY: 104 }] }); // ~6.4px <= 10px slop
                assert.equal(row1.getAttribute('data-long-pressing'), 'true', 'Small thumb jitter <=10px must keep highlight');
                advanceMs(310); // total 405ms
                assert.equal(invoked.length, 1);
                assert.equal(invoked[0].method, 'OnMobileLongPress');
                assert.equal(invoked[0].args[0], 'msg-1');
                assert.equal(haptics.length, 1);

                // Finger lifts BEFORE Blazor Task resolves -> must retain data-long-pressing so highlight does not blink
                container.dispatch('touchend', {});
                assert.equal(row1.getAttribute('data-long-pressing'), 'true', 'Must preserve highlight across touchend until Blazor settles');

                // Blazor applies .message-row-action-target and resolves the Task
                row1.classList.add('message-row-action-target');
                pendingResolve();
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Once Blazor target class is applied and promise settles, data-long-pressing clears immediately');
                row1.classList.remove('message-row-action-target');

                // 5) Handoff via rAF when Blazor class isn't yet present on promise resolution
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 80, clientY: 80 }] });
                advanceMs(405);
                assert.equal(invoked.length, 2);
                container.dispatch('touchend', {});
                pendingResolve();
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(row1.getAttribute('data-long-pressing'), 'true', 'Waits 1 rAF when Blazor patch has not yet painted');
                flushRaf();
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Clears data-long-pressing on next animation frame');

                // 6) Safety cap clears highlight even if Blazor promise never resolves
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 80, clientY: 80 }] });
                advanceMs(405);
                container.dispatch('touchend', {});
                assert.equal(row1.getAttribute('data-long-pressing'), 'true');
                advanceMs(505);
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Safety timeout must prevent stranded highlight if circuit stalls');

                // 7) Rejected Blazor promise clears highlight immediately
                invokeMode = 'reject';
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 80, clientY: 80 }] });
                advanceMs(405);
                await Promise.resolve();
                await Promise.resolve();
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Rejected OnMobileLongPress must clear highlight immediately');

                // 8) Editable controls (textarea), multi-touch, blur, visibilitychange, and dispose()
                invokeMode = 'manual';
                container.dispatch('touchstart', { target: editTextarea, touches: [{ clientX: 20, clientY: 20 }] });
                advanceMs(450);
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Editable textarea inside row must not highlight or open action sheet');

                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 20, clientY: 20 }, { clientX: 60, clientY: 60 }] });
                advanceMs(450);
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Multi-touch must not highlight or open action sheet');

                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 20, clientY: 20 }] });
                advanceMs(100);
                assert.equal(row1.getAttribute('data-long-pressing'), 'true');
                globalThis.window.dispatch('blur');
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'window blur must clear press highlight');

                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 20, clientY: 20 }] });
                advanceMs(100);
                assert.equal(row1.getAttribute('data-long-pressing'), 'true');
                globalThis.document.hidden = true;
                globalThis.document.dispatch('visibilitychange');
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'visibilitychange hidden must clear press highlight');
                globalThis.document.hidden = false;

                // Re-initializing on same container must not duplicate listeners and clears active press
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 20, clientY: 20 }] });
                advanceMs(100);
                chatMod.initLongPress(container, dotNetHelper);
                assert.equal(container.listenerCount('touchstart'), 1, 'Re-init on same container must not stack duplicate listeners');
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'Re-init must clear any active press state');

                // dispose() clears active press AND detaches all listeners
                container.dispatch('touchstart', { target: msgText, touches: [{ clientX: 20, clientY: 20 }] });
                advanceMs(100);
                chatMod.dispose();
                assert.equal(row1.getAttribute('data-long-pressing'), null, 'dispose() must clear active press highlight');
                assert.equal(container.listenerCount('touchstart'), 0, 'dispose() must remove container touchstart listener');
                assert.equal(winListeners.get('blur')?.size || 0, 0, 'dispose() must remove window blur listener');
                assert.equal(docListeners.get('visibilitychange')?.size || 0, 0, 'dispose() must remove document visibilitychange listener');

                console.log('LONG_PRESS_JS_PASS');
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"Long-press Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("LONG_PRESS_JS_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SpokesBootAndAuthJs_IosLockResumeRecovery_PreventsInfiniteSplashAndUnboundedHangs()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var spokesBootPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-boot.js");
        var spokesAuthPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-auth.js");
        var spokesAppPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-app.js");
        Assert.True(File.Exists(spokesBootPath));
        Assert.True(File.Exists(spokesAuthPath));
        Assert.True(File.Exists(spokesAppPath));

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_ios_resume_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerPath = Path.Combine(tempDir, "run_ios_resume_recovery_test.mjs");
            var script = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const rawBootSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesBootPath)}}, 'utf8');
                const rawAuthSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesAuthPath)}}, 'utf8');
                const appSrc = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(spokesAppPath)}}, 'utf8');

                // Verify spokes-app.js routes focus-socket-closed through handleDetectedDeadSocket
                assert.equal(appSrc.includes("window.spokesResume.handleDetectedDeadSocket('focus-socket-closed')"), true);

                // Compress wait constants for deterministic sub-second Node verification
                const fastBootSrc = rawBootSrc
                    .replace('const NEGOTIATE_TIMEOUT_MS = 5000;', 'const NEGOTIATE_TIMEOUT_MS = 40;')
                    .replace('const INITIAL_WATCHDOG_MS = 9500;', 'const INITIAL_WATCHDOG_MS = 80;')
                    .replace('const RECONNECT_WATCHDOG_MS = 8000;', 'const RECONNECT_WATCHDOG_MS = 70;')
                    .replace('const RECONNECT_HANG_GUARD_MS = 4000;', 'const RECONNECT_HANG_GUARD_MS = 45;')
                    .replace('const RECONNECT_DRAIN_MS = 500;', 'const RECONNECT_DRAIN_MS = 20;')
                    .replace('const SPLASH_DEBOUNCE_MS = 1500;', 'const SPLASH_DEBOUNCE_MS = 20;')
                    .replace('const RELOAD_REFRESH_WAIT_MS = 2500;', 'const RELOAD_REFRESH_WAIT_MS = 40;')
                    .replace('const RELOAD_STALL_GUARD_MS = 6000;', 'const RELOAD_STALL_GUARD_MS = 60;')
                    .replace('const REACHABILITY_TIMEOUT_MS = 2000;', 'const REACHABILITY_TIMEOUT_MS = 40;')
                    .replace('const RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];', 'const RETRY_DELAYS_MS = [25, 25, 25, 25, 25];');

                const fastAuthSrc = rawAuthSrc
                    .replace('const PREFS_TIMEOUT_MS = 3000;', 'const PREFS_TIMEOUT_MS = 35;')
                    .replace('const REFRESH_FETCH_TIMEOUT_MS = 10000;', 'const REFRESH_FETCH_TIMEOUT_MS = 45;');

                function makeHarness(options = {}) {
                    const splashEl = { id: 'startup-splash', style: { display: 'none' } };
                    const statusEl = { id: 'splash-status', textContent: '', style: { color: '#999' } };
                    const actionsEl = { id: 'spokes-splash-watchdog-actions', style: { display: 'none' } };
                    const switchBtnEl = { id: 'spokes-splash-switch-server-btn', style: { display: 'none' } };

                    const docListeners = new Map();
                    const winListeners = new Map();
                    const dispatchedEvents = [];
                    const locationCalls = { reload: 0, replace: [] };
                    let negotiateAborted = false;

                    const context = {
                        console: { log() {}, warn() {}, error() {}, debug() {}, info() {}, trace() {} },
                        Date,
                        URL,
                        URLSearchParams,
                        AbortController,
                        Request: globalThis.Request,
                        Response: globalThis.Response,
                        CustomEvent: class CustomEvent { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
                        Event: class Event { constructor(type) { this.type = type; } },
                        MutationObserver: class { observe() {} disconnect() {} },
                        setTimeout,
                        clearTimeout,
                        requestAnimationFrame: (cb) => cb(),
                        performance: { timeOrigin: Date.now(), getEntriesByType: () => [] },
                        sessionStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                        localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                        history: { replaceState: () => {} },
                        navigator: { onLine: true, userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Capacitor' },
                        location: {
                            protocol: 'https:',
                            host: 'spokes.example.com',
                            origin: 'https://spokes.example.com',
                            href: options.href || 'https://spokes.example.com/chat?client=mobile',
                            pathname: '/chat',
                            search: '?client=mobile',
                            hash: options.hash || '',
                            reload: () => { locationCalls.reload++; },
                            replace: (u) => { locationCalls.replace.push(u); }
                        },
                        document: {
                            readyState: 'loading',
                            visibilityState: 'visible',
                            cookie: '',
                            documentElement: { appendChild: () => {} },
                            body: { children: [], appendChild: () => {} },
                            getElementById: (id) => {
                                if (id === 'startup-splash') return splashEl;
                                if (id === 'splash-status') return statusEl;
                                if (id === 'spokes-splash-watchdog-actions') return actionsEl;
                                if (id === 'spokes-splash-switch-server-btn') return switchBtnEl;
                                return null;
                            },
                            querySelectorAll: () => [],
                            addEventListener: (evt, fn) => {
                                if (!docListeners.has(evt)) docListeners.set(evt, new Set());
                                docListeners.get(evt).add(fn);
                            },
                            removeEventListener: (evt, fn) => docListeners.get(evt)?.delete(fn)
                        },
                        addEventListener: (evt, fn) => {
                            if (!winListeners.has(evt)) winListeners.set(evt, new Set());
                            winListeners.get(evt).add(fn);
                        },
                        removeEventListener: (evt, fn) => winListeners.get(evt)?.delete(fn),
                        dispatchEvent: (ev) => { dispatchedEvents.push(ev.type); },
                        fetch: options.fetch || ((url, init) => {
                            if (String(url).includes('/negotiate')) {
                                return new Promise((resolve, reject) => {
                                    init?.signal?.addEventListener('abort', () => {
                                        negotiateAborted = true;
                                        reject(new Error('Negotiate aborted'));
                                    }, { once: true });
                                });
                            }
                            return Promise.resolve({ ok: true, headers: { get: () => 'application/json' }, json: () => Promise.resolve({}) });
                        }),
                        WebSocket: class FakeWs {
                            constructor() { this.readyState = 1; }
                            addEventListener() {}
                            close() { this.readyState = 3; }
                        },
                        Capacitor: { isNativePlatform: () => true, Plugins: options.plugins || {} },
                        Blazor: {
                            start: () => Promise.resolve(),
                            addEventListener: () => {},
                            reconnect: options.reconnect || (() => Promise.resolve(true))
                        },
                        __spokesBootConfig: {
                            isStaticLoginRoute: false,
                            session: { authenticated: true, expiresAt: Date.now() + 86_400_000 * 80 }
                        }
                    };
                    context.window = context;
                    const bootSrcForRun = typeof options.hangGuardMs === 'number'
                        ? fastBootSrc.replace('const RECONNECT_HANG_GUARD_MS = 45;', `const RECONNECT_HANG_GUARD_MS = ${options.hangGuardMs};`)
                        : fastBootSrc;
                    vm.runInNewContext(bootSrcForRun, context);
                    context.window._blazorCircuitEverOpened = true;
                    context.window._spokesHostViewActive = true;
                    context.window._spokesHostViewExplicitlyActivated = true;
                    return {
                        context,
                        splashEl,
                        statusEl,
                        actionsEl,
                        switchBtnEl,
                        dispatchedEvents,
                        locationCalls,
                        wasNegotiateAborted: () => negotiateAborted,
                        fireVisibility: (state) => {
                            context.document.visibilityState = state;
                            for (const fn of Array.from(docListeners.get('visibilitychange') || [])) fn();
                        }
                    };
                }

                async function waitFor(predicate, timeoutMs, message) {
                    const deadline = Date.now() + timeoutMs;
                    while (Date.now() < deadline) {
                        if (predicate()) return;
                        await new Promise(r => setTimeout(r, 10));
                    }
                    assert.ok(predicate(), message);
                }

                // Scenario 1: Blazor.reconnect() resolves true WITHOUT calling onConnectionUp() AFTER splash appeared -> MUST hide #startup-splash!
                {
                    let resolveReconnect;
                    const h = makeHarness({
                        hangGuardMs: 2000,
                        reconnect: () => new Promise(r => { resolveReconnect = r; })
                    });
                    h.context.window.__spokesBlazorSocket = { readyState: 1 };
                    h.context.window.spokesResume.onConnectionDown('lock-drop');
                    await waitFor(() => h.splashEl.style.display === 'flex', 2000, 'Splash should be shown after debounce');
                    assert.equal(h.splashEl.style.display, 'flex', 'Splash should be shown after debounce');

                    // Resolve true WITHOUT invoking onConnectionUp()
                    resolveReconnect(true);
                    await waitFor(() => h.splashEl.style.display === 'none' && h.context.window._blazorDisconnected === false, 2000, 'outcome===ok without onConnectionUp must still hide #startup-splash');
                    assert.equal(h.context.window._blazorDisconnected, false);
                    assert.equal(h.splashEl.style.display, 'none', 'outcome===ok without onConnectionUp must still hide #startup-splash');
                    assert.ok(h.dispatchedEvents.includes('spokes:blazor-connected'));
                }

                // Scenario 2: Blazor.reconnect() resolves true WITHOUT onConnectionUp() while WebSocket is CLOSED (desynced SignalR state) -> MUST trigger reload recovery!
                {
                    const h = makeHarness({
                        reconnect: () => Promise.resolve(true)
                    });
                    h.context.window.__spokesBlazorSocket = { readyState: 3 };
                    h.context.window.spokesResume.onConnectionDown('focus-socket-closed');
                    await waitFor(() => h.locationCalls.reload + h.locationCalls.replace.length === 1, 2000, 'Desynced closed socket returning true must reload instead of faking connected');
                    assert.equal(h.locationCalls.reload + h.locationCalls.replace.length, 1, 'Desynced closed socket returning true must reload instead of faking connected');
                }

                // Scenario 3: Reconnect hangs on /_blazor/negotiate fetch (never resolves on its own) -> hang guard aborts negotiate, detaches boundedly, AND reconnect watchdog reveals Retry/Switch buttons!
                {
                    let reconnectCalls = 0;
                    const h = makeHarness({
                        fetch: (url, init) => {
                            if (String(url).includes('/negotiate')) {
                                return new Promise((_, reject) => {
                                    init?.signal?.addEventListener('abort', () => reject(new Error('Negotiate aborted')), { once: true });
                                });
                            }
                            // Simulate server temporarily unreachable so retries continue behind splash
                            return Promise.resolve({ ok: false, headers: { get: () => 'text/html' } });
                        },
                        reconnect: () => {
                            reconnectCalls++;
                            // Trigger wrapped /_blazor/negotiate fetch and return a promise that never resolves by itself
                            h.context.window.fetch('/_blazor/negotiate').catch(() => {});
                            return new Promise(() => {});
                        }
                    });
                    h.context.window.spokesResume.onConnectionDown('post-lock-hang');
                    await waitFor(() => reconnectCalls >= 1 && h.splashEl.style.display === 'flex' && h.actionsEl.style.display === 'flex', 2000, 'Post-boot reconnect splash must arm watchdog and reveal Retry/Switch actions');
                    assert.equal(reconnectCalls >= 1, true);
                    assert.equal(h.splashEl.style.display, 'flex');
                    assert.equal(h.actionsEl.style.display, 'flex', 'Post-boot reconnect splash must arm watchdog and reveal Retry/Switch actions');
                    assert.ok(h.statusEl.textContent.length > 0, 'Watchdog must show explanatory status text');

                    // Retry button resets _reloadInProgress and forces reload
                    h.context.window._reloadInProgress = true;
                    h.context.window.spokesRetryConnection();
                    assert.equal(h.context.window._reloadInProgress, false);
                    assert.equal(h.locationCalls.reload, 1);

                    // Stop recurring mock retry timer so Node's event loop can exit cleanly
                    h.context.window._isServerRestarting = true;
                    h.context.window.spokesResume.onConnectionUp();
                }

                // Scenario 4: Foreground preemption of a hung background reconnect promise does NOT deadlock on unbounded await
                {
                    let attempt = 0;
                    const h = makeHarness({
                        hangGuardMs: 2000,
                        reconnect: () => {
                            attempt++;
                            if (attempt === 1) return new Promise(() => {}); // stuck background promise
                            return Promise.resolve(true);
                        }
                    });
                    h.context.window.__spokesBlazorSocket = { readyState: 1 };
                    h.context.window.spokesResume.onConnectionDown('background-suspend');
                    h.fireVisibility('hidden');
                    await new Promise(r => setTimeout(r, 15));
                    h.fireVisibility('visible');
                    await waitFor(() => attempt === 2 && h.context.window._blazorDisconnected === false && h.splashEl.style.display === 'none', 2000, 'Preemption must detach hung background reconnectPromise and start clean foreground attempt');
                    assert.equal(attempt, 2, 'Preemption must detach hung background reconnectPromise and start clean foreground attempt');
                    assert.equal(h.context.window._blazorDisconnected, false);
                    assert.equal(h.splashEl.style.display, 'none');
                }

                // Scenario 5: reloadNow() with hung _refreshPromise + #hash URL + cancelled navigation -> bounded wait, uses location.reload(), and releases _reloadInProgress latch after stall!
                {
                    const h = makeHarness({
                        href: 'https://spokes.example.com/chat?client=mobile#msg-42',
                        hash: '#msg-42',
                        reconnect: () => Promise.resolve(false) // rejected -> reloadNow()
                    });
                    h.context.window.spokesAuth = { _refreshPromise: new Promise(() => {}) }; // hung refresh
                    h.context.window.spokesResume.onConnectionDown('circuit-expired');
                    await waitFor(() => h.locationCalls.reload === 1 && h.context.window._reloadInProgress === false && h.actionsEl.style.display === 'flex', 2000, 'Stalled reload must release _reloadInProgress latch and reveal Retry/Switch recovery buttons');
                    assert.equal(h.locationCalls.reload, 1, 'Same-URL with #hash must call location.reload() instead of no-op location.replace()');
                    assert.equal(h.locationCalls.replace.length, 0);
                    assert.equal(h.context.window._reloadInProgress, false, 'Stalled/cancelled navigation must release _reloadInProgress latch');
                    assert.equal(h.actionsEl.style.display, 'flex', 'Stalled reload must reveal Retry/Switch recovery buttons');
                }

                // Scenario 6: spokes-auth.js refreshSessionAsync() bounds hung fetch() and Preferences.get() and clears _refreshPromise
                {
                    let refreshFetchAborted = false;
                    const authCtx = {
                        console: { log() {}, warn() {}, error() {} },
                        Date,
                        URL,
                        AbortController,
                        setTimeout,
                        clearTimeout,
                        localStorage: { getItem: () => 'dev-1', setItem: () => {} },
                        location: { protocol: 'https:', origin: 'https://spokes.example.com', href: 'https://spokes.example.com/chat?client=mobile', hostname: 'spokes.example.com' },
                        Capacitor: {
                            isNativePlatform: () => true,
                            Plugins: {
                                Preferences: {
                                    get: ({ key }) => key.includes('spokes_refresh') ? Promise.resolve({ value: 'rt-1' }) : Promise.resolve({ value: 'dev-1' }),
                                    set: () => Promise.resolve(),
                                    remove: () => Promise.resolve()
                                }
                            }
                        },
                        fetch: (url, init) => new Promise((_, reject) => {
                            init?.signal?.addEventListener('abort', () => {
                                refreshFetchAborted = true;
                                reject(new Error('aborted'));
                            }, { once: true });
                        })
                    };
                    authCtx.window = authCtx;
                    vm.runInNewContext(fastAuthSrc, authCtx);
                    const ok = await authCtx.window.spokesAuth.refreshSessionAsync();
                    assert.equal(ok, false);
                    assert.equal(refreshFetchAborted, true, 'Hung /spokesapi/auth/refresh fetch must be aborted by AbortController timeout');
                    assert.equal(authCtx.window.spokesAuth._refreshPromise, null, '_refreshPromise must clear after timeout');

                    // Also verify hung Preferences.get() does not wedge _refreshPromise
                    authCtx.Capacitor.Plugins.Preferences.get = () => new Promise(() => {});
                    const ok2 = await authCtx.window.spokesAuth.refreshSessionAsync();
                    assert.equal(ok2, false);
                    assert.equal(authCtx.window.spokesAuth._refreshPromise, null);
                }

                // Scenario 7: spokes-auth.js _getOriginPrefAsync() copies legacy keys to ::origin without deleting legacy keys
                {
                    const store = new Map([
                        ['spokes_refresh_spokes.example.com', 'legacy-rt-1'],
                        ['spokes_device_id', 'shared-dev-1']
                    ]);
                    const removedKeys = [];
                    const authCtx = {
                        console: { log() {}, warn() {}, error() {} },
                        URL,
                        location: { protocol: 'https:', origin: 'https://spokes.example.com', href: 'https://spokes.example.com/chat?client=mobile', hostname: 'spokes.example.com' },
                        Capacitor: {
                            isNativePlatform: () => true,
                            Plugins: {
                                Preferences: {
                                    get: ({ key }) => Promise.resolve({ value: store.has(key) ? store.get(key) : null }),
                                    set: ({ key, value }) => { store.set(key, value); return Promise.resolve(); },
                                    remove: ({ key }) => { removedKeys.push(key); store.delete(key); return Promise.resolve(); }
                                }
                            }
                        }
                    };
                    authCtx.window = authCtx;
                    vm.runInNewContext(fastAuthSrc, authCtx);

                    const headers = await authCtx.window.spokesAuth.getHeaders();
                    assert.equal(headers['Authorization'], 'Bearer legacy-rt-1');
                    assert.equal(headers['X-Device-Id'], 'shared-dev-1');
                    assert.equal(store.get('spokes_refresh::https://spokes.example.com'), 'legacy-rt-1');
                    assert.equal(store.get('spokes_device_id::https://spokes.example.com'), 'shared-dev-1');
                    assert.equal(store.get('spokes_refresh_spokes.example.com'), 'legacy-rt-1', 'Legacy host refresh key must remain intact for pre-OriginPreferencesPlugin mobile shells');
                    assert.equal(store.get('spokes_device_id'), 'shared-dev-1', 'Shared legacy device ID must remain intact for pre-OriginPreferencesPlugin multi-server mobile shells');
                    assert.deepEqual(removedKeys, [], '_getOriginPrefAsync must copy without deleting legacy keys');
                }

                console.log('IOS_LOCK_RESUME_RECOVERY_PASS');
                process.exit(0);
                """;

            File.WriteAllText(runnerPath, script);

            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("iOS lock/resume recovery Node test timed out after 15s.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            Assert.True(proc.ExitCode == 0, $"iOS lock/resume recovery Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("IOS_LOCK_RESUME_RECOVERY_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void IosNativeAndMobileBootstrap_LifecycleRecoveryContracts_PreventBlankOrStuckScreens()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ServerDirectory, "..", ".."));
        var iosAppDelegatePath = Path.Combine(repoRoot, "Spokes_Mobile", "ios", "App", "App", "AppDelegate.swift");
        var middlewarePath = Path.Combine(ServerDirectory, "Core", "Extensions", "SpokesMiddlewareExtensions.cs");
        var appRazorPath = Path.Combine(ServerDirectory, "Components", "App.razor");

        Assert.True(File.Exists(iosAppDelegatePath), $"Missing {iosAppDelegatePath}");
        Assert.True(File.Exists(middlewarePath), $"Missing {middlewarePath}");
        Assert.True(File.Exists(appRazorPath), $"Missing {appRazorPath}");

        var iosSrc = File.ReadAllText(iosAppDelegatePath);
        var middlewareSrc = File.ReadAllText(middlewarePath);
        var appRazorSrc = File.ReadAllText(appRazorPath);

        // 1. iOS SpokesNavigationDelegate must enforce canonical-origin containment and intercept webViewWebContentProcessDidTerminate without forwarding blind Capacitor reload()
        int navDelegateIdx = iosSrc.IndexOf("class SpokesNavigationDelegate", StringComparison.Ordinal);
        Assert.True(navDelegateIdx > 0);
        var navDelegateSlice = iosSrc.Substring(navDelegateIdx);
        Assert.Contains("func webViewWebContentProcessDidTerminate(_ webView: WKWebView)", navDelegateSlice);
        Assert.Contains("onContentProcessTerminated()", navDelegateSlice);
        Assert.DoesNotContain("primaryDelegate?.webViewWebContentProcessDidTerminate", navDelegateSlice);
        Assert.Contains("decidePolicyFor navigationAction: WKNavigationAction", navDelegateSlice);
        Assert.Contains("SpokesNavigationPolicy.decide(", navDelegateSlice);
        Assert.Contains("CapacitorBridge.httpInterceptorStartIdentifier", navDelegateSlice);
        Assert.Contains("canOpenExternalUrl()", navDelegateSlice);

        // 1b. iOS SpokesViewController must pass clean origin-only serverURL to InstanceDescriptor (never path/query) and load currentInitialUrl in viewDidLoad
        Assert.Contains("enum SpokesNavigationPolicy", iosSrc);
        Assert.Contains("SpokesOriginSecurity.canonicalizeExplicitHttpOrigin(url.absoluteString)", iosSrc);
        int instanceDescIdx = iosSrc.IndexOf("override func instanceDescriptor() -> InstanceDescriptor", StringComparison.Ordinal);
        Assert.True(instanceDescIdx > 0);
        var instanceDescSlice = iosSrc.Substring(instanceDescIdx, Math.Min(600, iosSrc.Length - instanceDescIdx));
        Assert.Contains("descriptor.serverURL = origin", instanceDescSlice);
        Assert.DoesNotContain("descriptor.serverURL = initialUrl", instanceDescSlice);
        Assert.Contains("wv.stopLoading()", iosSrc);
        Assert.Contains("window.addEventListener('pageshow'", iosSrc);

        // 1c. iOS SsoAuthPlugin must cancel stale sessions with generation guarding and handle start() failure without hanging
        var iosSsoAuthPath = Path.GetFullPath(Path.Combine(ServerDirectory, "..", "..", "Spokes_Mobile", "ios", "App", "App", "SsoAuthPlugin.swift"));
        Assert.True(File.Exists(iosSsoAuthPath), $"Missing {iosSsoAuthPath}");
        var iosSsoAuthSrc = File.ReadAllText(iosSsoAuthPath);
        Assert.Contains("authSessionGeneration", iosSsoAuthSrc);
        Assert.Contains("existing.cancel()", iosSsoAuthSrc);
        Assert.Contains("if !session.start()", iosSsoAuthSrc);

        // 1d. Superseded pre-PKCE MobileLoginSuccess.razor must stay deleted
        Assert.False(File.Exists(Path.Combine(ServerDirectory, "Components", "Pages", "Public", "MobileLoginSuccess.razor")));

        // 2. iOS SpokesWebViewLifecycleState & SpokesViewController foreground health recovery contracts
        Assert.Contains("struct SpokesWebViewLifecycleState", iosSrc);
        Assert.Contains("verifyAndSyncForegroundSession(", iosSrc);
        Assert.Contains("WKError.webContentProcessTerminated.rawValue", iosSrc);
        Assert.Contains("EMPTY_DOM", iosSrc);
        Assert.Contains("CustomEvent('spokes:host-view-active')", iosSrc);
        Assert.Contains("CustomEvent('spokes:host-view-active-changed'", iosSrc);

        // 3. Mobile bootstrap splash interceptor renders spinner and bounds Preferences / NotificationCrypto calls
        Assert.Contains("spokes-boot-spinner", middlewareSrc);
        Assert.Contains("withTimeout(prefs.get(", middlewareSrc);
        Assert.Contains("withTimeout(window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId()", middlewareSrc);

        // 4. App.razor retry button routes through spokesRetryConnection()
        Assert.Contains("window.spokesRetryConnection()", appRazorSrc);
    }

    [Fact]
    public async Task NotificationRouting_ZombieSocketAndReconnectWithoutOnConnectionUp_DrainsPendingRoute()
    {
        if (!IsNodeAvailable())
        {
            return;
        }

        var capacitorInitPath = Path.Combine(ServerDirectory, "wwwroot", "js", "capacitor-init.js");
        var spokesBootPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-boot.js");
        Assert.True(File.Exists(capacitorInitPath));
        Assert.True(File.Exists(spokesBootPath));

        var tempDir = Path.Combine(Path.GetTempPath(), "spokes_notif_route_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var runnerPath = Path.Combine(tempDir, "runner.js");
            var runnerScript = """
const fs = require('fs');
const assert = require('assert');
const capacitorInitSrc = fs.readFileSync(process.argv[2], 'utf8');
const spokesBootSrc = fs.readFileSync(process.argv[3], 'utf8');

function createBaseWindow(href) {
    const parsed = new URL(href);
    const listeners = new Map();
    const docListeners = new Map();
    const w = {
        location: {
            href: parsed.href,
            origin: parsed.origin,
            protocol: parsed.protocol,
            pathname: parsed.pathname,
            search: parsed.search,
            hash: parsed.hash,
            reload: () => {},
            replace: () => {}
        },
        navigator: { userAgent: 'Mozilla/5.0 Spokes Capacitor', onLine: true },
        Capacitor: { isNativePlatform: () => true, Plugins: {} },
        SpokesNative: {},
        localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
        sessionStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
        history: { length: 1, replaceState: () => {} },
        addEventListener: (ev, fn) => {
            if (!listeners.has(ev)) listeners.set(ev, []);
            listeners.get(ev).push(fn);
        },
        dispatchEvent: (ev) => {
            const list = listeners.get(ev.type) || [];
            for (const fn of list) fn(ev);
        },
        CustomEvent: function(type, init) { this.type = type; this.detail = init && init.detail; },
        URL,
        URLSearchParams,
        console,
        setTimeout,
        clearTimeout
    };
    const doc = {
        visibilityState: 'visible',
        readyState: 'complete',
        documentElement: {},
        body: { innerHTML: '', children: [] },
        getElementById: () => null,
        querySelectorAll: () => [],
        addEventListener: (ev, fn) => {
            if (!docListeners.has(ev)) docListeners.set(ev, []);
            docListeners.get(ev).push(fn);
        }
    };
    return { w, doc };
}

(async () => {
    const { w, doc } = createBaseWindow('https://chat.example.com/chat/old?client=mobile');
    w.__spokesSession = { authenticated: true, expiresAt: Date.now() + 3600000 };
    w.spokesAuth = { refreshSessionAsync: async () => true };
    global.window = w;
    global.document = doc;
    Object.defineProperty(global, 'navigator', { value: w.navigator, configurable: true, writable: true });
    global.localStorage = w.localStorage;
    global.sessionStorage = w.sessionStorage;
    global.location = w.location;
    global.CustomEvent = w.CustomEvent;
    global.performance = { timeOrigin: Date.now(), getEntriesByType: () => [] };
    global.MutationObserver = class { observe() {} disconnect() {} };
    global.WebSocket = class { constructor() { this.readyState = 1; } addEventListener() {} close() {} };
    global.WebSocket.OPEN = 1;
    global.fetch = async () => ({ ok: true, headers: { get: () => 'application/json' } });

    eval(capacitorInitSrc);

    // 1. Zombie socket: NavigateTo rejects after circuit already reconnected -> retries once and clears pending route
    const navCalls = [];
    let failFirst = true;
    w._blazorDisconnected = false;
    w.spokesDotNetRef = {
        invokeMethodAsync: async (method, route) => {
            navCalls.push({ method, route });
            if (failFirst) {
                failFirst = false;
                // Ensure pendingNotificationRoute was already staged before rejection
                assert.strictEqual(w.pendingNotificationRoute, '/chat/chan_target?client=mobile');
                throw new Error('zombie socket closed');
            }
        }
    };

    const ok = await w.spokesNavigateFromNative('/chat/chan_target');
    assert.strictEqual(ok, true);
    assert.strictEqual(navCalls.length, 2, 'Expected single immediate retry after reconnect');
    assert.strictEqual(navCalls[1].route, '/chat/chan_target?client=mobile');
    assert.strictEqual(w.pendingNotificationRoute, null);
    assert.strictEqual(w.spokesPendingNav, null);

    // 2. If completeCircuitConnected already consumed pendingNotificationRoute while a zombie call was in flight,
    //    the stale rejection must NOT re-queue the already-consumed route.
    let rejectStale;
    w._blazorDisconnected = false;
    w.spokesDotNetRef = {
        invokeMethodAsync: () => new Promise((_, reject) => { rejectStale = reject; })
    };
    const stalePromise = w.spokesNavigateFromNative('/chat/chan_consumed');
    assert.strictEqual(w.pendingNotificationRoute, '/chat/chan_consumed?client=mobile');
    // Simulate completeCircuitConnected consuming the pending route
    w.pendingNotificationRoute = null;
    rejectStale(new Error('socket replaced'));
    const staleResult = await stalePromise;
    assert.strictEqual(staleResult, false);
    assert.strictEqual(w.pendingNotificationRoute, null);

    // 3. spokes-boot.js completeCircuitConnected drains pendingNotificationRoute even when Blazor.reconnect()
    //    resolves true without invoking onConnectionUp().
    const drained = [];
    w.spokesNavigateFromNative = async (route) => { drained.push(route); return true; };
    global.Blazor = {
        reconnect: async () => true,
        start: async () => {},
        addEventListener: () => {}
    };
    eval(spokesBootSrc);
    w._spokesHostViewActive = true;
    w._spokesHostViewExplicitlyActivated = true;
    w._blazorDisconnected = true;
    w.pendingNotificationRoute = '/chat/chan_reconnected?client=mobile';
    await w.spokesResume.onHostViewActivated();
    await new Promise(r => setTimeout(r, 20));
    assert.deepStrictEqual(drained, ['/chat/chan_reconnected?client=mobile']);
    assert.strictEqual(w.pendingNotificationRoute, null);

    console.log('NOTIF_ROUTING_RECONNECT_PASS');
})().catch(err => {
    console.error(err);
    process.exit(1);
});
""";
            File.WriteAllText(runnerPath, runnerScript);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{runnerPath}\" \"{capacitorInitPath}\" \"{spokesBootPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("Notification routing reconnect Node test timed out after 15s.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.True(proc.ExitCode == 0, $"Notification routing reconnect Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("NOTIF_ROUTING_RECONNECT_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void IosNativeNotificationRouting_DeferredForegroundAndRecoveryContracts()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ServerDirectory, "..", ".."));
        var iosAppDelegatePath = Path.Combine(repoRoot, "Spokes_Mobile", "ios", "App", "App", "AppDelegate.swift");
        var iosSceneDelegatePath = Path.Combine(repoRoot, "Spokes_Mobile", "ios", "App", "App", "SceneDelegate.swift");
        Assert.True(File.Exists(iosAppDelegatePath), $"Missing {iosAppDelegatePath}");
        Assert.True(File.Exists(iosSceneDelegatePath), $"Missing {iosSceneDelegatePath}");

        var appDelegateSrc = File.ReadAllText(iosAppDelegatePath);
        var sceneDelegateSrc = File.ReadAllText(iosSceneDelegatePath);

        // Cold-start notification response must be consumed in SceneDelegate before viewDidLoad
        Assert.Contains("connectionOptions.notificationResponse", sceneDelegateSrc);
        Assert.Contains("ServerHostViewController.handleNotificationResponse(", sceneDelegateSrc);

        // Background tap must queue pendingNavigationTarget and flush on setAppForeground(true)
        Assert.Contains("private var pendingNavigationTarget:", appDelegateSrc);
        Assert.Contains("let pending = self.pendingNavigationTarget", appDelegateSrc);

        // Jetsam recovery must prioritize explicitTargetUrl over stale currentWebViewUrl
        Assert.Contains("explicitTargetUrl: String? = nil", appDelegateSrc);
        Assert.Contains("explicitTargetUrl: explicitTargetUrl", appDelegateSrc);
    }

    [Fact]
    public async Task SpokesSwipePager_OsGestureGuardTrackingHeightAndRapidNavContracts()
    {
        var pagerJsPath = Path.Combine(ServerDirectory, "Components", "Shared", "SpokesSwipePager.razor.js");
        Assert.True(File.Exists(pagerJsPath), $"Missing {pagerJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"Spokes_SwipePagerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "swipe-pager-test.mjs");
            var pagerModuleUrl = new Uri(Path.GetFullPath(pagerJsPath)).AbsoluteUri;
            var testScript = $$"""
                import assert from 'node:assert/strict';
                import {
                    EDGE_GUARD_DEFAULT_PX,
                    isInEdgeGuardZone,
                    resolveAxisLock,
                    shouldCommitSwipe,
                    computeInterpolatedHeight,
                    computeSettleDurationMs,
                    hasScrollableHorizontalAncestor,
                    createSwipePager
                } from '{{pagerModuleUrl}}';

                // ── 1) Pure gesture policy contracts ──
                assert.equal(EDGE_GUARD_DEFAULT_PX, 32);
                assert.equal(isInEdgeGuardZone(0, 390, 32), true);
                assert.equal(isInEdgeGuardZone(31, 390, 32), true);
                assert.equal(isInEdgeGuardZone(32, 390, 32), false);
                assert.equal(isInEdgeGuardZone(358, 390, 32), false);
                assert.equal(isInEdgeGuardZone(359, 390, 32), true);

                assert.equal(resolveAxisLock(4, 3), 'undecided');
                assert.equal(resolveAxisLock(20, 5), 'horizontal');
                assert.equal(resolveAxisLock(12, 11), 'vertical'); // fails 1.25x horizontal bias
                assert.equal(resolveAxisLock(3, 18), 'vertical');

                assert.equal(shouldCommitSwipe(-110, 0, 400), 1);  // > 25% left -> next (+1)
                assert.equal(shouldCommitSwipe(110, 0, 400), -1);  // > 25% right -> prev (-1)
                assert.equal(shouldCommitSwipe(-60, 0, 400), 0);   // < 25% slow -> snap back
                assert.equal(shouldCommitSwipe(-35, -0.5, 400), 1); // fast left flick -> next (+1)
                assert.equal(shouldCommitSwipe(-35, 0.5, 400), 0);  // mismatched velocity direction -> snap back
                assert.equal(shouldCommitSwipe(-15, -0.8, 400), 0); // below min flick distance -> snap back

                assert.equal(computeInterpolatedHeight(400, 480, 0.5), 440);
                assert.equal(computeInterpolatedHeight(400, 480, 1.5), 480);
                assert.equal(computeSettleDurationMs(200, 0, true), 0);
                assert.ok(computeSettleDurationMs(200, 0, false) >= 110 && computeSettleDurationMs(200, 0, false) <= 210);

                // ── 2) Simulated DOM + Pointer/Touch/MutationObserver harness ──
                let activeMutationCallback = null;
                globalThis.MutationObserver = class FakeMutationObserver {
                    constructor(cb) { this.cb = cb; }
                    observe() { activeMutationCallback = this.cb; }
                    disconnect() { if (activeMutationCallback === this.cb) activeMutationCallback = null; }
                };

                function createMockEl(opts = {}) {
                    const listeners = new Map();
                    const classes = new Set();
                    const captured = new Set();
                    const el = {
                        nodeType: 1,
                        clientWidth: opts.clientWidth ?? 400,
                        scrollWidth: opts.scrollWidth ?? (opts.clientWidth ?? 400),
                        scrollLeft: opts.scrollLeft ?? 0,
                        offsetHeight: opts.offsetHeight ?? 400,
                        scrollHeight: opts.offsetHeight ?? 400,
                        parentElement: opts.parentElement ?? null,
                        children: opts.children ?? [],
                        dataset: { ...(opts.dataset || {}) },
                        style: { transition: '', transform: '', height: '' },
                        overflowX: opts.overflowX ?? 'visible',
                        classList: {
                            add: (c) => classes.add(c),
                            remove: (c) => classes.delete(c),
                            contains: (c) => classes.has(c)
                        },
                        getBoundingClientRect: () => ({ width: el.clientWidth, height: el.offsetHeight }),
                        setPointerCapture: (id) => captured.add(id),
                        releasePointerCapture: (id) => captured.delete(id),
                        hasPointerCapture: (id) => captured.has(id),
                        addEventListener: (type, fn, captureOrOpts) => {
                            const capture = typeof captureOrOpts === 'boolean' ? captureOrOpts : Boolean(captureOrOpts?.capture);
                            const key = `${type}:${capture}`;
                            if (!listeners.has(key)) listeners.set(key, new Set());
                            listeners.get(key).add(fn);
                        },
                        removeEventListener: (type, fn, captureOrOpts) => {
                            const capture = typeof captureOrOpts === 'boolean' ? captureOrOpts : Boolean(captureOrOpts?.capture);
                            listeners.get(`${type}:${capture}`)?.delete(fn);
                        },
                        dispatch: (type, ev, capture = false) => {
                            const set = listeners.get(`${type}:${capture}`);
                            if (!set) return;
                            for (const fn of Array.from(set)) fn(ev);
                        }
                    };
                    return el;
                }

                globalThis.window = {
                    innerWidth: 390,
                    matchMedia: () => ({ matches: false }),
                    getComputedStyle: (el) => ({ overflowX: el?.overflowX || 'visible' }),
                    addEventListener: () => {},
                    removeEventListener: () => {}
                };
                globalThis.document = {
                    documentElement: { clientWidth: 390 },
                    addEventListener: () => {},
                    removeEventListener: () => {}
                };

                // Nested horizontal scroller check
                {
                    const viewport = createMockEl({ clientWidth: 390 });
                    const innerScroller = createMockEl({ clientWidth: 300, scrollWidth: 600, scrollLeft: 100, overflowX: 'auto', parentElement: viewport });
                    const chip = createMockEl({ clientWidth: 80, parentElement: innerScroller });
                    assert.equal(hasScrollableHorizontalAncestor(chip, viewport, -20), true);
                    assert.equal(hasScrollableHorizontalAncestor(chip, viewport, 20), true);
                    innerScroller.scrollLeft = 0;
                    assert.equal(hasScrollableHorizontalAncestor(chip, viewport, 20), false);
                    assert.equal(hasScrollableHorizontalAncestor(chip, viewport, -20), true);
                }

                function buildPagerHarness() {
                    const prevPane = createMockEl({ offsetHeight: 360 });
                    const currPane = createMockEl({ offsetHeight: 400 });
                    const nextPane = createMockEl({ offsetHeight: 480 });
                    const track = createMockEl({ clientWidth: 400, children: [prevPane, currPane, nextPane] });
                    const viewport = createMockEl({ clientWidth: 400, dataset: { pageKey: '2026-05-01' }, children: [track] });
                    const commits = [];
                    const dotNetRef = {
                        invokeMethodAsync: (method, delta) => {
                            commits.push({ method, delta });
                            return Promise.resolve();
                        }
                    };
                    const pager = createSwipePager(viewport, track, dotNetRef, { edgeGuardPx: 32, animateHeight: true });
                    return { pager, viewport, track, commits };
                }

                // A) Edge dead-zone touch (left & right OS back edges) must be completely ignored
                {
                    const { pager, viewport, track, commits } = buildPagerHarness();
                    let prevented = false;
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 14, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 200, clientY: 202 });
                    viewport.dispatch('touchmove', { touches: [{ clientX: 200 }], cancelable: true, preventDefault: () => { prevented = true; } });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'touch', clientX: 200, clientY: 202 });
                    assert.equal(prevented, false, 'Edge touch must never call preventDefault');
                    assert.equal(track.style.transform, '', 'Edge touch must never move track');
                    assert.equal(commits.length, 0);

                    // Right edge on 390px viewport
                    viewport.dispatch('pointerdown', { pointerId: 2, pointerType: 'touch', clientX: 375, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 2, pointerType: 'touch', clientX: 150, clientY: 200 });
                    viewport.dispatch('pointerup', { pointerId: 2, pointerType: 'touch', clientX: 150, clientY: 200 });
                    assert.equal(commits.length, 0);
                    pager.destroy();
                }

                // B) Desktop mouse pointer must be completely ignored
                {
                    const { pager, viewport, track, commits } = buildPagerHarness();
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'mouse', clientX: 200, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'mouse', clientX: 40, clientY: 200 });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'mouse', clientX: 40, clientY: 200 });
                    assert.equal(track.style.transform, '');
                    assert.equal(commits.length, 0);
                    pager.destroy();
                }

                // C) Vertical scroll priority locks vertical and refuses later horizontal drift
                {
                    const { pager, viewport, track, commits } = buildPagerHarness();
                    let prevented = false;
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 200, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 204, clientY: 225 });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 60, clientY: 230 });
                    viewport.dispatch('touchmove', { touches: [{ clientX: 60 }], cancelable: true, preventDefault: () => { prevented = true; } });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'touch', clientX: 60, clientY: 230 });
                    assert.equal(prevented, false);
                    assert.equal(track.style.transform, '');
                    assert.equal(commits.length, 0);
                    pager.destroy();
                }

                // D) 1:1 finger tracking + height interpolation + commit + MutationObserver pre-paint reset + click suppression
                {
                    const { pager, viewport, track, commits } = buildPagerHarness();
                    let touchPrevented = false;
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 260, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 160, clientY: 202 });
                    viewport.dispatch('touchmove', { touches: [{ clientX: 160 }], cancelable: true, preventDefault: () => { touchPrevented = true; } });
                    assert.equal(touchPrevented, true, 'Locked horizontal drag must prevent vertical scroll');
                    assert.equal(track.style.transform, 'translate3d(-100px, 0, 0)');
                    assert.equal(viewport.style.height, '420.0px', '25% drag from 400px to 480px must interpolate height to 420px');

                    // Release past 25% threshold (-150px on 400px pane) -> commits +1
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 110, clientY: 202 });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'touch', clientX: 110, clientY: 202 });

                    // Immediate trailing synthetic click must be suppressed
                    let clickPrevented = false;
                    let clickStopped = false;
                    viewport.dispatch('click', {
                        preventDefault: () => { clickPrevented = true; },
                        stopPropagation: () => { clickStopped = true; }
                    }, true);
                    assert.equal(clickPrevented, true, 'Post-swipe synthetic click must be suppressed');
                    assert.equal(clickStopped, true);

                    await new Promise(r => setTimeout(r, 260));
                    assert.equal(commits.length, 1);
                    assert.deepEqual(commits[0], { method: 'HandleSwipeCommit', delta: 1 });
                    assert.equal(pager.awaitingServerCommit, true);

                    // Blazor patches data-page-key -> MutationObserver snaps back to center before paint
                    viewport.dataset.pageKey = '2026-06-01';
                    activeMutationCallback();
                    assert.equal(pager.awaitingServerCommit, false);
                    assert.equal(track.style.transform, '');
                    assert.equal(viewport.style.height, '');
                    pager.destroy();
                }

                // E) Multi-touch & pointercancel must abort cleanly without navigating
                {
                    const { pager, viewport, commits } = buildPagerHarness();
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 250, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 120, clientY: 200 });
                    // Second finger lands (pinch/OS gesture) -> aborts
                    viewport.dispatch('pointerdown', { pointerId: 2, pointerType: 'touch', clientX: 200, clientY: 250, target: viewport });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'touch', clientX: 120, clientY: 200 });
                    viewport.dispatch('pointerup', { pointerId: 2, pointerType: 'touch', clientX: 200, clientY: 250 });
                    await new Promise(r => setTimeout(r, 240));
                    assert.equal(commits.length, 0);

                    // OS pointercancel mid-drag
                    viewport.dispatch('pointerdown', { pointerId: 3, pointerType: 'touch', clientX: 250, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 3, pointerType: 'touch', clientX: 100, clientY: 200 });
                    viewport.dispatch('pointercancel', { pointerId: 3, pointerType: 'touch' });
                    await new Promise(r => setTimeout(r, 240));
                    assert.equal(commits.length, 0);
                    pager.destroy();
                }

                // F) Normal tap (< slop) must NOT suppress click
                {
                    const { pager, viewport } = buildPagerHarness();
                    viewport.dispatch('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 200, clientY: 200, target: viewport });
                    viewport.dispatch('pointermove', { pointerId: 1, pointerType: 'touch', clientX: 203, clientY: 202 });
                    viewport.dispatch('pointerup', { pointerId: 1, pointerType: 'touch', clientX: 203, clientY: 202 });
                    let clickPrevented = false;
                    viewport.dispatch('click', {
                        preventDefault: () => { clickPrevented = true; },
                        stopPropagation: () => {}
                    }, true);
                    assert.equal(clickPrevented, false, 'Normal tap must never be suppressed');
                    pager.destroy();
                }

                // G) Fast programmatic chevron slide + rapid click immediate snap
                {
                    const { pager, viewport, track } = buildPagerHarness();
                    const firstNav = pager.animateNavigate(1);
                    assert.equal(pager.isAnimating, true);
                    assert.equal(track.style.transform, 'translate3d(-400px, 0, 0)');

                    // Rapid second click mid-animation snaps immediately and resolves both cleanly
                    const secondNav = await pager.animateNavigate(1);
                    const firstResult = await firstNav;
                    assert.equal(secondNav, false);
                    assert.equal(firstResult, false);
                    assert.equal(pager.isAnimating, false);
                    assert.equal(track.style.transform, '');

                    // Calm single click completes fast slide and enters awaitingServerCommit
                    const calmResult = await pager.animateNavigate(-1);
                    assert.equal(calmResult, true);
                    assert.equal(pager.awaitingServerCommit, true);
                    assert.equal(track.style.transform, 'translate3d(400px, 0, 0)');
                    viewport.dataset.pageKey = '2026-04-01';
                    activeMutationCallback();
                    assert.equal(pager.awaitingServerCommit, false);
                    assert.equal(track.style.transform, '');
                    pager.destroy();
                }

                console.log('SWIPE_PAGER_TEST_PASS');
                """;

            await File.WriteAllTextAsync(scriptPath, testScript);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("SpokesSwipePager Node test timed out after 15s.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.True(proc.ExitCode == 0, $"SpokesSwipePager Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("SWIPE_PAGER_TEST_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task WireProfiler_AppWideTriggersReconnectAndClockCache_AndBootRenderer1ErrorHygiene()
    {
        var profilerPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-chat-profiler.js");
        var spokesBootPath = Path.Combine(ServerDirectory, "wwwroot", "js", "spokes-boot.js");
        Assert.True(File.Exists(profilerPath), $"Missing {profilerPath}");
        Assert.True(File.Exists(spokesBootPath), $"Missing {spokesBootPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_wire_profiler_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "test_wire_profiler.mjs");
            var profilerEsc = profilerPath.Replace("\\", "\\\\");
            var bootEsc = spokesBootPath.Replace("\\", "\\\\");

            var testScript = $$"""
                import fs from 'node:fs';
                import vm from 'node:vm';
                import assert from 'node:assert/strict';

                const profilerSrc = fs.readFileSync('{{profilerEsc}}', 'utf8');

                let nowMs = 10000;
                const logs = [];
                const warns = [];
                const docListeners = new Map();
                const winListeners = new Map();
                let intervalCbs = [];
                let mutationCb = null;
                let drawerOpenState = false;
                let dialogCountState = 0;

                const makeTarget = (matchesMap, props = {}) => ({
                    tagName: props.tagName || 'BUTTON',
                    id: props.id || '',
                    className: props.className || '',
                    getAttribute: (name) => (props.attrs && props.attrs[name]) || null,
                    closest: (sel) => {
                        for (const [key, val] of Object.entries(matchesMap)) {
                            if (sel.includes(key)) return val;
                        }
                        return null;
                    }
                });

                const dotNetCalls = [];
                let tidCounter = 0;
                const dotNetRef = {
                    invokeMethodAsync: async (method, ...args) => {
                        dotNetCalls.push({ method, args });
                        if (method === 'SetChatProfilerEnabled') return null;
                        if (method === 'DiagBeginTransition') return ++tidCounter;
                        if (method === 'DiagClockPing') return nowMs + 20;
                        if (method === 'DiagDrain') {
                            return {
                                transitionId: args[0],
                                transitionName: '',
                                entries: [{ name: `Server: Transition #${args[0]} begun`, timestampMs: nowMs + 15 }],
                                nextCursor: 1,
                                openSpans: 0,
                                lastEntryAgeMs: 600,
                                serverNowMs: nowMs + 20
                            };
                        }
                        return null;
                    }
                };

                const createSocket = () => {
                    const sent = [];
                    return {
                        extensions: 'permessage-deflate',
                        sent,
                        send(data) { sent.push(data); }
                    };
                };
                let ws1 = createSocket();

                const profWin = {
                    console: {
                        log: (...a) => logs.push(a.join(' ')),
                        warn: (...a) => warns.push(a.join(' ')),
                        error: () => {}
                    },
                    innerWidth: 412,
                    innerHeight: 915,
                    SpokesBridge: { platform: 'android' },
                    performance: { now: () => nowMs, timeOrigin: 0 },
                    MutationObserver: class {
                        constructor(cb) { mutationCb = cb; }
                        observe() {}
                        disconnect() {}
                    },
                    requestAnimationFrame: (cb) => cb(),
                    setInterval: (cb, ms) => { intervalCbs.push({ cb, ms }); return intervalCbs.length; },
                    clearInterval: () => {},
                    addEventListener: (ev, cb) => {
                        if (!winListeners.has(ev)) winListeners.set(ev, []);
                        winListeners.get(ev).push(cb);
                    },
                    removeEventListener: (ev, cb) => {
                        const arr = winListeners.get(ev) || [];
                        winListeners.set(ev, arr.filter(x => x !== cb));
                    },
                    document: {
                        body: {},
                        querySelector: () => null,
                        querySelectorAll: (sel) => sel.includes('mud-dialog') ? new Array(dialogCountState) : [],
                        getElementById: (id) => {
                            if (id === 'nav-drawer') {
                                return {
                                    classList: {
                                        contains: (cls) => drawerOpenState && (cls === 'is-nav-drawer-open' || cls === 'mud-drawer--open')
                                    }
                                };
                            }
                            return null;
                        },
                        addEventListener: (ev, cb) => {
                            if (!docListeners.has(ev)) docListeners.set(ev, []);
                            docListeners.get(ev).push(cb);
                        },
                        removeEventListener: (ev, cb) => {
                            const arr = docListeners.get(ev) || [];
                            docListeners.set(ev, arr.filter(x => x !== cb));
                        }
                    },
                    location: { pathname: '/planning/calendar' },
                    spokesDotNetRef: dotNetRef,
                    __spokesBlazorSocket: ws1
                };
                profWin.window = profWin;
                vm.runInNewContext(profilerSrc, profWin);

                const profiler = profWin.__spokesChatProfilerFactory({
                    now: () => nowMs,
                    pushCapped: (arr, item) => arr.push(item),
                    getNetworkSummary: () => ({}),
                    formatNetworkSummary: () => '4g',
                    flushMediaBatch: () => {},
                    setPendingMediaTapAnchorMs: () => {},
                    setProfilerActive: () => {},
                    setMediaHook: () => {},
                    getSession: () => ({ longTasks: [], channelOpens: [], backNavigations: [] })
                });

                await profiler.enable(dotNetRef);
                assert.equal(dotNetCalls[0].method, 'SetChatProfilerEnabled');
                assert.equal(dotNetCalls[0].args[0], true);
                assert.equal(ws1.__spokesDiagHooked, true);

                // 1) Click #nav-drawer-toggle-btn on /planning/calendar
                const clickHandlers = docListeners.get('click') || [];
                assert.equal(clickHandlers.length, 1);
                const drawerBtn = makeTarget({ '#nav-drawer-toggle-btn': { id: 'nav-drawer-toggle-btn' } }, { id: 'nav-drawer-toggle-btn' });
                clickHandlers[0]({ target: drawerBtn });
                await new Promise(r => setTimeout(r, 10));

                assert.ok(logs.some(l => l.includes('[Perf] Capturing #1: DrawerToggle (Open) (/planning/calendar)')), 'Expected immediate capture log for DrawerToggle');

                // Simulate outbound DispatchEvent + inbound RenderBatch + drawer DOM open
                ws1.send('{"target":"BeginInvokeDotNetFromJS","arguments":[null,null,"DispatchEventAsync"]}');
                profWin.__spokesWsIn('{"target":"JS.RenderBatch","arguments":[1]}');
                drawerOpenState = true;
                mutationCb();

                // Advance past UI_QUIET_MS (900ms) and trigger tick
                nowMs += 1000;
                const tickEntry = intervalCbs.find(x => x.ms === 500);
                assert.ok(tickEntry, 'Expected tick timer');
                tickEntry.cb();
                await new Promise(r => setTimeout(r, 20));

                assert.ok(logs.some(l => l.includes('[UI TRANSITION REPORT #1] DrawerToggle (Open)')), 'Expected UI transition report header');
                assert.ok(logs.some(l => l.includes('Tap → drawer painted')), 'Expected drawer painted summary row');
                const pingsAfterFirst = dotNetCalls.filter(c => c.method === 'DiagClockPing').length;
                assert.equal(pingsAfterFirst, 3, 'Expected 3 clock pings on initial sync');

                // 2) Second UI tap (FAB button) within 60s should reuse cached clock offset (0 additional clock pings)
                nowMs += 500;
                const fabBtn = makeTarget({ '.mud-fab': { tagName: 'BUTTON', className: 'mud-button-root m-fab' } }, { tagName: 'BUTTON', className: 'mud-button-root m-fab' });
                clickHandlers[0]({ target: fabBtn });
                await new Promise(r => setTimeout(r, 10));
                assert.ok(logs.some(l => l.includes('[Perf] Capturing #2: UIAction (button.mud-button-root.m-fab)')), 'Expected FAB capture');

                dialogCountState = 1;
                mutationCb();
                nowMs += 1000;
                tickEntry.cb();
                await new Promise(r => setTimeout(r, 20));

                assert.ok(logs.some(l => l.includes('[UI TRANSITION REPORT #2]')), 'Expected second UI report');
                assert.ok(logs.some(l => l.includes('Tap → dialog painted')), 'Expected dialog painted summary row');
                const pingsAfterSecond = dotNetCalls.filter(c => c.method === 'DiagClockPing').length;
                assert.equal(pingsAfterSecond, 3, 'Expected cached clock offset to avoid extra DiagClockPing RTTs');

                // 3) Text input clicks must be ignored
                const textInput = makeTarget({ 'input:not': { tagName: 'INPUT' }, 'button': null }, { tagName: 'INPUT' });
                const reportCountBeforeInput = profiler.reports.length;
                clickHandlers[0]({ target: textInput });
                assert.equal(profiler.reports.length, reportCountBeforeInput);
                assert.ok(!logs.some(l => l.includes('[Perf] Capturing #3:')), 'Text input click must not start a transition');

                // 4) Reconnect event must re-hook new socket, re-sync server flag, and invalidate clock cache
                const ws2 = createSocket();
                profWin.__spokesBlazorSocket = ws2;
                const reconnectedHandlers = winListeners.get('spokes:blazor-connected') || [];
                assert.equal(reconnectedHandlers.length, 1);
                reconnectedHandlers[0]();
                await new Promise(r => setTimeout(r, 10));
                assert.equal(ws2.__spokesDiagHooked, true, 'Expected new WebSocket hooked after reconnect');
                assert.ok(dotNetCalls.filter(c => c.method === 'SetChatProfilerEnabled' && c.args[0] === true).length >= 2, 'Expected SetChatProfilerEnabled(true) re-synced after reconnect');

                // 5) Non-chat popstate must also start a UI transition and re-measure clock after cache invalidation
                profWin.location.pathname = '/albums';
                const popHandlers = winListeners.get('popstate') || [];
                assert.equal(popHandlers.length, 1);
                popHandlers[0]();
                await new Promise(r => setTimeout(r, 10));
                assert.ok(logs.some(l => l.includes('[Perf] Capturing #3: PopState (/albums)')), 'Expected non-chat popstate capture');
                nowMs += 1600;
                tickEntry.cb();
                await new Promise(r => setTimeout(r, 20));
                assert.equal(dotNetCalls.filter(c => c.method === 'DiagClockPing').length, 6, 'Expected clock cache invalidated on reconnect');

                await profiler.disable();

                // 6) Verify spokes-boot.js downgrades renderer-1 error ONLY while _blazorDisconnected === true
                const bootSrc = fs.readFileSync('{{bootEsc}}', 'utf8');
                const bootErrors = [];
                const bootLogs = [];
                const bootWinListeners = new Map();
                const bootWin = {
                    location: { href: 'https://example.test/', origin: 'https://example.test', host: 'example.test', protocol: 'https:', pathname: '/', search: '' },
                    navigator: { userAgent: 'Mozilla/5.0' },
                    console: {
                        log: (...a) => bootLogs.push(a.join(' ')),
                        warn: () => {},
                        error: (...a) => bootErrors.push(a.join(' '))
                    },
                    document: {
                        visibilityState: 'visible',
                        documentElement: {},
                        addEventListener: () => {},
                        getElementById: () => null,
                        querySelectorAll: () => []
                    },
                    sessionStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                    localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
                    addEventListener: (ev, cb) => {
                        if (!bootWinListeners.has(ev)) bootWinListeners.set(ev, []);
                        bootWinListeners.get(ev).push(cb);
                    },
                    removeEventListener: () => {},
                    dispatchEvent: () => {},
                    setTimeout: () => 1,
                    clearTimeout: () => {},
                    setInterval: () => 1,
                    clearInterval: () => {},
                    fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) }),
                    WebSocket: function() { this.addEventListener = () => {}; },
                    CustomEvent: function(name) { this.type = name; },
                    __spokesBootConfig: { isStaticLoginRoute: true },
                    _blazorDisconnected: true
                };
                bootWin.window = bootWin;
                vm.runInNewContext(bootSrc, bootWin);

                const errHandlers = bootWinListeners.get('error') || [];
                assert.ok(errHandlers.length >= 1, 'Expected global error listener in spokes-boot.js');

                let prevented = false;
                errHandlers[0]({
                    message: 'Uncaught Error: No interop methods are registered for renderer 1',
                    error: new Error('No interop methods are registered for renderer 1'),
                    preventDefault: () => { prevented = true; }
                });
                assert.equal(prevented, true, 'Expected preventDefault when disconnected');
                assert.equal(bootErrors.length, 0, 'Must not log [Global Error] while disconnected');
                assert.ok(bootLogs.some(l => l.includes('Ignored DOM event while Blazor renderer detached during reconnect')), 'Expected downgrade log');

                // When connected, renderer errors must still surface as [Global Error]
                bootWin._blazorDisconnected = false;
                let preventedConnected = false;
                errHandlers[0]({
                    message: 'Uncaught Error: No interop methods are registered for renderer 1',
                    error: new Error('No interop methods are registered for renderer 1'),
                    preventDefault: () => { preventedConnected = true; }
                });
                assert.equal(preventedConnected, false, 'Must not suppress error while connected');
                assert.equal(bootErrors.length, 1, 'Must log [Global Error] while connected');

                console.log('WIRE_PROFILER_AND_RENDERER1_TEST_PASS');
                """;

            await File.WriteAllTextAsync(scriptPath, testScript);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("WireProfiler Node test timed out after 15s.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.True(proc.ExitCode == 0, $"WireProfiler Node test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("WIRE_PROFILER_AND_RENDERER1_TEST_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
