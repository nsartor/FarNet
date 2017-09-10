
// FarNet module FSharpFar
// Copyright (c) Roman Kuzmin

module FSharpFar.Config
open System
open System.IO

/// Configuration data for checkers and sessions.
type Config = {
    FscArgs: string []
    FsiArgs: string []
    OutArgs: string []
    LoadFiles: string []
    UseFiles: string []
}

/// Empty configuration.
let empty = {FscArgs = [||]; FsiArgs = [||]; OutArgs = [||]; LoadFiles = [||]; UseFiles = [||]}

type private ConfigSection =
    | NoSection
    | FscSection
    | FsiSection
    | OutSection

type private ConfigLine =
    | Empty
    | Comment
    | Section of string
    | Switch of string
    | Value of string
    | Pair of Key : string * Value : string

let private parse (line: string) =
    let text = line.Trim ()
    if text.Length = 0 then
        Empty
    elif text.[0] = ';' then
        Comment
    elif text.[0] = '[' then
        if not (text.EndsWith "]") then
            invalidOp "Invalid section, expected '[...]'."
        Section (text.Substring(1, text.Length - 2).Trim ())
    elif text.[0] <> '-' then
        Value text
    else
        let i = text.IndexOf ':'
        if i < 0 then
            Switch text
        else
            Pair (text.Substring(0, i).Trim (), text.Substring(i + 1).Trim ())

let private resolve root key value =
    let value = Environment.ExpandEnvironmentVariables(value).Replace ("__SOURCE_DIRECTORY__", root)
    match key with
    | "-r" | "--reference" ->
        if value.[0] = '.' then
            Path.GetFullPath (Path.Combine(root, value))
        else
            value
    | "" | "-l" | "--lib" | "-o" | "--out" | "--use" | "--doc" ->
        if Path.IsPathRooted value then
            Path.GetFullPath value
        else
            Path.GetFullPath (Path.Combine(root, value))
    | _ ->
        value

let readConfigFromFile path =
    let lines = File.ReadAllLines path
    let root = Path.GetDirectoryName path

    let fscArgs = ResizeArray ()
    let fsiArgs = ResizeArray ()
    let outArgs = ResizeArray ()
    let loadScripts = ResizeArray ()
    let useScripts = ResizeArray ()

    let mutable currentSection = NoSection
    let mutable lineNo = 0

    let raiseSection () = invalidOp "Expected section [fsc]|[fsi]|[out], found data or unknown section."
    let raiseUnexpected () = invalidOp "Unexpected value."
    try
        for line in lines do
            lineNo <- lineNo + 1
            match parse line with
            | Empty | Comment ->
                ()
            | Section section ->
                currentSection <-
                    match section with
                    | "fsc" -> FscSection
                    | "fsi" -> FsiSection
                    | "out" -> OutSection
                    | _ -> raiseSection ()
            | Switch it ->
                match currentSection with
                | FscSection ->
                    fscArgs.Add it
                | FsiSection ->
                    fsiArgs.Add it
                | OutSection ->
                    outArgs.Add it
                | NoSection ->
                    raiseSection ()
            | Value it ->
                match currentSection with
                | FsiSection ->
                    loadScripts.Add (resolve root "" it)
                | FscSection
                | OutSection ->
                    raiseUnexpected ()
                | NoSection ->
                    raiseSection ()
            | Pair (key, value) ->
                let text = resolve root key value
                match currentSection with
                | FscSection ->
                    // use -r instead of --reference to avoid duplicates added by FCS
                    // https://github.com/fsharp/FSharp.Compiler.Service/issues/697
                    let key = if key = "--reference" then "-r" else key
                    fscArgs.Add (key + ":" + text)
                | FsiSection ->
                    if key = "--use" then
                        useScripts.Add text
                    else
                        fsiArgs.Add (key + ":" + text)
                | OutSection ->
                    outArgs.Add (key + ":" + text)
                | NoSection ->
                    raiseSection ()
     with e ->
        invalidOp (sprintf "%s(%d): %s" path lineNo e.Message)

    {
        FscArgs = fscArgs.ToArray ()
        FsiArgs = fsiArgs.ToArray ()
        OutArgs = outArgs.ToArray ()
        LoadFiles = loadScripts.ToArray ()
        UseFiles = useScripts.ToArray ()
    }

/// Gets and caches the config from a file.
let getConfigFromFileCached =
    let cache = System.Collections.Concurrent.ConcurrentDictionary<string, DateTime * Config> StringComparer.OrdinalIgnoreCase
    fun path ->
        let time1 = File.GetLastWriteTime path
        let add path = time1, readConfigFromFile path
        let update path ((time2, _) as value) = if time1 = time2 then value else add path
        let _, config = cache.AddOrUpdate (path, add, update)
        config

/// Gets some config path in a directory.
let tryConfigPathInDirectory dir =
    match Directory.GetFiles (dir, "*.fs.ini") with
    | [|file|] ->
        Some file
    | _ ->
        None

/// Gets the local or main config path for a file.
let getConfigPathForFile path =
    let dir = Path.GetDirectoryName path
    match tryConfigPathInDirectory dir with
    // local config
    | Some file ->
        file
    // main config
    | _ ->
        farMainConfigPath

/// Gets the local or main config for a file.
let getConfigForFile path =
    getConfigFromFileCached (getConfigPathForFile path)
