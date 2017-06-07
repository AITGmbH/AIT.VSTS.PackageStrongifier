(* -- Fake Dependencies paket-inline
source https://nuget.org/api/v2

nuget FSharp.Core
nuget Mono.Cecil prerelease
nuget Paket.Core prerelease
nuget System.Security.Cryptography.Algorithms

-- Fake Dependencies -- *)
//#load ".fake/processPackages.fsx/loadDependencies.fsx"
#I "packages/FAKE/tools"
#r "FakeLib.dll"
#r "Mono.Cecil.dll"
#r "Chessie.dll"
#r "Paket.Core.dll"
// https://www.nuget.org/packages/Microsoft.Build.Utilities.Core/15.1.1012
#r "packages/Microsoft.Build.Utilities.Core/lib/net46/Microsoft.Build.Utilities.Core.dll"
#r "System.Xml"
#r "System.Xml.Linq"

open Mono.Cecil
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.IO
open System
open System.Reflection
open System.Collections.Generic

// port of https://github.com/brutaldev/StrongNameSigner/blob/master/src/Brutal.Dev.StrongNameSigner/SigningHelper.cs
module SigningHelper =
    open System.Runtime.CompilerServices
    
    type AssemblyInfo =
        { //FilePath : string
          IsSigned : bool
          IsManagedAssembly : bool
          Is64BitOnly : bool
          Is32BitOnly : bool
          Is32BitPreferred : bool }
        member x.IsAnyCpu =
            x.IsManagedAssembly && not x.Is32BitOnly && not x.Is64BitOnly

    type AssemblyModifier = AssemblyDefinition -> unit

    // see https://stackoverflow.com/questions/29768562/obtain-net-publickeytoken-from-snk-file
    let getTokenFromPublicKey (publicKey:byte[]) =
        use csp = new SHA1CryptoServiceProvider()
        let hash = csp.ComputeHash(publicKey)

        let token = Array.zeroCreate 8;
        for i in 0 .. 7 do
            token.[i] <- hash.[hash.Length - i - 1]
        token

    let getPublicTokenFromStrongName (sn:StrongNameKeyPair) =
        getTokenFromPublicKey sn.PublicKey

    let getStrongNameKeyPair keyPath keyFilePassword =
        match String.IsNullOrEmpty keyPath, String.IsNullOrEmpty keyFilePassword with
        | false, false ->
            let cert = new X509Certificate2(keyPath, keyFilePassword, X509KeyStorageFlags.Exportable)
            let provider = cert.PrivateKey :?> RSACryptoServiceProvider
            if isNull provider then
                raise <| InvalidOperationException("The key file is not password protected or the incorrect password was provided.")
            
            StrongNameKeyPair(provider.ExportCspBlob(true))
        | false, true ->
            StrongNameKeyPair(File.ReadAllBytes(keyPath))
        | _ ->
            failwithf "please use a valid keyPath"

    let getAssemblyResolver searchpaths =
        let resolve name =
            let n = AssemblyName(name)
            match searchpaths
                    |> Seq.collect (fun p -> Directory.GetFiles(p, "*.dll"))
                    |> Seq.tryFind (fun f -> f.ToLowerInvariant().Contains(n.Name.ToLowerInvariant())) with
            | Some f -> f
            | None ->
                failwithf "Could not resolve '%s'" name
        let known = System.Collections.Concurrent.ConcurrentDictionary<string, Mono.Cecil.AssemblyDefinition>()
        let readAssemblyE (name:string) (parms: Mono.Cecil.ReaderParameters) =
            known.GetOrAdd(
                name, 
                (fun _ ->
                    Mono.Cecil.AssemblyDefinition.ReadAssembly(
                        resolve name,
                        parms)))
            
            
        let readAssembly (name:string) (x:Mono.Cecil.IAssemblyResolver) =
            readAssemblyE name (Mono.Cecil.ReaderParameters(AssemblyResolver = x))
        { new Mono.Cecil.IAssemblyResolver with
            member x.Dispose () = ()
            member x.Resolve (name : Mono.Cecil.AssemblyNameReference) = readAssembly name.FullName x
            member x.Resolve (name : Mono.Cecil.AssemblyNameReference, parms : Mono.Cecil.ReaderParameters) = readAssemblyE name.FullName parms
            }

    let getReadParameters assemblyPath probingPaths =
        let searchPaths =
            [
                if (not (String.IsNullOrEmpty(assemblyPath)) && File.Exists(assemblyPath)) then
                    yield (Path.GetDirectoryName(assemblyPath));
                
                if not (isNull probingPaths) then
                    for searchDir in probingPaths do
                        if (Directory.Exists(searchDir)) then
                            yield (searchDir)
            ]
        
        let readerParams = ReaderParameters()
        readerParams.AssemblyResolver <- getAssemblyResolver searchPaths
        readerParams.ReadWrite <- true
        readerParams.ReadSymbols <- File.Exists(Path.ChangeExtension(assemblyPath, ".pdb"))
        readerParams

    let getAssemblyInfo (assemblyDef:AssemblyDefinition) =
        {
            IsSigned = assemblyDef.MainModule.Attributes.HasFlag(ModuleAttributes.StrongNameSigned)
            IsManagedAssembly = assemblyDef.MainModule.Attributes.HasFlag(ModuleAttributes.ILOnly)
            Is64BitOnly = assemblyDef.MainModule.Architecture = TargetArchitecture.AMD64 || assemblyDef.MainModule.Architecture = TargetArchitecture.IA64
            Is32BitOnly = assemblyDef.MainModule.Attributes.HasFlag(ModuleAttributes.Required32Bit) && not (assemblyDef.MainModule.Attributes.HasFlag(ModuleAttributes.Preferred32Bit))
            Is32BitPreferred = assemblyDef.MainModule.Attributes.HasFlag(ModuleAttributes.Preferred32Bit)
        }

    let fixAssemblyReferences (sn:StrongNameKeyPair) (assemblyDef:AssemblyDefinition) =
        assemblyDef.Modules
        |> Seq.iter (fun modul ->
            modul.AssemblyReferences
            |> Seq.iter (fun ref ->
                if isNull ref.PublicKeyToken || ref.PublicKeyToken.Length = 0 then
                    printfn "    - Reference '%s' '%O' added token" ref.Name ref.Version
                    ref.PublicKeyToken <- getPublicTokenFromStrongName sn))
        assemblyDef.CustomAttributes
        |> Seq.filter (fun attr -> attr.AttributeType.FullName = typeof<InternalsVisibleToAttribute>.FullName)
        |> Seq.choose (fun attr ->
            let curArg = attr.ConstructorArguments.[0]
            let argument = curArg.Value :?> string
            if argument.Contains "PublicKey=" then
                None
            else Some attr.ConstructorArguments)
        |> Seq.iter (fun args ->
            let typ = args.[0].Type
            let prevArgs = args.[0].Value :?> string
            let newArg = prevArgs + ", PublicKey=" + BitConverter.ToString(sn.PublicKey).Replace("-", String.Empty)
            printfn "    - InternalsVisibleTo '%s' -> '%s'" prevArgs newArg
            args.Clear()
            args.Add(CustomAttributeArgument(typ, newArg)))


    let signAssemblyInPlace assemblyPath (sn:StrongNameKeyPair) probingPaths =
        let writeSymbols = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb"))
        use assemblyDev = AssemblyDefinition.ReadAssembly(assemblyPath, getReadParameters assemblyPath probingPaths)
        let info = getAssemblyInfo assemblyDev
        if info.IsSigned then
            false
        else
            printfn "  - rewriting '%s'" assemblyPath
            fixAssemblyReferences sn assemblyDev
            let writerParams = WriterParameters(StrongNameKeyPair = sn, WriteSymbols = writeSymbols)
            assemblyDev.Write(writerParams)
            true

