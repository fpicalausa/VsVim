﻿namespace Vim

open EditorUtils
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Outlining
open Microsoft.VisualStudio.Utilities
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Collections.Generic

module GlobalSettingNames = 
    let BackspaceName = "backspace"
    let CaretOpacityName = "vsvimcaret"
    let ClipboardName = "clipboard"
    let HighlightSearchName = "hlsearch"
    let HistoryName = "history"
    let IgnoreCaseName = "ignorecase"
    let IncrementalSearchName = "incsearch"
    let JoinSpacesName = "joinspaces"
    let KeyModelName = "keymodel"
    let MagicName = "magic"
    let MaxMapCount =  "vsvim_maxmapcount"
    let MaxMapDepth =  "maxmapdepth"
    let MouseModelName = "mousemodel"
    let ParagraphsName = "paragraphs"
    let ScrollOffsetName = "scrolloff"
    let SectionsName = "sections"
    let SelectionName = "selection"
    let SelectModeName = "selectmode"
    let ShellName = "shell"
    let ShellFlagName = "shellcmdflag"
    let SmartCaseName = "smartcase"
    let StartOfLineName = "startofline"
    let TildeOpName = "tildeop"
    let TimeoutExName = "ttimeout"
    let TimeoutName = "timeout"
    let TimeoutLengthName = "timeoutlen"
    let TimeoutLengthExName = "ttimeoutlen"
    let UseEditorIndentName = "vsvim_useeditorindent"
    let UseEditorSettingsName = "vsvim_useeditorsettings"
    let VisualBellName = "visualbell"
    let VirtualEditName = "virtualedit"
    let VimRcName = "vimrc"
    let VimRcPathsName = "vimrcpaths"
    let WrapScanName = "wrapscan"

module LocalSettingNames =

    let AutoIndentName = "autoindent"
    let ExpandTabName = "expandtab"
    let NumberName = "number"
    let NumberFormatsName = "nrformats"
    let ShiftWidthName = "shiftwidth"
    let TabStopName = "tabstop"
    let QuoteEscapeName = "quoteescape"

module WindowSettingNames =

    let CursorLineName = "cursorline"
    let ScrollName = "scroll"

/// Types of number formats supported by CTRL-A CTRL-A
[<RequireQualifiedAccess>]
type NumberFormat =
    | Alpha
    | Decimal
    | Hex
    | Octal

/// The options which can be set in the 'clipboard' setting
type ClipboardOptions = 
    | None = 0
    | Unnamed = 0x1 
    | AutoSelect = 0x2
    | AutoSelectMl = 0x4

/// The options which can be set in the 'selectmode' setting
type SelectModeOptions =
    | None = 0
    | Mouse = 0x1
    | Keyboard = 0x2
    | Command = 0x4

/// The options which can be set in the 'keymodel' setting
type KeyModelOptions =
    | None = 0
    | StartSelection = 0x1
    | StopSelection = 0x2

[<RequireQualifiedAccess>]
type SelectionKind =
    | Inclusive
    | Exclusive

type SettingKind =
    | NumberKind
    | StringKind
    | ToggleKind

type SettingValue =
    | NumberValue of int
    | StringValue of string
    | ToggleValue of bool
    | CalculatedValue of (unit -> SettingValue)

    /// Get the AggregateValue of the SettingValue.  This will dig through any CalculatedValue
    /// instances and return the actual value
    member x.AggregateValue = 

        let rec digThrough value = 
            match value with 
            | CalculatedValue(func) -> digThrough (func())
            | _ -> value
        digThrough x

[<DebuggerDisplay("{Name}={Value}")>]
type Setting = {
    Name : string
    Abbreviation : string
    Kind : SettingKind
    DefaultValue : SettingValue
    Value : SettingValue
    IsGlobal : bool
} with 

    member x.AggregateValue = x.Value.AggregateValue

    /// Is the value calculated
    member x.IsValueCalculated =
        match x.Value with
        | CalculatedValue(_) -> true
        | _ -> false

    /// Is the setting value currently set to the default value
    member x.IsValueDefault = 
        match x.Value, x.DefaultValue with
        | CalculatedValue(_), CalculatedValue(_) -> true
        | NumberValue(left), NumberValue(right) -> left = right
        | StringValue(left), StringValue(right) -> left = right
        | ToggleValue(left), ToggleValue(right) -> left = right
        | _ -> false

