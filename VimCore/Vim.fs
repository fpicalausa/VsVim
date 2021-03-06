﻿#light

namespace Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Outlining
open Microsoft.VisualStudio.Text.Classification
open System.ComponentModel.Composition
open System.Collections.Generic
open Vim.Modes
open Vim.Interpreter

[<Export(typeof<IBulkOperations>)>]
type internal BulkOperations  
    [<ImportingConstructor>]
    (
        _vimHost : IVimHost
    ) =

    /// The active count of bulk operations
    let mutable _bulkOperationCount = 0

    /// Called when a bulk operation is initiated in VsVim.
    member x.BeginBulkOperation () = 

        if _bulkOperationCount = 0 then
            _vimHost.BeginBulkOperation()

        _bulkOperationCount <- _bulkOperationCount + 1

        {
            new System.IDisposable with 
                member x.Dispose() = 
                    _bulkOperationCount <- _bulkOperationCount - 1
                    if _bulkOperationCount = 0 then
                        _vimHost.EndBulkOperation() }

    interface IBulkOperations with
        member x.InBulkOperation = _bulkOperationCount > 0
        member x.BeginBulkOperation () = x.BeginBulkOperation()

type internal VimData() =

    let mutable _currentDirectory = System.Environment.CurrentDirectory
    let mutable _previousCurrentDirecotry = _currentDirectory
    let mutable _commandHistory = HistoryList()
    let mutable _searchHistory = HistoryList()
    let mutable _lastSubstituteData : SubstituteData option = None
    let mutable _lastPatternData = { Pattern = StringUtil.empty; Path = Path.Forward }
    let mutable _lastShellCommand : string option = None
    let mutable _lastCharSearch : (CharSearchKind * Path * char) option = None
    let mutable _lastMacroRun : char option = None
    let mutable _lastCommand : StoredCommand option = None
    let _searchRanEvent = StandardEvent()
    let _highlightSearchOneTimeDisabled = StandardEvent()

    interface IVimData with 
        member x.CurrentDirectory
            with get () = _currentDirectory
            and set value = 
                _previousCurrentDirecotry <- _currentDirectory
                _currentDirectory <- value
        member x.CommandHistory
            with get () = _commandHistory
            and set value = _commandHistory <- value
        member x.SearchHistory 
            with get () = _searchHistory
            and set value = _searchHistory <- value
        member x.LastSubstituteData 
            with get () = _lastSubstituteData
            and set value = _lastSubstituteData <- value
        member x.LastCommand 
            with get () = _lastCommand
            and set value = _lastCommand <- value
        member x.LastShellCommand
            with get () = _lastShellCommand
            and set value = _lastShellCommand <- value
        member x.LastPatternData 
            with get () = _lastPatternData
            and set value = _lastPatternData <- value
        member x.PreviousCurrentDirectory = _previousCurrentDirecotry
        member x.LastCharSearch 
            with get () = _lastCharSearch
            and set value = _lastCharSearch <- value
        member x.LastMacroRun 
            with get () = _lastMacroRun
            and set value = _lastMacroRun <- value
        member x.RaiseHighlightSearchOneTimeDisable () = _highlightSearchOneTimeDisabled.Trigger ()
        member x.RaiseSearchRanEvent () = _searchRanEvent.Trigger()
        [<CLIEvent>]
        member x.SearchRan = _searchRanEvent.Publish
        [<CLIEvent>]
        member x.HighlightSearchOneTimeDisabled = _highlightSearchOneTimeDisabled.Publish

