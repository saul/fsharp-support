namespace JetBrains.ReSharper.Plugins.FSharp

open System
open System.IO
open FSharp.Compiler.SourceCodeServices
open JetBrains.Application
open JetBrains.Application.Environment
open JetBrains.Application.Environment.Helpers
open JetBrains.Application.Threading
open JetBrains.DataFlow
open JetBrains.Diagnostics
open JetBrains.Lifetimes
open JetBrains.ProjectModel
open JetBrains.ReSharper.Host.Features.BackgroundTasks
open JetBrains.Util

[<ShellComponent>]
type FcsReactorMonitor
        (
            lifetime: Lifetime,
            locks: IShellLocks,
            backgroundTaskHost: RiderBackgroundTaskHost,
            threading: IThreading,
            configurations: RunsProducts.ProductConfigurations,
            logger: ILogger
        ) =

    /// How long after the reactor becoming busy that the background task should be shown
    let showDelay =
        if configurations.IsInternalMode() then 1.0 else 5.0
        |> TimeSpan.FromSeconds

    /// How long after the reactor becoming free that the background task should be hidden
    let hideDelay = TimeSpan.FromSeconds 0.5

    let isReactorBusy = new Property<bool>("isReactorBusy")
    let operationCount = new Property<int64>("operationCount")

    let taskHeader = new Property<string>("taskHeader")
    let taskDescription = new Property<string>("taskDescription")
    let showBackgroundTask = new Property<bool>("showBackgroundTask")

    let createNewTask (activeLifetime: Lifetime) =
        locks.Dispatcher.AssertAccess()

        let task =
            RiderBackgroundTaskBuilder.Create()
                .WithTitle("F# Compiler Service is busy...")
                .WithHeader(taskHeader)
                .WithDescription(taskDescription)
                .AsIndeterminate()
                .AsNonCancelable()
                .Build()

        // Only show the background task after we've been busy for some time
        threading.QueueAt(
            activeLifetime,
            "FcsReactorMonitor.AddNewTask",
            showDelay,
            fun () -> backgroundTaskHost.AddNewTask(activeLifetime, task)
        )

    let onOperationStart (opDescription: string) (opArg: string) =
        locks.Dispatcher.AssertAccess()

        operationCount.SetValue(operationCount.Value + 1L) |> ignore
        taskHeader.SetValue opDescription |> ignore

        let opArg = if Path.IsPathRooted opArg then Path.GetFileName opArg else opArg
        taskDescription.SetValue(sprintf "%s (operation #%d)" opArg operationCount.Value) |> ignore

        isReactorBusy.SetValue true |> ignore

    let onOperationEnd () =
        locks.Dispatcher.AssertAccess()

        isReactorBusy.SetValue false |> ignore

    do
        showBackgroundTask.WhenTrue(lifetime, Action<_> createNewTask)

        isReactorBusy.WhenTrue(lifetime, fun _ -> showBackgroundTask.SetValue true |> ignore)

        isReactorBusy.WhenFalse(lifetime, fun lt ->
            threading.QueueAt(lt, "FcsReactorMonitor.HideTask", hideDelay, fun () ->
                showBackgroundTask.SetValue false |> ignore))

    interface IReactorListener with
        override __.OnReactorPauseBeforeBackgroundWork pauseMillis =
            logger.Verbose("Pausing before background work for {0} ms", pauseMillis)
        override __.OnReactorOperationStart userOpName opName opArg approxQueueLength =
            logger.Verbose("--> {0}.{1} ({2}), queue length {3}", userOpName, opName, opArg, approxQueueLength)
            locks.ExecuteOrQueue(lifetime, "FcsReactorMonitor.OnReactorOperationStart", fun () ->
                onOperationStart (userOpName + "." + opName) opArg)
        override __.OnReactorOperationEnd userOpName opName elapsed =
            let level =
                if elapsed > showDelay then LoggingLevel.WARN
                else LoggingLevel.VERBOSE
            logger.LogMessage(level, "<-- {0}.{1}, took {2} ms", userOpName, opName, elapsed.TotalMilliseconds)
            locks.ExecuteOrQueue(lifetime, "FcsReactorMonitor.OnReactorOperationEnd", onOperationEnd)
        override __.OnReactorBackgroundStart bgUserOpName bgOpName bgOpArg =
            // todo: do we want to show background steps too?
            logger.Verbose("--> Background step {0}.{1} ({2})", bgUserOpName, bgOpName, bgOpArg)
        override __.OnReactorBackgroundCancelled bgUserOpName bgOpName =
            logger.Verbose("<-- Background step {0}.{1}, was cancelled", bgUserOpName, bgOpName)
        override __.OnReactorBackgroundEnd _bgUserOpName _bgOpName elapsed =
            let level =
                if elapsed > showDelay then LoggingLevel.WARN
                else LoggingLevel.VERBOSE
            logger.LogMessage(level, "<-- Background step took {0} ms", elapsed.TotalMilliseconds)
        override __.OnSetBackgroundOp approxQueueLength =
            logger.Verbose("Enqueue start background, queue length {0}", approxQueueLength)
        override __.OnCancelBackgroundOp () =
            logger.Verbose("Trying to cancel any active background work...")
        override __.OnEnqueueOp userOpName opName opArg approxQueueLength =
            logger.Verbose("Enqueue: {0}.{1} ({2}), queue length {3}", userOpName, opName, opArg, approxQueueLength)
