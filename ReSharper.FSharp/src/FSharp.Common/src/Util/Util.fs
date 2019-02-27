﻿namespace JetBrains.ReSharper.Plugins.FSharp.Common.Util

open JetBrains.ProjectModel
open JetBrains.ReSharper.Psi.Modules

[<AutoOpen>]
module rec CommonUtil =
    open System
    open System.Collections.Generic
    open System.Diagnostics
    open JetBrains.Application.UI.Icons.ComposedIcons
    open JetBrains.DataFlow
    open JetBrains.DocumentModel
    open JetBrains.Lifetimes
    open JetBrains.Util
    open JetBrains.Util.dataStructures.TypedIntrinsics
    open Microsoft.FSharp.Compiler
    open Microsoft.FSharp.Compiler.SourceCodeServices

    let inline isNotNull x = not (isNull x)

    let ensureAbsolute (path: FileSystemPath) (projectDirectory: FileSystemPath) =
        match path.AsRelative() with
        | null -> path
        | relativePath -> projectDirectory.Combine(relativePath)

    let concatErrors errors =
        Seq.fold (fun s (e: FSharpErrorInfo) -> s + "\n" + e.Message) "" errors

    [<CompiledName("DecompileOpName")>]
    let decompileOpName name =
        PrettyNaming.DecompileOpName name
        
    type IDictionary<'TKey, 'TValue> with
        member x.remove (key: 'TKey) = x.Remove key |> ignore
        member x.add (key: 'TKey, value: 'TValue) = x.Add(key, value) |> ignore
        member x.contains (key: 'TKey) = x.ContainsKey key

    type ISet<'T> with
        member x.remove el = x.Remove el |> ignore
        member x.add el = x.Add el |> ignore

    let fsExtensions = ["fs"; "fsi"; "ml"; "mli"; "fsx"; "fsscript"] |> Set.ofList
    let dllExtensions = ["dll"; "exe"] |> Set.ofList

    let isImplFileExtension extension =
        extension = "fs" || extension = "ml"

    let isSigFileExtension extension =
        extension = "fsi" || extension = "mli"

    let (|ImplExtension|_|) extension =
        if isImplFileExtension extension then someUnit else None

    let (|SigExtension|_|) extension =
        if isSigFileExtension extension then someUnit else None

    let isImplFile (path: FileSystemPath) =
        isImplFileExtension path.ExtensionNoDot

    let isSigFile (path: FileSystemPath) =
        isSigFileExtension path.ExtensionNoDot

    type Line = Int32<DocLine>
    type Column = Int32<DocColumn>
    type FileSystemPath = JetBrains.Util.FileSystemPath

    type Int32<'T> with

        [<DebuggerStepThrough>]
        member x.Next = x.Plus1()

        [<DebuggerStepThrough>]
        member x.Previous = x.Minus1()

    let inline docLine (x: int)   = Line.op_Explicit(x)
    let inline docColumn (x: int) = Column.op_Explicit(x)

    type Range.range with
        member inline x.GetStartLine()   = x.StartLine - 1 |> docLine
        member inline x.GetEndLine()     = x.EndLine - 1   |> docLine
        member inline x.GetStartColumn() = x.StartColumn   |> docColumn
        member inline x.GetEndColumn()   = x.EndColumn     |> docColumn

        member x.ToTextRange(document: IDocument) =
            let startOffset = document.GetLineStartOffset(x.GetStartLine()) + x.StartColumn
            let endOffset = document.GetLineStartOffset(x.GetEndLine()) + x.EndColumn
            TextRange(startOffset, endOffset)
        
        member x.ToDocumentRange(document: IDocument) =
            DocumentRange(document, x.ToTextRange(document))

    let tryGetValue (key: 'TKey) (dictionary: IDictionary<'TKey,'TValue>) =
        let res = ref Unchecked.defaultof<'TValue>
        match dictionary.TryGetValue(key, res), res with
        | true, value -> Some !value
        | _ -> None

    let (|ArgValue|) (arg: PropertyChangedEventArgs<_>) = arg.New

    let (|AddRemoveArgs|) (args: AddRemoveEventArgs<_>) =
        args.Value

    let (|Pair|) (pair: Pair<_,_>) =
        pair.First, pair.Second

    let (|PsiModuleReference|) (ref: IPsiModuleReference) =
        ref.Module

    let (|ProjectFile|ProjectFolder|UnknownProjectItem|) (projectItem: IProjectItem) =
        match projectItem with
        | :? IProjectFile as file -> ProjectFile file
        | :? IProjectFolder as folder -> ProjectFolder folder
        | _ -> UnknownProjectItem

    let equalsIgnoreCase other (string: string) =
        string.Equals(other, StringComparison.OrdinalIgnoreCase)

    let startsWith other (string: string) =
        string.StartsWith(other, StringComparison.Ordinal)

    let endsWith other (string: string) =
        string.EndsWith(other, StringComparison.Ordinal)

    let eq a b = a = b

    let getCommonParent path1 path2 =
        FileSystemPath.GetDeepestCommonParent(path1, path2)

    /// Used in tests. Should not be invoked on BackSlashSeparatedRelativePath.
    let (|UnixSeparators|) (path: IPath) =
        let separatorStyle = FileSystemPathEx.SeparatorStyle.Unix
        match path with
        | :? FileSystemPath as path -> path.NormalizeSeparators(separatorStyle)
        | :? RelativePath as path -> path.NormalizeSeparators(separatorStyle)
        | _ -> failwith "Should not be invoked on BackSlashSeparatedRelativePath."

    let setComparer =
        { new IEqualityComparer<HashSet<_>> with
            member this.Equals(x, y) = x.SetEquals(y)
            member this.GetHashCode(x) = x.Count }

    type Lifetime with
        member x.AddAction2(func: Func<_,_>) =
            x.OnTermination(fun _ -> func.Invoke() |> ignore) |> ignore

    let compose a b = CompositeIconId.Compose(a, b)

[<AutoOpen>]
module rec FcsUtil =
    open Microsoft.FSharp.Compiler.Ast

    let (|ExprRange|) (expr: SynExpr) = expr.Range
    let (|PatRange|) (pat: SynPat) = pat.Range
    let (|IdentRange|) (id: Ident) = id.idRange
    let (|TypeRange|) (typ: SynType) = typ.Range


[<AutoOpen>]
module rec FSharpMsBuildUtils =
    open BuildActions
    open ItemTypes
    open JetBrains.Platform.MsBuildHost.Models

    module ItemTypes =
        let [<Literal>] compileBeforeItemType = "CompileBefore"
        let [<Literal>] compileAfterItemType = "CompileAfter"
        let [<Literal>] folderItemType = "Folder"

    module BuildActions =
        let compileBefore = BuildAction.GetOrCreate(compileBeforeItemType)  
        let compileAfter = BuildAction.GetOrCreate(compileAfterItemType)  

    let isCompileBefore itemType =
        equalsIgnoreCase compileBeforeItemType itemType

    let isCompileAfter itemType =
        equalsIgnoreCase compileAfterItemType itemType

    let (|CompileBefore|_|) itemType =
        if isCompileBefore itemType then someUnit else None

    let (|CompileAfter|_|) itemType =
        if isCompileAfter itemType then someUnit else None

    let (|Folder|_|) (itemType: string) =
        if equalsIgnoreCase folderItemType itemType then someUnit else None

    let (|BuildAction|) itemType =
        BuildAction.GetOrCreate(itemType)

    let (|RdItem|) (item: RdProjectItem) =
        RdItem (item.ItemType, item.EvaluatedInclude)

    let (|SourceFile|_|) (buildAction: BuildAction) =
        if buildAction.IsCompile() || buildAction = compileBefore || buildAction = compileAfter then someUnit else None

    let (|Resource|_|) buildAction =
        if buildAction = BuildAction.RESOURCE then someUnit else None

    let changesOrder = function
        | CompileBefore | CompileAfter -> true
        | _ -> false

    type BuildAction with
        member x.ChangesOrder = changesOrder x.Value

[<Extension>]
module PsiUtil =
    [<Extension; CompiledName("GetSymbolScope")>]
    let getSymbolScope (psiModule: IPsiModule) =
        psiModule.GetPsiServices().Symbols.GetSymbolScope(psiModule, true, true)