[<Export(typeof<IVimBufferFactory>)>]
type internal VimBufferFactory

    [<ImportingConstructor>]
    (
        _host : IVimHost,
        _editorOperationsFactoryService : IEditorOperationsFactoryService,
        _editorOptionsFactoryService : IEditorOptionsFactoryService,
        _outliningManagerService : IOutliningManagerService,
        _completionWindowBrokerFactoryService : IDisplayWindowBrokerFactoryService,
        _commonOperationsFactory : ICommonOperationsFactory,
        _wordUtilFactory : IWordUtilFactory,
        _textChangeTrackerFactory : ITextChangeTrackerFactory,
        _textSearchService : ITextSearchService,
        _bufferTrackingService : IBufferTrackingService,
        _undoManagerProvider : ITextBufferUndoManagerProvider,
        _statusUtilFactory : IStatusUtilFactory,
        _foldManagerFactory : IFoldManagerFactory,
        _keyboardDevice : IKeyboardDevice,
        _mouseDevice : IMouseDevice,
        _wordCompletionSessionFactoryService : IWordCompletionSessionFactoryService,
        _bulkOperations : IBulkOperations
    ) =

    /// Create an IVimTextBuffer instance for the provided ITextBuffer
    member x.CreateVimTextBuffer textBuffer (vim : IVim) = 
        let localSettings = LocalSettings(vim.GlobalSettings) :> IVimLocalSettings
        let wordUtil = _wordUtilFactory.GetWordUtil textBuffer
        let wordNavigator = wordUtil.CreateTextStructureNavigator WordKind.NormalWord
        VimTextBuffer(textBuffer, localSettings, wordNavigator, _bufferTrackingService, vim)

    /// Create a VimBufferData instance for the given ITextView and IVimTextBuffer.  This is mainly
    /// used for testing purposes
    member x.CreateVimBufferData (vimTextBuffer : IVimTextBuffer) (textView : ITextView) =
        Contract.Requires (vimTextBuffer.TextBuffer = textView.TextBuffer)

        let vim = vimTextBuffer.Vim
        let textBuffer = textView.TextBuffer
        let editOperations = _editorOperationsFactoryService.GetEditorOperations(textView)
        let statusUtil = _statusUtilFactory.GetStatusUtil textView
        let localSettings = vimTextBuffer.LocalSettings
        let jumpList = JumpList(textView, _bufferTrackingService) :> IJumpList

        let undoRedoOperations = 
            let history = 
                let manager = _undoManagerProvider.GetTextBufferUndoManager textBuffer
                if manager = null then None
                else manager.TextBufferUndoHistory |> Some
            UndoRedoOperations(statusUtil, history, editOperations) :> IUndoRedoOperations
        let wordUtil = _wordUtilFactory.GetWordUtil textBuffer
        let windowSettings = WindowSettings(vim.GlobalSettings, textView)
        VimBufferData(vimTextBuffer,textView, windowSettings, jumpList, statusUtil, undoRedoOperations, wordUtil) :> IVimBufferData

    /// Create an IVimBuffer instance for the provided VimBufferData
    member x.CreateVimBuffer (vimBufferData : IVimBufferData) = 
        let textView = vimBufferData.TextView
        let commonOperations = _commonOperationsFactory.GetCommonOperations vimBufferData
        let wordUtil = vimBufferData.WordUtil

        /// Setup the initial mode for an IVimBuffer.  The mode should be the current mode of the
        /// underlying IVimTextBuffer.  This should be as easy as switching the mode on startup 
        /// but this is complicated by the initialization of ITextView instances.  They can, and 
        /// often are, passed to CreateVimBuffer in an uninitialized state.  In that state certain
        /// operations like Select can't be done.  Hence we have to delay the mode switch until 
        /// the ITextView is fully initialized.  Put all of that logic here.
        let rec setupInitialMode (vimBuffer : IVimBuffer) = 
            if textView.TextViewLines = null then
                // It's not initialized.  Need to wait for the ITextView to get initialized. Setup 
                // the event listener so we can setup the true mode.  Make sure to unhook from the 
                // event to avoid memory leaks
                let bag = DisposableBag()

                textView.LayoutChanged
                |> Observable.subscribe (fun _ -> 
                    setupInitialMode vimBuffer
                    bag.DisposeAll())
                |> bag.Add
            elif vimBuffer.ModeKind = ModeKind.Uninitialized then
                // The ITextView is uninitialized and no one has forced the IVimBuffer out of
                // the uninitialized state.  Do the switch now to the correct mode
                vimBuffer.SwitchMode vimBufferData.VimTextBuffer.ModeKind ModeArgument.None |> ignore

        let wordNav = wordUtil.CreateTextStructureNavigator WordKind.NormalWord
        let incrementalSearch = IncrementalSearch(vimBufferData, commonOperations) :> IIncrementalSearch
        let capture = MotionCapture(vimBufferData, incrementalSearch) :> IMotionCapture

        let textChangeTracker = _textChangeTrackerFactory.GetTextChangeTracker vimBufferData
        let motionUtil = MotionUtil(vimBufferData, commonOperations) :> IMotionUtil
        let foldManager = _foldManagerFactory.GetFoldManager textView
        let insertUtil = InsertUtil(vimBufferData, commonOperations) :> IInsertUtil
        let commandUtil = CommandUtil(vimBufferData, motionUtil, commonOperations, foldManager, insertUtil, _bulkOperations) :> ICommandUtil

        let bufferRaw = VimBuffer(vimBufferData, incrementalSearch, motionUtil, wordNav, vimBufferData.WindowSettings)
        let buffer = bufferRaw :> IVimBuffer

        let vim = vimBufferData.Vim
        let createCommandRunner kind = CommandRunner (textView, vim.RegisterMap, capture, commandUtil, vimBufferData.StatusUtil, kind) :>ICommandRunner
        let broker = _completionWindowBrokerFactoryService.CreateDisplayWindowBroker textView
        let bufferOptions = _editorOptionsFactoryService.GetOptions(textView.TextBuffer)
        let interpreter = Interpreter.Interpreter(buffer, commonOperations, foldManager, FileSystem() :> IFileSystem, _bufferTrackingService)
        let visualOptsFactory visualKind = Modes.Visual.SelectionTracker(textView, vim.GlobalSettings, incrementalSearch, visualKind) :> Modes.Visual.ISelectionTracker
        let undoRedoOperations = vimBufferData.UndoRedoOperations

        let visualModeSeq =
            VisualKind.All
            |> Seq.map (fun visualKind -> 
                let tracker = visualOptsFactory visualKind
                ((Modes.Visual.VisualMode(vimBufferData, commonOperations, motionUtil, visualKind, createCommandRunner visualKind, capture, tracker)) :> IMode) )

        let selectModeSeq = 
            VisualKind.All
            |> Seq.map (fun visualKind ->
                let tracker = visualOptsFactory visualKind
                Modes.Visual.SelectMode(vimBufferData, visualKind, commonOperations, undoRedoOperations, tracker) :> IMode)
            |> List.ofSeq

        let visualModeList =
            visualModeSeq
            |> Seq.append selectModeSeq
            |> List.ofSeq

        // Normal mode values
        let editOptions = _editorOptionsFactoryService.GetOptions(textView)
        let modeList = 
            [
                ((Modes.Normal.NormalMode(vimBufferData, commonOperations, motionUtil, createCommandRunner VisualKind.Character, capture)) :> IMode)
                ((Modes.Command.CommandMode(buffer, commonOperations, interpreter)) :> IMode)
                ((Modes.Insert.InsertMode(buffer, commonOperations, broker, editOptions, undoRedoOperations, textChangeTracker, insertUtil, false, _keyboardDevice, _mouseDevice, wordUtil, _wordCompletionSessionFactoryService)) :> IMode)
                ((Modes.Insert.InsertMode(buffer, commonOperations, broker, editOptions, undoRedoOperations, textChangeTracker, insertUtil, true, _keyboardDevice, _mouseDevice, wordUtil, _wordCompletionSessionFactoryService)) :> IMode)
                ((Modes.SubstituteConfirm.SubstituteConfirmMode(vimBufferData, commonOperations) :> IMode))
                (DisabledMode(vimBufferData) :> IMode)
                (ExternalEditMode(vimBufferData) :> IMode)
            ] @ visualModeList
        modeList |> List.iter (fun m -> bufferRaw.AddMode m)
        setupInitialMode buffer
        bufferRaw

    interface IVimBufferFactory with
        member x.CreateVimTextBuffer textBuffer vim = x.CreateVimTextBuffer textBuffer vim :> IVimTextBuffer
        member x.CreateVimBufferData vimTextBuffer textView = x.CreateVimBufferData vimTextBuffer textView 
        member x.CreateVimBuffer vimBufferData = x.CreateVimBuffer vimBufferData :> IVimBuffer

