#r "paket:
// Currently we use 'old' FAKE in the task because
// not all APIs (StrongNameKeyPair) are available in netcore
// And Mono.Cecil has removed support for Strong naming in netcore version
// Sadly we run into a size limitation when trying to bundle all binaries
// -> therefore we download stuff when running the task first time.
//nuget FAKE prerelease
//nuget Microsoft.Build.Utilities.Core
//nuget NuGet.CommandLine

nuget FSharp.Core
nuget Fake.Core.Process prerelease
nuget Fake.IO.FileSystem prerelease
nuget Fake.Core.Targets prerelease
nuget Fake.Core.Environment prerelease
//"
#load ".fake/build.fsx/intellisense.fsx"

open System
open System.Text
open System.Text.RegularExpressions
open System.IO
open System.Collections.Generic
open Fake.Core
open Fake.IO


open System.Net.Http
open System.Collections.Generic
type WebClient = HttpClient
type HttpClient with
    member x.DownloadFileTaskAsync (uri : Uri, filePath : string) =
      async {
        let! response = x.GetAsync(uri) |> Async.AwaitTask
        use fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None)
        do! response.Content.CopyToAsync(fileStream) |> Async.AwaitTask
        fileStream.Flush()
      } |> Async.StartAsTask
    member x.DownloadFileTaskAsync (uri : string, filePath : string) =
        x.DownloadFileTaskAsync(Uri uri, filePath)
    member x.DownloadFile (uri : string, filePath : string) =
        x.DownloadFileTaskAsync(uri, filePath).GetAwaiter().GetResult()
    member x.DownloadFile (uri : Uri, filePath : string) =
        x.DownloadFileTaskAsync(uri, filePath).GetAwaiter().GetResult()
    member x.DownloadStringTaskAsync (uri : Uri) =
      async {
        let! response = x.GetAsync(uri) |> Async.AwaitTask
        let! result = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return result
      } |> Async.StartAsTask
    member x.DownloadStringTaskAsync (uri : string) = x.DownloadStringTaskAsync(Uri uri)
    member x.DownloadString (uri : string) =
        x.DownloadStringTaskAsync(uri).GetAwaiter().GetResult()
    member x.DownloadString (uri : Uri) =
        x.DownloadStringTaskAsync(uri).GetAwaiter().GetResult()

    member x.DownloadDataTaskAsync(uri : Uri) =
      async {
        let! response = x.GetAsync(uri) |> Async.AwaitTask
        let! result = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        return result
      } |> Async.StartAsTask
    member x.DownloadDataTaskAsync (uri : string) = x.DownloadDataTaskAsync(Uri uri)
    member x.DownloadData(uri : string) =
        x.DownloadDataTaskAsync(uri).GetAwaiter().GetResult()
    member x.DownloadData(uri : Uri) =
        x.DownloadDataTaskAsync(uri).GetAwaiter().GetResult()

    member x.UploadFileAsMultipart (url : Uri) filename =
        let fileTemplate =
            "--{0}\r\nContent-Disposition: form-data; name=\"{1}\"; filename=\"{2}\"\r\nContent-Type: {3}\r\n\r\n"
        let boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture)
        let fileInfo = (new FileInfo(Path.GetFullPath(filename)))
        let fileHeaderBytes =
            System.String.Format
                (System.Globalization.CultureInfo.InvariantCulture, fileTemplate, boundary, "package", "package", "application/octet-stream")
            |> Encoding.UTF8.GetBytes
        let newlineBytes = Environment.NewLine |> Encoding.UTF8.GetBytes
        let trailerbytes = String.Format(System.Globalization.CultureInfo.InvariantCulture, "--{0}--", boundary) |> Encoding.UTF8.GetBytes
        x.DefaultRequestHeaders.Add("ContentType", "multipart/form-data; boundary=" + boundary)
        use stream = new MemoryStream() // x.OpenWrite(url, "PUT")
        stream.Write(fileHeaderBytes, 0, fileHeaderBytes.Length)
        use fileStream = File.OpenRead fileInfo.FullName
        fileStream.CopyTo(stream, (4 * 1024))
        stream.Write(newlineBytes, 0, newlineBytes.Length)
        stream.Write(trailerbytes, 0, trailerbytes.Length)
        stream.Write(newlineBytes, 0, newlineBytes.Length)
        stream.Position <- 0L
        x.PutAsync(url, new StreamContent(stream)).GetAwaiter().GetResult()

    member x.DownloadStringAndHeaders (url :string) =
      async {
        let! response = x.GetAsync(url) |> Async.AwaitTask
        let! result = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        return
            result,
            response.Headers :> seq<KeyValuePair<string, seq<string>>>
            |> Seq.map (fun kv -> kv.Key, kv.Value |> Seq.toList)
            |> Map.ofSeq
      } |> Async.RunSynchronously
