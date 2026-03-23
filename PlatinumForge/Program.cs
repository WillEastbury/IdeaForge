using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

// ─────────────────────────────────────────────
// What if software built itself? — Single-file C# console app
// ─────────────────────────────────────────────

// ── SystemState ──────────────────────────────

public class SystemState
{
    // Project metadata
    public string ProjectName { get; set; } = "untitled";
    public string Version { get; set; } = "0.1.0";

    // 0. Intent
    public Dictionary<string, string> Description { get; set; } = new();
    public Dictionary<string, string> Personas { get; set; } = new();

    // 1. Constraints
    public Dictionary<string, string> Rules { get; set; } = new();
    public Dictionary<string, string> Invariants { get; set; } = new();

    // 2. Shape
    public Dictionary<string, string> Architecture { get; set; } = new();
    public Dictionary<string, string> Dataflow { get; set; } = new();
    public Dictionary<string, string> Frameworks { get; set; } = new();
    public Dictionary<string, string> Language { get; set; } = new();
    public Dictionary<string, string> Deployment { get; set; } = new();

    // 3. Behaviour
    public Dictionary<string, string> Features { get; set; } = new();
    public Dictionary<string, string> Stories { get; set; } = new();
    public Dictionary<string, string> NFR { get; set; } = new();

    // Quality sliders (0–100) — influence code shape via LLM prompts
    public Dictionary<string, int> Sliders { get; set; } = DefaultSliders();

    public static Dictionary<string, int> DefaultSliders() => new()
    {
        ["performance"]    = 50,
        ["latency"]        = 50,
        ["ui-polish"]      = 50,
        ["simplicity"]     = 70,
        ["readability"]    = 70,
        ["conciseness"]    = 50,
        ["security"]       = 50,
        ["test-coverage"]  = 50,
        ["error-handling"] = 50,
        ["abstraction"]    = 40,
        ["layering"]       = 50,
        ["solid"]          = 60,
    };

    // 4. Forge (LLM-generated)
    public Dictionary<string, string> Interfaces { get; set; } = new();
    public Dictionary<string, string> UnitTests { get; set; } = new();
    public Dictionary<string, string> Code { get; set; } = new();
    public Dictionary<string, string> NfrTests { get; set; } = new();
    public Dictionary<string, string> SoakTests { get; set; } = new();
    public Dictionary<string, string> IntegrationTests { get; set; } = new();

    // 5. Finetune — micro-corrections fed back to LLM on next generation
    public string ArchitectureTweaks { get; set; } = "";        // single global note
    public Dictionary<string, string> CodeTweaks { get; set; } = new();   // keyed by code file
    public Dictionary<string, string> TestTweaks { get; set; } = new();   // keyed by test file

    // 6. Deploy — IaC templates and per-file deployment corrections
    public Dictionary<string, string> IaC { get; set; } = new();          // keyed by IaC file
    public Dictionary<string, string> DeployTweaks { get; set; } = new(); // keyed by IaC file

    // 7. Pipeline config — which stages to run
    public Dictionary<string, bool> PipelineConfig { get; set; } = new()
    {
        ["interfaces"] = true, ["unitTests"] = true, ["code"] = true, ["build"] = true,
        ["nfrTests"] = true, ["soakTests"] = true, ["integrationTests"] = true,
        ["iac"] = true, ["publish"] = true,
    };

    public SystemState Clone()
    {
        return new SystemState
        {
            ProjectName = ProjectName,
            Version = Version,
            Description = new(Description),
            Personas = new(Personas),
            Rules = new(Rules),
            Invariants = new(Invariants),
            Architecture = new(Architecture),
            Dataflow = new(Dataflow),
            Frameworks = new(Frameworks),
            Language = new(Language),
            Deployment = new(Deployment),
            Features = new(Features),
            Stories = new(Stories),
            NFR = new(NFR),
            Sliders = new(Sliders),
            Interfaces = new(Interfaces),
            UnitTests = new(UnitTests),
            Code = new(Code),
            NfrTests = new(NfrTests),
            SoakTests = new(SoakTests),
            IntegrationTests = new(IntegrationTests),
            ArchitectureTweaks = ArchitectureTweaks,
            CodeTweaks = new(CodeTweaks),
            TestTweaks = new(TestTweaks),
            IaC = new(IaC),
            DeployTweaks = new(DeployTweaks),
            PipelineConfig = new(PipelineConfig),
        };
    }
}

// ── OpenAI Client ────────────────────────────

public static class OpenAIClient
{
    private static readonly HttpClient Http = new();
    private static string? _apiKey;
    private static string ApiKey => _apiKey ??=
        Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? throw new InvalidOperationException("Set OPENAI_API_KEY env var. Generation requires an OpenAI API key.");

    private static readonly string Model =
        Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1";
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "https://api.openai.com/v1/chat/completions";

    public static async Task<string> Complete(string systemPrompt, string userPrompt)
    {
        var body = new
        {
            model = Model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt },
            }
        };

        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {ApiKey}");

        Console.WriteLine($"[OpenAI] Calling {Endpoint} model={Model}...");
        var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[OpenAI] Error {resp.StatusCode}: {text}");
            // Extract a concise error message from the JSON response if possible
            string detail = text;
            try
            {
                using var errDoc = JsonDocument.Parse(text);
                if (errDoc.RootElement.TryGetProperty("error", out var errObj) &&
                    errObj.TryGetProperty("message", out var msg))
                    detail = msg.GetString() ?? text;
            }
            catch { }
            throw new Exception($"OpenAI {resp.StatusCode}: {detail}");
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()!;
    }
}

// ── Prompt Builder ───────────────────────────

public static class PromptBuilder
{
    public static string BuildContext(SystemState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PIPELINE CONTEXT ===");
        sb.AppendLine();
        sb.AppendLine("## 0 · Intent");
        Append(sb, "DESCRIPTION",  state.Description);
        Append(sb, "PERSONAS",     state.Personas);
        sb.AppendLine("## 1 · Constraints");
        Append(sb, "RULES",        state.Rules);
        Append(sb, "INVARIANTS",   state.Invariants);
        sb.AppendLine("## 2 · Shape");
        Append(sb, "ARCHITECTURE", state.Architecture);
        Append(sb, "DATAFLOW",     state.Dataflow);
        Append(sb, "FRAMEWORKS",   state.Frameworks);
        Append(sb, "LANGUAGE",     state.Language);
        Append(sb, "DEPLOYMENT",   state.Deployment);
        sb.AppendLine("## 3 · Behaviour");
        Append(sb, "FEATURES",     state.Features);
        Append(sb, "STORIES",      state.Stories);
        Append(sb, "NFR",          state.NFR);
        sb.AppendLine("## Quality Sliders (0 = low priority, 100 = critical)");
        foreach (var (k, v) in state.Sliders)
            sb.AppendLine($"- {k}: {v}/100");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Build a tweaks addendum for a specific generation stage.</summary>
    public static string BuildTweaks(SystemState state, string stage)
    {
        var sb = new StringBuilder();
        // Architecture tweaks apply to interfaces and code stages
        if ((stage == "interfaces" || stage == "code") && !string.IsNullOrEmpty(state.ArchitectureTweaks))
        {
            sb.AppendLine("\n⚠️ ARCHITECTURE CORRECTIONS (apply these structural changes):");
            sb.AppendLine(state.ArchitectureTweaks);
        }
        // Code tweaks apply only to code stage
        if (stage == "code" && state.CodeTweaks.Count > 0)
        {
            sb.AppendLine("\n⚠️ PER-FILE CODE CORRECTIONS (apply these fixes to the named files):");
            foreach (var (file, fix) in state.CodeTweaks)
                sb.AppendLine($"  [{file}]: {fix}");
        }
        // Test tweaks apply to all test stages
        if ((stage == "unit" || stage == "nfr" || stage == "soak" || stage == "integration") && state.TestTweaks.Count > 0)
        {
            sb.AppendLine("\n⚠️ PER-FILE TEST CORRECTIONS (apply these fixes to the named test files):");
            foreach (var (file, fix) in state.TestTweaks)
                sb.AppendLine($"  [{file}]: {fix}");
        }
        // Deploy tweaks apply to IaC stage
        if (stage == "iac" && state.DeployTweaks.Count > 0)
        {
            sb.AppendLine("\n⚠️ PER-FILE DEPLOYMENT CORRECTIONS:");
            foreach (var (file, fix) in state.DeployTweaks)
                sb.AppendLine($"  [{file}]: {fix}");
        }
        return sb.ToString();
    }

    public static string BuildFullContext(SystemState state)
    {
        var sb = new StringBuilder(BuildContext(state));
        Append(sb, "INTERFACES", state.Interfaces);
        Append(sb, "UNIT TESTS", state.UnitTests);
        Append(sb, "CODE",       state.Code);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string section, Dictionary<string, string> dict)
    {
        if (dict.Count == 0) return;
        sb.AppendLine($"── {section} ──");
        foreach (var (k, v) in dict)
            sb.AppendLine($"[{k}]\n{v}\n");
    }
}

// ── Code Compiler ────────────────────────────

public static class CodeCompiler
{
    public static string BuildFullSource(SystemState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine("namespace App {");
        sb.AppendLine();

        // Interfaces
        foreach (var (name, src) in state.Interfaces)
        {
            sb.AppendLine($"// ── Interface: {name}");
            sb.AppendLine(src);
            sb.AppendLine();
        }

        // Code implementations
        foreach (var (name, src) in state.Code)
        {
            sb.AppendLine($"// ── Code: {name}");
            sb.AppendLine(src);
            sb.AppendLine();
        }

        // Test runner
        sb.AppendLine("// ── Test Runner");
        sb.AppendLine("public static class TestRunner {");
        sb.AppendLine("    public static List<(string Name, bool Passed, string Message)> Run() {");
        sb.AppendLine("        var results = new List<(string, bool, string)>();");
        foreach (var (name, src) in state.UnitTests)
        {
            sb.AppendLine($"        // Test: {name}");
            sb.AppendLine(src);
        }
        sb.AppendLine("        return results;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("} // namespace App");
        return sb.ToString();
    }

    private static readonly string[] TrustedAssemblies =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

    public static (Assembly? Asm, string[] Errors) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = TrustedAssemblies
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "GeneratedAssembly",
            syntaxTrees: new[] { tree },
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            return (null, errors);
        }

        ms.Seek(0, SeekOrigin.Begin);
        var ctx = new AssemblyLoadContext("gen-" + Guid.NewGuid(), isCollectible: true);
        var asm = ctx.LoadFromStream(ms);
        return (asm, Array.Empty<string>());
    }

    public static (bool AllPassed, List<(string Name, bool Passed, string Msg)> Results, string? Error)
        RunTests(Assembly asm)
    {
        try
        {
            var type = asm.GetType("App.TestRunner")
                      ?? throw new Exception("TestRunner type not found");
            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                         ?? throw new Exception("Run method not found");
            var raw = method.Invoke(null, null);
            var results = (List<(string, bool, string)>)raw!;
            var allPassed = results.All(r => r.Item2);
            return (allPassed, results, null);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            return (false, new(), $"Runtime error: {inner.Message}");
        }
    }
}

// ── Invariant Checker ────────────────────────

public static class InvariantChecker
{
    private static readonly Dictionary<string, Func<string, string?>> BuiltInChecks = new()
    {
        ["no-reflection"] = src =>
            src.Contains("System.Reflection") ? "Code must not use System.Reflection" : null,
        ["no-file-io"] = src =>
            src.Contains("System.IO.File") ? "Code must not use System.IO.File" : null,
    };

    public static List<string> Check(SystemState state, string fullSource)
    {
        var violations = new List<string>();
        foreach (var (key, desc) in state.Invariants)
        {
            if (BuiltInChecks.TryGetValue(key, out var check))
            {
                var msg = check(fullSource);
                if (msg != null) violations.Add($"[{key}] {msg}");
            }
        }
        return violations;
    }
}

// ── Diff Utility ─────────────────────────────

public static class DiffUtil
{
    public static string Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var sb = new StringBuilder();
        var allKeys = before.Keys.Union(after.Keys).OrderBy(k => k);
        foreach (var key in allKeys)
        {
            before.TryGetValue(key, out var oldVal);
            after.TryGetValue(key, out var newVal);
            oldVal ??= "";
            newVal ??= "";
            if (oldVal == newVal) continue;

            sb.AppendLine($"━━━ {key} ━━━");
            var oldLines = oldVal.Split('\n');
            var newLines = newVal.Split('\n');
            var max = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < max; i++)
            {
                var ol = i < oldLines.Length ? oldLines[i] : "";
                var nl = i < newLines.Length ? newLines[i] : "";
                if (ol != nl)
                {
                    if (!string.IsNullOrWhiteSpace(ol))
                        sb.AppendLine($"  - {ol.TrimEnd()}");
                    if (!string.IsNullOrWhiteSpace(nl))
                        sb.AppendLine($"  + {nl.TrimEnd()}");
                }
            }
        }
        return sb.Length == 0 ? "(no changes)" : sb.ToString();
    }
}

// ── LLM Generation ───────────────────────────

public static class Generator
{
    private const string SystemPromptBase =
        """
        You are a code generation engine for a autonomous software generation system.
        You output ONLY raw C# code. No markdown fences, no explanations.
        The code will be placed inside `namespace App { }`.
        Do NOT produce a Main method. Do NOT use top-level statements.
        Do NOT use System.Reflection or System.IO.File.
        """;

    private static string BuildSystemPrompt(SystemState state)
    {
        var sb = new StringBuilder(SystemPromptBase);
        sb.AppendLine();
        sb.AppendLine("QUALITY GUIDANCE (adjust your output to match these priorities):");
        var s = state.Sliders;
        int Val(string k) => s.TryGetValue(k, out var v) ? v : 50;

        void Guidance(string slider, string low, string high)
        {
            var v = Val(slider);
            if (v <= 25) sb.AppendLine($"- {low}");
            else if (v >= 75) sb.AppendLine($"- {high}");
        }

        Guidance("performance",   "Performance is NOT a priority — favour clarity over speed.",
                                  "Performance is CRITICAL — use efficient algorithms, avoid allocations, prefer Span<T>/stackalloc where safe.");
        Guidance("latency",       "Latency tolerance is high — batch operations are fine.",
                                  "Latency must be ultra-low — async paths, minimal blocking, pre-computed lookups.");
        Guidance("ui-polish",     "UI can be minimal/functional — plain HTML is fine.",
                                  "UI must be polished — use animations, transitions, consistent spacing, professional styling.");
        Guidance("simplicity",    "Sophisticated patterns are welcome — use advanced design patterns freely.",
                                  "Keep it SIMPLE — avoid over-engineering, minimal abstractions, straightforward code.");
        Guidance("readability",   "Terse code is acceptable — abbreviations and compact style are fine.",
                                  "Maximise readability — descriptive names, XML doc comments, clear structure.");
        Guidance("conciseness",   "Verbose/explicit code is preferred — spell everything out.",
                                  "Be concise — use expression bodies, LINQ, pattern matching, minimal boilerplate.");
        Guidance("security",      "Basic security is sufficient — trust inputs in this context.",
                                  "Security is paramount — validate all inputs, sanitise outputs, use parameterised queries, apply least privilege.");
        Guidance("test-coverage", "Minimal tests — cover only happy paths.",
                                  "Exhaustive tests — cover edge cases, error paths, boundary conditions, property-based where appropriate.");
        Guidance("error-handling","Fail-fast is fine — throw on unexpected input.",
                                  "Resilient error handling — use Result types, graceful degradation, structured error responses.");
        Guidance("abstraction",   "Keep concrete — direct implementations, minimal interfaces beyond what's specified.",
                                  "Highly abstract — use generics, strategy pattern, dependency inversion, pluggable components.");
        Guidance("layering",      "Flat structure is fine — all code can live together, minimal separation.",
                                  "Strict layering — separate Domain, Application, Infrastructure, Presentation layers. No cross-layer leakage.");
        Guidance("solid",         "Pragmatic — SOLID principles are guidelines, not rules. Inline logic is fine.",
                                  "Strict SOLID — Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, Dependency Inversion must all be followed.");
        return sb.ToString();
    }

