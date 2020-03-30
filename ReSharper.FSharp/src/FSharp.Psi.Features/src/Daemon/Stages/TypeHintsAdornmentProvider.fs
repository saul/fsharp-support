﻿namespace JetBrains.ReSharper.Plugins.FSharp.Psi.Features.Daemon.Stages

open System
open System.Drawing
open JetBrains.DocumentModel
open JetBrains.ProjectModel
open JetBrains.ReSharper.Daemon.Stages
open JetBrains.ReSharper.Feature.Services.Daemon.Attributes
open JetBrains.ReSharper.Plugins.FSharp
open JetBrains.ReSharper.Psi.Tree
open JetBrains.ReSharper.Feature.Services.Daemon
open JetBrains.ReSharper.Plugins.FSharp.Daemon.Cs.Stages
open JetBrains.ReSharper.Plugins.FSharp.Psi.Tree
open JetBrains.Rider.Model
open JetBrains.TextControl.DocumentMarkup
open JetBrains.UI.RichText
open FSharp.Compiler.SourceCodeServices
open FSharp.Compiler.Layout
open JetBrains.ReSharper.Plugins.FSharp.Psi.Impl.Tree
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Psi.Modules

[<DaemonIntraTextAdornmentProvider(typeof<TypeHintsAdornmentProvider>)>]
[<StaticSeverityHighlighting(Severity.INFO,
     HighlightingGroupIds.IntraTextAdornmentsGroup,
     AttributeId = AnalysisHighlightingAttributeIds.PARAMETER_NAME_HINT,
     OverlapResolve = OverlapResolveKind.NONE,
     ShowToolTipInStatusBar = false)>]
type TypeHintHighlighting(text: RichText, range: DocumentRange) =
    interface IHighlighting with
        member x.ToolTip = null
        member x.ErrorStripeToolTip = null
        member x.IsValid() = not text.IsEmpty && not range.IsEmpty
        member x.CalculateRange() = range

    interface IHighlightingWithTestOutput with
        member x.TestOutput = text.ToString()

    member x.Text = text

and [<SolutionComponent>] TypeHintsAdornmentProvider() =
    interface IHighlighterIntraTextAdornmentProvider with
        member x.CreateDataModel(highlighter) =
            match highlighter.UserData with
            | :? TypeHintHighlighting as thh ->
                { new IIntraTextAdornmentDataModel with
                    override x.Text = thh.Text
                    override x.HasContextMenu = false
                    override x.ContextMenuTitle = null
                    override x.ContextMenuItems = null
                    override x.IsNavigable = false
                    override x.ExecuteNavigation _ = ()
                    override x.SelectionRange = Nullable<_>()
                    override x.IconId = null
                    override x.IsPreceding = false
                }
            | _ -> null

type TypeHighlightingVisitor(fsFile: IFSharpFile, checkResults: FSharpCheckFileResults) =
    inherit TreeNodeVisitor<IHighlightingConsumer>()

    let tokenNames = ["|>"]

    override x.VisitNode(node, context) =
        for child in node.Children() do
            match child with
            | :? IFSharpTreeNode as treeNode -> treeNode.Accept(x, context)
            | _ -> ()

    override x.VisitBinaryAppExpr(binding, consumer) =
        let opExpr = binding.Operator
        if opExpr.QualifiedName <> "|>" then () else

        match opExpr.Identifier.As<FSharpIdentifierToken>() with
        | null -> ()
        | token ->

            let sourceFile = opExpr.GetSourceFile()
            let coords = sourceFile.Document.GetCoordsByOffset(opExpr.GetTreeEndOffset().Offset)
            let lineText = sourceFile.Document.GetLineText(coords.Line)
            use cookie = CompilationContextCookie.GetOrCreate(fsFile.GetPsiModule().GetContextFromModule())

            let getTooltip = checkResults.GetStructuredToolTipText(int coords.Line + 1, int coords.Column, lineText, tokenNames, FSharpTokenTag.Identifier)
            let (FSharpToolTipText layouts) = getTooltip.RunAsTask()

            // The |> operator shouldn't have any overloads, and it should have two type parameters
            match layouts with
            | [ FSharpStructuredToolTipElement.Group [ { TypeMapping = [ _; returnTypeParam ] } ] ] ->
                // TODO: do something way less hacky here
                // Trim off the: "'U is " prefix
                let text = ": " + (showL returnTypeParam).Substring(6)

                TypeHintHighlighting(RichText text, binding.RightArgument.GetNavigationRange().EndOffsetRange())
                |> consumer.AddHighlighting
            | _ -> ()

        x.VisitNode(binding, consumer)

type TypeHintsHighlightingProcess(fsFile, settings, daemonProcess) =
    inherit FSharpDaemonStageProcessBase(fsFile, daemonProcess)

    let [<Literal>] opName = "TypeHintsHighlightingProcess"

    override x.Execute(committer) =
        match fsFile.GetParseAndCheckResults(true, opName) with
        | None -> ()
        | Some results ->

        let consumer = FilteringHighlightingConsumer(daemonProcess.SourceFile, fsFile, settings)
        fsFile.Accept(TypeHighlightingVisitor(fsFile, results.CheckResults), consumer)
        committer.Invoke(DaemonStageResult(consumer.Highlightings))

[<DaemonStage(StagesBefore = [| typeof<GlobalFileStructureCollectorStage> |])>]
type TypeHintsStage() =
    inherit FSharpDaemonStageBase()

    override x.IsSupported(sourceFile, processKind) =
        processKind = DaemonProcessKind.VISIBLE_DOCUMENT
        && base.IsSupported(sourceFile, processKind)
        && not (sourceFile.LanguageType.Is<FSharpSignatureProjectFileType>())

    override x.CreateStageProcess(fsFile, settings, daemonProcess) =
        TypeHintsHighlightingProcess(fsFile, settings, daemonProcess) :> _