type SettingEventArgs(_setting : Setting) =
    inherit System.EventArgs()

    member x.Setting = _setting


/// Represent the setting supported by the Vim implementation.  This class **IS** mutable
/// and the values will change.  Setting names are case sensitive but the exposed property
/// names tend to have more familiar camel case names
type IVimSettings =

    /// Returns a sequence of all of the settings and values
    abstract AllSettings : Setting seq

    /// Try and set a setting to the passed in value.  This can fail if the value does not 
    /// have the correct type.  The provided name can be the full name or abbreviation
    abstract TrySetValue : settingName : string -> value : SettingValue -> bool

    /// Try and set a setting to the passed in value which originates in string form.  This 
    /// will fail if the setting is not found or the value cannot be converted to the appropriate
    /// value
    abstract TrySetValueFromString : settingName : string -> strValue : string -> bool

    /// Get the value for the named setting.  The name can be the full setting name or an 
    /// abbreviation
    abstract GetSetting : settingName : string -> Setting option

    /// Raised when a Setting changes
    [<CLIEvent>]
    abstract SettingChanged : IDelegateEvent<System.EventHandler<SettingEventArgs>>

and IVimGlobalSettings = 

    /// The multi-value option for determining backspace behavior.  Valid values include 
    /// indent, eol, start.  Usually accessed through the IsBackSpace helpers
    abstract Backspace : string with get, set

    /// Opacity of the caret.  This must be an integer between values 0 and 100 which
    /// will be converted into a double for the opacity of the caret
    abstract CaretOpacity : int with get, set

    /// The clipboard option.  Use the IsClipboard helpers for finding out if specific options 
    /// are set
    abstract Clipboard : string with get, set

    /// The parsed set of clipboard options
    abstract ClipboardOptions : ClipboardOptions with get, set

    /// Whether or not to highlight previous search patterns matching cases
    abstract HighlightSearch : bool with get,set

    /// The number of items to keep in the history lists
    abstract History : int with get, set

    /// Whether or not the magic option is set
    abstract Magic : bool with get,set

    /// Maximum number of maps which can occur for a key map.  This is not a standard vim or gVim
    /// setting.  It's a hueristic setting meant to prevent infinite recursion in the specific cases
    /// that maxmapdepth can't or won't catch (see :help maxmapdepth).  
    abstract MaxMapCount : int with get, set

    /// Maximum number of recursive depths which occur for a mapping
    abstract MaxMapDepth : int with get, set

    /// Whether or not we should be ignoring case in the ITextBuffer
    abstract IgnoreCase : bool with get, set

    /// Whether or not incremental searches should be highlighted and focused 
    /// in the ITextBuffer
    abstract IncrementalSearch : bool with get, set

    /// Is the 'indent' option inside of Backspace set
    abstract IsBackspaceIndent : bool with get

    /// Is the 'eol' option inside of Backspace set
    abstract IsBackspaceEol : bool with get

    /// Is the 'start' option inside of Backspace set
    abstract IsBackspaceStart : bool with get

    /// Is the 'onemore' option inside of VirtualEdit set
    abstract IsVirtualEditOneMore : bool with get

    /// Is the Selection setting set to a value which calls for inclusive 
    /// selection.  This does not directly track if Setting = "inclusive" 
    /// although that would cause this value to be true
    abstract IsSelectionInclusive : bool with get

    /// Is the Selection setting set to a value which permits the selection
    /// to extend past the line
    abstract IsSelectionPastLine : bool with get

    /// Whether or not to insert two spaces after certain constructs in a 
    /// join operation
    abstract JoinSpaces : bool with get, set

    /// The 'keymodel' setting
    abstract KeyModel : string with get, set

    /// The 'keymodel' in a type safe form
    abstract KeyModelOptions : KeyModelOptions with get, set

    /// The 'mousemodel' setting
    abstract MouseModel : string with get, set

    /// The nrooff macros that separate paragraphs
    abstract Paragraphs : string with get, set

    /// The nrooff macros that separate sections
    abstract Sections : string with get, set

    /// The name of the shell to use for shell commands
    abstract Shell : string with get, set

    /// The flag which is passed to the shell when executing shell commands
    abstract ShellFlag : string with get, set

    abstract StartOfLine : bool with get, set

    /// Controls the behavior of ~ in normal mode
    abstract TildeOp : bool with get,set

    /// Part of the control for key mapping and code timeout
    abstract Timeout : bool with get, set

    /// Part of the control for key mapping and code timeout
    abstract TimeoutEx : bool with get, set

    /// Timeout for a key mapping in milliseconds
    abstract TimeoutLength : int with get, set

    /// Timeout control for key mapping / code
    abstract TimeoutLengthEx : int with get, set

    /// Holds the scroll offset value which is the number of lines to keep visible
    /// above the cursor after a move operation
    abstract ScrollOffset : int with get, set

    /// Holds the Selection option
    abstract Selection : string with get, set

    /// Get the SelectionKind for the current settings
    abstract SelectionKind : SelectionKind

    /// Options for how select mode is entered
    abstract SelectMode : string with get, set 

    /// The options which are set via select mode
    abstract SelectModeOptions : SelectModeOptions with get, set

    /// Overrides the IgnoreCase setting in certain cases if the pattern contains
    /// any upper case letters
    abstract SmartCase : bool with get,set

    /// Let the editor control indentation of lines instead.  Overrides the AutoIndent
    /// setting
    abstract UseEditorIndent : bool with get, set

    /// Use the editor tab setting over the ExpandTab one
    abstract UseEditorSettings : bool with get, set

    /// Retrieves the location of the loaded VimRC file.  Will be the empty string if the load 
    /// did not succeed or has not been tried
    abstract VimRc : string with get, set

    /// Set of paths considered when looking for a .vimrc file.  Will be the empty string if the 
    /// load has not been attempted yet
    abstract VimRcPaths : string with get, set

    /// Holds the VirtualEdit string.  
    abstract VirtualEdit : string with get,set

    /// Whether or not to use a visual indicator of errors instead of a beep
    abstract VisualBell : bool with get,set

    /// Whether or not searches should wrap at the end of the file
    abstract WrapScan : bool with get, set

    /// The key binding which will cause all IVimBuffer instances to enter disabled mode
    abstract DisableAllCommand: KeyInput;

    inherit IVimSettings