    public static async Task<Dictionary<string, string>> GenerateUnitTests(SystemState state, string? previousSource = null, string[]? compileErrors = null)
    {
        var context = PromptBuilder.BuildContext(state);
        var errorFeedback = "";
        if (previousSource != null && compileErrors != null && compileErrors.Length > 0)
        {
            errorFeedback =
                $"""

                ⚠️ The previous full assembled source FAILED to compile due to errors in the TEST code:
                {previousSource}

                Compilation errors:
                {string.Join("\n", compileErrors)}

                The error is in the test assertions. Fix the type mismatches.
                For example, if a method returns string, compare with a string literal ("5"), not an int (5).
                If a method returns int, compare with an int literal, not a string.
                """;
        }
        var prompt =
            $"""
            Given these requirements:
            {context}
            {errorFeedback}

            Generate C# test code as inline statements that will go inside a static method body.
            Each test should:
            1. Create instances and call methods
            2. Check results with if-statements
            3. Add to `results` list: results.Add(("TestName", passed, "message"));

            CRITICAL TYPE SAFETY RULES:
            - If a method returns string, compare with a string: if (result == "5") NOT if (result == 5)
            - If a method returns int, compare with an int: if (result == 5) NOT if (result == "5")
            - Use .ToString() or int.Parse() when needed to match types
            - Every == comparison must have matching types on both sides

            The variable `results` (List<(string, bool, string)>) is already declared.
            Generate 3-5 meaningful tests. Output ONLY the C# statements, no method/class wrapper.
            Use simple assertions (if/else adding to results).
            Reference interfaces/classes you expect to exist (e.g., ICalculator, Calculator).
            """;

        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "unit"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["core-tests"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateInterfaces(SystemState state)
    {
        var context = PromptBuilder.BuildContext(state);
        var tests = string.Join("\n", state.UnitTests.Values);
        var prompt =
            $"""
            Given these requirements:
            {context}

            And these tests that must pass:
            {tests}

            Generate C# interface definitions that the tests reference.
            Output ONLY interface declarations (public interface IXxx).
            No class implementations, no using statements, no namespace.
            """;

        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "interfaces"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["core-interfaces"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateCode(SystemState state, string? previousCode = null, string[]? compileErrors = null, List<(string Name, bool Passed, string Msg)>? testResults = null)
    {
        var context = PromptBuilder.BuildFullContext(state);
        var errorFeedback = "";
        if (previousCode != null && compileErrors != null && compileErrors.Length > 0)
        {
            errorFeedback =
                $"""

                ⚠️ The FULL ASSEMBLED source (with line numbers) FAILED to compile:
                {previousCode}

                Compilation errors:
                {string.Join("\n", compileErrors)}

                The error line numbers reference the full source above.
                You can ONLY change the class implementations. The tests and interfaces are fixed.
                If the tests compare a string to an int (or vice versa), your implementation must return
                the type that matches what the tests expect.
                Fix these errors in your new output. Pay close attention to type mismatches.
                """;
        }
        else if (previousCode != null && testResults != null)
        {
            var failures = testResults.Where(t => !t.Passed).ToList();
            if (failures.Count > 0)
            {
                errorFeedback =
                    $"""

                    ⚠️ Your previous code compiled but some tests FAILED. Here is the code:
                    {previousCode}

                    Failed tests:
                    {string.Join("\n", failures.Select(f => $"  - {f.Name}: {f.Msg}"))}

                    Fix the logic so all tests pass.
                    """;
            }
        }

        var prompt =
            $"""
            Given this full system context:
            {context}
            {errorFeedback}

            Generate C# class implementations that:
            1. Implement all interfaces defined above
            2. Make all tests pass
            3. Follow the rules, architecture, and NFR constraints

            Output ONLY class declarations (public class Xxx : IXxx).
            No interfaces, no using statements, no namespace, no Main method.
            """;

        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "code"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["core-impl"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateNfrTests(SystemState state)
    {
        var context = PromptBuilder.BuildFullContext(state);
        var nfrEntries = string.Join("\n", state.NFR.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var prompt =
            $"""
            Given this system context:
            {context}

            And these non-functional requirements:
            {nfrEntries}

            Generate Playwright test code (TypeScript) that validates the NFR constraints against a web UI.
            The tests should:
            1. Use @playwright/test imports
            2. Test UI responsiveness, accessibility, error states
            3. Verify performance constraints where applicable
            4. Be runnable with `npx playwright test`

            Output ONLY the TypeScript test code. No markdown fences, no explanations.
            """;
        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "nfr"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["nfr-tests.spec.ts"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateSoakTests(SystemState state)
    {
        var context = PromptBuilder.BuildFullContext(state);
        var nfrEntries = string.Join("\n", state.NFR.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var prompt =
            $"""
            Given this system context:
            {context}

            And these non-functional requirements:
            {nfrEntries}

            Generate a Locust load test file (Python) that validates performance constraints.
            The tests should:
            1. Import from locust (HttpUser, task, between)
            2. Define user behaviours that exercise core API endpoints
            3. Include wait times and realistic load patterns
            4. Be runnable with `locust -f locustfile.py --headless -u 10 -r 2 --run-time 30s`

            Output ONLY the Python code. No markdown fences, no explanations.
            """;
        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "soak"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["locustfile.py"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateIntegrationTests(SystemState state)
    {
        var context = PromptBuilder.BuildFullContext(state);
        var stories = string.Join("\n", state.Stories.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var features = string.Join("\n", state.Features.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var prompt =
            $"""
            Given this system context:
            {context}

            User stories:
            {stories}

            Features:
            {features}

            Generate Jest integration test code (TypeScript) that validates the system end-to-end.
            The tests should:
            1. Use Jest describe/it/expect syntax
            2. Test API endpoints, data flows, and feature interactions
            3. Cover the user stories as test scenarios
            4. Be runnable with `npx jest`

            Output ONLY the TypeScript test code. No markdown fences, no explanations.
            """;
        var code = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "integration"));
        code = StripMarkdownFences(code);
        return new Dictionary<string, string> { ["integration.test.ts"] = code };
    }

    public static async Task<Dictionary<string, string>> GenerateIaC(SystemState state)
    {
        var context = PromptBuilder.BuildContext(state);
        var existingIaC = state.IaC.Count > 0
            ? "Existing IaC templates to update/extend:\n" + string.Join("\n", state.IaC.Select(kv => $"── {kv.Key} ──\n{kv.Value}"))
            : "";
        var deploymentInfo = string.Join("\n", state.Deployment.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var prompt =
            $"""
            Given these requirements:
            {context}

            And this deployment configuration:
            {deploymentInfo}

            {existingIaC}

            Generate Infrastructure as Code files for deploying this application.
            Include:
            - Dockerfile (multi-stage build)
            - docker-compose.yml (if applicable)
            - Deployment manifests appropriate to the deployment target (Terraform, Bicep, ARM, Kubernetes YAML, etc.)
            - CI/CD pipeline file (GitHub Actions)

            Output as a JSON object where keys are filenames and values are the file contents.
            Example: {"{"}\"Dockerfile\": \"FROM ...\", \"deploy.bicep\": \"...\"{"}"}
            Output ONLY valid JSON. No markdown fences, no explanations.
            """;
        var raw = await OpenAIClient.Complete(BuildSystemPrompt(state), prompt + PromptBuilder.BuildTweaks(state, "iac"));
        raw = StripMarkdownFences(raw);
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(raw);
            var result = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.GetString() ?? "";
            return result;
        }
        catch
        {
            return new Dictionary<string, string> { ["iac-output"] = raw };
        }
    }

    public static string StripMarkdownFences(string s)
    {
        var lines = s.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```"))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("```"))
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }
}

// ── Chat & History Models ────────────────────

public class ChatEntry
{
    public string Role { get; set; } = ""; // "user", "system", "error", "success", "agent"
    public string Message { get; set; } = "";
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    public List<ProposedAction>? Actions { get; set; } // null if no actions proposed
}

public class ProposedAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";     // human-readable, e.g. "Set rule: no-nulls"
    public string Layer { get; set; } = "";      // target layer, e.g. "rules", "architecture"
    public string Op { get; set; } = "set";      // "set", "remove", "replace", "set-string"
    public string Key { get; set; } = "";         // dict key (or empty for string layers)
    public string Value { get; set; } = "";       // new value
    public bool Applied { get; set; } = false;
}

public class StateSnapshot
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    public SystemState State { get; set; } = new();
}

// ── Session Model ────────────────────────────

public class SessionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? SharedFromSub { get; set; } // non-null if this is a linked shared session
}

public class SessionManifest
{
    public List<SessionInfo> Sessions { get; set; } = new();
    public string ActiveSessionId { get; set; } = "";
}

// ── User & Auth Models ──────────────────────

public class UserProfile
{
    public string Sub { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Picture { get; set; } = "";
}

public class ShareToken
{
    public string Token { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string OwnerSub { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

// ── Auth Manager (Google OIDC) ──────────────

public class OAuthProvider
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string AuthorizeUrl { get; init; } = "";
    public string TokenUrl { get; init; } = "";
    public string UserInfoUrl { get; init; } = "";
    public string Scopes { get; init; } = "";
    public string IconSvg { get; init; } = "";
    public string BtnColor { get; init; } = "#fff";
    public string BtnTextColor { get; init; } = "#333";
    // Maps provider-specific JSON fields to our UserProfile fields
    public Func<JsonElement, UserProfile> ParseProfile { get; init; } = _ => new();
}

public static class AuthManager
{
    private static readonly HttpClient Http = new();

    private static readonly string BaseUrl =
        (Environment.GetEnvironmentVariable("PLATINUMFORGE_BASE_URL") ?? "http://localhost:5005").TrimEnd('/');

    // HMAC key for signing auth cookies (generated per-process, or from env)
    private static readonly byte[] HmacKey = GetOrCreateHmacKey();

    // Data root: configurable via PLATINUMFORGE_DATA_DIR, defaults to ~/.platinumforge
    public static readonly string DataRoot =
        Environment.GetEnvironmentVariable("PLATINUMFORGE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platinumforge");

    // In-memory user cache: sub → UserProfile
    private static readonly ConcurrentDictionary<string, UserProfile> Users = new();

    // Share tokens: token → ShareToken
    private static readonly ConcurrentDictionary<string, ShareToken> ShareTokens = new();
    private static readonly string ShareTokensFile = Path.Combine(AuthManager.DataRoot, "shares.json");

    // ── OAuth Providers ──

    private static readonly Dictionary<string, OAuthProvider> Providers = InitProviders();

    private static Dictionary<string, OAuthProvider> InitProviders()
    {
        var providers = new Dictionary<string, OAuthProvider>(StringComparer.OrdinalIgnoreCase);

        var googleId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
        var googleSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";
        if (!string.IsNullOrEmpty(googleId) && !string.IsNullOrEmpty(googleSecret))
        {
            providers["google"] = new OAuthProvider
            {
                Name = "google", DisplayName = "Google",
                ClientId = googleId, ClientSecret = googleSecret,
                AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenUrl = "https://oauth2.googleapis.com/token",
                UserInfoUrl = "https://www.googleapis.com/oauth2/v3/userinfo",
                Scopes = "openid email profile",
                IconSvg = """<svg viewBox="0 0 24 24"><path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z"/><path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/><path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"/><path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"/></svg>""",
                ParseProfile = root => new UserProfile
                {
                    Sub = "google:" + (root.GetProperty("sub").GetString() ?? ""),
                    Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Picture = root.TryGetProperty("picture", out var p) ? p.GetString() ?? "" : "",
                },
            };
        }

        var msId = Environment.GetEnvironmentVariable("MICROSOFT_CLIENT_ID") ?? "";
        var msSecret = Environment.GetEnvironmentVariable("MICROSOFT_CLIENT_SECRET") ?? "";
        if (!string.IsNullOrEmpty(msId) && !string.IsNullOrEmpty(msSecret))
        {
            providers["microsoft"] = new OAuthProvider
            {
                Name = "microsoft", DisplayName = "Microsoft",
                ClientId = msId, ClientSecret = msSecret,
                AuthorizeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                UserInfoUrl = "https://graph.microsoft.com/oidc/userinfo",
                Scopes = "openid email profile",
                IconSvg = """<svg viewBox="0 0 24 24"><path fill="#f25022" d="M1 1h10v10H1z"/><path fill="#00a4ef" d="M1 13h10v10H1z"/><path fill="#7fba00" d="M13 1h10v10H13z"/><path fill="#ffb900" d="M13 13h10v10H13z"/></svg>""",
                ParseProfile = root => new UserProfile
                {
                    Sub = "microsoft:" + (root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""),
                    Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    Picture = "",
                },
            };
        }

        var ghId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID") ?? "";
        var ghSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET") ?? "";
        if (!string.IsNullOrEmpty(ghId) && !string.IsNullOrEmpty(ghSecret))
        {
            providers["github"] = new OAuthProvider
            {
                Name = "github", DisplayName = "GitHub",
                ClientId = ghId, ClientSecret = ghSecret,
                AuthorizeUrl = "https://github.com/login/oauth/authorize",
                TokenUrl = "https://github.com/login/oauth/access_token",
                UserInfoUrl = "https://api.github.com/user",
                Scopes = "read:user user:email",
                IconSvg = """<svg viewBox="0 0 24 24"><path fill="currentColor" d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"/></svg>""",
                BtnColor = "#24292f", BtnTextColor = "#fff",
                ParseProfile = root => new UserProfile
                {
                    Sub = "github:" + (root.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : ""),
                    Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : root.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "",
                    Picture = root.TryGetProperty("avatar_url", out var a) ? a.GetString() ?? "" : "",
                },
            };
        }

        var fbId = Environment.GetEnvironmentVariable("FACEBOOK_CLIENT_ID") ?? "";
        var fbSecret = Environment.GetEnvironmentVariable("FACEBOOK_CLIENT_SECRET") ?? "";
        if (!string.IsNullOrEmpty(fbId) && !string.IsNullOrEmpty(fbSecret))
        {
            providers["facebook"] = new OAuthProvider
            {
                Name = "facebook", DisplayName = "Facebook",
                ClientId = fbId, ClientSecret = fbSecret,
                AuthorizeUrl = "https://www.facebook.com/v18.0/dialog/oauth",
                TokenUrl = "https://graph.facebook.com/v18.0/oauth/access_token",
                UserInfoUrl = "https://graph.facebook.com/me?fields=id,name,email,picture.width(200)",
                Scopes = "email public_profile",
                IconSvg = """<svg viewBox="0 0 24 24"><path fill="#1877F2" d="M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z"/></svg>""",
                ParseProfile = root => new UserProfile
                {
                    Sub = "facebook:" + (root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""),
                    Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Picture = root.TryGetProperty("picture", out var pic) && pic.TryGetProperty("data", out var data) && data.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                },
            };
        }

        var appleId = Environment.GetEnvironmentVariable("APPLE_CLIENT_ID") ?? "";
        var appleSecret = Environment.GetEnvironmentVariable("APPLE_CLIENT_SECRET") ?? "";
        if (!string.IsNullOrEmpty(appleId) && !string.IsNullOrEmpty(appleSecret))
        {
            providers["apple"] = new OAuthProvider
            {
                Name = "apple", DisplayName = "Apple",
                ClientId = appleId, ClientSecret = appleSecret,
                AuthorizeUrl = "https://appleid.apple.com/auth/authorize",
                TokenUrl = "https://appleid.apple.com/auth/token",
                UserInfoUrl = "", // Apple uses id_token, not userinfo endpoint
                Scopes = "name email",
                IconSvg = """<svg viewBox="0 0 24 24"><path fill="currentColor" d="M17.05 20.28c-.98.95-2.05.88-3.08.4-1.09-.5-2.08-.48-3.24 0-1.44.62-2.2.44-3.06-.4C2.79 15.25 3.51 7.59 9.05 7.31c1.35.07 2.29.74 3.08.8 1.18-.24 2.31-.93 3.57-.84 1.51.12 2.65.72 3.4 1.8-3.12 1.87-2.38 5.98.48 7.13-.57 1.5-1.31 2.99-2.54 4.09zM12.03 7.25c-.15-2.23 1.66-4.07 3.74-4.25.29 2.58-2.34 4.5-3.74 4.25z"/></svg>""",
                BtnColor = "#000", BtnTextColor = "#fff",
                ParseProfile = root => new UserProfile
                {
                    Sub = "apple:" + (root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : ""),
                    Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Picture = "",
                },
            };
        }

        return providers;
    }

    public static bool IsConfigured => Providers.Count > 0;
    public static IReadOnlyDictionary<string, OAuthProvider> ConfiguredProviders => Providers;

    private static byte[] GetOrCreateHmacKey()
    {
        var envKey = Environment.GetEnvironmentVariable("PLATINUMFORGE_HMAC_KEY");
        if (!string.IsNullOrEmpty(envKey))
            return Convert.FromBase64String(envKey);
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    // ── Cookie Auth ──

    public static string CreateAuthCookie(string sub)
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        var payload = $"{sub}|{expiry}";
        using var hmac = new HMACSHA256(HmacKey);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return $"{payload}|{sig}";
    }

    public static string? ValidateCookie(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue)) return null;
        var parts = cookieValue.Split('|');
        if (parts.Length != 3) return null;
        var sub = parts[0];
        if (!long.TryParse(parts[1], out var expiry)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return null;
        var payload = $"{sub}|{parts[1]}";
        using var hmac = new HMACSHA256(HmacKey);
        var expectedSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        if (expectedSig != parts[2]) return null;
        return sub;
    }

    public static string? GetSubFromRequest(HttpListenerRequest req)
    {
        var cookies = req.Cookies;
        var authCookie = cookies["platinumforge_auth"];
        return ValidateCookie(authCookie?.Value);
    }

    // ── Generic OAuth ──

    public static string? GetLoginUrl(string providerName, string? state = null)
    {
        if (!Providers.TryGetValue(providerName, out var provider)) return null;
        var s = state ?? Guid.NewGuid().ToString("N");
        var redirectUri = $"{BaseUrl}/auth/callback/{provider.Name}";
        var url = $"{provider.AuthorizeUrl}?" +
               $"client_id={Uri.EscapeDataString(provider.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&scope={Uri.EscapeDataString(provider.Scopes)}" +
               $"&state={Uri.EscapeDataString(s)}";
        // Provider-specific params
        if (provider.Name == "google") url += "&access_type=online&prompt=select_account";
        if (provider.Name == "apple") url += "&response_mode=form_post";
        return url;
    }

    public static async Task<UserProfile?> HandleCallback(string providerName, string code)
    {
        if (!Providers.TryGetValue(providerName, out var provider)) return null;

        var redirectUri = $"{BaseUrl}/auth/callback/{provider.Name}";

        // Exchange code for tokens
        var tokenParams = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = provider.ClientId,
            ["client_secret"] = provider.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        var tokenReq = new HttpRequestMessage(HttpMethod.Post, provider.TokenUrl)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };
        // GitHub needs Accept: application/json
        if (provider.Name == "github")
            tokenReq.Headers.Add("Accept", "application/json");

        var tokenResp = await Http.SendAsync(tokenReq);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode) return null;

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;

        // Apple: parse id_token JWT instead of userinfo
        if (provider.Name == "apple" && string.IsNullOrEmpty(provider.UserInfoUrl))
        {
            if (tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenEl))
            {
                var idToken = idTokenEl.GetString() ?? "";
                var payload = idToken.Split('.').ElementAtOrDefault(1) ?? "";
                var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
                using var claimsDoc = JsonDocument.Parse(json);
                var profile = provider.ParseProfile(claimsDoc.RootElement);
                Users[profile.Sub] = profile;
                SaveUserProfile(profile);
                return profile;
            }
            return null;
        }

        // Fetch user info
        var userReq = new HttpRequestMessage(HttpMethod.Get, provider.UserInfoUrl);
        userReq.Headers.Add("Authorization", $"Bearer {accessToken}");
        if (provider.Name == "github")
            userReq.Headers.Add("User-Agent", "PlatinumForge");
        var userResp = await Http.SendAsync(userReq);
        var userJson = await userResp.Content.ReadAsStringAsync();
        if (!userResp.IsSuccessStatusCode) return null;

        using var userDoc = JsonDocument.Parse(userJson);
        var userProfile = provider.ParseProfile(userDoc.RootElement);

        // GitHub: email may be null in /user, fetch from /user/emails
        if (provider.Name == "github" && string.IsNullOrEmpty(userProfile.Email))
        {
            try
            {
                var emailReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
                emailReq.Headers.Add("Authorization", $"Bearer {accessToken}");
                emailReq.Headers.Add("User-Agent", "PlatinumForge");
                var emailResp = await Http.SendAsync(emailReq);
                if (emailResp.IsSuccessStatusCode)
                {
                    var emailJson = await emailResp.Content.ReadAsStringAsync();
                    using var emailDoc = JsonDocument.Parse(emailJson);
                    foreach (var em in emailDoc.RootElement.EnumerateArray())
                    {
                        if (em.TryGetProperty("primary", out var pri) && pri.GetBoolean())
                        {
                            userProfile.Email = em.GetProperty("email").GetString() ?? "";
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        Users[userProfile.Sub] = userProfile;
        SaveUserProfile(userProfile);
        return userProfile;
    }

    // ── User persistence ──

    private static string UserDir(string sub) => Path.Combine(AuthManager.DataRoot, "users", sub);

    private static string UserProfileFile(string sub) => Path.Combine(UserDir(sub), "profile.json");

    public static string UserSessionsDir(string sub) => Path.Combine(UserDir(sub), "sessions");
    public static string UserManifestFile(string sub) => Path.Combine(UserDir(sub), "sessions.json");

    private static void SaveUserProfile(UserProfile profile)
    {
        var dir = UserDir(profile.Sub);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(UserSessionsDir(profile.Sub));
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(UserProfileFile(profile.Sub), json);
    }

    public static UserProfile? LoadUserProfile(string sub)
    {
        if (Users.TryGetValue(sub, out var cached)) return cached;
        var file = UserProfileFile(sub);
        if (!File.Exists(file)) return null;
        try
        {
            var json = File.ReadAllText(file);
            var profile = JsonSerializer.Deserialize<UserProfile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (profile != null) Users[sub] = profile;
            return profile;
        }
        catch { return null; }
    }

    // ── Share Tokens ──

    public static void LoadShareTokens()
    {
        if (!File.Exists(ShareTokensFile)) return;
        try
        {
            var json = File.ReadAllText(ShareTokensFile);
            var tokens = JsonSerializer.Deserialize<List<ShareToken>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (tokens != null)
                foreach (var t in tokens) ShareTokens[t.Token] = t;
        }
        catch { }
    }

    private static void SaveShareTokens()
    {
        var dir = Path.GetDirectoryName(ShareTokensFile)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(ShareTokens.Values.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ShareTokensFile, json);
    }

    public static string CreateShareToken(string sessionId, string ownerSub)
    {
        var token = Guid.NewGuid().ToString("N");
        ShareTokens[token] = new ShareToken
        {
            Token = token,
            SessionId = sessionId,
            OwnerSub = ownerSub,
        };
        SaveShareTokens();
        return token;
    }

    public static ShareToken? GetShareToken(string token)
    {
        ShareTokens.TryGetValue(token, out var st);
        return st;
    }
}

// ── SSE Client ──────────────────────────────

public class SseClient
{
    public string ClientId { get; }
    public string UserSub { get; }
    public HttpListenerResponse Response { get; }
    public CancellationTokenSource Cts { get; } = new();

    public SseClient(string clientId, string userSub, HttpListenerResponse response)
    {
        ClientId = clientId;
        UserSub = userSub;
        Response = response;
    }

    public async Task<bool> SendEvent(string eventType, string data)
    {
        try
        {
            var payload = $"event: {eventType}\ndata: {data}\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, Cts.Token);
            await Response.OutputStream.FlushAsync(Cts.Token);
            return true;
        }
        catch { return false; }
    }
}

// ── Live Session (shared collaborative state) ─

public class LiveSession
{
    public string SessionId { get; }
    public string OwnerSub { get; }
    public SystemState State { get; set; } = new();
    public string CurrentSource { get; set; } = "// No code generated yet";
    public List<ChatEntry> ChatLog { get; } = new();
    public List<StateSnapshot> History { get; } = new();
    public bool Generating { get; set; } = false;
    public int SnapshotCounter { get; set; } = 0;

    private readonly ConcurrentDictionary<string, SseClient> _sseClients = new();
    private readonly object _lock = new();

    public LiveSession(string sessionId, string ownerSub)
    {
        SessionId = sessionId;
        OwnerSub = ownerSub;
    }

    public void AddSseClient(SseClient client)
    {
        _sseClients[client.ClientId] = client;
    }

    public void RemoveSseClient(string clientId)
    {
        _sseClients.TryRemove(clientId, out _);
    }

    public int ClientCount => _sseClients.Count;

    public void AddChat(string role, string message)
    {
        lock (_lock)
        {
            ChatLog.Add(new ChatEntry { Role = role, Message = message });
        }
    }

    public void PushSnapshot(string label)
    {
        lock (_lock)
        {
            History.Add(new StateSnapshot
            {
                Index = SnapshotCounter++,
                Label = label,
                State = State.Clone(),
            });
        }
    }

    // Broadcast an SSE event to all clients except the one that initiated it
    public async Task Broadcast(string eventType, object data, string? excludeClientId = null)
    {
        var json = JsonSerializer.Serialize(data);
        var deadClients = new List<string>();

        foreach (var (id, client) in _sseClients)
        {
            if (id == excludeClientId) continue;
            var ok = await client.SendEvent(eventType, json);
            if (!ok) deadClients.Add(id);
        }

        foreach (var id in deadClients)
            _sseClients.TryRemove(id, out _);
    }

    // Broadcast to ALL clients (including initiator) — for system events
    public async Task BroadcastAll(string eventType, object data)
    {
        await Broadcast(eventType, data, excludeClientId: null);
    }
}

// ── HTTP UI (PlatinumForge) ─────────────────────

public static class PlatinumForgeServer
{
    private static HttpListener? _listener;

    // Ψ logo SVG — ideas converging on a psi/tuning fork with swirling waves
    private const string PsiLogoSvg = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>
      <defs>
        <linearGradient id='psi-g1' x1='0%' y1='0%' x2='100%' y2='100%'><stop offset='0%' stop-color='#3b82f6'/><stop offset='100%' stop-color='#93c5fd'/></linearGradient>
        <linearGradient id='psi-g2' x1='0%' y1='0%' x2='100%' y2='100%'><stop offset='0%' stop-color='#60a5fa' stop-opacity='0.6'/><stop offset='100%' stop-color='#3b82f6' stop-opacity='0.15'/></linearGradient>
      </defs>
      <!-- outer swirling wave arcs -->
      <path d='M12 35 Q28 12, 50 26' stroke='url(#psi-g2)' stroke-width='2.5' fill='none' opacity='0.7'>
        <animate attributeName='d' values='M12 35 Q28 12, 50 26;M12 30 Q28 18, 50 26;M12 35 Q28 12, 50 26' dur='3s' repeatCount='indefinite'/></path>
      <path d='M88 35 Q72 12, 50 26' stroke='url(#psi-g2)' stroke-width='2.5' fill='none' opacity='0.7'>
        <animate attributeName='d' values='M88 35 Q72 12, 50 26;M88 30 Q72 18, 50 26;M88 35 Q72 12, 50 26' dur='3s' repeatCount='indefinite'/></path>
      <!-- inner swirling wave arcs -->
      <path d='M20 25 Q34 8, 50 20' stroke='url(#psi-g2)' stroke-width='1.8' fill='none' opacity='0.5'>
        <animate attributeName='d' values='M20 25 Q34 8, 50 20;M20 21 Q34 14, 50 20;M20 25 Q34 8, 50 20' dur='2.4s' repeatCount='indefinite'/></path>
      <path d='M80 25 Q66 8, 50 20' stroke='url(#psi-g2)' stroke-width='1.8' fill='none' opacity='0.5'>
        <animate attributeName='d' values='M80 25 Q66 8, 50 20;M80 21 Q66 14, 50 20;M80 25 Q66 8, 50 20' dur='2.4s' repeatCount='indefinite'/></path>
      <!-- idea dots converging from sides -->
      <circle r='2.5' fill='#93c5fd' opacity='0.8'><animateMotion dur='2s' repeatCount='indefinite' path='M-30 8 Q-10 -10, 0 0'/><set attributeName='cx' to='50'/><set attributeName='cy' to='28'/></circle>
      <circle r='2.5' fill='#93c5fd' opacity='0.8'><animateMotion dur='2s' repeatCount='indefinite' path='M30 8 Q10 -10, 0 0'/><set attributeName='cx' to='50'/><set attributeName='cy' to='28'/></circle>
      <circle r='2' fill='#60a5fa' opacity='0.6'><animateMotion dur='2.6s' repeatCount='indefinite' path='M-22 -2 Q-8 -12, 0 -4'/><set attributeName='cx' to='50'/><set attributeName='cy' to='24'/></circle>
      <circle r='2' fill='#60a5fa' opacity='0.6'><animateMotion dur='2.6s' repeatCount='indefinite' path='M22 -2 Q8 -12, 0 -4'/><set attributeName='cx' to='50'/><set attributeName='cy' to='24'/></circle>
      <!-- Ψ left prong -->
      <path d='M32 28 C32 42, 38 52, 50 58' stroke='url(#psi-g1)' stroke-width='4' fill='none' stroke-linecap='round'/>
      <!-- Ψ right prong -->
      <path d='M68 28 C68 42, 62 52, 50 58' stroke='url(#psi-g1)' stroke-width='4' fill='none' stroke-linecap='round'/>
      <!-- Ψ stem -->
      <line x1='50' y1='58' x2='50' y2='88' stroke='url(#psi-g1)' stroke-width='4' stroke-linecap='round'/>
      <!-- convergence glow at top -->
      <circle cx='50' cy='24' r='4' fill='#3b82f6' opacity='0.3'>
        <animate attributeName='r' values='3;5;3' dur='2s' repeatCount='indefinite'/>
        <animate attributeName='opacity' values='0.3;0.6;0.3' dur='2s' repeatCount='indefinite'/></circle>
      <!-- base pedestal -->
      <ellipse cx='50' cy='90' rx='10' ry='3' fill='url(#psi-g1)' opacity='0.4'/>
    </svg>";

    // Favicon — static Ψ (no animations for favicon)
    private const string FaviconLink = @"<link rel='icon' type='image/svg+xml' href=""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'%3E%3Cdefs%3E%3ClinearGradient id='fg' x1='0%25' y1='0%25' x2='100%25' y2='100%25'%3E%3Cstop offset='0%25' stop-color='%233b82f6'/%3E%3Cstop offset='100%25' stop-color='%2393c5fd'/%3E%3C/linearGradient%3E%3C/defs%3E%3Cpath d='M32 28 C32 42 38 52 50 58' stroke='url(%23fg)' stroke-width='5' fill='none' stroke-linecap='round'/%3E%3Cpath d='M68 28 C68 42 62 52 50 58' stroke='url(%23fg)' stroke-width='5' fill='none' stroke-linecap='round'/%3E%3Cline x1='50' y1='58' x2='50' y2='88' stroke='url(%23fg)' stroke-width='5' stroke-linecap='round'/%3E%3Ccircle cx='50' cy='24' r='4' fill='%233b82f6' opacity='0.5'/%3E%3Cellipse cx='50' cy='90' rx='10' ry='3' fill='%233b82f6' opacity='0.4'/%3E%3C/svg%3E"">";

    // Live sessions keyed by session ID (shared across users)
    private static readonly ConcurrentDictionary<string, LiveSession> _liveSessions = new();

    // Per-user metadata (manifest, active session pointer)
    private static readonly ConcurrentDictionary<string, UserMeta> _userMeta = new();

    public class UserMeta
    {
        public string Sub { get; }
        public SessionManifest Manifest { get; set; } = new();
        public string CurrentSessionId { get; set; } = "";

        private string SessionsDir => AuthManager.UserSessionsDir(Sub);
        private string ManifestFile => AuthManager.UserManifestFile(Sub);

        public UserMeta(string sub) { Sub = sub; }

        public string SessionStoreFile(string sessionId) =>
            Path.Combine(SessionsDir, sessionId, "store.json");

        public void EnsureDirs() => Directory.CreateDirectory(SessionsDir);

        public SessionManifest LoadManifest()
        {
            if (!File.Exists(ManifestFile)) return new SessionManifest();
            try
            {
                var json = File.ReadAllText(ManifestFile);
                return JsonSerializer.Deserialize<SessionManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SessionManifest();
            }
            catch { return new SessionManifest(); }
        }

        public void SaveManifest()
        {
            EnsureDirs();
            var json = JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ManifestFile, json);
        }

        public string CreateSession(string name)
        {
            EnsureDirs();
            var id = Guid.NewGuid().ToString();
            Manifest.Sessions.Add(new SessionInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"Session {Manifest.Sessions.Count + 1}" : name,
            });
            Directory.CreateDirectory(Path.Combine(SessionsDir, id));
            SaveManifest();
            return id;
        }

        public bool DeleteSession(string sessionId)
        {
            if (sessionId == CurrentSessionId) return false;
            var session = Manifest.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null) return false;
            Manifest.Sessions.Remove(session);
            SaveManifest();
            var dir = Path.Combine(SessionsDir, sessionId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }

        public void Init(SystemState seedState)
        {
            EnsureDirs();
            Manifest = LoadManifest();
            if (Manifest.Sessions.Count == 0)
            {
                var id = CreateSession("Default");
                CurrentSessionId = id;
                Manifest.ActiveSessionId = id;
                // Persist seed data
                CommitState(id, seedState);
                SaveManifest();
            }
            else
            {
                CurrentSessionId = Manifest.ActiveSessionId;
                if (string.IsNullOrEmpty(CurrentSessionId) || !Manifest.Sessions.Any(s => s.Id == CurrentSessionId))
                    CurrentSessionId = Manifest.Sessions[0].Id;
                Manifest.ActiveSessionId = CurrentSessionId;
                SaveManifest();
            }
        }

        public void CommitState(string sessionId, SystemState state)
        {
            EnsureDirs();
            var dir = Path.Combine(SessionsDir, sessionId);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "store.json");
            var data = new Dictionary<string, object>
            {
                ["projectName"] = state.ProjectName,
                ["version"] = state.Version,
                ["description"] = state.Description,
                ["personas"] = state.Personas,
                ["rules"] = state.Rules,
                ["invariants"] = state.Invariants,
                ["architecture"] = state.Architecture,
                ["dataflow"] = state.Dataflow,
                ["frameworks"] = state.Frameworks,
                ["language"] = state.Language,
                ["deployment"] = state.Deployment,
                ["features"] = state.Features,
                ["stories"] = state.Stories,
                ["nfr"] = state.NFR,
                ["sliders"] = state.Sliders,
                ["tests"] = state.UnitTests,
                ["interfaces"] = state.Interfaces,
                ["code"] = state.Code,
                ["nfrTests"] = state.NfrTests,
                ["soakTests"] = state.SoakTests,
                ["integrationTests"] = state.IntegrationTests,
                ["architectureTweaks"] = state.ArchitectureTweaks,
                ["codeTweaks"] = state.CodeTweaks,
                ["testTweaks"] = state.TestTweaks,
                ["iac"] = state.IaC,
                ["deployTweaks"] = state.DeployTweaks,
                ["pipelineConfig"] = state.PipelineConfig,
                ["committedAt"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            var session = Manifest.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session != null) session.UpdatedAt = DateTime.UtcNow.ToString("o");
            SaveManifest();
        }

        public SystemState? LoadState(string sessionId)
        {
            var file = SessionStoreFile(sessionId);
            if (!File.Exists(file)) return null;
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var s = new SystemState();
                if (root.TryGetProperty("projectName", out var pn)) s.ProjectName = pn.GetString() ?? "untitled";
                if (root.TryGetProperty("version", out var ver)) s.Version = ver.GetString() ?? "0.1.0";
                if (root.TryGetProperty("description", out var desc)) s.Description = JsonToDict(desc);
                if (root.TryGetProperty("personas", out var p)) s.Personas = JsonToDict(p);
                if (root.TryGetProperty("rules", out var r)) s.Rules = JsonToDict(r);
                if (root.TryGetProperty("invariants", out var inv)) s.Invariants = JsonToDict(inv);
                if (root.TryGetProperty("architecture", out var a)) s.Architecture = JsonToDict(a);
                if (root.TryGetProperty("dataflow", out var df)) s.Dataflow = JsonToDict(df);
                if (root.TryGetProperty("frameworks", out var fw)) s.Frameworks = JsonToDict(fw);
                if (root.TryGetProperty("language", out var lang)) s.Language = JsonToDict(lang);
                if (root.TryGetProperty("deployment", out var dep)) s.Deployment = JsonToDict(dep);
                if (root.TryGetProperty("features", out var feat)) s.Features = JsonToDict(feat);
                if (root.TryGetProperty("stories", out var st)) s.Stories = JsonToDict(st);
                if (root.TryGetProperty("nfr", out var n)) s.NFR = JsonToDict(n);
                if (root.TryGetProperty("sliders", out var sl)) s.Sliders = JsonToIntDict(sl);
                if (root.TryGetProperty("tests", out var t)) s.UnitTests = JsonToDict(t);
                if (root.TryGetProperty("interfaces", out var ifc)) s.Interfaces = JsonToDict(ifc);
                if (root.TryGetProperty("code", out var c)) s.Code = JsonToDict(c);
                if (root.TryGetProperty("nfrTests", out var nfrT)) s.NfrTests = JsonToDict(nfrT);
                if (root.TryGetProperty("soakTests", out var soakT)) s.SoakTests = JsonToDict(soakT);
                if (root.TryGetProperty("integrationTests", out var intT)) s.IntegrationTests = JsonToDict(intT);
                if (root.TryGetProperty("architectureTweaks", out var at)) s.ArchitectureTweaks = at.GetString() ?? "";
                if (root.TryGetProperty("codeTweaks", out var ct)) s.CodeTweaks = JsonToDict(ct);
                if (root.TryGetProperty("testTweaks", out var tt)) s.TestTweaks = JsonToDict(tt);
                if (root.TryGetProperty("iac", out var iac)) s.IaC = JsonToDict(iac);
                if (root.TryGetProperty("deployTweaks", out var dt)) s.DeployTweaks = JsonToDict(dt);
                if (root.TryGetProperty("pipelineConfig", out var pc)) s.PipelineConfig = JsonToBoolDict(pc);
                return s;
            }
            catch { return null; }
        }

        // Add a shared session reference (points to another user's session)
        public void AddSharedSession(string sessionId, string name, string ownerSub)
        {
            if (Manifest.Sessions.Any(s => s.Id == sessionId)) return; // already have it
            Manifest.Sessions.Add(new SessionInfo
            {
                Id = sessionId,
                Name = name,
                SharedFromSub = ownerSub,
            });
            SaveManifest();
        }

        private static Dictionary<string, string> JsonToDict(JsonElement el)
        {
            var d = new Dictionary<string, string>();
            foreach (var prop in el.EnumerateObject())
                d[prop.Name] = prop.Value.GetString() ?? "";
            return d;
        }

        private static Dictionary<string, int> JsonToIntDict(JsonElement el)
        {
            var d = new Dictionary<string, int>();
            foreach (var prop in el.EnumerateObject())
                d[prop.Name] = prop.Value.TryGetInt32(out var v) ? v : 50;
            return d;
        }

        private static Dictionary<string, bool> JsonToBoolDict(JsonElement el)
        {
            var d = new Dictionary<string, bool>();
            foreach (var prop in el.EnumerateObject())
                d[prop.Name] = prop.Value.ValueKind == JsonValueKind.True;
            return d;
        }
    }

    private static UserMeta GetOrCreateUser(string sub)
    {
        return _userMeta.GetOrAdd(sub, s => new UserMeta(s));
    }

    private static readonly SystemState _seedState = Program.SeedState();

    private static void EnsureUserInit(UserMeta meta)
    {
        if (string.IsNullOrEmpty(meta.CurrentSessionId))
            meta.Init(_seedState.Clone());
    }

    // Get or create a LiveSession. Loads from the owner's disk if not yet active.
    private static LiveSession GetLiveSession(string sessionId, UserMeta requestingUser)
    {
        return _liveSessions.GetOrAdd(sessionId, id =>
        {
            // Determine who owns this session on disk
            var ownerSub = requestingUser.Sub;
            var sessionInfo = requestingUser.Manifest.Sessions.FirstOrDefault(s => s.Id == id);
            if (sessionInfo?.SharedFromSub != null)
                ownerSub = sessionInfo.SharedFromSub;

            var live = new LiveSession(id, ownerSub);

            // Try load from owner's disk
            var ownerMeta = GetOrCreateUser(ownerSub);
            EnsureUserInit(ownerMeta);
            var loaded = ownerMeta.LoadState(id);
            if (loaded != null)
            {
                live.State = loaded;
                live.CurrentSource = CodeCompiler.BuildFullSource(loaded);
            }

            live.PushSnapshot("Session loaded");
            live.AddChat("system", $"📂 Session active");
            return live;
        });
    }

    // Get the active LiveSession for a user
    private static LiveSession GetActiveLive(UserMeta meta)
    {
        return GetLiveSession(meta.CurrentSessionId, meta);
    }

    // Switch active session for a user
    private static void SwitchUserSession(UserMeta meta, string sessionId)
    {
        // Commit current live session to disk
        CommitLiveToDisk(meta);

        meta.CurrentSessionId = sessionId;
        meta.Manifest.ActiveSessionId = sessionId;
        meta.SaveManifest();
    }

    private static string CommitLiveToDisk(UserMeta meta)
    {
        var live = GetActiveLive(meta);
        // Persist to the owner's storage
        var ownerSub = live.OwnerSub;
        var ownerMeta = GetOrCreateUser(ownerSub);
        EnsureUserInit(ownerMeta);
        ownerMeta.CommitState(live.SessionId, live.State);
        return ownerMeta.SessionStoreFile(live.SessionId);
    }

    public static void Start()
    {
        AuthManager.LoadShareTokens();
        var port = Environment.GetEnvironmentVariable("PORT")
            ?? Environment.GetEnvironmentVariable("WEBSITES_PORT")
            ?? "5005";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _listener.Start();
        Console.WriteLine($"  → WaveFunctionLabs / PlatinumForge UI at http://localhost:{port} (all interfaces)");
        Console.WriteLine($"  → Data directory: {AuthManager.DataRoot}");
        if (AuthManager.IsConfigured)
        {
            var providers = string.Join(", ", AuthManager.ConfiguredProviders.Keys);
            Console.WriteLine($"  → OAuth providers enabled: {providers}");
        }
        else
            Console.WriteLine("  ⚠ No OAuth providers configured — auth disabled (open access)");
        Task.Run(Listen);
    }

    private static async Task Listen()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleRequest(ctx); // Don't await — allow concurrent SSE + normal requests
            }
            catch { /* listener stopped */ }
        }
    }

    // Helper to extract clientId from request header
    private static string GetClientId(HttpListenerRequest req) =>
        req.Headers["X-Client-Id"] ?? "";

    private static async Task HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;
        string body;
        string contentType = "application/json";

        try
        {
            // ── Auth endpoints (no auth required) ──

            if (path == "/auth/login" || path.StartsWith("/auth/login/"))
            {
                if (!AuthManager.IsConfigured)
                {
                    SetAuthCookie(ctx.Response, "local");
                    ctx.Response.Redirect("/");
                    ctx.Response.Close();
                    return;
                }
                var provider = path.Length > "/auth/login/".Length
                    ? path.Substring("/auth/login/".Length)
                    : AuthManager.ConfiguredProviders.Keys.First();
                var url = AuthManager.GetLoginUrl(provider);
                if (url == null) { ctx.Response.Redirect("/"); ctx.Response.Close(); return; }
                ctx.Response.Redirect(url);
                ctx.Response.Close();
                return;
            }
            else if (path.StartsWith("/auth/callback/"))
            {
                var provider = path.Substring("/auth/callback/".Length);
                var qs = ctx.Request.QueryString;
                var code = qs["code"];
                if (string.IsNullOrEmpty(code))
                {
                    body = "Missing authorization code";
                    contentType = "text/plain";
                    await WriteResponse(ctx, body, contentType, 400);
                    return;
                }
                var profile = await AuthManager.HandleCallback(provider, code);
                if (profile == null)
                {
                    body = "Authentication failed";
                    contentType = "text/plain";
                    await WriteResponse(ctx, body, contentType, 401);
                    return;
                }
                SetAuthCookie(ctx.Response, profile.Sub);
                var um = GetOrCreateUser(profile.Sub);
                EnsureUserInit(um);
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                return;
            }
            else if (path == "/auth/callback")
            {
                // Legacy Google-only callback (redirect to provider-specific)
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                return;
            }
            else if (path == "/auth/logout")
            {
                ctx.Response.SetCookie(new Cookie("platinumforge_auth", "") { Expires = DateTime.UtcNow.AddDays(-1), Path = "/" });
                ctx.Response.Redirect("/auth/login");
                ctx.Response.Close();
                return;
            }
            else if (path.StartsWith("/share/"))
            {
                var token = path.Substring("/share/".Length);
                var sub = AuthManager.GetSubFromRequest(ctx.Request);
                if (sub == null && AuthManager.IsConfigured)
                {
                    ctx.Response.Redirect($"/auth/login");
                    ctx.Response.Close();
                    return;
                }
                sub ??= "local";
                var share = AuthManager.GetShareToken(token);
                if (share == null)
                {
                    await WriteResponse(ctx, "Invalid share link", "text/plain", 404);
                    return;
                }
                // Add the SAME session to this user's manifest (not a copy)
                var ownerMeta = GetOrCreateUser(share.OwnerSub);
                EnsureUserInit(ownerMeta);
                var ownerSession = ownerMeta.Manifest.Sessions.FirstOrDefault(s => s.Id == share.SessionId);
                var sessionName = ownerSession?.Name ?? "Shared session";

                var userMeta = GetOrCreateUser(sub);
                EnsureUserInit(userMeta);
                userMeta.AddSharedSession(share.SessionId, sessionName, share.OwnerSub);
                SwitchUserSession(userMeta, share.SessionId);
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                return;
            }

            // ── Auth gate for everything else ──

            var userSub = AuthManager.GetSubFromRequest(ctx.Request);
            if (userSub == null && !AuthManager.IsConfigured)
                userSub = "local";
            if (userSub == null)
            {
                if (path.StartsWith("/api/"))
                {
                    body = JsonSerializer.Serialize(new { error = "unauthorized" });
                    await WriteResponse(ctx, body, contentType, 401);
                    return;
                }
                body = LoginPage();
                contentType = "text/html";
                await WriteResponse(ctx, body, contentType);
                return;
            }

            var meta = GetOrCreateUser(userSub);
            EnsureUserInit(meta);
            var live = GetActiveLive(meta);
            var clientId = GetClientId(ctx.Request);

            // ── SSE endpoint (long-lived) ──

            if (path == "/api/events" && method == "GET")
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.Add("Cache-Control", "no-cache");
                ctx.Response.Headers.Add("Connection", "keep-alive");
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

                var sseClientId = ctx.Request.QueryString["clientId"] ?? Guid.NewGuid().ToString("N");
                var sseClient = new SseClient(sseClientId, userSub, ctx.Response);
                live.AddSseClient(sseClient);

                // Send initial full-sync
                await sseClient.SendEvent("full-sync", JsonSerializer.Serialize(new
                {
                    state = new {
                        projectName = live.State.ProjectName,
                        version = live.State.Version,
                        description = live.State.Description,
                        personas = live.State.Personas,
                        rules = live.State.Rules,
                        invariants = live.State.Invariants,
                        architecture = live.State.Architecture,
                        dataflow = live.State.Dataflow,
                        frameworks = live.State.Frameworks,
                        language = live.State.Language,
                        deployment = live.State.Deployment,
                        features = live.State.Features,
                        stories = live.State.Stories,
                        nfr = live.State.NFR,
                        sliders = live.State.Sliders,
                        tests = live.State.UnitTests,
                        interfaces = live.State.Interfaces,
                        code = live.State.Code,
                        nfrTests = live.State.NfrTests,
                        soakTests = live.State.SoakTests,
                        integrationTests = live.State.IntegrationTests,
                        architectureTweaks = live.State.ArchitectureTweaks,
                        codeTweaks = live.State.CodeTweaks,
                        testTweaks = live.State.TestTweaks,
                        iac = live.State.IaC,
                        deployTweaks = live.State.DeployTweaks, pipelineConfig = live.State.PipelineConfig,
                    },
                    code = live.CurrentSource,
                    generating = live.Generating,
                    clients = live.ClientCount,
                }));

                // Keep alive until client disconnects
                try
                {
                    while (!sseClient.Cts.IsCancellationRequested)
                    {
                        await Task.Delay(15000, sseClient.Cts.Token);
                        // Heartbeat
                        if (!await sseClient.SendEvent("ping", $"{{\"clients\":{live.ClientCount}}}"))
                            break;
                    }
                }
                catch { }
                finally
                {
                    live.RemoveSseClient(sseClientId);
                    try { ctx.Response.Close(); } catch { }
                }
                return;
            }

            // ── API endpoints (authenticated) ──

            if (path == "/api/me" && method == "GET")
            {
                var profile = AuthManager.LoadUserProfile(userSub);
                body = JsonSerializer.Serialize(new
                {
                    sub = userSub,
                    email = profile?.Email ?? "",
                    name = profile?.Name ?? (userSub == "local" ? "Local User" : ""),
                    picture = profile?.Picture ?? "",
                    authEnabled = AuthManager.IsConfigured,
                });
            }
            else if (path == "/api/code" && method == "GET")
            {
                body = JsonSerializer.Serialize(new { code = live.CurrentSource });
            }
            else if (path == "/api/state" && method == "GET")
            {
                body = JsonSerializer.Serialize(new
                {
                    projectName = live.State.ProjectName,
                    version = live.State.Version,
                    description = live.State.Description,
                    personas = live.State.Personas,
                    rules = live.State.Rules,
                    invariants = live.State.Invariants,
                    architecture = live.State.Architecture,
                    dataflow = live.State.Dataflow,
                    frameworks = live.State.Frameworks,
                    language = live.State.Language,
                    deployment = live.State.Deployment,
                    features = live.State.Features,
                    stories = live.State.Stories,
                    nfr = live.State.NFR,
                    sliders = live.State.Sliders,
                    tests = live.State.UnitTests,
                    interfaces = live.State.Interfaces,
                    code = live.State.Code,
                    nfrTests = live.State.NfrTests,
                    soakTests = live.State.SoakTests,
                    integrationTests = live.State.IntegrationTests,
                        architectureTweaks = live.State.ArchitectureTweaks,
                        codeTweaks = live.State.CodeTweaks,
                        testTweaks = live.State.TestTweaks,
                        iac = live.State.IaC,
                        deployTweaks = live.State.DeployTweaks, pipelineConfig = live.State.PipelineConfig,
                });
            }
            else if (path == "/api/state" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var root = doc.RootElement;
                var delta = new Dictionary<string, object>();
                if (root.TryGetProperty("projectName", out var pn)) { live.State.ProjectName = pn.GetString() ?? "untitled"; delta["projectName"] = live.State.ProjectName; }
                if (root.TryGetProperty("version", out var ver)) { live.State.Version = ver.GetString() ?? "0.1.0"; delta["version"] = live.State.Version; }
                if (root.TryGetProperty("description", out var desc)) { live.State.Description = JsonToDict(desc); delta["description"] = live.State.Description; }
                if (root.TryGetProperty("personas", out var p)) { live.State.Personas = JsonToDict(p); delta["personas"] = live.State.Personas; }
                if (root.TryGetProperty("rules", out var r)) { live.State.Rules = JsonToDict(r); delta["rules"] = live.State.Rules; }
                if (root.TryGetProperty("invariants", out var inv)) { live.State.Invariants = JsonToDict(inv); delta["invariants"] = live.State.Invariants; }
                if (root.TryGetProperty("architecture", out var a)) { live.State.Architecture = JsonToDict(a); delta["architecture"] = live.State.Architecture; }
                if (root.TryGetProperty("dataflow", out var df)) { live.State.Dataflow = JsonToDict(df); delta["dataflow"] = live.State.Dataflow; }
                if (root.TryGetProperty("frameworks", out var fw)) { live.State.Frameworks = JsonToDict(fw); delta["frameworks"] = live.State.Frameworks; }
                if (root.TryGetProperty("language", out var lang)) { live.State.Language = JsonToDict(lang); delta["language"] = live.State.Language; }
                if (root.TryGetProperty("deployment", out var dep)) { live.State.Deployment = JsonToDict(dep); delta["deployment"] = live.State.Deployment; }
                if (root.TryGetProperty("features", out var feat)) { live.State.Features = JsonToDict(feat); delta["features"] = live.State.Features; }
                if (root.TryGetProperty("nfr", out var n)) { live.State.NFR = JsonToDict(n); delta["nfr"] = live.State.NFR; }
                if (root.TryGetProperty("stories", out var st)) { live.State.Stories = JsonToDict(st); delta["stories"] = live.State.Stories; }
                if (root.TryGetProperty("sliders", out var sli)) { live.State.Sliders = JsonToIntDict(sli); delta["sliders"] = live.State.Sliders; }
                if (root.TryGetProperty("architectureTweaks", out var at)) { live.State.ArchitectureTweaks = at.GetString() ?? ""; delta["architectureTweaks"] = live.State.ArchitectureTweaks; }
                if (root.TryGetProperty("codeTweaks", out var ctw)) { live.State.CodeTweaks = JsonToDict(ctw); delta["codeTweaks"] = live.State.CodeTweaks; }
                if (root.TryGetProperty("testTweaks", out var ttw)) { live.State.TestTweaks = JsonToDict(ttw); delta["testTweaks"] = live.State.TestTweaks; }
                if (root.TryGetProperty("iac", out var iac)) { live.State.IaC = JsonToDict(iac); delta["iac"] = live.State.IaC; }
                if (root.TryGetProperty("deployTweaks", out var dtw)) { live.State.DeployTweaks = JsonToDict(dtw); delta["deployTweaks"] = live.State.DeployTweaks; }
                if (root.TryGetProperty("pipelineConfig", out var pcfg)) { live.State.PipelineConfig = JsonToBoolDict(pcfg); delta["pipelineConfig"] = live.State.PipelineConfig; }
                live.AddChat("system", "🔧 Constraints updated");
                _ = live.Broadcast("state", delta, clientId);
                _ = live.Broadcast("chat", new { role = "system", message = "🔧 Constraints updated" }, clientId);
                // Auto-save to disk
                if (meta != null) meta.CommitState(live.SessionId, live.State);
                body = JsonSerializer.Serialize(new { ok = true });
            }
            else if (path == "/api/prompt" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
                if (live.Generating)
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Generation already in progress" });
                }
                else
                {
                    live.AddChat("user", prompt);
                    _ = live.BroadcastAll("chat", new { role = "user", message = prompt });
                    _ = Task.Run(() => RunGeneration(live, prompt, userSub));
                    body = JsonSerializer.Serialize(new { ok = true, message = "Generation started" });
                }
            }
            else if (path == "/api/history" && method == "GET")
            {
                body = JsonSerializer.Serialize(live.History.Select(h => new
                {
                    h.Index, h.Label, h.Timestamp
                }));
            }
            else if (path == "/api/revert" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var idx = doc.RootElement.GetProperty("index").GetInt32();
                var snapshot = live.History.FirstOrDefault(h => h.Index == idx);
                if (snapshot != null)
                {
                    live.State = snapshot.State.Clone();
                    live.CurrentSource = CodeCompiler.BuildFullSource(live.State);
                    live.AddChat("system", $"↩ Reverted to snapshot #{idx}: {snapshot.Label}");
                    _ = live.Broadcast("full-sync", new {
                        state = new {
                            projectName = live.State.ProjectName, version = live.State.Version,
                            description = live.State.Description, personas = live.State.Personas,
                            rules = live.State.Rules, invariants = live.State.Invariants,
                            architecture = live.State.Architecture, dataflow = live.State.Dataflow,
                            frameworks = live.State.Frameworks, language = live.State.Language,
                            deployment = live.State.Deployment,
                            features = live.State.Features, nfr = live.State.NFR, stories = live.State.Stories,
                            sliders = live.State.Sliders,
                            tests = live.State.UnitTests, interfaces = live.State.Interfaces,
                            code = live.State.Code,
                            nfrTests = live.State.NfrTests, soakTests = live.State.SoakTests,
                            integrationTests = live.State.IntegrationTests,
                        architectureTweaks = live.State.ArchitectureTweaks,
                        codeTweaks = live.State.CodeTweaks,
                        testTweaks = live.State.TestTweaks,
                        iac = live.State.IaC,
                        deployTweaks = live.State.DeployTweaks, pipelineConfig = live.State.PipelineConfig,
                        },
                        code = live.CurrentSource, generating = live.Generating,
                    }, clientId);
                    body = JsonSerializer.Serialize(new { ok = true });
                }
                else
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Snapshot not found" });
                }
            }
            else if (path == "/api/chat" && method == "GET")
            {
                body = JsonSerializer.Serialize(live.ChatLog);
            }
            else if (path == "/api/chat/send" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var message = doc.RootElement.GetProperty("message").GetString() ?? "";
                live.AddChat("user", message);
                _ = live.BroadcastAll("chat", new { role = "user", message });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stateJson = JsonSerializer.Serialize(new {
                            description = live.State.Description, personas = live.State.Personas,
                            rules = live.State.Rules, invariants = live.State.Invariants,
                            architecture = live.State.Architecture, dataflow = live.State.Dataflow,
                            frameworks = live.State.Frameworks, language = live.State.Language,
                            deployment = live.State.Deployment,
                            features = live.State.Features, nfr = live.State.NFR, stories = live.State.Stories,
                            codeTweaks = live.State.CodeTweaks, testTweaks = live.State.TestTweaks,
                            iac = live.State.IaC, deployTweaks = live.State.DeployTweaks, pipelineConfig = live.State.PipelineConfig,
                            architectureTweaks = live.State.ArchitectureTweaks,
                        });
                        var chatHistory = string.Join("\n", live.ChatLog.TakeLast(20).Select(c => $"[{c.Role}] {c.Message}"));
                        var response = await OpenAIClient.Complete(
                            """
                            You are a collaborative design agent for PlatinumForge, an autonomous software generation platform.
                            You help users refine their project specification across these layers:
                            description, personas, rules, invariants, architecture, dataflow, frameworks, language,
                            deployment, features, stories, nfr, codeTweaks, testTweaks, iac, deployTweaks, architectureTweaks.

                            When you want to suggest a change, include a JSON block in your response wrapped in ```actions ... ```.
                            The JSON is an array of action objects:
                            [
                              {"label": "human-readable description", "layer": "rules", "op": "set", "key": "no-nulls", "value": "Public methods must never return null"},
                              {"label": "Remove old rule", "layer": "rules", "op": "remove", "key": "outdated-rule", "value": ""},
                              {"label": "Set architecture note", "layer": "architectureTweaks", "op": "set-string", "key": "", "value": "Use vertical slice architecture"}
                            ]
                            ops: "set" = add/update a key-value pair, "remove" = delete a key, "set-string" = set a string-valued layer (architectureTweaks).

                            Your conversational text goes OUTSIDE the actions block. Be helpful, concise, and opinionated.
                            If the user is just chatting or asking questions, respond without actions.
                            Always explain WHY you suggest each change.
                            """,
                            $"""
                            Current project state:
                            {stateJson}

                            Recent chat:
                            {chatHistory}

                            User says: {message}
                            """);

                        // Parse response: extract actions block if present
                        List<ProposedAction>? actions = null;
                        var conversational = response;
                        var actionsMatch = System.Text.RegularExpressions.Regex.Match(response, @"```actions\s*([\s\S]*?)```");
                        if (actionsMatch.Success)
                        {
                            conversational = response[..actionsMatch.Index].TrimEnd() +
                                response[(actionsMatch.Index + actionsMatch.Length)..].TrimStart();
                            try
                            {
                                var actionsJson = actionsMatch.Groups[1].Value.Trim();
                                var parsed = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(actionsJson);
                                if (parsed != null)
                                {
                                    actions = parsed.Select(a => new ProposedAction
                                    {
                                        Label = a.GetValueOrDefault("label", ""),
                                        Layer = a.GetValueOrDefault("layer", ""),
                                        Op = a.GetValueOrDefault("op", "set"),
                                        Key = a.GetValueOrDefault("key", ""),
                                        Value = a.GetValueOrDefault("value", ""),
                                    }).ToList();
                                }
                            }
                            catch { /* actions parse failed, just show text */ }
                        }

                        var entry = new ChatEntry { Role = "agent", Message = conversational.Trim(), Actions = actions };
                        live.ChatLog.Add(entry);
                        _ = live.BroadcastAll("chat", new
                        {
                            role = "agent",
                            message = entry.Message,
                            actions = actions?.Select(a => new { a.Id, a.Label, a.Layer, a.Op, a.Key, a.Value, a.Applied }),
                        });
                    }
                    catch (Exception ex)
                    {
                        await BroadcastChat(live, "error", $"Agent error: {ex.Message}");
                    }
                });
                body = JsonSerializer.Serialize(new { ok = true });
            }
            else if (path == "/api/chat/apply" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var actionId = doc.RootElement.GetProperty("actionId").GetString() ?? "";

                // Find the action in chat history
                ProposedAction? action = null;
                foreach (var entry in live.ChatLog)
                {
                    if (entry.Actions == null) continue;
                    action = entry.Actions.FirstOrDefault(a => a.Id == actionId);
                    if (action != null) break;
                }

                if (action == null)
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Action not found" });
                }
                else if (action.Applied)
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Action already applied" });
                }
                else
                {
                    // Apply the action
                    var delta = new Dictionary<string, object>();
                    var layer = action.Layer;
                    var op = action.Op;
                    var key = action.Key;
                    var value = action.Value;

                    if (op == "set-string" && layer == "architectureTweaks")
                    {
                        live.State.ArchitectureTweaks = value;
                        delta["architectureTweaks"] = value;
                    }
                    else
                    {
                        // Get the target dictionary
                        var dict = layer switch
                        {
                            "description" => live.State.Description, "personas" => live.State.Personas,
                            "rules" => live.State.Rules, "invariants" => live.State.Invariants,
                            "architecture" => live.State.Architecture, "dataflow" => live.State.Dataflow,
                            "frameworks" => live.State.Frameworks, "language" => live.State.Language,
                            "deployment" => live.State.Deployment,
                            "features" => live.State.Features, "nfr" => live.State.NFR,
                            "stories" => live.State.Stories,
                            "codeTweaks" => live.State.CodeTweaks, "testTweaks" => live.State.TestTweaks,
                            "iac" => live.State.IaC, "deployTweaks" => live.State.DeployTweaks,
                            _ => null,
                        };

                        if (dict != null)
                        {
                            if (op == "set") dict[key] = value;
                            else if (op == "remove") dict.Remove(key);
                            else if (op == "replace") dict[key] = value;
                            delta[layer] = dict;
                        }
                    }

                    action.Applied = true;
                    live.AddChat("success", $"✅ Applied: {action.Label}");
                    _ = live.BroadcastAll("state", delta);
                    _ = live.BroadcastAll("chat", new { role = "success", message = $"✅ Applied: {action.Label}", appliedActionId = actionId });
                    body = JsonSerializer.Serialize(new { ok = true, applied = action.Label });
                }
            }
            else if (path == "/api/generating" && method == "GET")
            {
                body = JsonSerializer.Serialize(new { generating = live.Generating });
            }
            else if (path == "/api/enrich" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var layer = doc.RootElement.GetProperty("layer").GetString() ?? "";
                var isStringLayer = doc.RootElement.TryGetProperty("isString", out var isl) && isl.GetBoolean();

                // Build full spec context
                var specGraph = JsonSerializer.Serialize(new {
                    description = live.State.Description, personas = live.State.Personas,
                    rules = live.State.Rules, invariants = live.State.Invariants,
                    architecture = live.State.Architecture, dataflow = live.State.Dataflow,
                    frameworks = live.State.Frameworks, language = live.State.Language,
                    deployment = live.State.Deployment, features = live.State.Features,
                    stories = live.State.Stories, nfr = live.State.NFR,
                }, new JsonSerializerOptions { WriteIndented = true });

                string currentContent;
                if (isStringLayer)
                {
                    currentContent = layer switch {
                        "architectureTweaks" => live.State.ArchitectureTweaks,
                        _ => ""
                    };
                }
                else
                {
                    var dict = layer switch {
                        "description" => live.State.Description, "personas" => live.State.Personas,
                        "rules" => live.State.Rules, "invariants" => live.State.Invariants,
                        "architecture" => live.State.Architecture, "dataflow" => live.State.Dataflow,
                        "frameworks" => live.State.Frameworks, "language" => live.State.Language,
                        "deployment" => live.State.Deployment, "features" => live.State.Features,
                        "stories" => live.State.Stories, "nfr" => live.State.NFR,
                        "codeTweaks" => live.State.CodeTweaks, "testTweaks" => live.State.TestTweaks,
                        "iac" => live.State.IaC, "deployTweaks" => live.State.DeployTweaks,
                        _ => null
                    };
                    currentContent = dict != null ? JsonSerializer.Serialize(dict) : "{}";
                }

                try
                {
                    var enriched = await OpenAIClient.Complete(
                        """
                        You are a product design enrichment assistant for PlatinumForge by WaveFunctionLabs.
                        Your job is to take rough, half-formed ideas and sharpen them into clear, actionable specifications.

                        Rules:
                        - Expand vague ideas into specific, concrete items
                        - Split compound ideas into separate well-defined entries
                        - Add missing considerations the user likely hasn't thought of
                        - Improve wording to be precise and unambiguous
                        - Preserve the user's intent — enhance, don't replace
                        - Keep each value concise (1-3 sentences max)
                        - Output ONLY valid JSON, no markdown fences, no explanation
                        """,
                        $"""
                        Full project specification for context:
                        {specGraph}

                        The user wants to enrich the "{layer}" layer.
                        Current content of this layer:
                        {currentContent}

                        {(isStringLayer
                            ? "This is a free-text field. Return a JSON object: {\"value\": \"enriched text here\"}"
                            : "This is a key-value dictionary. Return a JSON object with enriched/expanded key-value pairs. Keep existing good entries, improve weak ones, split compound ones, and add missing entries that make sense for this project."
                        )}
                        """);

                    var stripped = Generator.StripMarkdownFences(enriched);
                    body = JsonSerializer.Serialize(new { ok = true, result = stripped, layer });
                }
                catch (Exception ex)
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                }
            }
            else if (path == "/api/commit" && method == "POST")
            {
                var file = CommitLiveToDisk(meta);
                live.AddChat("success", $"💾 State committed to disk");
                _ = live.BroadcastAll("chat", new { role = "success", message = "💾 State committed to disk" });
                body = JsonSerializer.Serialize(new { ok = true, path = file });
            }
            else if (path == "/api/sessions" && method == "GET")
            {
                body = JsonSerializer.Serialize(new
                {
                    activeId = meta.CurrentSessionId,
                    sessions = meta.Manifest.Sessions.Select(s => new
                    {
                        s.Id, s.Name, s.CreatedAt, s.UpdatedAt,
                        active = s.Id == meta.CurrentSessionId,
                        shared = s.SharedFromSub != null,
                        clients = _liveSessions.TryGetValue(s.Id, out var ls) ? ls.ClientCount : 0,
                    })
                });
            }
            else if (path == "/api/sessions" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var name = doc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                var id = meta.CreateSession(name);
                SwitchUserSession(meta, id);
                body = JsonSerializer.Serialize(new { ok = true, id, name = meta.Manifest.Sessions.Last().Name });
            }
            else if (path == "/api/sessions/switch" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                if (meta.Manifest.Sessions.Any(s => s.Id == id))
                {
                    SwitchUserSession(meta, id);
                    body = JsonSerializer.Serialize(new { ok = true, id });
                }
                else
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Session not found" });
                }
            }
            else if (path == "/api/sessions/rename" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                var name = doc.RootElement.GetProperty("name").GetString() ?? "";
                var session = meta.Manifest.Sessions.FirstOrDefault(s => s.Id == id);
                if (session != null)
                {
                    session.Name = name;
                    meta.SaveManifest();
                    body = JsonSerializer.Serialize(new { ok = true });
                }
                else
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Session not found" });
                }
            }
            else if (path.StartsWith("/api/sessions/delete/") && method == "POST")
            {
                var id = path.Substring("/api/sessions/delete/".Length);
                if (meta.DeleteSession(id))
                {
                    live.AddChat("system", $"🗑 Session deleted");
                    body = JsonSerializer.Serialize(new { ok = true });
                }
                else
                {
                    body = JsonSerializer.Serialize(new { ok = false, error = "Cannot delete active session or session not found" });
                }
            }
            else if (path == "/api/sessions/share" && method == "POST")
            {
                var reqBody = await ReadBody(ctx.Request);
                var doc = JsonDocument.Parse(reqBody);
                var sessionId = doc.RootElement.TryGetProperty("sessionId", out var sid)
                    ? sid.GetString() ?? meta.CurrentSessionId
                    : meta.CurrentSessionId;
                CommitLiveToDisk(meta);
                var token = AuthManager.CreateShareToken(sessionId, userSub);
                var baseUrl = (Environment.GetEnvironmentVariable("PLATINUMFORGE_BASE_URL") ?? "http://localhost:5005").TrimEnd('/');
                var shareUrl = $"{baseUrl}/share/{token}";
                live.AddChat("success", $"🔗 Share link created");
                _ = live.BroadcastAll("chat", new { role = "success", message = "🔗 Share link created — collaborators will join this session" });
                body = JsonSerializer.Serialize(new { ok = true, token, url = shareUrl });
            }
            else if (path == "/api/store/tree" && method == "GET")
            {
                // Only show generated artifacts, not configuration layers
                var tree = new Dictionary<string, List<string>>();
                if (live.State.Interfaces.Count > 0) tree["Interfaces"] = live.State.Interfaces.Keys.ToList();
                if (live.State.UnitTests.Count > 0) tree["UnitTests"] = live.State.UnitTests.Keys.ToList();
                if (live.State.Code.Count > 0) tree["Code"] = live.State.Code.Keys.ToList();
                if (live.State.NfrTests.Count > 0) tree["NfrTests"] = live.State.NfrTests.Keys.ToList();
                if (live.State.SoakTests.Count > 0) tree["SoakTests"] = live.State.SoakTests.Keys.ToList();
                if (live.State.IntegrationTests.Count > 0) tree["IntegrationTests"] = live.State.IntegrationTests.Keys.ToList();
                if (live.State.IaC.Count > 0) tree["IaC"] = live.State.IaC.Keys.ToList();
                body = JsonSerializer.Serialize(tree);
            }
            else if (path == "/api/store/file" && method == "GET")
            {
                var qs = ctx.Request.QueryString;
                var layer = qs["layer"] ?? "";
                var key = qs["key"] ?? "";
                var dict = layer.ToLowerInvariant() switch
                {
                    "description" => live.State.Description,
                    "personas" => live.State.Personas,
                    "rules" => live.State.Rules,
                    "invariants" => live.State.Invariants,
                    "architecture" => live.State.Architecture,
                    "dataflow" => live.State.Dataflow,
                    "frameworks" => live.State.Frameworks,
                    "language" => live.State.Language,
                    "deployment" => live.State.Deployment,
                    "features" => live.State.Features,
                    "nfr" => live.State.NFR,
                    "stories" => live.State.Stories,
                    "tests" or "unittests" => live.State.UnitTests,
                    "interfaces" => live.State.Interfaces,
                    "code" => live.State.Code,
                    "nfrtests" => live.State.NfrTests,
                    "soaktests" => live.State.SoakTests,
                    "integrationtests" => live.State.IntegrationTests,
                    "codetweaks" => live.State.CodeTweaks,
                    "testtweaks" => live.State.TestTweaks,
                    "iac" => live.State.IaC,
                    "deploytweaks" => live.State.DeployTweaks,
                    _ => null,
                };
                if (dict != null && dict.TryGetValue(key, out var content))
                    body = JsonSerializer.Serialize(new { layer, key, content });
                else
                    body = JsonSerializer.Serialize(new { error = "Not found", layer, key });
            }
            else if (path == "/api/builds" && method == "GET")
            {
                var builds = LoadBuildsManifest(userSub);
                body = JsonSerializer.Serialize(builds);
            }
            else if (path.StartsWith("/api/builds/download/") && method == "GET")
            {
                var fileName = path.Substring("/api/builds/download/".Length);
                var builds = LoadBuildsManifest(userSub);
                var build = builds.FirstOrDefault(b =>
                    b.TryGetValue("fileName", out var fn) && fn.ToString() == fileName);
                if (build != null && build.TryGetValue("path", out var bp) && File.Exists(bp.ToString()!))
                {
                    var filePath = bp.ToString()!;
                    var fileBytes = File.ReadAllBytes(filePath);
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = fileBytes.Length;
                    ctx.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                    ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    await ctx.Response.OutputStream.WriteAsync(fileBytes);
                    return;
                }
                else
                {
                    body = JsonSerializer.Serialize(new { error = "Build not found" });
                    ctx.Response.StatusCode = 404;
                }
            }
            else
            {
                body = HtmlPage();
                contentType = "text/html";
            }

            var buf = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            await ctx.Response.OutputStream.WriteAsync(buf);
        }
        catch (Exception ex)
        {
            var err = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = err.Length;
            await ctx.Response.OutputStream.WriteAsync(err);
        }
        ctx.Response.Close();
    }

    private static async Task<string> ReadBody(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private static Dictionary<string, string> JsonToDict(JsonElement el)
    {
        var d = new Dictionary<string, string>();
        foreach (var prop in el.EnumerateObject())
            d[prop.Name] = prop.Value.GetString() ?? "";
        return d;
    }

    private static Dictionary<string, int> JsonToIntDict(JsonElement el)
    {
        var d = new Dictionary<string, int>();
        foreach (var prop in el.EnumerateObject())
            d[prop.Name] = prop.Value.TryGetInt32(out var v) ? v : 50;
        return d;
    }

    private static Dictionary<string, bool> JsonToBoolDict(JsonElement el)
    {
        var d = new Dictionary<string, bool>();
        foreach (var prop in el.EnumerateObject())
            d[prop.Name] = prop.Value.ValueKind == JsonValueKind.True;
        return d;
    }

    private static void SetAuthCookie(HttpListenerResponse resp, string sub)
    {
        var value = AuthManager.CreateAuthCookie(sub);
        resp.SetCookie(new Cookie("platinumforge_auth", value) { Path = "/" });
    }

    private static async Task WriteResponse(HttpListenerContext ctx, string body, string contentType, int statusCode = 200)
    {
        var buf = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.Close();
    }

    // ── Generation Pipeline (runs async) ─────

    private static bool StageEnabled(LiveSession live, string stage) =>
        !live.State.PipelineConfig.TryGetValue(stage, out var v) || v;

    private static async Task RunGeneration(LiveSession live, string prompt, string userSub = "local")
    {
        live.Generating = true;
        _ = live.BroadcastAll("generating", new { generating = true });
        var pipelineStart = DateTime.UtcNow;
        try
        {
            live.PushSnapshot("Before generation");
            await BroadcastProgress(live, 0, 9, "Initialising", "running", "Preparing pipeline...");
            await BroadcastChat(live, "system", "⏳ Starting generation pipeline...");

            if (!string.IsNullOrWhiteSpace(prompt) && prompt != "generate" && prompt != "run")
            {
                await BroadcastProgress(live, 0, 9, "Interpreting Prompt", "running", "Sending prompt to LLM...");
                await BroadcastChat(live, "system", "💭 Interpreting prompt...");
                var constraintJson = $"Rules: {JsonSerializer.Serialize(live.State.Rules)}\nArchitecture: {JsonSerializer.Serialize(live.State.Architecture)}\nNFR: {JsonSerializer.Serialize(live.State.NFR)}\nInvariants: {JsonSerializer.Serialize(live.State.Invariants)}";
                var interpretation = await OpenAIClient.Complete(
                    "You update system constraints for a autonomous software generation engine. Output ONLY valid JSON, no markdown.",
                    $"""
                    Current constraints:
                    {constraintJson}

                    User says: "{prompt}"

                    If the user is asking to change the domain, add features, modify rules, or adjust constraints,
                    output a JSON object with any of these keys to update: "description", "personas", "rules", "invariants", "architecture", "dataflow", "frameworks", "language", "deployment", "features", "nfr", "stories".
                    Each value is an object of key-value pairs to SET (merge with existing).
                    "description" is for what the user wants to build. "personas" is for user personas/actors.
                    "features" is for system capabilities and features. "stories" is for user stories as interactions.
                    "dataflow" is for data flow descriptions. "frameworks" is for allowed frameworks/tools. "language" is for programming language choices.
                    "deployment" is for deployment target (e.g. bare metal, Azure App Service, AKS, Docker, serverless).
                    If the user just wants to regenerate without changes, output this exact JSON: {"{"}\"action\":\"regenerate\"{"}"}
                    If unclear, make your best interpretation.
                    """);
                try
                {
                    var stripped = Generator.StripMarkdownFences(interpretation);
                    var doc = JsonDocument.Parse(stripped);
                    var root = doc.RootElement;
                    bool changed = false;
                    if (root.TryGetProperty("description", out var desc)) { MergeDict(live.State.Description, desc); changed = true; }
                    if (root.TryGetProperty("personas", out var p)) { MergeDict(live.State.Personas, p); changed = true; }
                    if (root.TryGetProperty("rules", out var r)) { MergeDict(live.State.Rules, r); changed = true; }
                    if (root.TryGetProperty("invariants", out var inv)) { MergeDict(live.State.Invariants, inv); changed = true; }
                    if (root.TryGetProperty("architecture", out var a)) { MergeDict(live.State.Architecture, a); changed = true; }
                    if (root.TryGetProperty("dataflow", out var df)) { MergeDict(live.State.Dataflow, df); changed = true; }
                    if (root.TryGetProperty("frameworks", out var fw)) { MergeDict(live.State.Frameworks, fw); changed = true; }
                    if (root.TryGetProperty("language", out var lang)) { MergeDict(live.State.Language, lang); changed = true; }
                    if (root.TryGetProperty("deployment", out var dep)) { MergeDict(live.State.Deployment, dep); changed = true; }
                    if (root.TryGetProperty("features", out var feat)) { MergeDict(live.State.Features, feat); changed = true; }
                    if (root.TryGetProperty("nfr", out var n)) { MergeDict(live.State.NFR, n); changed = true; }
                    if (root.TryGetProperty("stories", out var st)) { MergeDict(live.State.Stories, st); changed = true; }
                    if (changed)
                    {
                        await BroadcastChat(live, "system", "📝 Constraints updated from prompt");
                        _ = live.BroadcastAll("state", new {
                            description = live.State.Description, personas = live.State.Personas,
                            rules = live.State.Rules, invariants = live.State.Invariants,
                            architecture = live.State.Architecture, dataflow = live.State.Dataflow,
                            frameworks = live.State.Frameworks, language = live.State.Language,
                            deployment = live.State.Deployment,
                            features = live.State.Features, nfr = live.State.NFR, stories = live.State.Stories,
                        });
                    }
                }
                catch { await BroadcastChat(live, "system", "ℹ️ Proceeding with current constraints"); }
            }

            // Stage 1: Interfaces
            if (StageEnabled(live, "interfaces")) {
                await BroadcastProgress(live, 1, 9, "Interfaces", "running", "Generating interface definitions...");
                await BroadcastChat(live, "system", "⏳ [1/9] Generating Interfaces...");
                live.State.Interfaces = await Generator.GenerateInterfaces(live.State);
                await BroadcastProgress(live, 1, 9, "Interfaces", "done", $"{live.State.Interfaces.Count} files");
                await BroadcastChat(live, "system", "✅ Interfaces generated");
            } else { await BroadcastProgress(live, 1, 9, "Interfaces", "done", "Skipped"); }

            // Stage 2: Unit Tests
            if (StageEnabled(live, "unitTests")) {
                await BroadcastProgress(live, 2, 9, "Unit Tests", "running", "Generating test cases...");
                await BroadcastChat(live, "system", "⏳ [2/9] Generating Unit Tests...");
                live.State.UnitTests = await Generator.GenerateUnitTests(live.State);
                await BroadcastProgress(live, 2, 9, "Unit Tests", "done", $"{live.State.UnitTests.Count} files");
                await BroadcastChat(live, "system", "✅ Unit Tests generated");
            } else { await BroadcastProgress(live, 2, 9, "Unit Tests", "done", "Skipped"); }

            // Stage 3: Code
            if (StageEnabled(live, "code")) {
                await BroadcastProgress(live, 3, 9, "Code Generation", "running", "Implementing from interfaces + tests...");
                await BroadcastChat(live, "system", "⏳ [3/9] Generating Code...");
                live.State.Code = await Generator.GenerateCode(live.State);
                await BroadcastProgress(live, 3, 9, "Code Generation", "done", $"{live.State.Code.Count} files");
                await BroadcastChat(live, "system", "✅ Code generated");
            } else { await BroadcastProgress(live, 3, 9, "Code Generation", "done", "Skipped"); }

            // Stage 4: Build + Unit Test retry loop
            var unitTestsPassed = true;
            if (StageEnabled(live, "build") && StageEnabled(live, "unitTests")) {
                await BroadcastProgress(live, 4, 9, "Build & Test", "running", "Compiling and running unit tests...");
                await BroadcastChat(live, "system", "⏳ [4/9] Build & Unit Test loop...");
                unitTestsPassed = await RunRetryLoop(live);
                await BroadcastProgress(live, 4, 9, "Build & Test", unitTestsPassed ? "done" : "fail",
                    unitTestsPassed ? "All tests passing" : "Tests failed after retries");
            } else { await BroadcastProgress(live, 4, 9, "Build & Test", "done", "Skipped"); }

            if (unitTestsPassed)
            {
                // Stage 5: NFR Tests (Playwright)
                if (StageEnabled(live, "nfrTests")) {
                    await BroadcastProgress(live, 5, 9, "NFR Tests", "running", "Generating Playwright tests...");
                    await BroadcastChat(live, "system", "⏳ [5/9] Generating NFR Tests (Playwright)...");
                    live.State.NfrTests = await Generator.GenerateNfrTests(live.State);
                    await BroadcastProgress(live, 5, 9, "NFR Tests", "done", "Generated");
                    await BroadcastChat(live, "system", "✅ NFR Tests generated");
                    await RunExternalTests(live, "nfr", "Playwright", live.State.NfrTests);
                } else { await BroadcastProgress(live, 5, 9, "NFR Tests", "done", "Skipped"); }

                // Stage 6: Soak / Performance Tests (Locust)
                if (StageEnabled(live, "soakTests")) {
                    await BroadcastProgress(live, 6, 9, "Soak Tests", "running", "Generating Locust load tests...");
                    await BroadcastChat(live, "system", "⏳ [6/9] Generating Soak Tests (Locust)...");
                    live.State.SoakTests = await Generator.GenerateSoakTests(live.State);
                    await BroadcastProgress(live, 6, 9, "Soak Tests", "done", "Generated");
                    await BroadcastChat(live, "system", "✅ Soak Tests generated");
                    await RunExternalTests(live, "soak", "Locust", live.State.SoakTests);
                } else { await BroadcastProgress(live, 6, 9, "Soak Tests", "done", "Skipped"); }

                // Stage 7: Integration Tests (Jest)
                if (StageEnabled(live, "integrationTests")) {
                    await BroadcastProgress(live, 7, 9, "Integration Tests", "running", "Generating Jest integration tests...");
                    await BroadcastChat(live, "system", "⏳ [7/9] Generating Integration Tests (Jest)...");
                    live.State.IntegrationTests = await Generator.GenerateIntegrationTests(live.State);
                    await BroadcastProgress(live, 7, 9, "Integration Tests", "done", "Generated");
                    await BroadcastChat(live, "system", "✅ Integration Tests generated");
                    await RunExternalTests(live, "integration", "Jest", live.State.IntegrationTests);
                } else { await BroadcastProgress(live, 7, 9, "Integration Tests", "done", "Skipped"); }

                // Stage 8: IaC Generation
                if (StageEnabled(live, "iac") && (live.State.Deployment.Count > 0 || live.State.IaC.Count > 0))
                {
                    await BroadcastProgress(live, 8, 9, "Infrastructure", "running", "Generating IaC artifacts...");
                    await BroadcastChat(live, "system", "⏳ [8/9] Generating Infrastructure as Code...");
                    live.State.IaC = await Generator.GenerateIaC(live.State);
                    await BroadcastProgress(live, 8, 9, "Infrastructure", "done", $"{live.State.IaC.Count} files");
                    await BroadcastChat(live, "system", $"✅ IaC generated ({live.State.IaC.Count} files)");
                }
                else
                {
                    await BroadcastProgress(live, 8, 9, "Infrastructure", "done", "Skipped — no deployment config");
                }

                // Stage 9: Publish artifact
                if (StageEnabled(live, "publish")) {
                    await BroadcastProgress(live, 9, 9, "Publish", "running", "Packaging artifact...");
                    await BroadcastChat(live, "system", "⏳ [9/9] Publishing artifact...");
                    var artifactPath = await PublishArtifact(live, userSub);
                    await BroadcastProgress(live, 9, 9, "Publish", "done", "Artifact ready");
                    await BroadcastChat(live, "success", $"📦 Artifact published: {artifactPath}");
                } else { await BroadcastProgress(live, 9, 9, "Publish", "done", "Skipped"); }
            }
            else
            {
                await BroadcastProgress(live, 4, 9, "Build & Test", "fail", "Pipeline halted");
                await BroadcastChat(live, "error", "⚠️ Unit tests failed — skipping NFR/Soak/Integration/Publish stages");
            }
        }
        catch (Exception ex)
        {
            await BroadcastProgress(live, 0, 9, "Error", "fail", ex.Message);
            await BroadcastChat(live, "error", $"❌ Generation failed: {ex.Message}");
        }
        finally
        {
            var elapsed = (DateTime.UtcNow - pipelineStart).TotalSeconds;
            await BroadcastProgress(live, 9, 9, "Complete", "complete", $"Total: {elapsed:F1}s");
            live.Generating = false;
            _ = live.BroadcastAll("generating", new { generating = false });
        }
    }

    // Helper: add chat + broadcast to all SSE clients
    private static async Task BroadcastChat(LiveSession live, string role, string message)
    {
        live.AddChat(role, message);
        await live.BroadcastAll("chat", new { role, message });
    }

    // Helper: broadcast generation pipeline progress
    private static async Task BroadcastProgress(LiveSession live, int stage, int total, string name, string status, string detail = "")
    {
        await live.BroadcastAll("progress", new { stage, total, name, status, detail });
    }

    private static void MergeDict(Dictionary<string, string> target, JsonElement el)
    {
        foreach (var prop in el.EnumerateObject())
            target[prop.Name] = prop.Value.GetString() ?? "";
    }

    private static async Task<bool> RunRetryLoop(LiveSession live)
    {
        const int maxAttempts = 5;
        string[]? lastErrors = null;
        List<(string Name, bool Passed, string Msg)>? lastTestResults = null;
        string? lastFullSource = null;
        int regenFromLayer = 7;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await BroadcastChat(live, "system", $"── Build attempt {attempt}/{maxAttempts} ──");

            var proposed = live.State.Clone();

            if (attempt > 1)
            {
                var numberedPrev = lastFullSource != null
                    ? string.Join("\n", lastFullSource.Split('\n').Select((line, i) => $"{i + 1}: {line}"))
                    : null;

                if (regenFromLayer <= 4)
                {
                    await BroadcastChat(live, "system", "⏳ Regenerating Unit Tests...");
                    proposed.UnitTests = await Generator.GenerateUnitTests(proposed, numberedPrev, lastErrors);
                }
                if (regenFromLayer <= 5)
                {
                    await BroadcastChat(live, "system", "⏳ Regenerating Interfaces...");
                    proposed.Interfaces = await Generator.GenerateInterfaces(proposed);
                }
                await BroadcastChat(live, "system", "⏳ Regenerating Code...");
                proposed.Code = await Generator.GenerateCode(proposed, numberedPrev, lastErrors, lastTestResults);
            }

            var fullSource = CodeCompiler.BuildFullSource(proposed);
            lastFullSource = fullSource;
            live.CurrentSource = fullSource;
            _ = live.BroadcastAll("code", new { code = fullSource });
            lastErrors = null;
            lastTestResults = null;
            regenFromLayer = 6;

            var violations = InvariantChecker.Check(proposed, fullSource);
            if (violations.Count > 0)
            {
                await BroadcastChat(live, "error", $"❌ Invariant violations:\n{string.Join("\n", violations)}");
                lastErrors = violations.Select(v => $"Invariant violation: {v}").ToArray();
                continue;
            }

            await BroadcastChat(live, "system", "🔨 Compiling...");
            var (asm, errors) = CodeCompiler.Compile(fullSource);
            if (asm == null)
            {
                await BroadcastChat(live, "error", $"❌ Compilation failed:\n{string.Join("\n", errors.Take(10))}");
                lastErrors = errors;

                var srcLines = fullSource.Split('\n');
                foreach (var err in errors)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(err, @"\((\d+),");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int errLine) && errLine > 0 && errLine <= srcLines.Length)
                    {
                        int layer = Program.DetectLayer(srcLines, errLine);
                        if (layer < regenFromLayer)
                        {
                            regenFromLayer = layer;
                            await BroadcastChat(live, "system", $"ℹ️ Error in L{layer} — will cascade regen");
                        }
                    }
                }
                continue;
            }

            await BroadcastChat(live, "system", "🧪 Running unit tests...");
            var (allPassed, results, runError) = CodeCompiler.RunTests(asm);
            if (runError != null)
            {
                await BroadcastChat(live, "error", $"❌ {runError}");
                lastErrors = new[] { runError };
                continue;
            }

            var testReport = string.Join("\n", results.Select(r =>
                $"  {(r.Passed ? "✅" : "❌")} {r.Name}: {r.Msg}"));
            await BroadcastChat(live, "system", $"Unit test results:\n{testReport}");

            if (!allPassed)
            {
                lastTestResults = results;
                continue;
            }

            live.State = proposed;
            live.CurrentSource = fullSource;
            live.PushSnapshot($"Generation attempt {attempt} — all unit tests passed");
            await BroadcastChat(live, "success", "🎉 All unit tests passed! Changes accepted.");
            _ = live.BroadcastAll("code", new { code = fullSource });
            return true;
        }

        await BroadcastChat(live, "error", "⚠️ Max attempts reached. Keeping previous state.");
        return false;
    }

    private static async Task RunExternalTests(LiveSession live, string category, string runner, Dictionary<string, string> testFiles)
    {
        if (testFiles.Count == 0)
        {
            await BroadcastChat(live, "system", $"ℹ️ No {category} tests to run");
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"platinumforge-{category}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var (name, content) in testFiles)
                File.WriteAllText(Path.Combine(tempDir, name), content);

            string command;
            string args;
            switch (runner)
            {
                case "Playwright":
                    command = "npx";
                    args = $"playwright test --reporter=line {tempDir}";
                    break;
                case "Locust":
                    var locustFile = testFiles.Keys.First();
                    command = "locust";
                    args = $"-f {Path.Combine(tempDir, locustFile)} --headless -u 10 -r 2 --run-time 30s --csv={Path.Combine(tempDir, "results")}";
                    break;
                case "Jest":
                    command = "npx";
                    args = $"jest --no-cache --roots {tempDir} --forceExit";
                    break;
                default:
                    await BroadcastChat(live, "error", $"Unknown runner: {runner}");
                    return;
            }

            await BroadcastChat(live, "system", $"▶ Running {runner}: {command} {args}");

            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                await BroadcastChat(live, "error", $"❌ Failed to start {runner}");
                return;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                    _ = BroadcastChat(live, "system", $"[{runner}] {e.Data}");
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                    _ = BroadcastChat(live, "error", $"[{runner}] {e.Data}");
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var exited = proc.WaitForExit(120_000); // 2 min timeout
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                await BroadcastChat(live, "error", $"⏱ {runner} timed out after 120s");
            }

            var exitCode = exited ? proc.ExitCode : -1;
            var status = exitCode == 0 ? "✅" : "❌";
            await BroadcastChat(live, exitCode == 0 ? "success" : "error",
                $"{status} {runner} finished (exit code {exitCode})");

            // Broadcast stats
            _ = live.BroadcastAll("test-result", new
            {
                category,
                runner,
                exitCode,
                passed = exitCode == 0,
                output = stdout.ToString(),
                errors = stderr.ToString(),
            });

            // Read CSV stats for Locust
            if (runner == "Locust" && exitCode == 0)
            {
                var statsFile = Path.Combine(tempDir, "results_stats.csv");
                if (File.Exists(statsFile))
                {
                    var stats = File.ReadAllText(statsFile);
                    await BroadcastChat(live, "system", $"📊 Locust stats:\n{stats}");
                }
            }
        }
        catch (Exception ex)
        {
            await BroadcastChat(live, "error", $"❌ {runner} error: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string BuildsManifestPath(string userSub) =>
        Path.Combine(AuthManager.DataRoot, "users", userSub, "builds.json");

    private static List<Dictionary<string, object>> LoadBuildsManifest(string userSub)
    {
        var path = BuildsManifestPath(userSub);
        if (!File.Exists(path)) return new();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static void SaveBuildsManifest(string userSub, List<Dictionary<string, object>> builds)
    {
        var path = BuildsManifestPath(userSub);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(builds, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildPipelineSpec(SystemState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {state.ProjectName} v{state.Version}");
        sb.AppendLine($"# Pipeline Specification");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("## Pipeline: Intent → Constraints → Shape → Behaviour → Forge → Evolve → Commit");
        sb.AppendLine();
        sb.AppendLine(PromptBuilder.BuildContext(state));
        sb.AppendLine("## 4 · Forge (Generated)");
        sb.AppendLine($"  Interfaces: {state.Interfaces.Count} file(s)");
        sb.AppendLine($"  Unit Tests: {state.UnitTests.Count} file(s)");
        sb.AppendLine($"  Code: {state.Code.Count} file(s)");
        sb.AppendLine($"  NFR Tests: {state.NfrTests.Count} file(s)");
        sb.AppendLine($"  Soak Tests: {state.SoakTests.Count} file(s)");
        sb.AppendLine($"  Integration Tests: {state.IntegrationTests.Count} file(s)");
        return sb.ToString();
    }

    private static async Task<string> PublishArtifact(LiveSession live, string userSub = "local")
    {
        var projectSlug = System.Text.RegularExpressions.Regex.Replace(
            live.State.ProjectName.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(projectSlug)) projectSlug = "untitled";
        var version = live.State.Version;

        var artifactDir = Path.Combine(AuthManager.DataRoot, "artifacts", projectSlug);
        Directory.CreateDirectory(artifactDir);

        var zipName = $"{projectSlug}-v{version}.zip";
        var zipPath = Path.Combine(artifactDir, zipName);

        // If file exists, append build number
        var buildNum = 1;
        while (File.Exists(zipPath))
        {
            zipName = $"{projectSlug}-v{version}-b{buildNum}.zip";
            zipPath = Path.Combine(artifactDir, zipName);
            buildNum++;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"platinumforge-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write pipeline spec
            File.WriteAllText(Path.Combine(tempDir, "SPEC.md"), BuildPipelineSpec(live.State));

            // Write generated source
            File.WriteAllText(Path.Combine(tempDir, "generated.cs"), live.CurrentSource);

            // Write individual layers
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            foreach (var (name, src) in live.State.Interfaces)
                File.WriteAllText(Path.Combine(srcDir, $"interface-{name}.cs"), src);
            foreach (var (name, src) in live.State.Code)
                File.WriteAllText(Path.Combine(srcDir, $"impl-{name}.cs"), src);
            foreach (var (name, src) in live.State.UnitTests)
                File.WriteAllText(Path.Combine(srcDir, $"test-{name}.cs"), src);

            // Write external test files
            var testsDir = Path.Combine(tempDir, "tests");
            Directory.CreateDirectory(testsDir);
            foreach (var (name, src) in live.State.NfrTests)
                File.WriteAllText(Path.Combine(testsDir, name), src);
            foreach (var (name, src) in live.State.SoakTests)
                File.WriteAllText(Path.Combine(testsDir, name), src);
            foreach (var (name, src) in live.State.IntegrationTests)
                File.WriteAllText(Path.Combine(testsDir, name), src);

            // Write IaC files
            if (live.State.IaC.Count > 0)
            {
                var iacDir = Path.Combine(tempDir, "iac");
                Directory.CreateDirectory(iacDir);
                foreach (var (name, src) in live.State.IaC)
                    File.WriteAllText(Path.Combine(iacDir, name), src);
            }

            // Write full constraints as JSON
            var constraints = new Dictionary<string, object>
            {
                ["projectName"] = live.State.ProjectName,
                ["version"] = version,
                ["description"] = live.State.Description,
                ["personas"] = live.State.Personas,
                ["rules"] = live.State.Rules,
                ["invariants"] = live.State.Invariants,
                ["architecture"] = live.State.Architecture,
                ["dataflow"] = live.State.Dataflow,
                ["frameworks"] = live.State.Frameworks,
                ["language"] = live.State.Language,
                ["deployment"] = live.State.Deployment,
                ["features"] = live.State.Features,
                ["stories"] = live.State.Stories,
                ["nfr"] = live.State.NFR,
                ["sliders"] = live.State.Sliders,
            };
            File.WriteAllText(Path.Combine(tempDir, "constraints.json"),
                JsonSerializer.Serialize(constraints, new JsonSerializerOptions { WriteIndented = true }));

            System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, zipPath);

            // Record in builds manifest
            var builds = LoadBuildsManifest(userSub);
            builds.Add(new Dictionary<string, object>
            {
                ["projectName"] = live.State.ProjectName,
                ["version"] = version,
                ["fileName"] = zipName,
                ["path"] = zipPath,
                ["sessionId"] = live.SessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["unitTests"] = live.State.UnitTests.Count,
                ["nfrTests"] = live.State.NfrTests.Count,
                ["soakTests"] = live.State.SoakTests.Count,
                ["integrationTests"] = live.State.IntegrationTests.Count,
            });
            SaveBuildsManifest(userSub, builds);

            // Auto-bump patch version for next build
            if (System.Version.TryParse(version, out var semver))
                live.State.Version = $"{semver.Major}.{semver.Minor}.{semver.Build + 1}";

            await BroadcastChat(live, "system", $"📦 Published: {zipName}");
            _ = live.BroadcastAll("artifact", new { path = zipPath, fileName = zipName, projectName = live.State.ProjectName, version });

            return zipPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string LoginPage()
    {
        var buttons = new StringBuilder();
        foreach (var (name, provider) in AuthManager.ConfiguredProviders)
        {
            buttons.AppendLine($"""
                <a href="/auth/login/{provider.Name}" class="login-btn" style="background:{provider.BtnColor}; color:{provider.BtnTextColor};">
                    {provider.IconSvg}
                    Sign in with {provider.DisplayName}
                </a>
            """);
        }
        if (buttons.Length == 0)
        {
            buttons.AppendLine("""
                <a href="/auth/login" class="login-btn">
                    🔓 Continue without sign-in
                </a>
            """);
        }
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>WaveFunctionLabs — Sign In</title>
            %%FAVICON%%
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { background: #0d1117; color: #c9d1d9; font-family: 'Segoe UI', system-ui, sans-serif; height: 100vh; display: flex; align-items: center; justify-content: center; }
                .login-card { background: #161b22; border: 1px solid #30363d; border-radius: 16px; padding: 48px 40px; text-align: center; max-width: 420px; width: 90%; box-shadow: 0 8px 32px rgba(0,0,0,0.4); }
                .logo-icon { font-size: 64px; margin-bottom: 16px; filter: drop-shadow(0 0 12px rgba(59,130,246,0.6)); }
                .logo-icon svg { width: 72px; height: 72px; }
                .logo-text { font-size: 32px; font-weight: 700; background: linear-gradient(135deg, #3b82f6, #60a5fa, #93c5fd); -webkit-background-clip: text; -webkit-text-fill-color: transparent; margin-bottom: 8px; }
                .logo-sub { font-size: 13px; color: #8b949e; letter-spacing: 2px; text-transform: uppercase; margin-bottom: 32px; }
                .login-buttons { display: flex; flex-direction: column; gap: 12px; align-items: center; }
                .login-btn { display: inline-flex; align-items: center; gap: 12px; background: #fff; color: #333; border: none; padding: 12px 32px; border-radius: 8px; font-size: 15px; font-weight: 600; cursor: pointer; transition: all 0.15s; text-decoration: none; width: 100%; justify-content: center; }
                .login-btn:hover { transform: translateY(-2px); box-shadow: 0 4px 12px rgba(255,255,255,0.1); }
                .login-btn svg { width: 20px; height: 20px; flex-shrink: 0; }
                .login-note { margin-top: 24px; font-size: 12px; color: #8b949e; }
            </style>
        </head>
        <body>
            <div class="login-card">
                <div class="logo-icon">%%PSILOGO%%</div>
                <div class="logo-text">WaveFunctionLabs</div>
                <div class="logo-sub">PlatinumForge · What if software built itself?</div>
                <div class="login-buttons">
                    {{buttons}}
                </div>
                <div class="login-note">Sign in to access your personal workspace</div>
            </div>
        </body>
        </html>
        """.Replace("%%FAVICON%%", FaviconLink).Replace("%%PSILOGO%%", PsiLogoSvg);
    }

    private static string HtmlPage() =>
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>WaveFunctionLabs — PlatinumForge</title>
            %%FAVICON%%
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                :root {
                    --bg: #0d1117; --surface: #161b22; --surface2: #1c2333;
                    --border: #30363d; --text: #c9d1d9; --text-dim: #8b949e;
                    --accent: #3b82f6; --accent2: #60a5fa; --green: #3fb950;
                    --red: #f85149; --blue: #58a6ff; --purple: #bc8cff;
                }
                body { background: var(--bg); color: var(--text); font-family: 'Segoe UI', system-ui, sans-serif; height: 100vh; display: flex; flex-direction: column; overflow: hidden; }

                /* Logo/Header */
                #header { background: linear-gradient(135deg, #1a1a2e 0%, #16213e 50%, #0f3460 100%); padding: 12px 24px; display: flex; align-items: center; gap: 16px; border-bottom: 2px solid var(--accent); flex-shrink: 0; }
                .logo { display: flex; align-items: center; gap: 12px; }
                .logo-icon { font-size: 32px; filter: drop-shadow(0 0 8px rgba(59,130,246,0.6)); }
                .logo-icon svg { width: 36px; height: 36px; }
                .logo-text { font-size: 22px; font-weight: 700; background: linear-gradient(135deg, var(--accent), var(--accent2), #93c5fd); -webkit-background-clip: text; -webkit-text-fill-color: transparent; letter-spacing: -0.5px; }
                .logo-sub { font-size: 11px; color: var(--text-dim); letter-spacing: 2px; text-transform: uppercase; }
                .header-status { margin-left: auto; display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-dim); }
                .status-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--green); }
                .status-dot.busy { background: var(--accent); animation: pulse 1s infinite; }
                @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }

                /* Pipeline Chevron Flow */
                #pipeline-nav { display: flex; align-items: stretch; flex-shrink: 0; overflow-x: auto; padding: 0; height: 44px; }
                .pipeline-arrow { position: relative; flex: 1; display: flex; align-items: center; justify-content: center; gap: 6px; padding: 0 12px 0 24px; font-size: 12px; font-weight: 600; letter-spacing: 0.5px; color: var(--text-dim); cursor: pointer; user-select: none; background: var(--surface2); transition: all 0.15s; white-space: nowrap; clip-path: polygon(0 0, calc(100% - 16px) 0, 100% 50%, calc(100% - 16px) 100%, 0 100%, 16px 50%); margin-left: -8px; }
                .pipeline-arrow:first-child { clip-path: polygon(0 0, calc(100% - 16px) 0, 100% 50%, calc(100% - 16px) 100%, 0 100%); margin-left: 0; padding-left: 16px; }
                .pipeline-arrow:last-child { clip-path: polygon(0 0, 100% 0, 100% 100%, 0 100%, 16px 50%); }
                .pipeline-arrow:hover { background: #252d3a; color: var(--text); }
                .pipeline-arrow.active { background: linear-gradient(135deg, #1e2a4a, #1a3560); color: var(--accent); text-shadow: 0 0 8px rgba(59,130,246,0.3); }
                .pipeline-arrow.completed { background: linear-gradient(135deg, #0d2818, #132f1e); color: var(--green); }
                .pipeline-arrow.running { background: linear-gradient(135deg, #1e2a4a, #1a3560); color: var(--accent); animation: pulse 1.5s infinite; }
                .pipeline-arrow.running::after { content: ''; position: absolute; bottom: 0; left: 0; height: 3px; background: var(--accent); animation: progress-sweep 2s ease-in-out infinite; }
                @keyframes progress-sweep { 0% { width: 0%; } 50% { width: 80%; } 100% { width: 100%; } }
                .pipeline-arrow.stage-done { background: linear-gradient(135deg, #0d2818, #132f1e); color: var(--green); }
                .pipeline-arrow.stage-fail { background: linear-gradient(135deg, #2d0d0d, #3d1515); color: #f85149; }
                .pipeline-arrow .stage-time { font-size: 9px; opacity: 0.7; }
                .pipeline-arrow .arrow-icon { font-size: 14px; }
                .pipeline-arrow .arrow-badge { background: var(--accent); color: #000; font-size: 9px; padding: 1px 5px; border-radius: 6px; font-weight: 700; min-width: 16px; text-align: center; }
                .pipeline-arrow.completed .arrow-badge { background: var(--green); }

                /* Generation Progress Panel */
                #gen-progress { display: none; background: linear-gradient(135deg, #0d1520, #111d2e); border-bottom: 1px solid var(--border); padding: 8px 16px; font-size: 12px; flex-shrink: 0; }
                #gen-progress.active { display: flex; align-items: center; gap: 12px; }
                #gen-progress .gen-stage-label { color: var(--accent); font-weight: 700; min-width: 200px; }
                #gen-progress .gen-bar-wrap { flex: 1; height: 6px; background: var(--surface2); border-radius: 3px; overflow: hidden; }
                #gen-progress .gen-bar { height: 100%; background: linear-gradient(90deg, var(--accent), var(--accent2)); border-radius: 3px; transition: width 0.3s ease; }
                #gen-progress .gen-elapsed { color: var(--text-dim); font-size: 11px; min-width: 50px; text-align: right; font-family: 'Cascadia Code', monospace; }

                /* Main Layout */
                #main { display: flex; flex: 1; overflow: hidden; }

                /* Left Panel */
                #left { width: 420px; min-width: 320px; display: flex; flex-direction: column; border-right: 1px solid var(--border); background: var(--surface); overflow: hidden; }

                /* Constraints */
                #constraints { flex: 1 1 auto; overflow-y: auto; border-bottom: 1px solid var(--border); }
                .constraint-section { border-bottom: 1px solid var(--border); }
                .constraint-header { display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; background: var(--surface2); cursor: pointer; user-select: none; font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 1px; color: var(--text-dim); }
                .constraint-header:hover { color: var(--text); }
                .constraint-header .badge { background: var(--accent); color: #000; font-size: 10px; padding: 1px 6px; border-radius: 8px; font-weight: 700; }
                .constraint-body { padding: 8px; display: none; }
                .constraint-body.open { display: block; }
                .constraint-item { display: flex; gap: 6px; margin-bottom: 6px; align-items: flex-start; }
                .constraint-item input { flex: 0 0 100px; background: var(--bg); border: 1px solid var(--border); color: var(--accent); padding: 4px 6px; border-radius: 3px; font-size: 12px; font-family: 'Cascadia Code', monospace; }
                .constraint-item textarea { flex: 1; background: var(--bg); border: 1px solid var(--border); color: var(--text); padding: 4px 6px; border-radius: 3px; font-size: 12px; resize: vertical; min-height: 28px; font-family: inherit; }
                .constraint-item .btn-del { flex: 0 0 20px; background: none; border: none; color: var(--text-dim); cursor: pointer; font-size: 14px; padding: 2px 0; line-height: 1; opacity: 0.4; transition: all 0.15s; }
                .constraint-item .btn-del:hover { color: #f85149; opacity: 1; }
                .constraint-actions { padding: 4px 8px; display: flex; gap: 6px; }
                .btn-sm { background: var(--surface2); border: 1px solid var(--border); color: var(--text-dim); padding: 3px 10px; border-radius: 3px; font-size: 11px; cursor: pointer; }
                .btn-sm:hover { color: var(--text); border-color: var(--accent); }
                .btn-sm.save { background: var(--accent); color: #000; border-color: var(--accent); font-weight: 600; }
                .btn-sm.gallery { background: #7c3aed; color: #fff; border-color: #7c3aed; }
                .btn-sm.gallery:hover { background: #6d28d9; }
                .slider-row { display: flex; align-items: center; gap: 8px; padding: 4px 12px; }
                .slider-row label { flex: 0 0 110px; font-size: 11px; color: var(--text-dim); text-transform: capitalize; }
                .slider-row input[type=range] { flex: 1; accent-color: var(--accent); height: 6px; cursor: pointer; }
                .slider-row .slider-val { flex: 0 0 28px; font-size: 11px; color: var(--accent); text-align: right; font-weight: 600; }
                .slider-row .slider-lo, .slider-row .slider-hi { font-size: 9px; color: var(--text-dim); flex: 0 0 50px; }
                .slider-row .slider-hi { text-align: right; }
                .slider-save { padding: 6px 12px; text-align: right; }
                .layer-group { margin-bottom: 12px; }
                .layer-group-title { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 1.5px; color: var(--accent); padding: 8px 10px 4px; border-bottom: 1px solid var(--border); margin-bottom: 2px; }
                .preset-dropdown { position: absolute; z-index: 100; background: var(--surface2); border: 1px solid var(--border); border-radius: 6px; box-shadow: 0 8px 24px rgba(0,0,0,.4); max-height: 280px; overflow-y: auto; min-width: 260px; }
                .preset-dropdown .preset-item { padding: 8px 14px; cursor: pointer; font-size: 12px; border-bottom: 1px solid var(--border); }
                .preset-dropdown .preset-item:last-child { border-bottom: none; }
                .preset-dropdown .preset-item:hover { background: var(--accent); color: #000; }
                .preset-dropdown .preset-item .preset-keys { font-size: 10px; color: var(--text-dim); margin-top: 2px; }
                .preset-dropdown .preset-item:hover .preset-keys { color: #333; }

                /* Prompt */
                #prompt-area { padding: 12px; border-bottom: 1px solid var(--border); flex-shrink: 0; }
                #prompt-input { width: 100%; background: var(--bg); border: 1px solid var(--border); color: var(--text); padding: 10px 12px; border-radius: 6px; font-size: 14px; font-family: inherit; resize: none; outline: none; }
                #prompt-input:focus { border-color: var(--accent); box-shadow: 0 0 0 2px rgba(59,130,246,0.2); }
                #prompt-actions { display: flex; gap: 8px; margin-top: 8px; }
                .btn { padding: 8px 16px; border: none; border-radius: 6px; font-size: 13px; font-weight: 600; cursor: pointer; transition: all 0.15s; }
                .btn-primary { background: linear-gradient(135deg, var(--accent), var(--accent2)); color: #000; }
                .btn-primary:hover { filter: brightness(1.1); transform: translateY(-1px); }
                .btn-primary:disabled { opacity: 0.5; cursor: not-allowed; transform: none; }
                .btn-secondary { background: var(--surface2); color: var(--text); border: 1px solid var(--border); }
                .btn-secondary:hover { border-color: var(--accent); }

                /* Chat */
                #chat { flex: 1; overflow-y: auto; padding: 8px; }
                .chat-entry { padding: 6px 10px; margin-bottom: 4px; border-radius: 6px; font-size: 13px; line-height: 1.5; white-space: pre-wrap; word-break: break-word; }
                .chat-entry.user { background: rgba(59,130,246,0.1); border-left: 3px solid var(--accent); }
                .chat-entry.system { background: rgba(88,166,255,0.06); color: var(--text-dim); }
                .chat-entry.error { background: rgba(248,81,73,0.1); color: var(--red); border-left: 3px solid var(--red); }
                .chat-entry.success { background: rgba(63,185,80,0.1); color: var(--green); border-left: 3px solid var(--green); }
                .chat-entry.agent { background: rgba(188,140,255,0.1); border-left: 3px solid var(--purple); }
                .chat-actions { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
                .chat-action-btn { background: var(--surface2); border: 1px solid var(--purple); color: var(--purple); padding: 4px 10px; border-radius: 4px; font-size: 11px; cursor: pointer; transition: all 0.15s; display: inline-flex; align-items: center; gap: 4px; }
                .chat-action-btn:hover { background: var(--purple); color: #fff; }
                .chat-action-btn.applied { background: var(--green); border-color: var(--green); color: #000; cursor: default; opacity: 0.7; }
                .chat-action-btn .action-layer { font-size: 9px; opacity: 0.7; text-transform: uppercase; }
                .chat-time { font-size: 10px; color: var(--text-dim); margin-bottom: 2px; }

                /* History Panel */
                #history-panel { flex: 0 0 auto; max-height: 25%; border-top: 1px solid var(--border); overflow-y: auto; }
                .history-header { padding: 8px 12px; background: var(--surface2); font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 1px; color: var(--text-dim); display: flex; justify-content: space-between; }
                .history-item { display: flex; align-items: center; justify-content: space-between; padding: 6px 12px; font-size: 12px; border-bottom: 1px solid var(--border); }
                .history-item:hover { background: var(--surface2); }
                .history-label { color: var(--text); }
                .history-time { color: var(--text-dim); font-size: 11px; }
                .btn-revert { background: none; border: 1px solid var(--border); color: var(--purple); padding: 2px 8px; border-radius: 3px; font-size: 11px; cursor: pointer; }
                .btn-revert:hover { border-color: var(--purple); background: rgba(188,140,255,0.1); }

                /* Right: Monaco */
                #right { flex: 1; display: flex; flex-direction: column; }
                #editor-header { background: var(--surface); padding: 8px 16px; display: flex; align-items: center; gap: 12px; border-bottom: 1px solid var(--border); font-size: 13px; color: var(--text-dim); flex-shrink: 0; }
                #editor { flex: 1; }

                /* Commit button */
                .btn-commit { background: linear-gradient(135deg, #3fb950, #2ea043); color: #000; border: none; padding: 6px 16px; border-radius: 6px; font-size: 12px; font-weight: 700; cursor: pointer; margin-right: 12px; transition: all 0.15s; }
                .btn-commit:hover { filter: brightness(1.1); transform: translateY(-1px); }
                .btn-commit.saving { opacity: 0.6; cursor: wait; }

                /* Editor tabs */
                .tab { background: var(--surface2); padding: 4px 12px; border-radius: 4px; font-size: 12px; color: var(--text-dim); border: 1px solid var(--border); cursor: pointer; transition: all 0.15s; }
                .tab:hover { color: var(--text); border-color: var(--accent); }
                .tab.active { color: var(--accent); border-color: var(--accent); background: rgba(59,130,246,0.1); }

                /* File tree */
                #file-tree { width: 240px; min-width: 200px; background: var(--surface); border-right: 1px solid var(--border); overflow-y: auto; flex-shrink: 0; }
                .tree-header { padding: 10px 12px; font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 1px; color: var(--text-dim); background: var(--surface2); border-bottom: 1px solid var(--border); }
                .tree-folder { cursor: pointer; user-select: none; }
                .tree-folder-label { display: flex; align-items: center; gap: 6px; padding: 6px 12px; font-size: 12px; font-weight: 600; color: var(--blue); }
                .tree-folder-label:hover { background: var(--surface2); }
                .tree-folder-label .folder-icon { transition: transform 0.15s; }
                .tree-folder-label.open .folder-icon { transform: rotate(90deg); }
                .tree-folder-children { display: none; }
                .tree-folder-children.open { display: block; }
                .tree-file { display: flex; align-items: center; gap: 6px; padding: 5px 12px 5px 28px; font-size: 12px; color: var(--text); cursor: pointer; border-left: 2px solid transparent; }
                .tree-file:hover { background: var(--surface2); color: var(--accent); }
                .tree-file.active { background: rgba(59,130,246,0.08); border-left-color: var(--accent); color: var(--accent); }
                .tree-file-count { font-size: 10px; color: var(--text-dim); background: var(--surface2); padding: 0 6px; border-radius: 8px; margin-left: auto; }

                /* Session button */
                .btn-sessions { background: linear-gradient(135deg, var(--purple), #a855f7); color: #fff; border: none; padding: 6px 14px; border-radius: 6px; font-size: 12px; font-weight: 700; cursor: pointer; margin-right: 8px; transition: all 0.15s; display: flex; align-items: center; gap: 6px; }
                .btn-sessions:hover { filter: brightness(1.15); transform: translateY(-1px); }

                /* Session Flyout */
                #session-overlay, #share-overlay, #builds-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.5); z-index: 900; }
                #session-flyout { position: fixed; top: 0; right: 0; bottom: 0; width: 380px; background: var(--surface); border-left: 2px solid var(--purple); z-index: 1000; display: flex; flex-direction: column; box-shadow: -4px 0 24px rgba(0,0,0,0.4); }
                .flyout-header { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px; background: linear-gradient(135deg, #1a1a2e, #16213e); border-bottom: 1px solid var(--border); font-size: 16px; font-weight: 700; color: var(--purple); }
                .flyout-close { background: none; border: none; color: var(--text-dim); font-size: 18px; cursor: pointer; padding: 4px 8px; border-radius: 4px; }
                .flyout-close:hover { background: var(--surface2); color: var(--text); }
                .flyout-actions { display: flex; gap: 8px; padding: 12px 16px; border-bottom: 1px solid var(--border); }
                .flyout-actions input { flex: 1; background: var(--bg); border: 1px solid var(--border); color: var(--text); padding: 8px 10px; border-radius: 6px; font-size: 13px; outline: none; }
                .flyout-actions input:focus { border-color: var(--purple); }
                .btn-new-session { white-space: nowrap; padding: 8px 14px !important; font-size: 12px !important; }
                #session-list { flex: 1; overflow-y: auto; padding: 8px; }
                .session-item { display: flex; flex-direction: column; padding: 12px 14px; margin-bottom: 6px; border-radius: 8px; background: var(--surface2); border: 1px solid var(--border); cursor: pointer; transition: all 0.15s; }
                .session-item:hover { border-color: var(--purple); background: rgba(188,140,255,0.05); }
                .session-item.active { border-color: var(--purple); background: rgba(188,140,255,0.1); box-shadow: 0 0 0 1px var(--purple); }
                .session-item-top { display: flex; align-items: center; justify-content: space-between; }
                .session-name { font-size: 14px; font-weight: 600; color: var(--text); }
                .session-name.editing { display: none; }
                .session-name-input { display: none; background: var(--bg); border: 1px solid var(--purple); color: var(--text); padding: 2px 6px; border-radius: 3px; font-size: 13px; font-weight: 600; width: 180px; }
                .session-name-input.editing { display: inline-block; }
                .session-id { font-size: 11px; color: var(--text-dim); font-family: 'Cascadia Code', monospace; margin-top: 4px; }
                .session-meta { display: flex; align-items: center; justify-content: space-between; margin-top: 6px; }
                .session-time { font-size: 11px; color: var(--text-dim); }
                .session-actions { display: flex; gap: 4px; }
                .session-actions button { background: none; border: 1px solid var(--border); color: var(--text-dim); padding: 2px 8px; border-radius: 3px; font-size: 11px; cursor: pointer; }
                .session-actions button:hover { border-color: var(--text); color: var(--text); }
                .session-actions .btn-del:hover { border-color: var(--red); color: var(--red); }
                .session-badge { font-size: 10px; background: var(--purple); color: #fff; padding: 1px 8px; border-radius: 8px; font-weight: 700; }

                /* User area */
                .user-area { display: flex; align-items: center; gap: 8px; margin-left: 12px; }
                .user-avatar { width: 28px; height: 28px; border-radius: 50%; border: 2px solid var(--accent); cursor: pointer; }
                .user-name { font-size: 12px; color: var(--text); max-width: 100px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .btn-logout { background: none; border: 1px solid var(--border); color: var(--text-dim); padding: 3px 10px; border-radius: 4px; font-size: 11px; cursor: pointer; }
                .btn-logout:hover { border-color: var(--red); color: var(--red); }
                .btn-share { background: linear-gradient(135deg, var(--blue), #3b82f6); color: #fff; border: none; padding: 4px 12px; border-radius: 4px; font-size: 11px; font-weight: 600; cursor: pointer; }
                .btn-share:hover { filter: brightness(1.15); }
                .share-dialog { position: fixed; top: 50%; left: 50%; transform: translate(-50%,-50%); background: var(--surface); border: 1px solid var(--purple); border-radius: 12px; padding: 24px; z-index: 1100; width: 420px; box-shadow: 0 8px 32px rgba(0,0,0,0.5); }
                .share-dialog h3 { color: var(--purple); margin-bottom: 12px; font-size: 16px; }
                .share-url { width: 100%; background: var(--bg); border: 1px solid var(--border); color: var(--text); padding: 10px; border-radius: 6px; font-size: 13px; font-family: 'Cascadia Code', monospace; }
                .share-actions { display: flex; gap: 8px; margin-top: 12px; justify-content: flex-end; }
            </style>
        </head>
        <body>
            <div id="header">
                <div class="logo">
                    <span class="logo-icon">%%PSILOGO%%</span>
                    <div>
                        <div class="logo-text">WaveFunctionLabs</div>
                        <div class="logo-sub">PlatinumForge · What if software built itself?</div>
                    </div>
                </div>
                <div style="display:flex; align-items:center; gap:8px; margin-left:16px;">
                    <input type="text" id="projectNameInput" placeholder="Project name" value="untitled"
                        style="background:var(--surface); border:1px solid var(--border); color:var(--text); padding:4px 8px; border-radius:4px; font-size:13px; font-weight:600; width:140px;"
                        onchange="updateProjectMeta()" />
                    <span style="color:var(--text-dim); font-size:12px;">v</span>
                    <input type="text" id="versionInput" placeholder="0.1.0" value="0.1.0"
                        style="background:var(--surface); border:1px solid var(--border); color:var(--text); padding:4px 8px; border-radius:4px; font-size:13px; width:70px;"
                        onchange="updateProjectMeta()" />
                </div>
                <div class="header-status">
                    <button class="btn" style="font-size:12px;" onclick="toggleBuildsFlyout()">📦 Builds</button>
                    <button class="btn" style="font-size:12px;" onclick="exportDefinitions()">📤 Export</button>
                    <button class="btn" style="font-size:12px;" onclick="importDefinitions()">📥 Import</button>
                    <button class="btn btn-sessions" id="sessionsBtn" onclick="toggleSessionFlyout()">🗂 <span id="sessionLabel">Session</span></button>
                    <button class="btn btn-commit" id="commitBtn" onclick="commitToDisk()">💾 Commit</button>
                    <span class="status-dot" id="statusDot"></span>
                    <span id="statusText">Ready</span>
                    <div class="user-area" id="userArea"></div>
                </div>
            </div>

            <div id="pipeline-nav"></div>
            <div id="gen-progress"><span class="gen-stage-label" id="genStageLabel">Idle</span><div class="gen-bar-wrap"><div class="gen-bar" id="genBar" style="width:0%"></div></div><span class="gen-elapsed" id="genElapsed">0s</span></div>

            <div id="main">
                <div id="left">
                    <div id="constraints"></div>
                    <div id="prompt-area">
                        <textarea id="prompt-input" rows="3" placeholder="Chat with the agent about your design, or describe what to build..."></textarea>
                        <div id="prompt-actions">
                            <button class="btn btn-primary" onclick="sendChat()" style="background:var(--purple);border-color:var(--purple);">💬 Chat</button>
                            <button class="btn btn-primary" id="generateBtn" onclick="submitPrompt()">Ψ Generate</button>
                            <button class="btn btn-secondary" onclick="submitPrompt('regenerate')">↻ Regen</button>
                        </div>
                    </div>
                    <div id="chat"></div>
                    <div id="history-panel">
                        <div class="history-header"><span>📜 History</span><span id="historyCount">0</span></div>
                        <div id="history-list"></div>
                    </div>
                </div>
                <div id="right">
                    <div id="editor-header">
                        <span class="tab active" id="tabGenerated" onclick="showEditorTab('generated')">📄 Code</span>
                        <span class="tab" id="tabUnitTests" onclick="showEditorTab('unitTests')">🧪 Unit</span>
                        <span class="tab" id="tabNfrTests" onclick="showEditorTab('nfrTests')">🎭 NFR</span>
                        <span class="tab" id="tabSoakTests" onclick="showEditorTab('soakTests')">🌊 Soak</span>
                        <span class="tab" id="tabIntTests" onclick="showEditorTab('intTests')">🔗 Integration</span>
                        <span class="tab" id="tabLogs" onclick="showEditorTab('logs')">📋 Logs</span>
                        <span class="tab" id="tabBrowser" onclick="showEditorTab('browser')">🗂 Store</span>
                        <span style="margin-left:auto; font-size:11px;" id="lineCount">0 lines</span>
                    </div>
                    <div id="editor-container" style="display:flex; flex:1; overflow:hidden;">
                        <div id="file-tree" style="display:none;">
                            <div class="tree-header">📁 Store Files</div>
                            <div id="tree-content"></div>
                        </div>
                        <div id="log-panel" style="display:none; flex:1; overflow-y:auto; padding:12px; font-family:monospace; font-size:12px; background:var(--bg); color:var(--text);"></div>
                        <div id="editor" style="flex:1;"></div>
                    </div>
                    <div id="pipeline-stats" style="display:none; padding:8px 12px; border-top:1px solid var(--border); font-size:11px; color:var(--text-dim);">
                        <span id="statUnitTests">🧪 Unit: —</span>
                        <span style="margin-left:12px" id="statNfrTests">🎭 NFR: —</span>
                        <span style="margin-left:12px" id="statSoakTests">🌊 Soak: —</span>
                        <span style="margin-left:12px" id="statIntTests">🔗 Int: —</span>
                        <span style="margin-left:12px" id="statArtifact">📦 —</span>
                    </div>
                </div>
            </div>

            <!-- Session Flyout -->
            <div id="session-overlay" onclick="toggleSessionFlyout()" style="display:none;"></div>
            <div id="session-flyout" style="display:none;">
                <div class="flyout-header">
                    <span>🗂 Sessions</span>
                    <button class="flyout-close" onclick="toggleSessionFlyout()">✕</button>
                </div>
                <div class="flyout-actions">
                    <input type="text" id="newSessionName" placeholder="New session name..." />
                    <button class="btn btn-primary btn-new-session" onclick="createSession()">+ New</button>
                    <button class="btn-share" onclick="shareCurrentSession()">🔗 Share</button>
                </div>
                <div id="session-list"></div>
            </div>

            <!-- Share Dialog -->
            <div id="share-overlay" onclick="closeShareDialog()" style="display:none;"></div>
            <div id="share-dialog" class="share-dialog" style="display:none;">
                <h3>🔗 Share Session</h3>
                <p style="font-size:13px;color:var(--text-dim);margin-bottom:12px;">Anyone with this link can import a copy of your current session.</p>
                <input type="text" class="share-url" id="shareUrl" readonly onclick="this.select()" />
                <div class="share-actions">
                    <button class="btn btn-secondary" onclick="closeShareDialog()">Close</button>
                    <button class="btn btn-primary" onclick="copyShareUrl()">📋 Copy</button>
                </div>
            </div>

            <!-- Builds Flyout -->
            <div id="builds-overlay" onclick="toggleBuildsFlyout()" style="display:none;"></div>
            <div id="builds-flyout" style="display:none; position:fixed; right:0; top:0; bottom:0; width:380px; background:var(--surface); border-left:2px solid var(--accent); z-index:1000; overflow-y:auto; box-shadow:-4px 0 24px rgba(0,0,0,0.3);">
                <div class="flyout-header">
                    <span>📦 Build History</span>
                    <button class="flyout-close" onclick="toggleBuildsFlyout()">✕</button>
                </div>
                <div id="builds-list" style="padding:8px;"></div>
            </div>

            <script src="https://cdn.jsdelivr.net/npm/monaco-editor@0.45.0/min/vs/loader.js"></script>
            <script>
                require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.45.0/min/vs' }});
                let editor;

                // Unique client ID per tab for SSE dedup
                const MY_CLIENT_ID = crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2);

                require(['vs/editor/editor.main'], function () {
                    editor = monaco.editor.create(document.getElementById('editor'), {
                        value: '// Waiting for generation...',
                        language: 'csharp',
                        theme: 'vs-dark',
                        readOnly: true,
                        minimap: { enabled: true },
                        fontSize: 13,
                        scrollBeyondLastLine: false,
                        padding: { top: 12 },
                        renderLineHighlight: 'none',
                    });
                    window.addEventListener('resize', () => editor.layout());
                    initSSE();
                    pollLoop();
                });

                // ── SSE Connection ──
                let sseConnected = false;
                let clientCount = 0;

                function initSSE() {
                    const es = new EventSource(`/api/events?clientId=${MY_CLIENT_ID}`);

                    es.addEventListener('full-sync', e => {
                        const d = JSON.parse(e.data);
                        if (d.state) {
                            currentState = d.state;
                            renderPipelineNav();
                            renderConstraints();
                            syncProjectMeta();
                        }
                        if (d.code && editor && currentTab !== 'browser') {
                            editor.setValue(d.code);
                            document.getElementById('lineCount').textContent = d.code.split('\n').length + ' lines';
                        }
                        if (d.generating !== undefined) updateGeneratingUI(d.generating);
                        if (d.clients) clientCount = d.clients;
                        updateClientsBadge();
                        sseConnected = true;
                    });

                    es.addEventListener('state', e => {
                        const d = JSON.parse(e.data);
                        Object.assign(currentState, d);
                        renderPipelineNav();
                        renderConstraints();
                        syncProjectMeta();
                    });

                    es.addEventListener('chat', e => {
                        const d = JSON.parse(e.data);
                        if (d.appliedActionId) {
                            // Mark action as applied in existing chat
                            const btn = document.querySelector(`[data-action-id="${d.appliedActionId}"]`);
                            if (btn) { btn.className = 'chat-action-btn applied'; btn.disabled = true; btn.innerHTML = '✅ ' + btn.textContent; }
                        }
                        let actionsHtml = '';
                        if (d.actions && d.actions.length > 0) {
                            actionsHtml = `<div class="chat-actions">${d.actions.map(a =>
                                a.Applied
                                    ? `<button class="chat-action-btn applied" disabled>✅ ${escHtml(a.Label)} <span class="action-layer">${a.Layer}</span></button>`
                                    : `<button class="chat-action-btn" data-action-id="${a.Id}" onclick="applyAction('${a.Id}')">▶ ${escHtml(a.Label)} <span class="action-layer">${a.Layer}</span></button>`
                            ).join('')}</div>`;
                        }
                        appendChatEntry(d.role, d.message, actionsHtml);
                    });

                    es.addEventListener('code', e => {
                        const d = JSON.parse(e.data);
                        if (editor && currentTab !== 'browser') {
                            editor.setValue(d.code);
                            document.getElementById('lineCount').textContent = d.code.split('\n').length + ' lines';
                        }
                    });

                    es.addEventListener('generating', e => {
                        const d = JSON.parse(e.data);
                        updateGeneratingUI(d.generating);
                    });

                    es.addEventListener('progress', e => {
                        const d = JSON.parse(e.data);
                        updateProgress(d);
                    });

                    es.addEventListener('ping', e => {
                        const d = JSON.parse(e.data);
                        if (d.clients) { clientCount = d.clients; updateClientsBadge(); }
                    });

                    es.addEventListener('test-result', e => {
                        const d = JSON.parse(e.data);
                        const logPanel = document.getElementById('log-panel');
                        const status = d.passed ? '✅' : '❌';
                        logPanel.innerHTML += `<div style="margin:4px 0;"><strong>${status} ${d.runner} (${d.category})</strong> — exit ${d.exitCode}</div>`;
                        if (d.output) logPanel.innerHTML += `<pre style="margin:0 0 8px; padding:8px; background:var(--surface); border-radius:4px; overflow-x:auto; font-size:11px;">${escHtml(d.output)}</pre>`;
                        if (d.errors) logPanel.innerHTML += `<pre style="margin:0 0 8px; padding:8px; background:#2d1b1b; border-radius:4px; overflow-x:auto; font-size:11px; color:#f85149;">${escHtml(d.errors)}</pre>`;
                        logPanel.scrollTop = logPanel.scrollHeight;
                        // Update stats bar
                        const statMap = { nfr: 'statNfrTests', soak: 'statSoakTests', integration: 'statIntTests' };
                        const statEl = document.getElementById(statMap[d.category]);
                        if (statEl) statEl.textContent = `${status} ${d.runner}: exit ${d.exitCode}`;
                        document.getElementById('pipeline-stats').style.display = 'flex';
                    });

                    es.addEventListener('artifact', e => {
                        const d = JSON.parse(e.data);
                        const statEl = document.getElementById('statArtifact');
                        statEl.textContent = `📦 ${d.timestamp}`;
                        document.getElementById('pipeline-stats').style.display = 'flex';
                    });

                    es.onerror = () => { sseConnected = false; };
                    es.onopen = () => { sseConnected = true; };
                }

                function updateGeneratingUI(generating) {
                    const dot = document.getElementById('statusDot');
                    const text = document.getElementById('statusText');
                    const btn = document.getElementById('generateBtn');
                    if (generating) {
                        dot.className = 'status-dot busy';
                        text.textContent = 'Generating...';
                        btn.disabled = true;
                        genStartTime = Date.now();
                        genTimerInterval = setInterval(updateGenTimer, 500);
                    } else {
                        dot.className = 'status-dot';
                        text.textContent = 'Ready';
                        btn.disabled = false;
                        clearInterval(genTimerInterval);
                        // Clear progress panel after a delay
                        setTimeout(() => {
                            const gp = document.getElementById('gen-progress');
                            if (gp) gp.className = '';
                            clearPipelineRunState();
                        }, 5000);
                    }
                }

                let genStartTime = 0;
                let genTimerInterval = null;
                let genStageStartTime = 0;
                const GEN_STAGES = ['Init', 'Interfaces', 'Unit Tests', 'Code', 'Build & Test', 'NFR Tests', 'Soak Tests', 'Integration', 'Infrastructure', 'Publish'];

                function updateGenTimer() {
                    const el = document.getElementById('genElapsed');
                    if (el && genStartTime) {
                        const s = ((Date.now() - genStartTime) / 1000).toFixed(0);
                        el.textContent = s + 's';
                    }
                }

                function updateProgress(d) {
                    const gp = document.getElementById('gen-progress');
                    const label = document.getElementById('genStageLabel');
                    const bar = document.getElementById('genBar');

                    if (d.status === 'complete') {
                        gp.className = 'active';
                        label.textContent = `✅ Pipeline complete — ${d.detail}`;
                        bar.style.width = '100%';
                        bar.style.background = 'linear-gradient(90deg, var(--green), #34d399)';
                        return;
                    }
                    if (d.status === 'fail') {
                        gp.className = 'active';
                        label.textContent = `❌ ${d.name} — ${d.detail}`;
                        bar.style.background = '#f85149';
                        return;
                    }

                    gp.className = 'active';
                    const pct = Math.round((d.stage / d.total) * 100);

                    if (d.status === 'running') {
                        label.innerHTML = `<span style="color:var(--accent)">⟳ ${d.name}</span> <span style="color:var(--text-dim);font-weight:400">${d.detail}</span>`;
                        bar.style.width = Math.max(pct - 5, 2) + '%';
                        bar.style.background = 'linear-gradient(90deg, var(--accent), var(--accent2))';
                        genStageStartTime = Date.now();
                    } else if (d.status === 'done') {
                        const stageTime = genStageStartTime ? ((Date.now() - genStageStartTime) / 1000).toFixed(1) : '?';
                        label.innerHTML = `<span style="color:var(--green)">✓ ${d.name}</span> <span style="color:var(--text-dim);font-weight:400">${d.detail} (${stageTime}s)</span>`;
                        bar.style.width = pct + '%';
                    }

                    // Update pipeline chevrons with running/done state
                    updatePipelineStageState(d);
                }

                function updatePipelineStageState(d) {
                    const nav = document.getElementById('pipeline-nav');
                    if (!nav) return;
                    const arrows = nav.querySelectorAll('.pipeline-arrow');
                    arrows.forEach(arrow => {
                        const stages = (arrow.dataset.genStages || '').split(',').map(Number);
                        if (!stages.length) return;
                        const maxStage = Math.max(...stages);
                        const minStage = Math.min(...stages);
                        if (maxStage < d.stage) {
                            // All gen stages for this chevron are done
                            arrow.classList.remove('running');
                            arrow.classList.add('stage-done');
                        } else if (stages.includes(d.stage) && d.status === 'running') {
                            arrow.classList.remove('stage-done', 'stage-fail');
                            arrow.classList.add('running');
                        } else if (stages.includes(d.stage) && d.status === 'done' && d.stage === maxStage) {
                            arrow.classList.remove('running');
                            arrow.classList.add('stage-done');
                        } else if (stages.includes(d.stage) && d.status === 'fail') {
                            arrow.classList.remove('running');
                            arrow.classList.add('stage-fail');
                        }
                    });
                }

                function clearPipelineRunState() {
                    document.querySelectorAll('.pipeline-arrow').forEach(a => {
                        a.classList.remove('running', 'stage-done', 'stage-fail');
                    });
                }

                function updateClientsBadge() {
                    let badge = document.getElementById('clientsBadge');
                    if (!badge) {
                        badge = document.createElement('span');
                        badge.id = 'clientsBadge';
                        badge.style.cssText = 'font-size:10px;background:#3fb950;color:#000;padding:1px 6px;border-radius:8px;font-weight:700;margin-left:4px;';
                        document.getElementById('statusText').after(badge);
                    }
                    badge.textContent = clientCount > 1 ? `${clientCount} online` : '';
                }

                function appendChatEntry(role, message, actionsHtml = '') {
                    const el = document.getElementById('chat');
                    const t = new Date().toLocaleTimeString();
                    const div = document.createElement('div');
                    div.className = `chat-entry ${role}`;
                    div.innerHTML = `<div class="chat-time">${t}</div>${escHtml(message)}${actionsHtml}`;
                    el.appendChild(div);
                    el.scrollTop = el.scrollHeight;
                    lastChatLen++;
                }

                // Helper: fetch with clientId header
                function apiFetch(url, opts = {}) {
                    opts.headers = { ...(opts.headers || {}), 'X-Client-Id': MY_CLIENT_ID };
                    return fetch(url, opts);
                }

                const LAYER_GROUPS = [
                    { name: 'Intent', icon: '💡', layers: ['description', 'personas'], genStages: [0] },
                    { name: 'Constraints', icon: '📏', layers: ['rules', 'invariants'], genStages: [1] },
                    { name: 'Shape', icon: '🏗️', layers: ['architecture', 'dataflow', 'frameworks', 'language', 'deployment'], genStages: [1,2,3] },
                    { name: 'Behaviour', icon: '🎭', layers: ['features', 'stories', 'nfr'], genStages: [2,3] },
                    { name: 'Quality', icon: '🎚️', layers: [], genStages: [4,5,6,7] },
                    { name: 'Finetune', icon: '🔧', layers: ['architectureTweaks', 'codeTweaks', 'testTweaks'], genStages: [3,4] },
                    { name: 'Deploy', icon: '🚀', layers: ['iac', 'deployTweaks'], genStages: [8,9] },
                ];
                const LAYERS = LAYER_GROUPS.flatMap(g => g.layers);
                const LAYER_LABELS = {
                    description: 'Description', personas: 'Personas',
                    rules: 'Rules', invariants: 'Invariants',
                    architecture: 'Architecture', dataflow: 'Dataflow', frameworks: 'Frameworks & Tools', language: 'Language', deployment: 'Deployment',
                    features: 'Features', stories: 'Stories', nfr: 'NFR',
                    architectureTweaks: 'Architecture Tweaks', codeTweaks: 'Code Tweaks (per file)', testTweaks: 'Test Tweaks (per file)',
                    iac: 'IaC Templates', deployTweaks: 'Deploy Tweaks (per file)',
                };

                let activeStage = 0;

                function stageItemCount(idx) {
                    const group = LAYER_GROUPS[idx];
                    if (idx === 4) return Object.keys(currentState.sliders || {}).length;
                    return group.layers.reduce((sum, l) => {
                        const val = currentState[l];
                        if (typeof val === 'string') return sum + (val.length > 0 ? 1 : 0);
                        return sum + Object.keys(val || {}).length;
                    }, 0);
                }

                function renderPipelineNav() {
                    const nav = document.getElementById('pipeline-nav');
                    nav.innerHTML = LAYER_GROUPS.map((g, i) => {
                        const count = stageItemCount(i);
                        const cls = i === activeStage ? 'active' : (count > 0 && i < activeStage ? 'completed' : '');
                        return `<div class="pipeline-arrow ${cls}" data-gen-stages="${g.genStages.join(',')}" onclick="setStage(${i})">
                            <span class="arrow-icon">${g.icon}</span>
                            <span>${g.name}</span>
                            ${count > 0 ? `<span class="arrow-badge">${count}</span>` : ''}
                            <span class="stage-time" data-stage-time="${i}"></span>
                        </div>`;
                    }).join('');
                }

                function setStage(idx) {
                    activeStage = idx;
                    renderPipelineNav();
                    renderConstraints();
                }

                // Periodically sync state from server to catch any drift
                setInterval(async () => {
                    try {
                        const r = await fetch('/api/state');
                        if (r.ok) {
                            const newState = await r.json();
                            // Only update if we're not mid-edit (check if any textarea is focused)
                            if (!document.activeElement || document.activeElement.tagName !== 'TEXTAREA') {
                                currentState = newState;
                            } else {
                                // Merge non-editing layers
                                for (const [k,v] of Object.entries(newState)) {
                                    if (!document.querySelector(`[data-layer="${k}"]`)) {
                                        currentState[k] = v;
                                    }
                                }
                            }
                        }
                    } catch {}
                }, 10000);

                const PRESETS = {
                    description: [
                        { label: '🛒 E-commerce Platform', items: { 'overview': 'An online marketplace where users can browse products, add to cart, and checkout with payment processing.', 'goals': 'Enable sellers to list products, buyers to purchase them, with reviews, search, and order tracking.' }},
                        { label: '📝 Task Manager', items: { 'overview': 'A task/project management tool for teams to organize work into boards, lists, and cards.', 'goals': 'Track progress, assign tasks, set deadlines, and collaborate with comments and attachments.' }},
                        { label: '💬 Chat Application', items: { 'overview': 'A real-time messaging platform supporting direct messages, group chats, and channels.', 'goals': 'Enable instant communication with presence indicators, typing status, message history, and file sharing.' }},
                        { label: '📊 Analytics Dashboard', items: { 'overview': 'A data visualization dashboard that aggregates metrics from multiple sources into charts and reports.', 'goals': 'Provide real-time KPIs, customizable widgets, date range filtering, and exportable reports.' }},
                        { label: '🏥 Appointment Booking', items: { 'overview': 'A scheduling system for service providers (medical, salon, consulting) and their clients.', 'goals': 'Allow clients to book time slots, receive reminders, reschedule, and manage recurring appointments.' }},
                    ],
                    rules: [
                        { label: '🧱 Pure Functions', items: { 'pure-functions': 'All business logic must be implemented as pure functions with no side effects.' }},
                        { label: '🎯 Single Responsibility', items: { 'single-responsibility': 'Each class/module must have exactly one responsibility.' }},
                        { label: '🔒 Immutability', items: { 'immutable-data': 'All data structures must be immutable. Use new instances instead of mutation.' }},
                        { label: '📜 No Exceptions for Flow', items: { 'no-exception-flow': 'Exceptions must not be used for control flow. Use Result/Option types instead.' }},
                        { label: '🧪 TDD First', items: { 'tdd-first': 'No production code may be written without a failing test first.' }},
                        { label: '📏 Max Method Length', items: { 'short-methods': 'No method may exceed 20 lines of code. Extract smaller methods if needed.' }},
                        { label: '🚫 No Static State', items: { 'no-static-state': 'No mutable static/global state. All state must be passed explicitly.' }},
                        { label: '🔗 Dependency Injection', items: { 'dependency-injection': 'All dependencies must be injected via constructor. No service locator pattern.' }},
                    ],
                    personas: [
                        { label: '👤 Admin / End User', items: { 'admin': 'System administrator who manages users, settings, and system configuration.', 'end-user': 'Regular user who interacts with core features of the application.' }},
                        { label: '👥 Multi-role SaaS', items: { 'owner': 'Organization owner with full access and billing control.', 'manager': 'Team manager who can assign work and view reports.', 'member': 'Team member who performs tasks and updates progress.', 'viewer': 'Read-only stakeholder who views dashboards and reports.' }},
                        { label: '🛍️ Marketplace', items: { 'buyer': 'Customer who browses, searches, and purchases products.', 'seller': 'Merchant who lists products, manages inventory, and fulfills orders.', 'support-agent': 'Customer support representative handling disputes and inquiries.' }},
                        { label: '🔧 Developer Platform', items: { 'developer': 'API consumer who integrates and builds on top of the platform.', 'platform-admin': 'Internal admin who manages API keys, rate limits, and access.', 'reviewer': 'Code/content reviewer who approves or rejects submissions.' }},
                    ],
                    architecture: [
                        { label: '📦 Layered (Clean)', items: { 'layered': 'Use Clean Architecture: Domain → Application → Infrastructure → Presentation. Dependencies point inward.' }},
                        { label: '🔌 Hexagonal / Ports & Adapters', items: { 'hexagonal': 'Use Hexagonal Architecture with ports (interfaces) and adapters (implementations). Core has zero external dependencies.' }},
                        { label: '📡 Event-Driven', items: { 'event-driven': 'Use event-driven architecture. Components communicate via events/messages, not direct calls.', 'event-store': 'Persist all state changes as an append-only event log.' }},
                        { label: '🧩 Microservices', items: { 'microservices': 'Decompose into independent services with own data stores. Communicate via API/messaging.', 'api-gateway': 'Use an API gateway for routing, auth, and rate limiting.' }},
                        { label: '🗂️ CQRS', items: { 'cqrs': 'Separate read and write models. Commands mutate state, Queries return projections.', 'eventual-consistency': 'Read models may be eventually consistent with write model.' }},
                        { label: '🌐 REST API + SPA', items: { 'rest-api': 'Backend exposes RESTful API with JSON. Frontend is a single-page application.', 'stateless-api': 'API must be stateless. Auth via tokens, no server-side sessions.' }},
                    ],
                    dataflow: [
                        { label: '🔄 Request-Response', items: { 'req-resp': 'Client sends request, server processes synchronously and returns response.' }},
                        { label: '📨 Message Queue', items: { 'message-queue': 'Producers publish messages to queues. Consumers process asynchronously.', 'dead-letter': 'Failed messages go to a dead-letter queue for inspection and retry.' }},
                        { label: '🔀 Pub/Sub', items: { 'pub-sub': 'Publishers emit events to topics. Multiple subscribers consume independently.', 'fan-out': 'Events are fanned out to all interested subscribers.' }},
                        { label: '🌊 Stream Processing', items: { 'event-stream': 'Data flows as a continuous stream of events processed in order.', 'windowing': 'Aggregate events over time windows for analytics.' }},
                        { label: '🔁 ETL Pipeline', items: { 'extract': 'Extract data from source systems on a schedule.', 'transform': 'Transform and validate data in a staging area.', 'load': 'Load cleaned data into the target data store.' }},
                    ],
                    frameworks: [
                        { label: '⚛️ React + Node', items: { 'frontend': 'React 18+ with TypeScript for the UI.', 'backend': 'Node.js with Express for the API server.', 'orm': 'Prisma ORM for database access.' }},
                        { label: '🅰️ Angular + .NET', items: { 'frontend': 'Angular 17+ with TypeScript.', 'backend': '.NET 8+ Web API with C#.', 'orm': 'Entity Framework Core for database access.' }},
                        { label: '🐍 Python FastAPI', items: { 'backend': 'FastAPI with Python 3.12+.', 'orm': 'SQLAlchemy for database access.', 'validation': 'Pydantic for request/response validation.' }},
                        { label: '☕ Spring Boot', items: { 'backend': 'Spring Boot 3+ with Java 21.', 'orm': 'Spring Data JPA with Hibernate.', 'security': 'Spring Security for auth.' }},
                        { label: '🦀 Rust Actix', items: { 'backend': 'Actix-web with Rust for high-performance API.', 'orm': 'Diesel or SQLx for database access.' }},
                        { label: '🐹 Go Chi/Fiber', items: { 'backend': 'Go with Chi or Fiber router.', 'orm': 'GORM or sqlc for database access.' }},
                    ],
                    language: [
                        { label: '🟦 TypeScript', items: { 'language': 'TypeScript with strict mode enabled.', 'style': 'Use functional style where possible. Prefer const, arrow functions, and immutable data.' }},
                        { label: '💜 C#', items: { 'language': 'C# 12+ targeting .NET 8+.', 'style': 'Use records for DTOs, pattern matching, and LINQ for collections.' }},
                        { label: '🐍 Python', items: { 'language': 'Python 3.12+ with type hints throughout.', 'style': 'Follow PEP 8. Use dataclasses, type hints, and async/await.' }},
                        { label: '☕ Java', items: { 'language': 'Java 21+ with records and sealed classes.', 'style': 'Use streams, Optional, and pattern matching where available.' }},
                        { label: '🦀 Rust', items: { 'language': 'Rust latest stable edition.', 'style': 'Prefer ownership over references. Use Result/Option instead of panics.' }},
                        { label: '🐹 Go', items: { 'language': 'Go 1.22+.', 'style': 'Follow Go idioms. Use error values, interfaces, and goroutines.' }},
                    ],
                    deployment: [
                        { label: '🖥️ Bare Metal / VM', items: { 'target': 'Deploy directly on bare-metal servers or virtual machines.', 'process': 'Run as a systemd service or Windows Service. Reverse proxy via Nginx/Caddy.' }},
                        { label: '☁️ Azure App Service', items: { 'target': 'Deploy to Azure App Service (PaaS).', 'scaling': 'Use built-in auto-scaling and deployment slots.', 'storage': 'Use Azure Blob Storage for persistent files.' }},
                        { label: '☸️ Azure AKS / Kubernetes', items: { 'target': 'Deploy to Azure Kubernetes Service (AKS).', 'containers': 'Package as Docker containers with health probes.', 'scaling': 'Use Horizontal Pod Autoscaler for elastic scaling.', 'ingress': 'Use NGINX Ingress Controller or Azure Application Gateway.' }},
                        { label: '🐳 Docker Compose', items: { 'target': 'Deploy with Docker Compose for single-host setups.', 'containers': 'Each service in its own container with shared network.', 'volumes': 'Use named volumes for data persistence.' }},
                        { label: '⚡ Azure Functions / Serverless', items: { 'target': 'Deploy as Azure Functions (serverless).', 'triggers': 'Use HTTP triggers for APIs, timer triggers for scheduled work.', 'scaling': 'Scales to zero when idle, auto-scales under load.' }},
                        { label: '🌍 AWS ECS / Fargate', items: { 'target': 'Deploy to AWS ECS with Fargate (serverless containers).', 'networking': 'Use ALB for load balancing, VPC for networking.', 'scaling': 'Auto-scaling based on CPU/memory or custom metrics.' }},
                    ],
                    nfr: [
                        { label: '⚡ High Performance', items: { 'response-time': 'All API responses must complete within 200ms at p95.', 'throughput': 'System must handle 1000 concurrent requests.' }},
                        { label: '🔒 Security First', items: { 'auth-required': 'All endpoints must require authentication except health checks.', 'input-validation': 'All user input must be validated and sanitized before processing.', 'encryption': 'All data at rest and in transit must be encrypted.' }},
                        { label: '♿ Accessibility', items: { 'wcag-aa': 'UI must meet WCAG 2.1 AA compliance.', 'keyboard-nav': 'All features must be fully accessible via keyboard navigation.' }},
                        { label: '📈 Scalability', items: { 'horizontal-scaling': 'System must support horizontal scaling with no shared mutable state.', 'caching': 'Frequently accessed data must be cached with configurable TTL.' }},
                        { label: '🧪 Testability', items: { 'code-coverage': 'Minimum 80% code coverage for all modules.', 'integration-tests': 'All API endpoints must have integration tests.' }},
                        { label: '📋 Observability', items: { 'structured-logging': 'All services must use structured logging (JSON format).', 'health-checks': 'Every service must expose a /health endpoint.', 'metrics': 'Expose Prometheus-compatible metrics for latency, errors, and throughput.' }},
                    ],
                    invariants: [
                        { label: '🚫 No Reflection', items: { 'no-reflection': 'Generated code must not use System.Reflection.' }},
                        { label: '🚫 No File I/O', items: { 'no-file-io': 'Generated code must not perform direct file I/O operations.' }},
                        { label: '🚫 No Network Calls', items: { 'no-network': 'Core domain logic must not make any network calls directly.' }},
                        { label: '💰 Money as Decimal', items: { 'decimal-money': 'All monetary values must use decimal types, never floating point.' }},
                        { label: '🔑 IDs are Typed', items: { 'typed-ids': 'Entity IDs must be strongly typed (not raw strings/ints) to prevent misuse.' }},
                        { label: '📦 No Null Returns', items: { 'no-null-returns': 'Public methods must never return null. Use Option/Maybe types or empty collections.' }},
                        { label: '🔒 Audit Trail', items: { 'audit-trail': 'All state-changing operations must produce an audit log entry with who/what/when.' }},
                    ],
                    stories: [
                        { label: '🔐 Auth Stories', items: { 'user-registration': 'As a user, I can register with email and password so I can access the system.', 'user-login': 'As a user, I can log in with my credentials so I can use protected features.', 'password-reset': 'As a user, I can reset my password via email so I can regain access.' }},
                        { label: '📋 CRUD Stories', items: { 'create-item': 'As a user, I can create a new item with a name and description.', 'list-items': 'As a user, I can view a paginated list of all my items.', 'edit-item': 'As a user, I can edit an existing item I own.', 'delete-item': 'As a user, I can delete an item I own with a confirmation prompt.' }},
                        { label: '🔍 Search & Filter', items: { 'full-text-search': 'As a user, I can search across all content by keyword.', 'filter-by-status': 'As a user, I can filter items by status (active/archived/draft).', 'sort-results': 'As a user, I can sort results by date, name, or relevance.' }},
                        { label: '🔔 Notifications', items: { 'in-app-notifications': 'As a user, I receive in-app notifications for important events.', 'email-notifications': 'As a user, I receive email notifications for critical updates.', 'notification-preferences': 'As a user, I can configure which notifications I receive.' }},
                        { label: '👥 Collaboration', items: { 'invite-members': 'As an owner, I can invite others to collaborate on my workspace.', 'role-assignment': 'As an owner, I can assign roles (admin/editor/viewer) to members.', 'activity-feed': 'As a member, I can see a feed of recent changes by all collaborators.' }},
                    ],
                    features: [
                        { label: '🔐 Authentication', items: { 'user-auth': 'User registration, login, logout, and session management.', 'oauth': 'Social login via OAuth2 providers (Google, GitHub).' }},
                        { label: '📊 Dashboard', items: { 'overview-dashboard': 'Main dashboard with KPIs, charts, and activity feed.', 'customizable-widgets': 'Users can add, remove, and rearrange dashboard widgets.' }},
                        { label: '🔍 Search', items: { 'full-text-search': 'Global full-text search across all entities.', 'advanced-filters': 'Filter and sort by multiple criteria with saved filter presets.' }},
                        { label: '📨 Messaging', items: { 'direct-messages': 'One-to-one messaging between users.', 'group-chat': 'Group conversations with member management.', 'file-sharing': 'Share files and images within conversations.' }},
                        { label: '📋 CRUD Operations', items: { 'create-read-update-delete': 'Full CRUD for core entities with validation.', 'bulk-operations': 'Bulk select, edit, and delete operations.', 'import-export': 'Import from CSV/JSON and export data.' }},
                    ],
                };
                let currentState = {};
                let lastChatLen = 0;

                const SLIDER_META = {
                    'performance':   { lo: 'Optional', hi: 'Critical', icon: '⚡' },
                    'latency':       { lo: 'Relaxed', hi: 'Ultra-low', icon: '🏎️' },
                    'ui-polish':     { lo: 'Minimal', hi: 'Polished', icon: '✨' },
                    'simplicity':    { lo: 'Sophisticated', hi: 'Simple', icon: '🧊' },
                    'readability':   { lo: 'Terse', hi: 'Verbose', icon: '📖' },
                    'conciseness':   { lo: 'Explicit', hi: 'Concise', icon: '✂️' },
                    'security':      { lo: 'Basic', hi: 'Hardened', icon: '🔒' },
                    'test-coverage': { lo: 'Happy-path', hi: 'Exhaustive', icon: '🧪' },
                    'error-handling':{ lo: 'Fail-fast', hi: 'Resilient', icon: '🛡️' },
                    'abstraction':   { lo: 'Concrete', hi: 'Abstract', icon: '🧩' },
                    'layering':      { lo: 'Flat', hi: 'Strict layers', icon: '🏗️' },
                    'solid':         { lo: 'Pragmatic', hi: 'Strict SOLID', icon: '📐' },
                };

                function buildSlidersHtml() {
                    const sliders = currentState.sliders || {};
                    return Object.entries(SLIDER_META).map(([key, meta]) => {
                        const val = sliders[key] ?? 50;
                        return `<div class="slider-row">
                            <label>${meta.icon} ${key.replace(/-/g,' ')}</label>
                            <span class="slider-lo">${meta.lo}</span>
                            <input type="range" min="0" max="100" value="${val}" data-slider="${key}" oninput="this.nextElementSibling.textContent=this.value" />
                            <span class="slider-val">${val}</span>
                            <span class="slider-hi">${meta.hi}</span>
                        </div>`;
                    }).join('') + `<div class="slider-save"><button class="btn-sm save" onclick="saveSliders()">💾 Save sliders</button></div>`;
                }

                const PIPELINE_STAGE_META = {
                    interfaces:       { label: 'Interfaces',              icon: '📐', desc: 'Generate interface/contract definitions' },
                    unitTests:        { label: 'Unit Tests',              icon: '🧪', desc: 'Generate unit test cases' },
                    code:             { label: 'Code Generation',         icon: '💻', desc: 'Generate implementation code' },
                    build:            { label: 'Build & Test Loop',       icon: '🔨', desc: 'Compile and run unit tests with retry' },
                    nfrTests:         { label: 'NFR Tests (Playwright)',   icon: '🎭', desc: 'Generate and run browser/UI tests' },
                    soakTests:        { label: 'Soak Tests (Locust)',     icon: '🌊', desc: 'Generate and run load/performance tests' },
                    integrationTests: { label: 'Integration Tests (Jest)', icon: '🔗', desc: 'Generate and run integration tests' },
                    iac:              { label: 'Infrastructure as Code',  icon: '☁️', desc: 'Generate deployment/IaC artifacts' },
                    publish:          { label: 'Publish Artifact',        icon: '📦', desc: 'Package and publish build artifact' },
                };

                function buildPipelineConfigHtml() {
                    const cfg = currentState.pipelineConfig || {};
                    return Object.entries(PIPELINE_STAGE_META).map(([key, meta]) => {
                        const enabled = cfg[key] !== false;
                        return `<div style="display:flex;align-items:center;gap:8px;padding:3px 8px;">
                            <input type="checkbox" id="pipe-${key}" data-pipe="${key}" ${enabled ? 'checked' : ''}
                                onchange="savePipelineConfig()" style="accent-color:var(--accent);cursor:pointer;" />
                            <label for="pipe-${key}" style="flex:1;cursor:pointer;font-size:12px;color:${enabled ? 'var(--text)' : 'var(--text-dim)'};">
                                ${meta.icon} ${meta.label}
                                <span style="font-size:10px;color:var(--text-dim);margin-left:4px;">${meta.desc}</span>
                            </label>
                        </div>`;
                    }).join('');
                }

                async function savePipelineConfig() {
                    const pipelineConfig = {};
                    document.querySelectorAll('[data-pipe]').forEach(cb => {
                        pipelineConfig[cb.dataset.pipe] = cb.checked;
                    });
                    currentState.pipelineConfig = pipelineConfig;
                    await apiFetch('/api/state', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ pipelineConfig }) });
                    renderPipelineNav();
                }

                function renderSliders() {
                    if (activeStage === 4) {
                        const el = document.getElementById('constraints');
                        if (el) {
                            // Update slider values in-place if already rendered
                            const sliders = currentState.sliders || {};
                            el.querySelectorAll('input[type=range][data-slider]').forEach(inp => {
                                const val = sliders[inp.dataset.slider] ?? 50;
                                inp.value = val;
                                const valSpan = inp.nextElementSibling;
                                if (valSpan) valSpan.textContent = val;
                            });
                        }
                    }
                }

                async function saveSliders() {
                    const sliders = {};
                    document.querySelectorAll('#constraints input[type=range]').forEach(inp => {
                        sliders[inp.dataset.slider] = parseInt(inp.value);
                    });
                    currentState.sliders = sliders;
                    await apiFetch('/api/state', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ sliders }) });
                    renderPipelineNav();
                }

                async function pollLoop() {
                    await refreshAll();
                    setInterval(refreshAll, 5000); // Slower poll as SSE handles real-time
                }

                async function refreshAll() {
                    await Promise.all([refreshCode(), refreshChat(), refreshHistory(), refreshStatus()]);
                }

                async function refreshCode() {
                    if (currentTab === 'browser' && activeFile) return;
                    if (sseConnected) return; // SSE handles this
                    try {
                        const r = await fetch('/api/code');
                        const j = await r.json();
                        if (editor) {
                            monaco.editor.setModelLanguage(editor.getModel(), 'csharp');
                            if (editor.getValue() !== j.code) {
                                editor.setValue(j.code);
                            }
                            document.getElementById('lineCount').textContent = j.code.split('\n').length + ' lines';
                        }
                    } catch {}
                }

                async function refreshChat() {
                    try {
                        const r = await fetch('/api/chat');
                        const entries = await r.json();
                        if (entries.length === lastChatLen) return;
                        lastChatLen = entries.length;
                        const el = document.getElementById('chat');
                        el.innerHTML = entries.map(e => {
                            const t = new Date(e.Timestamp).toLocaleTimeString();
                            let actionsHtml = '';
                            if (e.Actions && e.Actions.length > 0) {
                                actionsHtml = `<div class="chat-actions">${e.Actions.map(a =>
                                    a.Applied
                                        ? `<button class="chat-action-btn applied" disabled>✅ ${escHtml(a.Label)} <span class="action-layer">${a.Layer}</span></button>`
                                        : `<button class="chat-action-btn" onclick="applyAction('${a.Id}')">▶ ${escHtml(a.Label)} <span class="action-layer">${a.Layer}</span></button>`
                                ).join('')}</div>`;
                            }
                            return `<div class="chat-entry ${e.Role}"><div class="chat-time">${t}</div>${escHtml(e.Message)}${actionsHtml}</div>`;
                        }).join('');
                        el.scrollTop = el.scrollHeight;
                    } catch {}
                }

                async function refreshHistory() {
                    try {
                        const r = await fetch('/api/history');
                        const items = await r.json();
                        document.getElementById('historyCount').textContent = items.length;
                        const el = document.getElementById('history-list');
                        el.innerHTML = items.map(h => {
                            const t = new Date(h.Timestamp).toLocaleTimeString();
                            return `<div class="history-item"><span class="history-label">#${h.Index} ${escHtml(h.Label)}</span><span class="history-time">${t}</span><button class="btn-revert" onclick="revertTo(${h.Index})">↩ Revert</button></div>`;
                        }).reverse().join('');
                    } catch {}
                }

                async function refreshStatus() {
                    try {
                        const r = await fetch('/api/generating');
                        const j = await r.json();
                        const dot = document.getElementById('statusDot');
                        const text = document.getElementById('statusText');
                        const btn = document.getElementById('generateBtn');
                        if (j.generating) {
                            dot.className = 'status-dot busy';
                            text.textContent = 'Generating...';
                            btn.disabled = true;
                        } else {
                            dot.className = 'status-dot';
                            text.textContent = 'Ready';
                            btn.disabled = false;
                        }
                    } catch {}
                }

                async function refreshState() {
                    try {
                        const r = await fetch('/api/state');
                        currentState = await r.json();
                        renderPipelineNav();
                        renderConstraints();
                        syncProjectMeta();
                    } catch {}
                }

                function renderConstraints() {
                    const el = document.getElementById('constraints');
                    const group = LAYER_GROUPS[activeStage];

                    // Stage 4 = Quality Sliders + Pipeline Config
                    if (activeStage === 4) {
                        const slidersHtml = buildSlidersHtml();
                        const pipelineHtml = buildPipelineConfigHtml();
                        el.innerHTML = `<div class="layer-group" style="padding:8px;">
                            <div class="layer-group-title">${group.icon} ${group.name}</div>
                            ${slidersHtml}
                            <div class="layer-group-title" style="margin-top:12px;">⚙️ Pipeline Stages</div>
                            <div style="padding:4px 8px;font-size:11px;color:var(--text-dim);margin-bottom:4px;">Toggle which stages run during generation</div>
                            ${pipelineHtml}
                        </div>`;
                        return;
                    }

                    const STRING_LAYERS = ['architectureTweaks']; // single-string layers

                    const groupHtml = group.layers.map(layer => {
                        // Single-string layer (e.g. architectureTweaks)
                        if (STRING_LAYERS.includes(layer)) {
                            const val = currentState[layer] || '';
                            return `<div class="constraint-section">
                                <div class="constraint-header" onclick="toggleSection(this)">
                                    <span>${LAYER_LABELS[layer]}</span>
                                    <span class="badge">${val.length > 0 ? '✓' : '—'}</span>
                                </div>
                                <div class="constraint-body open" id="body-${layer}">
                                    <div style="padding:4px;">
                                        <textarea id="string-${layer}" style="width:100%;min-height:100px;background:var(--bg);border:1px solid var(--border);color:var(--text);padding:8px;border-radius:4px;font-size:12px;resize:vertical;font-family:inherit;"
                                            placeholder="Enter global ${LAYER_LABELS[layer].toLowerCase()}...">${escHtml(val)}</textarea>
                                    </div>
                                    <div class="constraint-actions">
                                        <button class="btn-sm save" onclick="saveStringLayer('${layer}')">💾 Save</button>
                                        <button class="btn-sm" onclick="enrichLayer('${layer}', true)" style="color:#a78bfa;">✨ Enrich</button>
                                        <button class="btn-sm" onclick="clearLayer('${layer}')" style="color:#f85149;">🗑️ Clear</button>
                                    </div>
                                </div>
                            </div>`;
                        }

                        // Dict-based layer (per-file key/value)
                        const data = currentState[layer] || {};
                        const entries = Object.entries(data);
                        return `<div class="constraint-section">
                            <div class="constraint-header" onclick="toggleSection(this)">
                                <span>${LAYER_LABELS[layer]}</span>
                                <span class="badge">${entries.length}</span>
                            </div>
                            <div class="constraint-body open" id="body-${layer}">
                                ${entries.map(([k,v]) => `<div class="constraint-item">
                                    <input value="${escAttr(k)}" data-layer="${layer}" data-oldkey="${escAttr(k)}" />
                                    <textarea data-layer="${layer}" data-key="${escAttr(k)}">${escHtml(v)}</textarea>
                                    <button class="btn-del" onclick="this.parentElement.remove()" title="Remove item">✕</button>
                                </div>`).join('')}
                                <div class="constraint-actions" style="position:relative;">
                                    <button class="btn-sm" onclick="addConstraint('${layer}')">+ Add</button>
                                    <button class="btn-sm save" onclick="saveConstraints('${layer}')">💾 Save</button>
                                    <button class="btn-sm" onclick="enrichLayer('${layer}')" style="color:#a78bfa;">✨ Enrich</button>
                                    <button class="btn-sm" onclick="clearLayer('${layer}')" style="color:#f85149;">🗑️ Clear</button>
                                    ${PRESETS[layer] ? `<button class="btn-sm gallery" onclick="togglePresets('${layer}', this)">📋 Quick Fill</button>` : ''}
                                </div>
                            </div>
                        </div>`;
                    }).join('');
                    el.innerHTML = `<div class="layer-group">
                        <div class="layer-group-title">${group.icon} ${group.name}</div>
                        ${groupHtml}
                    </div>`;
                    renderPipelineNav();
                }

                async function saveStringLayer(layer) {
                    const val = document.getElementById('string-' + layer).value;
                    currentState[layer] = val;
                    const payload = {};
                    payload[layer] = val;
                    await apiFetch('/api/state', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) });
                    renderPipelineNav();
                }

                async function enrichLayer(layer, isString = false) {
                    // Save current state first
                    if (isString) {
                        await saveStringLayer(layer);
                    } else {
                        await saveConstraints(layer);
                    }

                    // Find and update the enrich button
                    const bodyEl = document.getElementById('body-' + layer);
                    const enrichBtn = bodyEl?.querySelector('[onclick*="enrichLayer"]');
                    const origText = enrichBtn?.textContent;
                    if (enrichBtn) { enrichBtn.textContent = '⏳ Enriching...'; enrichBtn.disabled = true; }

                    try {
                        const r = await apiFetch('/api/enrich', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ layer, isString })
                        });
                        const d = await r.json();
                        if (!d.ok) { throw new Error(d.error || 'Enrich failed'); }

                        const parsed = JSON.parse(d.result);

                        if (isString) {
                            currentState[layer] = parsed.value || d.result;
                            await saveStringLayer(layer);
                        } else {
                            // Merge enriched entries into current state
                            if (!currentState[layer]) currentState[layer] = {};
                            Object.assign(currentState[layer], parsed);
                            await apiFetch('/api/state', {
                                method: 'POST',
                                headers: { 'Content-Type': 'application/json' },
                                body: JSON.stringify({ [layer]: currentState[layer] })
                            });
                        }

                        renderConstraints();
                        // Ensure section stays open
                        const newBody = document.getElementById('body-' + layer);
                        if (newBody) newBody.classList.add('open');

                        appendChatEntry('system', `✨ Enriched ${layer}: ${Object.keys(parsed).length} items`);
                    } catch (ex) {
                        appendChatEntry('error', `Enrich failed: ${ex.message}`);
                    } finally {
                        if (enrichBtn) { enrichBtn.textContent = origText; enrichBtn.disabled = false; }
                    }
                }

                async function clearLayer(layer) {
                    if (!confirm(`Clear all items in this section?`)) return;
                    if (STRING_LAYERS.includes(layer)) {
                        currentState[layer] = '';
                        const ta = document.getElementById('string-' + layer);
                        if (ta) ta.value = '';
                        await apiFetch('/api/state', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({[layer]:''}) });
                    } else {
                        currentState[layer] = {};
                        await apiFetch('/api/state', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({[layer]:{}}) });
                    }
                    renderConstraints();
                }

                function exportDefinitions() {
                    const groups = ['Intent','Constraints','Shape','Behaviour'];
                    const out = {};
                    LAYER_GROUPS.forEach((g,i) => {
                        if (i > 3) return;
                        out[g.name] = {};
                        g.layers.forEach(l => { out[g.name][l] = currentState[l] || (STRING_LAYERS.includes(l) ? '' : {}); });
                    });
                    const blob = new Blob([JSON.stringify(out, null, 2)], {type:'application/json'});
                    const a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    const proj = document.getElementById('project-name')?.value || 'platinumforge';
                    a.download = proj.replace(/\s+/g,'-').toLowerCase() + '-definitions.json';
                    a.click();
                    URL.revokeObjectURL(a.href);
                }

                async function importDefinitions() {
                    const input = document.createElement('input');
                    input.type = 'file'; input.accept = '.json';
                    input.onchange = async (e) => {
                        const file = e.target.files[0];
                        if (!file) return;
                        try {
                            const text = await file.text();
                            const data = JSON.parse(text);
                            const patch = {};
                            Object.values(data).forEach(group => {
                                if (typeof group === 'object' && group !== null) {
                                    Object.entries(group).forEach(([layer, val]) => {
                                        if (LAYERS.includes(layer)) {
                                            currentState[layer] = val;
                                            patch[layer] = val;
                                        }
                                    });
                                }
                            });
                            await apiFetch('/api/state', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(patch) });
                            renderConstraints();
                            appendChatEntry('system', '📥 Definitions imported successfully');
                        } catch(ex) { alert('Invalid JSON file: ' + ex.message); }
                    };
                    input.click();
                }

                function toggleSection(header) {
                    const body = header.nextElementSibling;
                    body.classList.toggle('open');
                }

                function addConstraint(layer) {
                    if (!currentState[layer]) currentState[layer] = {};
                    const key = 'new-' + Date.now();
                    currentState[layer][key] = '';
                    renderConstraints();
                }

                function togglePresets(layer, btn) {
                    const existing = btn.parentElement.querySelector('.preset-dropdown');
                    if (existing) { existing.remove(); return; }
                    // close any other open dropdowns
                    document.querySelectorAll('.preset-dropdown').forEach(d => d.remove());
                    const presets = PRESETS[layer] || [];
                    const dd = document.createElement('div');
                    dd.className = 'preset-dropdown';
                    dd.innerHTML = presets.map((p, i) => `<div class="preset-item" onclick="applyPreset('${layer}', ${i})">
                        <div>${p.label}</div>
                        <div class="preset-keys">${Object.keys(p.items).join(', ')}</div>
                    </div>`).join('');
                    btn.parentElement.appendChild(dd);
                    // close on outside click
                    setTimeout(() => {
                        const handler = (e) => { if (!dd.contains(e.target) && e.target !== btn) { dd.remove(); document.removeEventListener('click', handler); } };
                        document.addEventListener('click', handler);
                    }, 0);
                }

                async function applyPreset(layer, index) {
                    const preset = PRESETS[layer][index];
                    if (!currentState[layer]) currentState[layer] = {};
                    Object.assign(currentState[layer], preset.items);
                    document.querySelectorAll('.preset-dropdown').forEach(d => d.remove());
                    renderConstraints();
                    document.getElementById('body-' + layer).classList.add('open');
                    await saveConstraints(layer);
                }

                async function saveConstraints(layer) {
                    const bodyEl = document.getElementById('body-' + layer);
                    const items = bodyEl.querySelectorAll('.constraint-item');
                    const data = {};
                    items.forEach(item => {
                        const key = item.querySelector('input').value.trim();
                        const val = item.querySelector('textarea').value.trim();
                        if (key) data[key] = val;
                    });
                    currentState[layer] = data;
                    const resp = await apiFetch('/api/state', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ [layer]: data })
                    });
                    if (!resp.ok) console.error('Save failed for', layer, resp.status);
                    renderPipelineNav();
                    // Flash save confirmation
                    const saveBtn = bodyEl.querySelector('.save');
                    if (saveBtn) { saveBtn.textContent = '✅ Saved'; setTimeout(() => saveBtn.textContent = '💾 Save', 1500); }
                }

                async function submitPrompt(override) {
                    const input = document.getElementById('prompt-input');
                    const prompt = override || input.value.trim();
                    if (!prompt) return;
                    input.value = '';
                    await apiFetch('/api/prompt', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ prompt })
                    });
                    setTimeout(refreshAll, 500);
                }

                async function sendChat() {
                    const input = document.getElementById('prompt-input');
                    const message = input.value.trim();
                    if (!message) return;
                    input.value = '';
                    appendChatEntry('user', message);
                    try {
                        const r = await apiFetch('/api/chat/send', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ message })
                        });
                        const d = await r.json();
                        if (d.reply) {
                            let actionsHtml = '';
                            if (d.actions && d.actions.length > 0) {
                                actionsHtml = `<div class="chat-actions">${d.actions.map(a =>
                                    `<button class="chat-action-btn" data-action-id="${a.Id}" onclick="applyAction('${a.Id}')">▶ ${escHtml(a.Label)} <span class="action-layer">${a.Layer}</span></button>`
                                ).join('')}</div>`;
                            }
                            appendChatEntry('agent', d.reply, actionsHtml);
                        }
                    } catch (ex) {
                        appendChatEntry('error', 'Chat failed: ' + ex.message);
                    }
                }

                async function applyAction(actionId) {
                    const btn = document.querySelector(`[data-action-id="${actionId}"]`);
                    if (btn) { btn.disabled = true; btn.textContent = '⏳ Applying...'; }
                    try {
                        await apiFetch('/api/chat/apply', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ actionId })
                        });
                        if (btn) { btn.className = 'chat-action-btn applied'; btn.innerHTML = '✅ Applied'; }
                        await refreshState();
                    } catch (ex) {
                        if (btn) { btn.disabled = false; btn.textContent = '❌ Failed'; }
                    }
                }

                async function revertTo(idx) {
                    await apiFetch('/api/revert', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ index: idx })
                    });
                    await refreshAll();
                    await refreshState();
                }

                function escHtml(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
                function escAttr(s) { return s.replace(/"/g, '&quot;').replace(/</g, '&lt;'); }

                // Prompt: Enter to submit, Shift+Enter for newline
                document.addEventListener('DOMContentLoaded', () => {
                    const input = document.getElementById('prompt-input');
                    if (input) input.addEventListener('keydown', e => {
                        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChat(); }
                    });
                    refreshState();
                    initSessionLabel();
                    loadUserProfile();
                });

                // ── Commit to disk ──
                async function commitToDisk() {
                    const btn = document.getElementById('commitBtn');
                    btn.classList.add('saving');
                    btn.textContent = '⏳ Saving...';
                    try {
                        const r = await fetch('/api/commit', { method: 'POST' });
                        const j = await r.json();
                        if (j.ok) {
                            btn.textContent = '✅ Saved!';
                            setTimeout(() => { btn.textContent = '💾 Commit'; btn.classList.remove('saving'); }, 2000);
                        } else {
                            btn.textContent = '❌ Error';
                            setTimeout(() => { btn.textContent = '💾 Commit'; btn.classList.remove('saving'); }, 2000);
                        }
                    } catch {
                        btn.textContent = '❌ Error';
                        setTimeout(() => { btn.textContent = '💾 Commit'; btn.classList.remove('saving'); }, 2000);
                    }
                    await refreshChat();
                }

                // ── Store Browser ──
                let currentTab = 'generated';
                let activeFile = null;

                function showEditorTab(tab) {
                    currentTab = tab;
                    document.querySelectorAll('#editor-header .tab').forEach(t => t.classList.remove('active'));
                    const tabMap = { generated: 'tabGenerated', browser: 'tabBrowser', unitTests: 'tabUnitTests',
                        nfrTests: 'tabNfrTests', soakTests: 'tabSoakTests', intTests: 'tabIntTests', logs: 'tabLogs' };
                    const tabEl = document.getElementById(tabMap[tab]);
                    if (tabEl) tabEl.classList.add('active');

                    const tree = document.getElementById('file-tree');
                    const logPanel = document.getElementById('log-panel');
                    const editorEl = document.getElementById('editor');
                    const statsEl = document.getElementById('pipeline-stats');

                    tree.style.display = 'none';
                    logPanel.style.display = 'none';
                    editorEl.style.display = 'block';
                    statsEl.style.display = 'none';

                    if (tab === 'browser') {
                        tree.style.display = 'block';
                        refreshTree();
                    } else if (tab === 'logs') {
                        logPanel.style.display = 'block';
                        editorEl.style.display = 'none';
                        statsEl.style.display = 'flex';
                    } else if (tab === 'unitTests') {
                        const src = Object.values(currentState.tests || {}).join('\n\n');
                        if (editor) { monaco.editor.setModelLanguage(editor.getModel(), 'csharp'); editor.setValue(src || '// No unit tests generated yet'); }
                    } else if (tab === 'nfrTests') {
                        const src = Object.values(currentState.nfrTests || {}).join('\n\n');
                        if (editor) { monaco.editor.setModelLanguage(editor.getModel(), 'typescript'); editor.setValue(src || '// No NFR tests generated yet'); }
                    } else if (tab === 'soakTests') {
                        const src = Object.values(currentState.soakTests || {}).join('\n\n');
                        if (editor) { monaco.editor.setModelLanguage(editor.getModel(), 'python'); editor.setValue(src || '# No soak tests generated yet'); }
                    } else if (tab === 'intTests') {
                        const src = Object.values(currentState.integrationTests || {}).join('\n\n');
                        if (editor) { monaco.editor.setModelLanguage(editor.getModel(), 'typescript'); editor.setValue(src || '// No integration tests generated yet'); }
                    } else {
                        activeFile = null;
                        refreshCode();
                    }
                    if (editor) editor.layout();
                }

                async function refreshTree() {
                    try {
                        const r = await fetch('/api/store/tree');
                        const tree = await r.json();
                        const el = document.getElementById('tree-content');
                        const LAYER_ICONS = {
                            Rules: '📜', Architecture: '🏗', NFR: '⚡', Invariants: '🔒',
                            Features: '✨', UnitTests: '🧪', Interfaces: '🔌', Code: '💻',
                            NfrTests: '🎭', SoakTests: '🌊', IntegrationTests: '🔗'
                        };
                        el.innerHTML = Object.entries(tree).map(([layer, files]) => {
                            const icon = LAYER_ICONS[layer] || '📁';
                            const hasFiles = files.length > 0;
                            return `<div class="tree-folder">
                                <div class="tree-folder-label${hasFiles ? '' : ' empty'}" onclick="toggleTreeFolder(this)">
                                    <span class="folder-icon">▶</span>${icon} ${layer}
                                    <span class="tree-file-count">${files.length}</span>
                                </div>
                                <div class="tree-folder-children">
                                    ${files.map(f => `<div class="tree-file${activeFile && activeFile.layer === layer && activeFile.key === f ? ' active' : ''}" onclick="viewStoreFile('${escAttr(layer)}','${escAttr(f)}')">📄 ${escHtml(f)}</div>`).join('')}
                                </div>
                            </div>`;
                        }).join('');
                    } catch {}
                }

                function toggleTreeFolder(label) {
                    label.classList.toggle('open');
                    const children = label.nextElementSibling;
                    children.classList.toggle('open');
                }

                async function viewStoreFile(layer, key) {
                    try {
                        const r = await fetch(`/api/store/file?layer=${encodeURIComponent(layer)}&key=${encodeURIComponent(key)}`);
                        const j = await r.json();
                        if (j.content !== undefined && editor) {
                            const csharpLayers = ['UnitTests', 'Interfaces', 'Code'];
                            const tsLayers = ['NfrTests', 'IntegrationTests'];
                            const pyLayers = ['SoakTests'];
                            const lang = csharpLayers.includes(layer) ? 'csharp' :
                                         tsLayers.includes(layer) ? 'typescript' :
                                         pyLayers.includes(layer) ? 'python' : 'plaintext';
                            monaco.editor.setModelLanguage(editor.getModel(), lang);
                            editor.setValue(j.content);
                            document.getElementById('lineCount').textContent = j.content.split('\n').length + ' lines · ' + layer + '/' + key;
                            activeFile = { layer, key };
                            refreshTree();
                        }
                    } catch {}
                }

                // ── Sessions ──
                let sessionFlyoutOpen = false;

                function toggleSessionFlyout() {
                    sessionFlyoutOpen = !sessionFlyoutOpen;
                    document.getElementById('session-flyout').style.display = sessionFlyoutOpen ? 'flex' : 'none';
                    document.getElementById('session-overlay').style.display = sessionFlyoutOpen ? 'block' : 'none';
                    if (sessionFlyoutOpen) refreshSessions();
                }

                async function refreshSessions() {
                    try {
                        const r = await fetch('/api/sessions');
                        const j = await r.json();
                        const el = document.getElementById('session-list');
                        // Update header label
                        const active = j.sessions.find(s => s.active);
                        if (active) {
                            document.getElementById('sessionLabel').textContent = active.Name || active.Id.substring(0, 8);
                        }
                        el.innerHTML = j.sessions.map(s => {
                            const short = s.Id.substring(0, 8);
                            const updated = new Date(s.UpdatedAt).toLocaleString();
                            return `<div class="session-item${s.active ? ' active' : ''}" ondblclick="startRenameSession('${s.Id}', this)">
                                <div class="session-item-top">
                                    <span class="session-name" id="sname-${s.Id}">${escHtml(s.Name)}</span>
                                    <input class="session-name-input" id="sinput-${s.Id}" value="${escAttr(s.Name)}"
                                        onblur="finishRenameSession('${s.Id}')"
                                        onkeydown="if(event.key==='Enter')finishRenameSession('${s.Id}');if(event.key==='Escape'){this.classList.remove('editing');document.getElementById('sname-${s.Id}').classList.remove('editing');}" />
                                    ${s.active ? '<span class="session-badge">ACTIVE</span>' : ''}
                                </div>
                                <div class="session-id">${short}…</div>
                                <div class="session-meta">
                                    <span class="session-time">Updated ${updated}</span>
                                    <div class="session-actions">
                                        ${!s.active ? `<button onclick="event.stopPropagation();switchSession('${s.Id}')">↗ Switch</button>` : ''}
                                        ${!s.active ? `<button class="btn-del" onclick="event.stopPropagation();deleteSession('${s.Id}','${escAttr(s.Name)}')">🗑</button>` : ''}
                                    </div>
                                </div>
                            </div>`;
                        }).join('');
                    } catch {}
                }

                function startRenameSession(id, el) {
                    const nameEl = document.getElementById('sname-' + id);
                    const inputEl = document.getElementById('sinput-' + id);
                    nameEl.classList.add('editing');
                    inputEl.classList.add('editing');
                    inputEl.focus();
                    inputEl.select();
                }

                async function finishRenameSession(id) {
                    const inputEl = document.getElementById('sinput-' + id);
                    const nameEl = document.getElementById('sname-' + id);
                    inputEl.classList.remove('editing');
                    nameEl.classList.remove('editing');
                    const newName = inputEl.value.trim();
                    if (!newName) return;
                    await fetch('/api/sessions/rename', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ id, name: newName })
                    });
                    await refreshSessions();
                }

                async function createSession() {
                    const input = document.getElementById('newSessionName');
                    const name = input.value.trim();
                    input.value = '';
                    await fetch('/api/sessions', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ name })
                    });
                    await refreshSessions();
                    await refreshAll();
                    await refreshState();
                }

                async function switchSession(id) {
                    await fetch('/api/sessions/switch', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ id })
                    });
                    await refreshSessions();
                    await refreshAll();
                    await refreshState();
                }

                async function deleteSession(id, name) {
                    if (!confirm(`Delete session "${name}"?`)) return;
                    await fetch(`/api/sessions/delete/${id}`, { method: 'POST' });
                    await refreshSessions();
                }

                // Load session label on init
                async function initSessionLabel() {
                    try {
                        const r = await fetch('/api/sessions');
                        const j = await r.json();
                        const active = j.sessions.find(s => s.active);
                        if (active) {
                            document.getElementById('sessionLabel').textContent = active.Name || active.Id.substring(0, 8);
                        }
                    } catch {}
                }

                // ── User Profile ──
                async function loadUserProfile() {
                    try {
                        const r = await fetch('/api/me');
                        if (r.status === 401) { window.location = '/auth/login'; return; }
                        const u = await r.json();
                        const el = document.getElementById('userArea');
                        if (u.picture) {
                            el.innerHTML = `<img class="user-avatar" src="${escAttr(u.picture)}" title="${escAttr(u.name || u.email)}" referrerpolicy="no-referrer" />` +
                                `<span class="user-name">${escHtml(u.name || u.email || 'User')}</span>` +
                                (u.authEnabled ? `<button class="btn-logout" onclick="logout()">Logout</button>` : '');
                        } else {
                            el.innerHTML = `<span class="user-name">${escHtml(u.name || 'Local User')}</span>`;
                        }
                    } catch {}
                }

                function logout() {
                    window.location = '/auth/logout';
                }

                // ── Share ──
                async function shareCurrentSession() {
                    try {
                        const r = await fetch('/api/sessions/share', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({})
                        });
                        const j = await r.json();
                        if (j.ok) {
                            document.getElementById('shareUrl').value = j.url;
                            document.getElementById('share-dialog').style.display = 'block';
                            document.getElementById('share-overlay').style.display = 'block';
                        }
                    } catch {}
                }

                function closeShareDialog() {
                    document.getElementById('share-dialog').style.display = 'none';
                    document.getElementById('share-overlay').style.display = 'none';
                }

                function copyShareUrl() {
                    const input = document.getElementById('shareUrl');
                    input.select();
                    navigator.clipboard.writeText(input.value).then(() => {
                        const btn = event.target;
                        btn.textContent = '✅ Copied!';
                        setTimeout(() => { btn.textContent = '📋 Copy'; }, 1500);
                    });
                }

                // ── Project Meta ──
                function updateProjectMeta() {
                    const projectName = document.getElementById('projectNameInput').value;
                    const version = document.getElementById('versionInput').value;
                    apiFetch('/api/state', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ projectName, version })
                    });
                }

                function syncProjectMeta() {
                    if (currentState.projectName) document.getElementById('projectNameInput').value = currentState.projectName;
                    if (currentState.version) document.getElementById('versionInput').value = currentState.version;
                }

                // ── Builds ──
                let buildsFlyoutOpen = false;

                function toggleBuildsFlyout() {
                    buildsFlyoutOpen = !buildsFlyoutOpen;
                    document.getElementById('builds-flyout').style.display = buildsFlyoutOpen ? 'block' : 'none';
                    document.getElementById('builds-overlay').style.display = buildsFlyoutOpen ? 'block' : 'none';
                    if (buildsFlyoutOpen) refreshBuilds();
                }

                async function refreshBuilds() {
                    try {
                        const r = await fetch('/api/builds');
                        const builds = await r.json();
                        const el = document.getElementById('builds-list');
                        if (!builds.length) {
                            el.innerHTML = '<div style="padding:16px;color:var(--text-dim);text-align:center;">No builds yet.<br>Run a generation to create your first build.</div>';
                            return;
                        }
                        el.innerHTML = builds.slice().reverse().map(b => {
                            const name = b.projectName || 'untitled';
                            const ver = b.version || '?';
                            const fn = b.fileName || '?';
                            const ts = b.timestamp ? new Date(b.timestamp).toLocaleString() : '';
                            const unit = b.unitTests || 0;
                            const nfr = b.nfrTests || 0;
                            const soak = b.soakTests || 0;
                            const intg = b.integrationTests || 0;
                            return `<div style="padding:10px 12px; margin-bottom:6px; background:var(--bg); border-radius:6px; border:1px solid var(--border);">
                                <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                                    <strong style="color:var(--accent);">${escHtml(name)}</strong>
                                    <span style="font-size:11px; color:var(--text-dim);">v${escHtml(ver)}</span>
                                </div>
                                <div style="font-size:11px; color:var(--text-dim); margin-bottom:6px;">${escHtml(ts)}</div>
                                <div style="font-size:11px; display:flex; gap:8px; margin-bottom:6px;">
                                    <span>🧪 ${unit}</span>
                                    <span>🎭 ${nfr}</span>
                                    <span>🌊 ${soak}</span>
                                    <span>🔗 ${intg}</span>
                                </div>
                                <a href="/api/builds/download/${encodeURIComponent(fn)}" download
                                    style="font-size:12px; color:var(--accent); text-decoration:none; cursor:pointer;">
                                    ⬇ ${escHtml(fn)}
                                </a>
                            </div>`;
                        }).join('');
                    } catch(e) {
                        console.error('Failed to load builds', e);
                    }
                }
            </script>
        </body>
        </html>
        """.Replace("%%FAVICON%%", FaviconLink).Replace("%%PSILOGO%%", PsiLogoSvg);

    public static void Stop()
    {
        try { _listener?.Stop(); } catch { }
    }
}

// ── Main Program ─────────────────────────────

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║    Ψ WaveFunctionLabs — PlatinumForge v0.4  ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        // Start web server (user init happens on first request per-user)
        PlatinumForgeServer.Start();

        Console.WriteLine("\n🌐 Open the URL shown above in your browser");
        Console.WriteLine("   Sign in to access your workspace.");
        Console.WriteLine("   Press Ctrl+C to exit.\n");

        // Keep alive
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;

        PlatinumForgeServer.Stop();
    }

    public static int DetectLayer(string[] srcLines, int errLine)
    {
        for (int i = errLine - 1; i >= 0; i--)
        {
            if (srcLines[i].Contains("public static class TestRunner")) return 4;
            if (srcLines[i].Contains("// ── Code:")) return 6;
            if (srcLines[i].Contains("// ── Interface:")) return 5;
        }
        return 6;
    }

    // ── Seed data ────────────────────────────

    public static SystemState SeedState()
    {
        return new SystemState
        {
            Rules = new()
            {
                ["pure-functions"] = "All business logic must be implemented as pure functions with no side effects.",
                ["single-responsibility"] = "Each class must have exactly one responsibility.",
            },
            Architecture = new()
            {
                ["layered"] = "Use a simple layered architecture: Interface → Implementation. No circular dependencies.",
                ["calculator-domain"] = "Build a calculator service that supports Add, Subtract, Multiply, Divide operations.",
            },
            NFR = new()
            {
                ["performance"] = "All operations must complete in O(1) time.",
                ["error-handling"] = "Division by zero must return a descriptive error, not throw an exception.",
            },
            Invariants = new()
            {
                ["no-reflection"] = "Generated code must not use System.Reflection.",
                ["no-file-io"] = "Generated code must not use file I/O.",
            },
        };
    }

    // ── Helpers ──────────────────────────────

    static void PrintLayers(SystemState state)
    {
        PrintDict("Rules", state.Rules);
        PrintDict("Architecture", state.Architecture);
        PrintDict("NFR", state.NFR);
        PrintDict("Invariants", state.Invariants);
    }

    static void PrintDict(string label, Dictionary<string, string> dict)
    {
        Console.WriteLine($"  [{label}]");
        foreach (var (k, _) in dict)
            Console.WriteLine($"    • {k}");
    }
}