/// Default implementation of IVim 
[<Export(typeof<IVim>)>]
type internal Vim
    (
        _vimHost : IVimHost,
        _bufferFactoryService : IVimBufferFactory,
        _bufferCreationListeners : Lazy<IVimBufferCreationListener> list,
        _globalSettings : IVimGlobalSettings,
        _markMap : IMarkMap,
        _keyMap : IKeyMap,
        _clipboardDevice : IClipboardDevice,
        _search : ISearchService,
        _fileSystem : IFileSystem,
        _vimData : IVimData,
        _bulkOperations : IBulkOperations,
        _variableMap : VariableMap
    ) =

    /// Key for IVimTextBuffer instances inside of the ITextBuffer property bag
    let _vimTextBufferKey = System.Object()

    /// Holds an IVimBuffer and the DisposableBag for event handlers on the IVimBuffer.  This
    /// needs to be removed when we're done with the IVimBuffer to avoid leaks
    let _bufferMap = Dictionary<ITextView, IVimBuffer * DisposableBag>()

    /// Holds the active stack of IVimBuffer instances
    let mutable _activeBufferStack : IVimBuffer list = List.empty

    /// Whether or not the vimrc file should be automatically loaded before creating the 
    /// first IVimBuffer instance
    let mutable _autoLoadVimRc = true
    let mutable _isLoadingVimRc = false

    /// Holds the setting information which was stored when loading the VimRc file.  This 
    /// is applied to IVimBuffer instances which are created when there is no active IVimBuffer
    let mutable _vimRcLocalSettings = LocalSettings(_globalSettings) :> IVimLocalSettings
    let mutable _vimRcWindowSettings : IVimWindowSettings option = None

    let _registerMap =
        let currentFileNameFunc() = 
            match _activeBufferStack with
            | [] -> None
            | h::_ -> 
                let name = _vimHost.GetName h.TextBuffer 
                let name = System.IO.Path.GetFileName(name)
                Some name
        RegisterMap(_vimData, _clipboardDevice, currentFileNameFunc) :> IRegisterMap

    let _recorder = MacroRecorder(_registerMap)

    /// Add the IMacroRecorder to the list of IVimBufferCreationListeners.  
    let _bufferCreationListeners =
        let item = Lazy<IVimBufferCreationListener>(fun () -> _recorder :> IVimBufferCreationListener)
        item :: _bufferCreationListeners

    do
        // When the 'history' setting is changed it impacts our history limits.  Keep track of 
        // them here
        //
        // Up cast here to work around the F# bug which prevents accessing a CLIEvent from
        // a derived type

        (_globalSettings :> IVimSettings).SettingChanged 
        |> Event.filter (fun args -> StringUtil.isEqual args.Setting.Name GlobalSettingNames.HistoryName)
        |> Event.add (fun _ -> 
            _vimData.SearchHistory.Limit <- _globalSettings.History
            _vimData.CommandHistory.Limit <- _globalSettings.History)

    [<ImportingConstructor>]
    new(
        host : IVimHost,
        bufferFactoryService : IVimBufferFactory,
        bufferTrackingService : IBufferTrackingService,
        [<ImportMany>] bufferCreationListeners : Lazy<IVimBufferCreationListener> seq,
        search : ITextSearchService,
        fileSystem : IFileSystem,
        clipboard : IClipboardDevice,
        bulkOperations : IBulkOperations) =
        let markMap = MarkMap(bufferTrackingService)
        let vimData = VimData() :> IVimData
        let variableMap = VariableMap()
        let globalSettings = GlobalSettings() :> IVimGlobalSettings
        let listeners = bufferCreationListeners |> List.ofSeq
        Vim(
            host,
            bufferFactoryService,
            listeners,
            globalSettings,
            markMap :> IMarkMap,
            KeyMap(globalSettings, variableMap) :> IKeyMap,
            clipboard,
            SearchService(search, globalSettings) :> ISearchService,
            fileSystem,
            vimData,
            bulkOperations,
            variableMap)

    member x.ActiveBuffer = ListUtil.tryHeadOnly _activeBufferStack

    member x.AutoLoadVimRc 
        with get () = _autoLoadVimRc
        and set value = _autoLoadVimRc <- value

    member x.IsVimRcLoaded = not (System.String.IsNullOrEmpty(_globalSettings.VimRc))

    member x.VariableMap = _variableMap

    member x.VimBuffers = _bufferMap.Values |> Seq.map fst |> List.ofSeq

    member x.FocusedBuffer = 
        match _vimHost.GetFocusedTextView() with
        | None -> 
            None
        | Some textView -> 
            let found, (buffer, _) = _bufferMap.TryGetValue(textView)
            if found then Some buffer
            else None

    /// Get the IVimLocalSettings which should be the basis for a newly created IVimTextBuffer
    member x.GetLocalSettingsForNewTextBuffer () =
        match x.ActiveBuffer with
        | Some buffer -> Some buffer.LocalSettings
        | None -> Some _vimRcLocalSettings

    /// Get the IVimWindowSettings which should be the basis for a newly created IVimBuffer
    member x.GetWindowSettingsForNewBuffer () =
        match x.ActiveBuffer with
        | Some buffer -> Some buffer.WindowSettings
        | None -> _vimRcWindowSettings

    /// Close all IVimBuffer instances
    member x.CloseAllVimBuffers() =
        x.VimBuffers
        |> List.iter (fun vimBuffer -> vimBuffer.Close())

    /// Create an IVimTextBuffer for the given ITextBuffer.  If an IVimLocalSettings instance is 
    /// provided then attempt to copy them into the created IVimTextBuffer copy of the 
    /// IVimLocalSettings
    member x.CreateVimTextBuffer (textBuffer : ITextBuffer) (localSettings : IVimLocalSettings option) = 
        if textBuffer.Properties.ContainsProperty _vimTextBufferKey then
            invalidArg "textBuffer" Resources.Vim_TextViewAlreadyHasVimBuffer

        let vimTextBuffer = _bufferFactoryService.CreateVimTextBuffer textBuffer x

        // Apply the specified local buffer settings
        match localSettings with
        | None -> 
            ()
        | Some localSettings ->
            localSettings.AllSettings
            |> Seq.filter (fun s -> not s.IsGlobal && not s.IsValueCalculated)
            |> Seq.iter (fun s -> vimTextBuffer.LocalSettings.TrySetValue s.Name s.Value |> ignore)

        // Put the IVimTextBuffer into the ITextBuffer property bag so we can query for it in the future
        textBuffer.Properties.[_vimTextBufferKey] <- vimTextBuffer

        vimTextBuffer

    /// Create an IVimBuffer for the given ITextView and associated IVimTextBuffer.  This 
    /// will not notify the IVimBufferCreationListener collection about the new
    /// IVimBuffer
    member x.CreateVimBufferCore textView (windowSettings : IVimWindowSettings option) =
        if _bufferMap.ContainsKey(textView) then 
            invalidArg "textView" Resources.Vim_TextViewAlreadyHasVimBuffer

        let vimTextBuffer = x.GetOrCreateVimTextBuffer textView.TextBuffer
        let vimBufferData = _bufferFactoryService.CreateVimBufferData vimTextBuffer textView
        let vimBuffer = _bufferFactoryService.CreateVimBuffer vimBufferData

        // Apply the specified window settings
        match windowSettings with
        | None -> 
            ()
        | Some windowSettings ->
            windowSettings.AllSettings
            |> Seq.filter (fun s -> not s.IsGlobal && not s.IsValueCalculated)
            |> Seq.iter (fun s -> vimBuffer.WindowSettings.TrySetValue s.Name s.Value |> ignore)

        // Setup the handlers for KeyInputStart and KeyInputEnd to accurately track the active
        // IVimBuffer instance
        let eventBag = DisposableBag()
        vimBuffer.KeyInputStart
        |> Observable.subscribe (fun _ -> _activeBufferStack <- vimBuffer :: _activeBufferStack )
        |> eventBag.Add

        vimBuffer.KeyInputEnd 
        |> Observable.subscribe (fun _ -> 
            _activeBufferStack <- 
                match _activeBufferStack with
                | h::t -> t
                | [] -> [] )
        |> eventBag.Add

        _bufferMap.Add(textView, (vimBuffer, eventBag))
        vimBuffer

    /// Create an IVimBuffer for the given ITextView and associated IVimTextBuffer and notify
    /// the IVimBufferCreationListener collection about it
    member x.CreateVimBuffer textView windowSettings = 

        // Automatically load VimRc here if we haven't yet and are set to do so automatically
        if x.AutoLoadVimRc && not x.IsVimRcLoaded then
            x.LoadVimRc() |> ignore

        let vimBuffer = x.CreateVimBufferCore textView windowSettings
        _bufferCreationListeners |> Seq.iter (fun x -> x.Value.VimBufferCreated vimBuffer)
        vimBuffer

    member x.GetVimTextBuffer (textBuffer : ITextBuffer) =
        PropertyCollectionUtil.GetValue<IVimTextBuffer> _vimTextBufferKey textBuffer.Properties

    member x.GetVimBuffer textView =
        let tuple = _bufferMap.TryGetValue textView
        match tuple with 
        | (true,(buffer,_)) -> Some buffer
        | (false,_) -> None

    member x.GetOrCreateVimTextBuffer textBuffer =
        match x.GetVimTextBuffer textBuffer with
        | Some vimTextBuffer ->
            vimTextBuffer
        | None ->
            let settings = x.GetLocalSettingsForNewTextBuffer()
            x.CreateVimTextBuffer textBuffer settings

    member x.GetOrCreateVimBuffer textView =
        match x.GetVimBuffer textView with
        | Some buffer -> 
            buffer
        | None -> 
            let settings = x.GetWindowSettingsForNewBuffer()
            x.CreateVimBuffer textView settings

    member x.LoadVimRc () =
        _isLoadingVimRc <- true
        try

            _globalSettings.VimRc <- System.String.Empty
            _globalSettings.VimRcPaths <- _fileSystem.GetVimRcDirectories() |> String.concat ";"
    
            match _fileSystem.LoadVimRcContents() with
            | None -> false
            | Some fileContents ->
                _globalSettings.VimRc <- fileContents.FilePath
                let textView = _vimHost.CreateHiddenTextView()
    
                try
                    // Call into the core version of create.  We don't want to notify any consumers
                    // about the IVimBuffer created to load the vimrc file.  It causes confusion if
                    // we do and really there's just no reason to as it's going to almost immediately
                    // go away
                    let vimBuffer = x.CreateVimBufferCore textView None
                    let mode = vimBuffer.CommandMode
                    fileContents.Lines |> Seq.iter (fun input -> mode.RunCommand input |> ignore)
                    _vimRcLocalSettings <- LocalSettings.Copy vimBuffer.LocalSettings
                    _vimRcWindowSettings <- Some (WindowSettings.Copy vimBuffer.WindowSettings)
                finally
                    // Be careful not to leak the ITextView in the case of an exception
                    textView.Close()
                true
        finally
            _isLoadingVimRc <- false

    member x.RemoveVimBuffer textView = 
        let found, tuple = _bufferMap.TryGetValue(textView)
        if found then 
            let _,bag = tuple
            bag.DisposeAll()
        _bufferMap.Remove textView

    interface IVim with
        member x.ActiveBuffer = x.ActiveBuffer
        member x.AutoLoadVimRc 
            with get() = x.AutoLoadVimRc
            and set value = x.AutoLoadVimRc <- value
        member x.FocusedBuffer = x.FocusedBuffer
        member x.VariableMap = x.VariableMap
        member x.VimBuffers = x.VimBuffers
        member x.VimData = _vimData
        member x.VimHost = _vimHost
        member x.VimRcLocalSettings
            with get() = _vimRcLocalSettings
            and set value = _vimRcLocalSettings <- LocalSettings.Copy value
        member x.MacroRecorder = _recorder :> IMacroRecorder
        member x.MarkMap = _markMap
        member x.KeyMap = _keyMap
        member x.SearchService = _search
        member x.IsVimRcLoaded = x.IsVimRcLoaded
        member x.InBulkOperation = _bulkOperations.InBulkOperation
        member x.RegisterMap = _registerMap 
        member x.GlobalSettings = _globalSettings
        member x.CloseAllVimBuffers() = x.CloseAllVimBuffers()
        member x.CreateVimBuffer textView = x.CreateVimBuffer textView (x.GetWindowSettingsForNewBuffer())
        member x.CreateVimTextBuffer textBuffer = x.CreateVimTextBuffer textBuffer (x.GetLocalSettingsForNewTextBuffer())
        member x.GetOrCreateVimBuffer textView = x.GetOrCreateVimBuffer textView
        member x.GetOrCreateVimTextBuffer textBuffer = x.GetOrCreateVimTextBuffer textBuffer
        member x.RemoveVimBuffer textView = x.RemoveVimBuffer textView
        member x.GetVimBuffer textView = x.GetVimBuffer textView
        member x.GetVimTextBuffer textBuffer = x.GetVimTextBuffer textBuffer
        member x.LoadVimRc() = x.LoadVimRc()


