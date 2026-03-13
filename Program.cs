using System.Buffers;
using System.Diagnostics;
using HardCodeScan.Utilities;

namespace HardCodeScan;

internal class Program
{
    private static bool _excludeStrings;
    private static bool _excludeNumbers;
    private static HashSet<string> _nonMagic = new(StringComparer.CurrentCultureIgnoreCase);
    private static SearchValues<string>? _searchValues;

    static Task Main()
    {
        if (Debugger.IsAttached)
            return SafeMain();

        try
        {
            return SafeMain();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return Task.CompletedTask;
        }
    }

    static async Task SafeMain()
    {
        Console.WriteLine("HardCodeScan - Copyright (C) 2024-" + DateTime.Now.Year + " Simon Mourier. All rights reserved.");
        Console.WriteLine();

        var inputPath = CommandLine.Current.GetNullifiedArgument(0);
        if (CommandLine.Current.HelpRequested || inputPath == null)
        {
            Help();
            return;
        }

        _excludeStrings = CommandLine.Current.GetArgument<bool>("xs");
        _excludeNumbers = CommandLine.Current.GetArgument<bool>("xn");
        var nonMagic = CommandLine.Current.GetNullifiedArgument("nm");
        if (nonMagic != null)
        {
            _nonMagic = [.. nonMagic.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0)];
        }

