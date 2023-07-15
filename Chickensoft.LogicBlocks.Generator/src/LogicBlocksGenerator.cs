﻿namespace Chickensoft.LogicBlocks.Generator;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Chickensoft.LogicBlocks.Generator.Common.Models;
using Chickensoft.SourceGeneratorUtils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SuperNodes.Common.Services;

[Generator]
public class LogicBlocksGenerator :
  ChickensoftGenerator, IIncrementalGenerator {
  public static Log Log { get; } = new Log();
  public ICodeService CodeService { get; } = new CodeService();

  // #pragma warning disable IDE0052, CS0414
  private static bool _logsFlushed;
  // #pragma warning restore CS0414, IDE0052

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Add post initialization sources
    // (source code that is always generated regardless)
    foreach (var postInitSource in Constants.PostInitializationSources) {
      context.RegisterPostInitializationOutput(
        (context) => context.AddSource(
          hintName: $"{postInitSource.Key}.cs",
          source: postInitSource.Value.Clean()
        )
      );
    }

    var logicBlockCandidates = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (SyntaxNode node, CancellationToken _) =>
        IsLogicBlockCandidate(node),
      transform: (GeneratorSyntaxContext context, CancellationToken token) =>
        DiscoverStateGraph(
          (ClassDeclarationSyntax)context.Node, context.SemanticModel, token
        )
    ).Where(logicBlockImplementation => logicBlockImplementation is not null)
    .Select((logicBlockImplementation, token) => ConvertStateGraphToUml(
      logicBlockImplementation!, token
    ));

    context.RegisterSourceOutput(
      source: logicBlockCandidates,
      action: (
        SourceProductionContext context,
        ILogicBlockResult possibleResult
      ) => {
        if (possibleResult is not LogicBlockOutputResult result) { return; }

        // Since we need to output non-C# files, we have to write files to
        // the disk ourselves. This also allows us to output files that are
        // in the same directory as the source file.
        //
        // For best results, add `*.g.puml` to your `.gitignore` file.

        var destFile = result.FilePath;
        var content = result.Content;

        try {
          File.WriteAllText(destFile, content);
        }
        catch (Exception) {
          // If we can't write a file next to the source file, create a
          // commented out C# file with the UML content in it.
          //
          // This allows the source generator to be unit-tested.
          context.AddSource(
            hintName: $"{result.Name}.puml.g.cs",
            source: string.Join(
              "\n", result.Content.Split('\n').Select(line => $"// {line}")
            )
          );
        }
      }
    );

    Log.Print("Done finding candidates");

    // When debugging source generators, it can be nice to output a log file.
    // This is a total hack to print out a single file.
#if DEBUG
    var syntax = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: (syntaxNode, _) => syntaxNode is CompilationUnitSyntax,
      transform: (syntaxContext, _) => syntaxContext.Node
    );
    context.RegisterImplementationSourceOutput(
      syntax,
      (ctx, _) => {
        if (_logsFlushed) { return; }
        ctx.AddSource(
          "LOG", SourceText.From(Log.Contents, Encoding.UTF8)
        );
        _logsFlushed = true;
      }
    );