module ProcessPackages =
    open Paket
    open Paket.PackageResolver
    open Paket.PackageResolver.Resolution

    let resolveDependencies (dependenciesFile : DependenciesFile) =
        let semVerUpdateMode = SemVerUpdateMode.NoRestriction
        let force = false
        let lockFileName = DependenciesFile.FindLockfile dependenciesFile.FileName
        let oldLockFile,updateMode =
            LockFile.Parse(lockFileName.FullName, [||]),UpdateAll

        let getSha1 origin owner repo branch auth = RemoteDownload.getSHA1OfBranch origin owner repo branch auth |> Async.RunSynchronously
        let root = Path.GetDirectoryName dependenciesFile.FileName
        let inline getVersionsF sources groupName packageName = async {
            let! result = NuGetV2.GetVersions force None root (sources, packageName) 
            return result |> List.toSeq }
            
        dependenciesFile.Groups
        |> Map.iter (fun groupName group ->
            match group.Options.Settings.FrameworkRestrictions with
            | Requirements.FrameworkRestrictions.AutoDetectFramework ->
                failwithf "AutoDetectFramework is not supported"
            | x -> ())
        
        Paket.UpdateProcess.selectiveUpdate
            force 
            getSha1
            getVersionsF
            (NuGetV2.GetPackageDetails None root force)
            (RuntimeGraph.getRuntimeGraphFromNugetCache root)
            oldLockFile 
            dependenciesFile 
            updateMode
            semVerUpdateMode

    let getAllDependencies (depsContent:string) =
        // We don't need this feature currently, lets just assume all runtime dependencies are signed already.
        Environment.SetEnvironmentVariable("PAKET_DISABLE_RUNTIME_RESOLUTION", "true")
        let lines = depsContent.Split([|"\r\n"; "\n"|], StringSplitOptions.None)
        let f, groups, lines = Paket.DependenciesFileParser.parseDependenciesFile "paket.dependencies" false lines
        let dependencies = Paket.DependenciesFile.FromSource("signhelper.dependencies", depsContent)
        let filterByPackage groupName p =
            let filteredGroups =
                dependencies.Groups
                |> Map.filter (fun k v -> k = groupName)
                |> Map.map (fun k v -> { v with Packages = v.Packages |> List.filter (fun pi -> pi = p)})

            DependenciesFile(dependencies.FileName + "_" + groupName.Name + "_" + p.Name.Name, filteredGroups, [||])

        let resolverWork =
            [
                let groups = dependencies.Groups |> Seq.map (fun kv -> kv.Key, kv.Value.Packages)
                yield!
                    groups 
                    |> Seq.collect (fun (groupName, packageList) -> packageList |> Seq.map (fun p -> groupName, p))
                    |> Seq.distinctBy snd
                    |> Seq.map (fun (groupName, p) -> filterByPackage groupName p)
                yield dependencies
            ]

        resolverWork
        |> Seq.map (fun depsFile -> 
            let lockFile, _ = resolveDependencies depsFile
            DependencyCache(depsFile, lockFile))
        |> Seq.toList
    
    let extractPackageUserFolder groupName package =
        let root = Environment.CurrentDirectory
        // 1. downloading packages into cache
        let targetFileName, _ =
            NuGetV2.DownloadPackage (None, root, package.Source, [], groupName, package.Name, package.Version, false, false, false)
            |> Async.RunSynchronously

        NuGetV2.ExtractPackageToUserFolder (targetFileName, package.Name, package.Version, null) |> Async.RunSynchronously
    
    type HandledPackageInfo =
        | SignedPackage
        | AlreadySignedPackage
    
    type ProcessingInfo = {
        ConvertResult : HandledPackageInfo
        FinalDirectory : string
        ProbingPaths : string list
        Cache : DependencyCache
        Package : ResolvedPackage }
    let rec copyDirectory (source:DirectoryInfo) (target:DirectoryInfo) =
        Directory.CreateDirectory target.FullName |> ignore
        for fi in source.GetFiles() do
            fi.CopyTo(Path.Combine(target.FullName, fi.Name), true)
            |> ignore
        for di in source.GetDirectories() do
            let next = target.CreateSubdirectory(di.Name)
            copyDirectory di next

    open Microsoft.Build.Utilities
    let private getFrameworkReferences folderName =
        let findVersionForProfile fws =
            let netFramework = fws |> Seq.tryPick (function FrameworkIdentifier.DotNetFramework v -> Some v | _ -> None)
            match netFramework with
            | Some FrameworkVersion.V4 ->
                Version(4,0) |> Some
            | Some FrameworkVersion.V4_5 ->
                Version(4,5) |> Some
            | Some FrameworkVersion.V4_6 ->
                Version(4,6) |> Some
            | Some FrameworkVersion.V5_0 ->
                Version(5,0) |> Some
            | _ -> 
                eprintfn "unknown portable profile '%s'" folderName
                None
        let getRefs fwm v profile =
            let name =
                match profile with
                | None -> Runtime.Versioning.FrameworkName(fwm, v)
                | Some prof -> Runtime.Versioning.FrameworkName(fwm, v, prof)
            ToolLocationHelper.GetPathToReferenceAssemblies(name)
            |> Seq.toList
            |> Some
        let references =
            match PlatformMatching.extractPlatforms folderName |> Option.bind (fun t -> t.ToTargetProfile) with
            | None when folderName = "lib" ->
                getRefs ".NETFramework" (Version(3,5)) (Some "Client")
            | Some (SinglePlatform (FrameworkIdentifier.DotNetStandard _))
            | Some (SinglePlatform (FrameworkIdentifier.DotNetCore _)) -> 
                //printfn "Not signing netstandard stuff."
                Some []
            | Some (SinglePlatform (FrameworkIdentifier.DotNetFramework FrameworkVersion.V3))
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V3_5)) ->
                getRefs ".NETFramework" (Version(3,5)) (Some "Client")
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4)) ->
                getRefs ".NETFramework" (Version(4,0)) (Some "Client")
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4_5)) ->
                getRefs ".NETFramework" (Version(4,5)) None
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4_6)) ->
                getRefs ".NETFramework" (Version(4,6)) None
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4_6_1)) ->
                getRefs ".NETFramework" (Version(4,6,1)) None
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4_6_2)) ->
                getRefs ".NETFramework" (Version(4,6,2)) None
            | Some (SinglePlatform(FrameworkIdentifier.DotNetFramework FrameworkVersion.V4_6_3)) ->
                getRefs ".NETFramework" (Version(4,6,3)) None
            | Some (SinglePlatform(FrameworkIdentifier.MonoAndroid)) ->
                getRefs "MonoAndroid" (Version(1,0)) None
            | Some (SinglePlatform(FrameworkIdentifier.MonoTouch)) ->
                getRefs "MonoTouch" (Version(1,0)) None
            | Some (SinglePlatform(FrameworkIdentifier.XamariniOS)) ->
                getRefs "Xamarin.iOS" (Version(1,0)) None
            | Some (SinglePlatform(FrameworkIdentifier.XamarinMac)) ->
                getRefs "Xamarin.Mac" (Version(2,0)) None
            | Some (PortableProfile(PortableProfileType.UnsupportedProfile fws)) ->
                match findVersionForProfile fws with
                | Some v -> getRefs ".NETPortable" v None
                | None -> Some []
            | Some (PortableProfile(profile) as p) ->
                match findVersionForProfile p.Frameworks with
                | Some v -> getRefs ".NETPortable" v (Some profile.ProfileName)
                | None -> Some []
            | Some (SinglePlatform(FrameworkIdentifier.WindowsPhone WindowsPhoneVersion.V8)) ->
                getRefs "WindowsPhone" (Version(8,0)) None
            | Some (SinglePlatform(FrameworkIdentifier.WindowsPhone WindowsPhoneVersion.V8_1)) ->
                getRefs "WindowsPhone" (Version(8,1)) None
            | Some (SinglePlatform(FrameworkIdentifier.WindowsPhoneApp WindowsPhoneAppVersion.V8_1)) ->
                getRefs "WindowsPhoneApp" (Version(8,1)) None
            | Some fw ->
                eprintfn "Unsuported framework %A" fw
                Some []
            | None ->
                eprintfn "Could not detect framework from %s" folderName
                Some []
        references 
        |> Option.map (fun references ->
            references
            |> Seq.map (Path.GetDirectoryName)
            |> Seq.distinct
            |> Seq.toList)

    let signPackageIfNeeded (sn:StrongNameKeyPair) (handled:Map<_,ProcessingInfo>) (cache:DependencyCache) signedPackageDir groupName package =
        let dir = DirectoryInfo (extractPackageUserFolder groupName package)
        // GetFullPath works around 
        // System.ArgumentException: The directory specified, 'lib', is not a subdirectory of 'mypackages\WPFToolkit-3.5.50211.1'.
        let target = DirectoryInfo (Path.GetFullPath signedPackageDir)
        CleanDir signedPackageDir
        copyDirectory dir target
        
        let probingPaths =
            package.Dependencies
            |> Seq.map (fun (packName, verReq, restrictions) -> cache.LockFile.Groups.[groupName].Resolution.[packName])
            |> Seq.choose (fun package -> handled.TryFind (package.Name, package.Version))
            |> Seq.collect (fun proc -> proc.ProbingPaths)
            |> Seq.distinct
            |> Seq.toList
        
        let depsSigned =
            package.Dependencies
            |> Seq.map (fun (packName, verReq, restrictions) -> cache.LockFile.Groups.[groupName].Resolution.[packName])
            |> Seq.choose (fun package -> handled.TryFind (package.Name, package.Version))
            |> Seq.exists (fun proc -> match proc.ConvertResult with SignedPackage -> true | _ -> false)

        let results =
            target.GetFileSystemInfos("*.dll", SearchOption.AllDirectories)
            |> Seq.toList
            |> List.map (fun assembly ->
                try
                    let frameworkRef = getFrameworkReferences (Path.GetFileName (Path.GetDirectoryName assembly.FullName))
                    match frameworkRef with
                    | Some fw ->
                        // Note: We prefer the framework path here, because otherwise we run into problems with netstandard packages
                        // We probably want a more fine grained solution eventually.
                        // But maybe it's not as bad as we only really need the "API" and don't care about the implementation of references.
                        SigningHelper.signAssemblyInPlace assembly.FullName sn (fw @ probingPaths)
                    | None -> false
                with e ->
                    eprintfn "Error while signing assembly %s: \n%O" assembly.FullName e
                    false)
        let didWork = results |> Seq.exists id
        if depsSigned && not didWork then
            eprintfn "Detected that a dependency was signed but no assembly in this package '%s':'%s', remove this warning if you encountered a valid case for this."
                package.Name.Name package.Version.AsString
        let wasSigned = didWork || depsSigned
        let findDirs (dir:DirectoryInfo) =
            let newDirs =
                dir.GetFileSystemInfos("*.dll", SearchOption.AllDirectories)
                |> Seq.toList
                |> List.map (fun dll -> Path.GetDirectoryName dll.FullName)
            newDirs @ probingPaths
            |> List.distinct
        if wasSigned then
            printfn "  was already signed"
            { ConvertResult = SignedPackage
              FinalDirectory = target.FullName
              ProbingPaths = findDirs target
              Cache = cache
              Package = package }
        else
            printfn "  successfully signed"
            { ConvertResult = AlreadySignedPackage
              FinalDirectory = dir.FullName
              ProbingPaths = findDirs dir
              Cache = cache
              Package = package }

        
    let signPackages (sn:StrongNameKeyPair) signedPackagesDir (resolved:DependencyCache list) =
        let mutable handledPackages = Map.empty
        for cache in resolved do
            for kv in cache.OrderedGroups() do
                let groupName = kv.Key
                let group = kv.Value
                for package in group do
                    let key = package.Name,package.Version
                    match handledPackages.TryFind key with
                    | Some handled -> 
                        printfn "Package (handled already): %s - %s" package.Name.Name package.Version.AsString
                    | _ ->
                        printfn "- Package: %s - %s" package.Name.Name package.Version.AsString
                        let signedPackageDir = Path.Combine(signedPackagesDir, package.Name.Name + "-" + package.Version.AsString)
                        let procInfo = signPackageIfNeeded sn handledPackages cache signedPackageDir groupName package
                        handledPackages <- handledPackages.Add(key, procInfo)
        handledPackages

    let cleanPackageDirectory (proc:ProcessingInfo) =
        match proc.ConvertResult with
        | AlreadySignedPackage -> ()
        | SignedPackage ->
            let dir = proc.FinalDirectory
            // maybe have white instead of blacklist?
            let toRemove =
                [ "_rels"; "[Content_Types].xml"; "package"
                  sprintf "%s.%s.nupkg" proc.Package.Name.Name proc.Package.Version.AsString
                  sprintf "%s.%s.nupkg.sha512" proc.Package.Name.Name proc.Package.Version.AsString ]
            for r in toRemove do
                let fileOrDir = Path.Combine(dir, r)
                if Directory.Exists(fileOrDir) then
                    Directory.Delete(fileOrDir, true)
                elif File.Exists(fileOrDir) then
                    File.Delete(fileOrDir)
                else
                    printfn "Expected file or directory '%s' for deletion but they didn't exist" fileOrDir

    open System.Xml.Linq
    let convertNuspec signedPackagePostfix (handled:Map<(Paket.Domain.PackageName * Paket.SemVerInfo),ProcessingInfo>) (proc:ProcessingInfo) =
        // See https://docs.microsoft.com/en-us/nuget/schema/nuspec
        match proc.ConvertResult with
        | AlreadySignedPackage -> None
        | SignedPackage ->
            let dir = proc.FinalDirectory
            let nuspecFileName = sprintf "%s.nuspec" proc.Package.Name.Name
            let nuspecFile = Path.Combine(dir, nuspecFileName)
            if not (File.Exists nuspecFile) then
                eprintfn "Package '%s':'%s' did not contain a nuspec" proc.Package.Name.Name proc.Package.Version.AsString
                None
            else
                let xml = XDocument.Load nuspecFile
                let packageNode = xml.Root
                let metadataNode =
                    packageNode.Elements()
                    |> Seq.tryFind (fun elem -> elem.Name.LocalName = "metadata")
                if metadataNode.IsNone then
                    failwithf "NuSpec without metadata node ('%s':'%s)..." proc.Package.Name.Name proc.Package.Version.AsString

                // Change ID and add .Signed
                let idNode =
                    metadataNode.Value.Elements()
                    |> Seq.tryFind (fun elem -> elem.Name.LocalName = "id")
                if idNode.IsNone then
                    failwithf "NuSpec without id node ('%s':'%s)..." proc.Package.Name.Name proc.Package.Version.AsString
                
                idNode.Value.Value <- idNode.Value.Value + signedPackagePostfix

                // Update dependencies -> reference .Signed packages.
                let dependenciesNode =
                    metadataNode.Value.Elements()
                    |> Seq.tryFind (fun elem -> elem.Name.LocalName = "dependencies")
                match dependenciesNode with
                | None -> ()
                | Some dependenciesNode ->
                    let handleDependency (elem:XElement) =
                        let idAttr = elem.Attribute(XName.op_Implicit "id")
                        let id = idAttr.Value
                        if handled 
                            |> Seq.exists (fun kv ->
                                let name, _ = kv.Key
                                let results = kv.Value
                                // We need to change id, when we know the package and it was signed.
                                name.CompareString = id.ToLowerInvariant() && 
                                results.ConvertResult = SignedPackage) then
                            idAttr.Value <- id + signedPackagePostfix

                    let rec handleGroup (elem:XElement) =
                        for e in elem.Elements() do
                            match e.Name.LocalName with
                            | "dependency" -> handleDependency e
                            | "group" -> handleGroup e
                            | _ -> ()
                    handleGroup dependenciesNode


                // replace files section
                let filesNode =
                    match packageNode.Elements()
                          |> Seq.tryFind (fun elem -> elem.Name.LocalName = "files") with
                    | Some f ->
                        f.RemoveAll()
                        f
                    | None ->
                        let files = XElement(XName.Get("files", packageNode.Name.NamespaceName))
                        packageNode.Add(files)
                        files
                    
                let file = XElement(XName.Get("file", packageNode.Name.NamespaceName))
                file.Add(XAttribute(XName.op_Implicit "src", "**"))
                file.Add(XAttribute(XName.op_Implicit "target", ""))          
                filesNode.Add(file)

                xml.Save(nuspecFile)
                Some nuspecFile

    let nugetPack signedPackagePostfix outDir nuspec (proc:ProcessingInfo) =
        if proc.ConvertResult = AlreadySignedPackage then
            failwithf "Expected an SignedPackage but got an AlreadySignedPackage: %A" proc
        let dir = proc.FinalDirectory
        Fake.DotNet.NuGet.NuGet.NuGetPack (fun c ->
            { c with
                ToolPath = __SOURCE_DIRECTORY__ + "/packages/NuGet.CommandLine/tools/NuGet.exe"
                OutputPath = Path.GetFullPath outDir
                WorkingDir = dir
                Version = proc.Package.Version.AsString }) nuspec
        let nupkg = Path.Combine(outDir, sprintf "%s%s.%s.nupkg" proc.Package.Name.Name signedPackagePostfix proc.Package.Version.AsString)
        if not (File.Exists nupkg) then
            failwithf "File '%s' doesn't exist after nuget pack" nupkg
        
        nupkg

    let getVersions feed packName =
        let root = Environment.CurrentDirectory
        let sources =
            [ PackageSources.PackageSource.Parse feed ]
        
        try
            Paket.NuGetV2.GetVersions false None root (sources, packName)   
            |> Async.RunSynchronously
            |> List.map fst
        with e ->
            printfn "Failed to retrieve versions. Assuming no versions exist. Error: %O" e
            []