/// Settings class which is local to a given IVimBuffer.  This will hide the work of merging
/// global settings with non-global ones
and IVimLocalSettings =

    abstract AutoIndent : bool with get, set

    /// Whether or not to expand tabs into spaces
    abstract ExpandTab : bool with get, set

    /// Return the handle to the global IVimSettings instance
    abstract GlobalSettings : IVimGlobalSettings

    /// Whether or not to put the numbers on the left column of the display
    abstract Number : bool with get, set

    /// Fromats that vim considers a number for CTRL-A and CTRL-X
    abstract NumberFormats : string with get, set

    /// The number of spaces a << or >> command will shift by 
    abstract ShiftWidth : int with get, set

    /// How many spaces a tab counts for 
    abstract TabStop : int with get, set

    /// Which characters escape quotes for certain motion types
    abstract QuoteEscape : string with get, set

    /// Is the provided NumberFormat supported by the current options
    abstract IsNumberFormatSupported : NumberFormat -> bool

    inherit IVimSettings

/// Settings which are local to a given window.
and IVimWindowSettings = 

    /// Whether or not to highlight the line the cursor is on
    abstract CursorLine : bool with get, set

    /// Return the handle to the global IVimSettings instance
    abstract GlobalSettings : IVimGlobalSettings

    /// The scroll size 
    abstract Scroll : int with get, set

    inherit IVimSettings