#endif
  }

  public static bool IsLogicBlockCandidate(SyntaxNode node) =>
    node is ClassDeclarationSyntax classDeclaration &&
    classDeclaration.AttributeLists.SelectMany(list => list.Attributes)
      .Any(attribute => attribute.Name.ToString() ==
        Constants.STATE_MACHINE_ATTRIBUTE_NAME
      );

  /// <summary>
  /// Looks at a logic block subclass, finds the logic block type in its
  /// inheritance hierarchy, and builds a state graph from it based on the
  /// state type given to the logic block type in the inheritance hierarchy.
  /// </summary>
  /// <param name="logicBlockClassDecl"></param>
  /// <param name="model"></param>
  /// <param name="token"></param>
  /// <returns>Logic block graph.</returns>
  public LogicBlockImplementation? DiscoverStateGraph(
    ClassDeclarationSyntax logicBlockClassDecl,
    SemanticModel model,
    CancellationToken token
  ) {
    var filePath = logicBlockClassDecl.SyntaxTree.FilePath;
    var destFile = Path.ChangeExtension(filePath, ".g.puml");

    Log.Print($"File path: {filePath}");
    Log.Print($"Dest file: {destFile}");

    var semanticSymbol = model.GetDeclaredSymbol(logicBlockClassDecl, token);

    if (semanticSymbol is not INamedTypeSymbol symbol) {
      return null;
    }

    var matchingBases = CodeService.GetAllBaseTypes(symbol)
      .Where(
        baseType => CodeService.GetNameFullyQualifiedWithoutGenerics(
          baseType, baseType.Name
        ) == Constants.LOGIC_BLOCK_CLASS_ID
      );

    if (!matchingBases.Any()) {
      return null;
    }

    var logicBlockBase = matchingBases.First();
    var logicBlockGenericArgs = logicBlockBase.TypeArguments;

    if (logicBlockGenericArgs.Length < 3) {
      return null;
    }

    var inputType = logicBlockGenericArgs[0];
    var stateType = logicBlockGenericArgs[1];
    var outputType = logicBlockGenericArgs[2];

    if (
      stateType is not INamedTypeSymbol stateBaseType ||
      inputType is not INamedTypeSymbol inputBaseType ||
      outputType is not INamedTypeSymbol outputBaseType
    ) {
      return null;
    }

    var inputs = GetSubclassesById(symbol, inputBaseType);
    var outputs = GetSubclassesById(symbol, outputBaseType);

    var stateSubtypes = CodeService.GetAllTypes(
      stateBaseType,
      (type) => CodeService.GetAllBaseTypes(type).Any(
          (baseType) => SymbolEqualityComparer.Default.Equals(
            baseType, stateBaseType
          )
        )
      );

    // Base state becomes the root
    var root = new LogicBlockGraph(
      id: CodeService.GetNameFullyQualified(stateBaseType, stateBaseType.Name),
      name: stateBaseType.Name,
      baseId: CodeService.GetNameFullyQualifiedWithoutGenerics(
        stateBaseType, stateBaseType.Name
      )
    );

    var stateTypesById = new Dictionary<string, INamedTypeSymbol> {
      [root.Id] = stateBaseType
    };
    var stateGraphsById = new Dictionary<string, LogicBlockGraph> {
      [root.Id] = root
    };

    var subtypesByBaseType =
      new Dictionary<string, IList<INamedTypeSymbol>>();

    foreach (var subtype in stateSubtypes) {
      if (token.IsCancellationRequested) {
        return null;
      }

      var baseType = subtype.BaseType;

      if (baseType is not INamedTypeSymbol namedBaseType) {
        continue;
      }

      var baseTypeId = CodeService.GetNameFullyQualifiedWithoutGenerics(
        namedBaseType, namedBaseType.Name
      );

      if (!subtypesByBaseType.ContainsKey(baseTypeId)) {
        subtypesByBaseType[baseTypeId] = new List<INamedTypeSymbol>();
      }

      subtypesByBaseType[baseTypeId].Add(subtype);
    }

    // Find initial state
    var getInitialStateMethod = symbol.GetMembers()
      .FirstOrDefault(
        member => member is IMethodSymbol method &&
          member.Name == Constants.LOGIC_BLOCK_GET_INITIAL_STATE
      );

    string? initialStateId = null;

    if (
      getInitialStateMethod is IMethodSymbol initialStateMethod &&
      initialStateMethod
        .DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is
      SyntaxNode initialStateMethodNode
    ) {
      var initialStateVisitor = new ReturnTypeVisitor(
        model, token, CodeService, stateBaseType
      );
      initialStateVisitor.Visit(initialStateMethodNode);
      initialStateId = initialStateVisitor.ReturnTypes.FirstOrDefault();
      Log.Print($"Initial state type: {initialStateId}");
    }

    // Convert the subtypes into a graph by recursively building the graph
    // from the base state.
    LogicBlockGraph buildGraph(
      INamedTypeSymbol type, INamedTypeSymbol baseType
    ) {
      var typeId = CodeService.GetNameFullyQualifiedWithoutGenerics(
        type, type.Name
      );

      var graph = new LogicBlockGraph(
        id: typeId,
        name: type.Name,
        baseId: CodeService.GetNameFullyQualifiedWithoutGenerics(
          baseType, baseType.Name
        )
      );

      stateTypesById[typeId] = type;
      stateGraphsById[typeId] = graph;

      var subtypes = subtypesByBaseType.ContainsKey(typeId)
        ? subtypesByBaseType[typeId]
        : new List<INamedTypeSymbol>();

      foreach (var subtype in subtypes) {
        graph.Children.Add(buildGraph(subtype, type));
      }

      return graph;
    }

    root.Children.AddRange(subtypesByBaseType[root.BaseId]
      .Select((stateType) => buildGraph(stateType, stateBaseType))
    );

    foreach (var state in stateGraphsById.Values) {
      var statesAndOutputs = GetStatesAndOutputs(
        stateTypesById[state.Id], model, token, stateBaseType
      );
      foreach (var pair in statesAndOutputs.InputToStates) {
        state.InputToStates[pair.Key] = pair.Value;
      }
      foreach (var outputSet in statesAndOutputs.Outputs) {
        state.Outputs.Add(outputSet.Key, outputSet.Value);
      }
    }

    var implementation = new LogicBlockImplementation(
      FilePath: destFile,
      Id: CodeService.GetNameFullyQualified(symbol, symbol.Name),
      Name: symbol.Name,
      InitialStateId: initialStateId,
      Graph: root,
      Inputs: inputs.ToImmutableDictionary(),
      Outputs: outputs.ToImmutableDictionary(),
      StatesById: stateGraphsById.ToImmutableDictionary()
    );

    Log.Print("Graph: " + implementation.Graph.ToString());
    Log.Print("Inputs: " + string.Join(",", implementation.Inputs));
    Log.Print("Outputs: " + string.Join(",", implementation.Outputs));

    return implementation;
  }

  public ILogicBlockResult ConvertStateGraphToUml(
    LogicBlockImplementation implementation,
    CancellationToken token
  ) {
    var sb = new StringBuilder();

    // need to build up the uml string describing the state graph
    var graph = implementation.Graph;

    var transitions = new List<string>();
    foreach (var stateId in implementation.StatesById) {
      var state = stateId.Value;
      foreach (var inputToStates in state.InputToStates) {
        var inputId = inputToStates.Key;
        foreach (var destStateId in inputToStates.Value) {
          var dest = implementation.StatesById[destStateId];
          transitions.Add(
            $"{state.Name} --> " +
            $"{dest.Name} : {implementation.Inputs[inputId].Name}"
          );
        }
      }
    }

    transitions.Sort();

    var initialStateString = implementation.InitialStateId != null
      ? "[*] --> " +
        $"{implementation.StatesById[implementation.InitialStateId].Name}"
      : "";

    var text = Format($"""
    @startuml {implementation.Name}
    {WriteGraph(implementation.Graph, implementation, 0)}

    {transitions}

    {initialStateString}
    @enduml
    """);

    return new LogicBlockOutputResult(
      FilePath: implementation.FilePath,
      Name: implementation.Name,
      Content: text
    );
  }

  private IEnumerable<string> WriteGraph(
    LogicBlockGraph graph,
    LogicBlockImplementation impl,
    int t
  ) {
    var lines = new List<string>();

    var isMultilineState = graph.Children.Count > 0 ||
      graph.Outputs.Count > 0;

    var isRoot = graph == impl.Graph;

    if (isMultilineState) {
      if (isRoot) {
        lines.Add(
          $"{Tab(t)}state \"{impl.Name} {graph.Name}\" as {graph.Name} {{"
        );
      }
      else {
        lines.Add($"{Tab(t)}state {graph.Name} {{");
      }
    }
    else if (isRoot) {
      lines.Add($"{Tab(t)}state \"{impl.Name} {graph.Name}\" as {graph.Name}");
    }
    else {
      lines.Add($"{Tab(t)}state {graph.Name}");
    }

    foreach (var child in graph.Children) {
      lines.AddRange(
        WriteGraph(child, impl, t + 1)
      );
    }

    foreach (
      var outputContext in graph.Outputs.Keys.OrderBy(key => key.DisplayName)
    ) {
      var outputs = graph.Outputs[outputContext]
        .Select(outputId => impl.Outputs[outputId].Name)
        .OrderBy(output => output);
      var line = string.Join(", ", outputs);
      lines.Add(
        $"{Tab(t + 1)}{graph.Name} : {outputContext.DisplayName} → {line}"
      );
    }

    if (isMultilineState) { lines.Add($"{Tab(t)}}}"); }
    return lines;
  }

  private Dictionary<string, ILogicBlockSubclass> GetSubclassesById(
    INamedTypeSymbol containerType, INamedTypeSymbol ancestorType
  ) {
    var subclasses = new Dictionary<string, ILogicBlockSubclass>();
    var typesToSearch = CodeService.GetSubtypesExtending(
        containerType, ancestorType
      ).ToImmutableArray();
    Log.Print(
      $"Searching {typesToSearch.Length} types nested inside " +
      $"{containerType.Name} for subclasses of {ancestorType.Name}"
    );
    foreach (var subtype in typesToSearch) {
      Log.Print($"Finding subtype: {subtype.Name}");
      var id = CodeService.GetNameFullyQualified(subtype, subtype.Name);
      var baseType = CodeService.GetAllBaseTypes(subtype).FirstOrDefault() ??
        ancestorType;
      subclasses[id] = new LogicBlockSubclass(
        Id: id,
        Name: subtype.Name,
        BaseId: CodeService.GetNameFullyQualifiedWithoutGenerics(
          baseType, baseType.Name
        )
      );
    }
    return subclasses;
  }

  public StatesAndOutputs GetStatesAndOutputs(
    INamedTypeSymbol type,
    SemanticModel model,
    CancellationToken token,
    INamedTypeSymbol stateBaseType
  ) {
    // type is the state type

    var inputToStatesBuilder = ImmutableDictionary
      .CreateBuilder<string, ImmutableHashSet<string>>();
    var outputsBuilder = ImmutableDictionary
      .CreateBuilder<IOutputContext, ImmutableHashSet<string>>();

    // Get all of the handled inputs by looking at the implemented input
    // handler interfaces.

    var handledInputInterfaces = type.Interfaces.Where(
      (interfaceType) => CodeService.GetNameFullyQualifiedWithoutGenerics(
        interfaceType, interfaceType.Name
      ) is
        Constants.LOGIC_BLOCK_INPUT_INTERFACE_ID or
        Constants.LOGIC_BLOCK_INPUT_ASYNC_INTERFACE_ID &&
        interfaceType.TypeArguments.Length == 1
    );

    // Get all syntax nodes comprising this type declaration.
    var syntaxNodes = type.DeclaringSyntaxReferences
      .Select(syntaxRef => syntaxRef.GetSyntax(token));

    // Find constructors for the type, filtering out any constructors for nested
    // types.
    var constructorNodes = syntaxNodes
      .SelectMany(syntaxNode => syntaxNode.ChildNodes())
      .OfType<ConstructorDeclarationSyntax>().ToList();

    var handledInputInterfaceSyntaxes = handledInputInterfaces
      .SelectMany(
        interfaceType => interfaceType.DeclaringSyntaxReferences
          .Select(syntaxRef => syntaxRef.GetSyntax(token))
      );

    var inputHandlerMethods = new List<MethodDeclarationSyntax>();

    var outputVisitor = new OutputVisitor(
      model, token, CodeService, OutputContexts.None
    );
    foreach (var constructor in constructorNodes) {
      // Collect outputs from every syntax node comprising the state type.
      outputVisitor.Visit(constructor);
    }
    outputsBuilder.AddRange(outputVisitor.OutputTypes);

    foreach (var handledInputInterface in handledInputInterfaces) {
      var interfaceMembers = handledInputInterface.GetMembers();
      var inputTypeSymbol = handledInputInterface.TypeArguments[0];
      if (inputTypeSymbol is not INamedTypeSymbol inputType) {
        continue;
      }
      if (interfaceMembers.Length == 0) { continue; }
      var implementation = type.FindImplementationForInterfaceMember(
        interfaceMembers[0]
      );
      if (implementation is not IMethodSymbol methodSymbol) {
        continue;
      }
      var handlerMethodSyntax = methodSymbol
        .DeclaringSyntaxReferences
        .FirstOrDefault()?
        .GetSyntax(token) as MethodDeclarationSyntax;
      if (handlerMethodSyntax is not MethodDeclarationSyntax methodSyntax) {
        continue;
      }
      inputHandlerMethods.Add(methodSyntax);
      var inputId = CodeService.GetNameFullyQualifiedWithoutGenerics(
        inputType, inputType.Name
      );
      var outputContext = OutputContexts.OnInput(inputType.Name);
      var returnTypeVisitor = new ReturnTypeVisitor(
        model, token, CodeService, stateBaseType
      );
      outputVisitor = new OutputVisitor(
        model, token, CodeService, outputContext
      );

      returnTypeVisitor.Visit(methodSyntax);
      outputVisitor.Visit(methodSyntax);

      if (outputVisitor.OutputTypes.ContainsKey(outputContext)) {
        outputsBuilder.Add(
          outputContext, outputVisitor.OutputTypes[outputContext]
        );
      }

      inputToStatesBuilder.Add(
        inputId,
        returnTypeVisitor.ReturnTypes
      );
    }

    // find methods on type that aren't input handlers or constructors
    var allOtherMethods = syntaxNodes
      .SelectMany(syntaxNode => syntaxNode.ChildNodes())
      .OfType<MethodDeclarationSyntax>()
      .Where(
        methodSyntax => !inputHandlerMethods.Contains(methodSyntax)
      );

    foreach (var otherMethod in allOtherMethods) {
      Log.Print("Examining method: " + otherMethod.Identifier.Text);
      var outputContext = OutputContexts.Method(otherMethod.Identifier.Text);

      outputVisitor = new OutputVisitor(
        model, token, CodeService, outputContext
      );
      outputVisitor.Visit(otherMethod);

      if (outputVisitor.OutputTypes.ContainsKey(outputContext)) {
        outputsBuilder.Add(
          outputContext, outputVisitor.OutputTypes[outputContext]
        );
      }
    }

    var inputToStates = inputToStatesBuilder.ToImmutable();

    foreach (var input in inputToStates.Keys) {
      Log.Print(
        $"{type.Name} + {input.Split('.').Last()} -> " +
        $"{string.Join(", ", inputToStates[input].Select(
          s => s.Split('.').Last())
        )}"
      );
    }

    var outputs = outputsBuilder.ToImmutable();

    return new StatesAndOutputs(
      InputToStates: inputToStates,
      Outputs: outputs
    );
  }
}
