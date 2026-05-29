using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Text.Json;

class Program
{
    // JSONの構造を定義するクラス
    class SourceGeneratorRunnerConfig
    {
        public string GeneratorAssembly { get; set; } = "";
        public string[] TargetFiles { get; set; } = Array.Empty<string>();
        public string[] References { get; set; } = Array.Empty<string>();
        public string OutputDirectory { get; set; } = "";
    }

    static int Main(string[] args)
    {
        string jsonPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "SourceGeneratorRunnerConfig.json");
        Console.WriteLine($"[INFO] Loading config: {jsonPath}");

        try
        {
            // 1. JSONファイルの存在チェックと読み込み
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine($"[ERROR] JSON file not found at '{jsonPath}'");
                return 1;
            }

            string jsonContent = File.ReadAllText(jsonPath);
            var config = JsonSerializer.Deserialize<SourceGeneratorRunnerConfig>(jsonContent);

            if (config == null)
            {
                Console.WriteLine("[ERROR] Failed to deserialize JSON.");
                return 1;
            }

            // 2. パスの解決（exeがあるフォルダを基準に相対パスを絶対パスへ変換）
            ResolveRelativePaths(config);

            // 3. 解析対象ファイルを構文木（SyntaxTree）に変換
            Console.WriteLine("\n--- [1] Parsing Target Files ---");
            var syntaxTrees = config.TargetFiles
                .Where(path => {
                    if (File.Exists(path)) return true;
                    Console.WriteLine($"[WARN] Target file not found: {path}");
                    return false;
                })
                .Select(path => {
                    Console.WriteLine($"[INFO] Parsing: {path}");
                    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);

                    // 構文エラーチェック
                    var diagnostics = tree.GetDiagnostics();
                    foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        Console.WriteLine($"  [Syntax Error] {diag.GetMessage()} at line {diag.Location.GetLineSpan().StartLinePosition.Line}");
                    }
                    return tree;
                })
                .ToArray();

            if (syntaxTrees.Length == 0)
            {
                Console.WriteLine("[ERROR] No valid target files to analyze.");
                return 1;
            }

            // 4. 外部DLL参照を「MetadataReference」としてロード
            Console.WriteLine("\n--- [2] Loading Reference DLLs ---");
            var metadataReferences = config.References
                .Where(path => {
                    if (File.Exists(path)) return true;
                    Console.WriteLine($"[WARN] Reference DLL not found: {path}");
                    return false;
                })
                .Select(path => {
                    Console.WriteLine($"[INFO] Loading reference: {path}");
                    return MetadataReference.CreateFromFile(path);
                })
                .ToList();

            // 5. 仮想的なコンパイル環境の構築と意味解析（セマンティック）検証
            Console.WriteLine("\n--- [3] Creating Compilation Context ---");
            var compilation = CSharpCompilation.Create("DynamicCompilation")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddSyntaxTrees(syntaxTrees)
                .AddReferences(metadataReferences);

            var compDiagnostics = compilation.GetDiagnostics();
            bool hasSemanticErrors = false;
            foreach (var diag in compDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                Console.WriteLine($"  [Semantic Error] {diag.GetMessage()} (ID: {diag.Id})");
                hasSemanticErrors = true;
            }
            if (hasSemanticErrors)
            {
                Console.WriteLine("[WARN] Semantic errors detected. Source Generator will likely ignore these types.");
            }
            else
            {
                Console.WriteLine("[INFO] Context creation success. No semantic errors.");
            }

            // 6. ソースジェネレータDLLのロード（DLL内のすべてのジェネレータ型を対象にする）
            Console.WriteLine("\n--- [4] Loading Source Generator ---");
            if (!File.Exists(config.GeneratorAssembly))
            {
                Console.WriteLine($"[ERROR] Generator assembly not found at '{config.GeneratorAssembly}'");
                return 1;
            }

            var sgAssembly = Assembly.LoadFrom(config.GeneratorAssembly);

            // 抽象クラスを除外して、ISourceGeneratorまたはIIncrementalGeneratorを実装したすべての型を取得
            var generatorTypes = sgAssembly.GetTypes()
                .Where(t => !t.IsAbstract && (typeof(ISourceGenerator).IsAssignableFrom(t) ||
                             t.GetInterfaces().Any(i => i.FullName == "Microsoft.CodeAnalysis.IIncrementalGenerator")))
                .ToArray();

            if (generatorTypes.Length == 0)
            {
                Console.WriteLine("[ERROR] No valid Source Generator class found in the assembly.");
                return 1;
            }

            // 発見したすべてのジェネレータをインスタンス化
            var instances = new List<ISourceGenerator>();
            Console.WriteLine($"[INFO] Found {generatorTypes.Length} generators in assembly:");
            foreach (var type in generatorTypes)
            {
                Console.WriteLine($"  -> {type.FullName}");
                var instance = Activator.CreateInstance(type)!;

                if (instance is ISourceGenerator sg)
                {
                    instances.Add(sg);
                }
                else
                {
                    // IIncrementalGenerator の場合は AsSourceGenerator() でラップ
                    var asSourceGeneratorMethod = typeof(GeneratorExtensions).GetMethod("AsSourceGenerator")!;
                    var wrappedGenerator = (ISourceGenerator)asSourceGeneratorMethod.Invoke(null, new[] { instance })!;
                    instances.Add(wrappedGenerator);
                }
            }

            // 7. ドライバーの作成（すべてのジェネレータをまとめて登録）
            GeneratorDriver driver = CSharpGeneratorDriver.Create(instances.ToArray());

            // 8. ジェネレータの実行
            Console.WriteLine("\n--- [5] Running Source Generators ---");
            //driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
            driver = driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var outputCompilation,
                out var diagnostics);

            // ジェネレータが通知したログの出力
            foreach (var diag in diagnostics)
            {
                Console.WriteLine($"  [{diag.Severity}] Generator Message: {diag.GetMessage()}");
            }

            var runResult = driver.GetRunResult();
            Console.WriteLine($"[INFO] Total generated files counted by Roslyn: {runResult.GeneratedTrees.Length}");

            // 9. 生成されたコードの書き出し
            Console.WriteLine("\n--- [6] Writing Generated Files ---");
            if (runResult.GeneratedTrees.Length == 0)
            {
                Console.WriteLine("[INFO] No source files were generated. (Generator skipped code generation)");
                return 0;
            }

            Directory.CreateDirectory(config.OutputDirectory);
            foreach (var result in runResult.GeneratedTrees)
            {
                string fileName = Path.GetFileName(result.FilePath);
                string outputPath = Path.Combine(config.OutputDirectory, fileName);

                File.WriteAllText(outputPath, result.ToString());
                Console.WriteLine($"[SUCCESS] Generated: {outputPath}");
            }

            Console.WriteLine("\nSuccessfully completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[CRITICAL Exception] {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// JSON内の相対パスを、このツール（exe）の配置フォルダからの絶対パスに変換します。
    /// </summary>
    private static void ResolveRelativePaths(SourceGeneratorRunnerConfig config)
    {
        string exeDir = AppContext.BaseDirectory;

        if (!Path.IsPathRooted(config.GeneratorAssembly))
        {
            config.GeneratorAssembly = Path.GetFullPath(Path.Combine(exeDir, config.GeneratorAssembly));
        }

        config.TargetFiles = config.TargetFiles.Select(path =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(exeDir, path))
        ).ToArray();

        config.References = config.References.Select(path =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(exeDir, path))
        ).ToArray();

        if (!Path.IsPathRooted(config.OutputDirectory))
        {
            config.OutputDirectory = Path.GetFullPath(Path.Combine(exeDir, config.OutputDirectory));
        }
    }
}