let internal addAcceptHeader (client:HttpClient) (contentType:string) =
    for headerVal in contentType.Split([|','|], System.StringSplitOptions.RemoveEmptyEntries) do
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(headerVal))
let internal addHeader (client:HttpClient) (headerKey:string) (headerVal:string) =
    client.DefaultRequestHeaders.Add(headerKey, headerVal)

module Util =
    open System.Net

    let retryIfFails maxRetries f =
        let rec loop retriesRemaining =
            try
                f ()
            with _ when retriesRemaining > 0 ->
                loop (retriesRemaining - 1)
        loop maxRetries

    let (|RegexReplace|_|) =
        let cache = new Dictionary<string, Regex>()
        fun pattern (replacement: string) input ->
            let regex =
                match cache.TryGetValue(pattern) with
                | true, regex -> regex
                | false, _ ->
                    let regex = Regex pattern
                    cache.Add(pattern, regex)
                    regex
            let m = regex.Match(input)
            if m.Success
            then regex.Replace(input, replacement) |> Some
            else None

    let join pathParts =
        Path.Combine(Array.ofSeq pathParts)

    let run workingDir fileName args =
        printfn "CWD: %s" workingDir
        let fileName, args =
            if Environment.isUnix
            then fileName, args else "cmd", ("/C " + fileName + " " + args)
        let ok =
            Process.execProcess (fun info ->
                { info with
                    FileName = fileName
                    WorkingDirectory = workingDir
                    Arguments = args }) TimeSpan.MaxValue
        if not ok then failwith (sprintf "'%s> %s %s' task failed" workingDir fileName args)

    let start workingDir fileName args =
        let p = new System.Diagnostics.Process()
        p.StartInfo.FileName <- fileName
        p.StartInfo.WorkingDirectory <- workingDir
        p.StartInfo.Arguments <- args
        p.Start() |> ignore
        p

    let runAndReturn workingDir fileName args =
        printfn "CWD: %s" workingDir
        let fileName, args =
            if Environment.isUnix
            then fileName, args else "cmd", ("/C " + args)
        Process.ExecProcessAndReturnMessages (fun info ->
            { info with
                FileName = fileName
                WorkingDirectory = workingDir
                Arguments = args}) TimeSpan.MaxValue
        |> fun p -> p.Messages |> String.concat "\n"

    let downloadArtifact path (url: string) =
        async {
            let tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".zip")
            use client = new WebClient()
            do! client.DownloadFileTaskAsync(Uri url, tempFile) |> Async.AwaitTask
            Shell.mkdir path
            Shell.CleanDir path
            run path "unzip" (sprintf "-q %s" tempFile)
            File.Delete tempFile
        } |> Async.RunSynchronously

    let rmdir dir =
        if Environment.isUnix
        then Shell.rm_rf dir
        // Use this in Windows to prevent conflicts with paths too long
        else run "." "cmd" ("/C rmdir /s /q " + Path.GetFullPath dir)

    let visitFile (visitor: string->string) (fileName : string) =
        File.ReadAllLines(fileName)
        |> Array.map (visitor)
        |> fun lines -> File.WriteAllLines(fileName, lines)

    let replaceLines (replacer: string->Match->string option) (reg: Regex) (fileName: string) =
        fileName |> visitFile (fun line ->
            let m = reg.Match(line)
            if not m.Success
            then line
            else
                match replacer line m with
                | None -> line
                | Some newLine -> newLine)

    let normalizeVersion (version: string) =
        let i = version.IndexOf("-")
        if i > 0 then version.Substring(0, i) else version

    type ComparisonResult = Smaller | Same | Bigger

    let foldi f init (xs: 'T seq) =
        let mutable i = -1
        (init, xs) ||> Seq.fold (fun state x ->
            i <- i + 1
            f i state x)

    let compareVersions (expected: string) (actual: string) =
        if actual = "*" // Wildcard for custom fable-core builds
        then Same
        else
            let expected = expected.Split('.', '-')
            let actual = actual.Split('.', '-')
            (Same, expected) ||> foldi (fun i comp expectedPart ->
                match comp with
                | Bigger -> Bigger
                | Same when actual.Length <= i -> Smaller
                | Same ->
                    let actualPart = actual.[i]
                    match Int32.TryParse(expectedPart), Int32.TryParse(actualPart) with
                    // TODO: Don't allow bigger for major version?
                    | (true, expectedPart), (true, actualPart) ->
                        if actualPart > expectedPart
                        then Bigger
                        elif actualPart = expectedPart
                        then Same
                        else Smaller
                    | _ ->
                        if actualPart = expectedPart
                        then Same
                        else Smaller
                | Smaller -> Smaller)


module Npm =
    let script workingDir script args =
        sprintf "run %s -- %s" script (String.concat " " args)
        |> Util.run workingDir "npm"

    let install workingDir modules =
        let npmInstall () =
            sprintf "install %s" (String.concat " " modules)
            |> Util.run workingDir "npm"

        // On windows, retry npm install to avoid bug related to https://github.com/npm/npm/issues/9696
        Util.retryIfFails (if Environment.isWindows then 3 else 0) npmInstall

    let command workingDir command args =
        sprintf "%s %s" command (String.concat " " args)
        |> Util.run workingDir "npm"

    let commandAndReturn workingDir command args =
        sprintf "%s %s" command (String.concat " " args)
        |> Util.runAndReturn workingDir "npm"

    let getLatestVersion package tag =
        let package =
            match tag with
            | Some tag -> package + "@" + tag
            | None -> package
        commandAndReturn "." "show" [package; "version"]

    let updatePackageKeyValue f pkgDir keys =
        let pkgJson = Path.Combine(pkgDir, "package.json")
        let reg =
            String.concat "|" keys
            |> sprintf "\"(%s)\"\\s*:\\s*\"(.*?)\""
            |> Regex
        let lines =
            File.ReadAllLines pkgJson
            |> Array.map (fun line ->
                let m = reg.Match(line)
                if m.Success then
                    match f(m.Groups.[1].Value, m.Groups.[2].Value) with
                    | Some(k,v) -> reg.Replace(line, sprintf "\"%s\": \"%s\"" k v)
                    | None -> line
                else line)
        File.WriteAllLines(pkgJson, lines)

module Node =
    let run workingDir script args =
        let args = sprintf "%s %s" script (String.concat " " args)
        Util.run workingDir "node" args

open Fake.Core
open Fake.Core.TargetOperators
let dirs = ["CreateSignedPackages.dev"]

Target.Create "Clean" (fun _ -> ())

Target.Create "NpmInstall" (fun _ ->
    Npm.install "." []
    for dir in dirs do
        Npm.install dir []
)

//Target "PrepareBinaries" (fun _ ->
//    Directory.ensure "CreateSignedPackages.dev/bin/Fake"
//    Shell.cp_r ".fake/build.fsx/packages/FAKE/tools" "CreateSignedPackages.dev/bin/Fake"
//    Directory.ensure "CreateSignedPackages.dev/bin/Microsoft.Build.Utilities.Core"
//    Shell.cp_r ".fake/build.fsx/packages/Microsoft.Build.Utilities.Core/lib/net46" "CreateSignedPackages.dev/bin/Microsoft.Build.Utilities.Core"
//    Directory.ensure "CreateSignedPackages.dev/bin/NuGet"
//    Shell.cp_r ".fake/build.fsx/packages/NuGet.CommandLine/tools" "CreateSignedPackages.dev/bin/NuGet"
//)

Target.Create "Compile" (fun _ ->
    for dir in dirs do
        Npm.script dir "tsc" []
)

Target.Create "Bundle" (fun _ ->
    // Workaround for not having an "exclude" feature...
    Shell.CleanDir "CreateSignedPackages"
    Shell.cp_r "CreateSignedPackages.dev" "CreateSignedPackages"
    Shell.rm_rf "CreateSignedPackages/mypackages"
    Shell.rm_rf "CreateSignedPackages/packages"
    Shell.rm_rf "CreateSignedPackages/signedPackages"
    Shell.rm_rf "CreateSignedPackages/.fake"
    Npm.script "." "tfx" ["extension"; "create"; "--manifest-globs"; "vss-extension.json"]
)

Target.Create "Default" (fun _ -> ())

"Clean"
    ==> "NpmInstall"
    //==> "PrepareBinaries"
    ==> "Compile"
    ==> "Bundle"
    ==> "Default"

Target.RunOrDefault "Default"