let environVarOrDefault = Fake.Core.Environment.environVarOrDefault
let environVarOrFail = Fake.Core.Environment.environVarOrFail
let environVar = Fake.Core.Environment.environVar
let isVerbose = true
let workFolder = "mypackages"
let outDir = environVarOrDefault "OutputDirectory" "signedPackages"
let packageList = environVarOrFail "PackageList"
let snkFile = environVarOrFail "SnkFile"
let snkPass = environVarOrDefault "SnkPassword" ""

let signedPackagePostfix = 
    match environVar "SignedPackagePostfix" with
    | null -> ".Signed"
    | e -> e

let targetFeed =
    let rawFeed = environVarOrDefault "TargetFeed" ""
    if String.IsNullOrEmpty rawFeed then
        None
    else Some rawFeed

if isVerbose then
    Paket.Logging.verbose <- true

let sn =
    try SigningHelper.getStrongNameKeyPair snkFile snkPass
    with e ->
        eprintfn "File '%s' (pass: '%b') was not found or is invalid: %O" snkFile (String.IsNullOrEmpty snkPass |> not) e
        reraise()
        
printfn "Resolving packages..."
let resolved = ProcessPackages.getAllDependencies packageList

printfn "Signing packages..."
let handled = ProcessPackages.signPackages sn workFolder resolved

Fake.FileHelper.CleanDir outDir

printfn "Creating signed packages..."
for (kv) in handled do
    let name, v = kv.Key
    let results = kv.Value

    ProcessPackages.cleanPackageDirectory results
    let nuspec = ProcessPackages.convertNuspec signedPackagePostfix handled results
    match nuspec with
    | Some nuspec ->
        let nupkg = ProcessPackages.nugetPack signedPackagePostfix outDir nuspec results
        let signedName = Paket.Domain.PackageName (name.Name + signedPackagePostfix)
        let vs =
            match targetFeed with
            | Some targetFeed -> ProcessPackages.getVersions targetFeed signedName
            | None -> []
        if vs |> Seq.exists (fun ver -> ver = v) then
            File.Delete nupkg
            printfn "Not pushing package '%s':'%s' as it already exists on the target feed." signedName.Name v.AsString
        else
            ()
    | None -> ()        


printfn "Finished, now push your packages via regular push task."