        var fe = CommandLine.Current.GetArgument<string>("fe");
        if (fe != null)
        {
            var filteredExpressions = fe.Split('`').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            _searchValues = SearchValues.Create(filteredExpressions.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        Document? current = null;
        await ScanHardcodedFromInputPath(inputPath, (doc, node, suggestion) =>
        {
            if (current != doc)
            {
                Console.WriteLine($"Document: {doc.FilePath}");
                current = doc;
            }

            Console.WriteLine($" Found hardcoded value: {node.ToFullString().Trim()}");
            if (suggestion != null)
            {
                Console.WriteLine($"  {suggestion}");
            }
            Console.WriteLine();
        });
    }

    public static async Task ScanHardcodedFromInputPath(string inputPath, Action<Document, SyntaxNodeOrToken, string?> scannedFunction)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(scannedFunction);
        if (Directory.Exists(inputPath))
        {
            var files = Directory.GetFiles(inputPath, "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                await ScanHardcodedFromInputPath(file, scannedFunction);
            }
        }
        else if (File.Exists(inputPath))
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            if (ext == ".csproj")
            {
                await ScanHardcodedFromProject(inputPath, scannedFunction);
            }
            else if (ext == ".sln" || ext == ".slnx")
            {
                await ScanHardcodedFromSolution(inputPath, scannedFunction);
            }
            else
            {
                var text = await EncodingDetector.ReadAllTextAsync(inputPath);
                await ScanHardcodedFromText(Path.GetFileName(inputPath), text.text, scannedFunction);
            }
        }
        else
        {
            Console.WriteLine($"Input path does not exist: {inputPath}");
        }
    }

    static void Help()
    {
        Console.WriteLine("Format:");
        Console.WriteLine();
        Console.WriteLine(Assembly.GetEntryAssembly()!.GetName().Name + " <input path> [options]");
        Console.WriteLine();
        Console.WriteLine("Description:");
        Console.WriteLine("    This tool gather hardcoded strings and numbers from C# files.");
        Console.WriteLine("    Input path can be a directory or a C# file, a C# project file (.csproj) or a Visual Studio solution (.sln or .slnx).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("    /xs    Excludes strings from scan.");
        Console.WriteLine("    /xn    Excludes numbers from scan.");
        Console.WriteLine("    /nm    List of case-insensitive non-magic texts separated by the ; character.");
        Console.WriteLine("    /fe    List of case-insensitive filtered related expressions, separated by the ` character.");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine();
        Console.WriteLine("    " + Assembly.GetEntryAssembly()!.GetName().Name + " c:\\mypath /nm:255;0xFF /fe:Load`Color");
        Console.WriteLine();
        Console.WriteLine("    Dumps hardcoded strings and numbers in the c:\\mypath directory C# files.");
        Console.WriteLine("    Exclude '255' and '0xFF' from search. Exclude 'Load' and 'Color' when found in expressions.");
        Console.WriteLine();
    }

    public static async Task ScanHardcodedFromText(string documentName, string text, Action<Document, SyntaxNodeOrToken, string?> scannedFunction)
    {
        ArgumentNullException.ThrowIfNull(documentName);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scannedFunction);

        var ws = new AdhocWorkspace();
        var project = ws.AddProject(documentName + "Project", LanguageNames.CSharp);
        ws.AddDocument(project.Id, documentName, SourceText.From(text));
        await ScanHardcoded(ws, scannedFunction);
    }

    public static async Task ScanHardcodedFromSolution(string solutionFilePath, Action<Document, SyntaxNodeOrToken, string?> scannedFunction)
    {
        ArgumentNullException.ThrowIfNull(solutionFilePath);
        ArgumentNullException.ThrowIfNull(scannedFunction);

        var ws = MSBuildWorkspace.Create();
        await ws.OpenSolutionAsync(solutionFilePath);
        await ScanHardcoded(ws, scannedFunction);
    }

    public static async Task ScanHardcodedFromProject(string projectFilePath, Action<Document, SyntaxNodeOrToken, string?> scannedFunction)
    {
        ArgumentNullException.ThrowIfNull(projectFilePath);
        ArgumentNullException.ThrowIfNull(scannedFunction);

        var ws = MSBuildWorkspace.Create();
        await ws.OpenProjectAsync(projectFilePath);
        await ScanHardcoded(ws, scannedFunction);
    }

    public static async Task ScanHardcoded(Workspace workspace, Action<Document, SyntaxNodeOrToken, string?> scannedFunction)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scannedFunction);

        foreach (var project in workspace.CurrentSolution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var tree = await document.GetSyntaxTreeAsync();
                if (tree == null)
                    continue;

                var root = await tree.GetRootAsync();
                foreach (var node in root.DescendantNodesAndTokens())
                {
                    // if parent is enum member declaration, we don't want to report hardcoded values as they are often used to set the value of the enum member.
                    if (IsParentEnum(node))
                        continue;

                    if (!CanBeMagic(node.Kind()))
                        continue;

                    if (IsWellKnownConstant(node))
                        continue;

                    if (IsMagic(node, out var suggestion))
                    {
                        if (suggestion != null && _searchValues != null && suggestion.IndexOfAny(_searchValues) >= 0)
                            continue;

                        scannedFunction(document, node, suggestion);
                    }
                }
            }
        }
    }

    public static bool IsMagic(SyntaxNodeOrToken kind, [NotNullWhen(true)] out string? suggestion)
    {
        var vdec = kind.Parent?.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault();
        if (vdec != null)
        {
            if (vdec.Parent is MemberDeclarationSyntax dec)
            {
                if (!HasConstOrEquivalent(dec))
                {
                    suggestion = "Member declaration could be const: " + dec.ToFullString();
                    return true;
                }
            }
            else
            {
                if (vdec.Parent is LocalDeclarationStatementSyntax ldec)
                {
                    if (!HasConstOrEquivalent(ldec))
                    {
                        suggestion = "Local declaration contains at least one non const value: " + ldec.ToFullString();
                        return true;
                    }
                }
            }
        }
        else
        {
            var expr = kind.Parent?.Ancestors().OfType<ExpressionSyntax>().FirstOrDefault();
            if (expr != null)
            {
                suggestion = "Expression uses a non const value: " + expr.ToFullString();
                return true;
            }
        }

        suggestion = null;
        return false;
    }

    private static bool IsParentEnum(SyntaxNodeOrToken node)
    {
        if (node.IsKind(SyntaxKind.EnumMemberDeclaration))
            return true;

        if (node.Parent == null)
            return false;

        return IsParentEnum(node.Parent);
    }

    private static bool IsWellKnownConstant(SyntaxNodeOrToken node)
    {
        if (!node.IsToken)
            return false;

        var text = node.AsToken().Text;
        if (text == null)
            return false;

        if (text == "1" || text == "-1" || text == "0" ||
            text == "1.0" || text == "-1.0" ||
            text == "1.0f" || text == "-1.0f" ||
            text.EqualsIgnoreCase("0f") || text.EqualsIgnoreCase("0d") ||
            text.EqualsIgnoreCase("1f") || text.EqualsIgnoreCase("1d") ||
            text.EqualsIgnoreCase("-1f") || text.EqualsIgnoreCase("-1d")
            )
            return true;

        if (_nonMagic.Contains(text))
            return true;

        // ok for '\0' or '\r', etc.
        if (text.Length == 4 && text.StartsWith("'\\") && text.EndsWith('\''))
            return true;

        if (text == "' '")
            return true;

        return false;
    }

    private static bool CanBeMagic(SyntaxKind kind)
    {
        if (!_excludeStrings && (kind == SyntaxKind.CharacterLiteralToken || kind == SyntaxKind.StringLiteralToken))
            return true;

        if (!_excludeNumbers && kind == SyntaxKind.NumericLiteralToken)
            return true;

        return false;
    }

    private static bool HasConstOrEquivalent(SyntaxNode node)
    {
        var hasStatic = false;
        var hasReadOnly = false;
        foreach (var tok in node.ChildTokens())
        {
            switch (tok.Kind())
            {
                case SyntaxKind.ReadOnlyKeyword:
                    hasReadOnly = true;
                    if (hasStatic)
                        return true;
                    break;

                case SyntaxKind.StaticKeyword:
                    hasStatic = true;
                    if (hasReadOnly)
                        return true;
                    break;

                case SyntaxKind.ConstKeyword:
                    return true;
            }
        }
        return false;
    }
}
