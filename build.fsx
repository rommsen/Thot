#r @"packages/build/FAKE/tools/FakeLib.dll"
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.UserInputHelper
open Fake.YarnHelper
open System
open System.IO
open System.Text.RegularExpressions

#if MONO
// prevent incorrect output encoding (e.g. https://github.com/fsharp/FAKE/issues/1196)
System.Console.OutputEncoding <- System.Text.Encoding.UTF8
#endif

let dotnetcliVersion = "2.0.3"

let mutable dotnetExePath = "dotnet"

let release = LoadReleaseNotes "RELEASE_NOTES.md"
let srcGlob = "src/**/*.fsproj"
let testsGlob = "tests/**/*.fsproj"
let docsGlob = "docs/**/*.fsproj"

module Util =

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

// Module to print colored message in the console
module Logger =
    let consoleColor (fc : ConsoleColor) =
        let current = Console.ForegroundColor
        Console.ForegroundColor <- fc
        { new IDisposable with
              member x.Dispose() = Console.ForegroundColor <- current }

    let warn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printf "%s" s) str
    let warnfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printfn "%s" s) str
    let error str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printf "%s" s) str
    let errorfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printfn "%s" s) str


Target "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    ++ "docs/**/bin"
    ++ "docs/**/obj"
    ++ "docs/**/build"
    |> CleanDirs
)

Target "InstallDotNetCore" (fun _ ->
   dotnetExePath <- DotNetCli.InstallDotNetSDK dotnetcliVersion
)

Target "YarnInstall"(fun _ ->
    Yarn (fun p ->
        { p with
            Command = Install Standard
        })
)

Target "DotnetRestore" (fun _ ->
    !! srcGlob
    ++ testsGlob
    ++ docsGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Restore (fun c ->
            { c with
                Project = proj
                ToolPath = dotnetExePath
                //This makes sure that Proj2 references the correct version of Proj1
                AdditionalArgs = [sprintf "/p:PackageVersion=%s" release.NugetVersion]
            })
))

Target "DotnetBuild" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Build (fun c ->
            { c with
                Project = proj
                ToolPath = dotnetExePath
            })
))


let dotnet workingDir args =
    DotNetCli.RunCommand(fun c ->
        { c with WorkingDir = workingDir
                 ToolPath = dotnetExePath }
        ) args

let mocha args =
    Yarn(fun yarnParams ->
        { yarnParams with Command = args |> sprintf "run mocha %s" |> YarnCommand.Custom }
    )

Target "MochaTest" (fun _ ->
    !! testsGlob
    |> Seq.iter(fun proj ->
        let projDir = proj |> DirectoryName
        //Compile to JS
        dotnet projDir "fable yarn-run rollup --port free -- -c tests/rollup.config.js "

        //Run mocha tests
        let projDirOutput = projDir </> "bin"
        mocha projDirOutput
    )
)

Target "DotnetPack" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Pack (fun c ->
            { c with
                Project = proj
                Configuration = "Release"
                ToolPath = dotnetExePath
                AdditionalArgs =
                    [
                        sprintf "/p:PackageVersion=%s" release.NugetVersion
                        sprintf "/p:PackageReleaseNotes=\"%s\"" (String.Join("\n",release.Notes))
                    ]
            })
    )
)

Target "Docs.Watch" (fun _ ->
    !! docsGlob
    |> Seq.iter (fun proj ->
        let projDir = proj |> DirectoryName

        [ async {
            dotnet projDir "fable yarn-run fable-splitter --port free -- -c docs/splitter.config.js -w"
          }
          async {
            Yarn(fun yarnParams ->
                    { yarnParams
                        with Command = "node-sass --output-style compressed --watch --output docs/public/ docs/scss/main.scss" |> YarnCommand.Custom }
                )
          }
        ]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore
    )
)

Target "Docs.Setup" (fun _ ->
    CopyDir "./docs/public/fonts" "./node_modules/font-awesome/fonts" (fun _ -> true)
)

Target "Docs.Build" (fun _ ->
    !! docsGlob
    |> Seq.iter (fun proj ->
        let projDir = proj |> DirectoryName

        dotnet projDir "fable yarn-run fable-splitter --port free -- -c docs/splitter.config.js"
        Yarn(fun yarnParams ->
                { yarnParams
                    with Command = "node-sass --output-style compressed --output docs/public/ docs/scss/main.scss" |> YarnCommand.Custom }
            )
    )
)

Target "Watch" (fun _ ->
    !! testsGlob
    |> Seq.iter(fun proj ->
        let projDir = proj |> DirectoryName
        //Compile to JS
        dotnet projDir "fable webpack --port free -- --watch"
    )
)

let needsPublishing (versionRegex: Regex) (releaseNotes: ReleaseNotes) projFile =
    printfn "Project: %s" projFile
    if releaseNotes.NugetVersion.ToUpper().EndsWith("NEXT")
    then
        Logger.warnfn "Version in Release Notes ends with NEXT, don't publish yet."
        false
    else
        File.ReadLines(projFile)
        |> Seq.tryPick (fun line ->
            let m = versionRegex.Match(line)
            if m.Success then Some m else None)
        |> function
            | None -> failwith "Couldn't find version in project file"
            | Some m ->
                let sameVersion = m.Groups.[1].Value = releaseNotes.NugetVersion
                if sameVersion then
                    Logger.warnfn "Already version %s, no need to publish." releaseNotes.NugetVersion
                not sameVersion

Target "Publish" (fun _ ->
    let versionRegex = Regex("<Version>(.*?)</Version>", RegexOptions.IgnoreCase)
    !! srcGlob
    |> Seq.filter(needsPublishing versionRegex release)
    |> Seq.iter(fun projFile ->
        let projDir = Path.GetDirectoryName(projFile)
        let nugetKey =
            match environVarOrNone "NUGET_KEY" with
            | Some nugetKey -> nugetKey
            | None -> failwith "The Nuget API key must be set in a NUGET_KEY environmental variable"
        Directory.GetFiles(projDir </> "bin" </> "Release", "*.nupkg")
        |> Array.find (fun nupkg -> nupkg.Contains(release.NugetVersion))
        |> (fun nupkg ->
            (Path.GetFullPath nupkg, nugetKey)
            ||> sprintf "nuget push %s -s nuget.org -k %s"
            |> DotNetCli.RunCommand (fun c ->
                                            { c with ToolPath = dotnetExePath }))

        // After successful publishing, update the project file
        (versionRegex, projFile) ||> Util.replaceLines (fun line _ ->
            versionRegex.Replace(line, "<Version>" + release.NugetVersion + "</Version>") |> Some)
    )
)

Target "Release" (fun _ ->

    if Git.Information.getBranchName "" <> "master" then failwith "Not on master"

    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion
)

"Clean"
    ==> "InstallDotNetCore"
    ==> "YarnInstall"
    ==> "DotnetRestore"
    ==> "DotnetBuild"
    ==> "MochaTest"
    ==> "DotnetPack"
    ==> "Publish"
    ==> "Release"

"Watch"
    <== [ "DotnetBuild" ]

"Docs.Build"
    <== [ "DotnetRestore"
          "Docs.Setup" ]

"Docs.Watch"
    <== [ "Docs.Setup" ]
    // <== [ "DotnetRestore" ]

RunTargetOrDefault "DotnetPack"
