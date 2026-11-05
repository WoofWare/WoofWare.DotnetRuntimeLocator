namespace WoofWare.DotnetRuntimeLocator.Test

open System
open System.IO
open System.Text
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open WoofWare.DotnetRuntimeLocator
open NUnit.Framework

[<TestFixture>]
module TestDotnetEnvironmentInfo =

    [<Test>]
    let ``Can locate the runtime`` () =
        let runtimes = DotnetEnvironmentInfo.Get ()

        // In the test setup, there should be an SDK!
        runtimes.Sdks |> Seq.length |> shouldBeGreaterThan 0
        runtimes.Frameworks |> Seq.length |> shouldBeGreaterThan 0

        Console.WriteLine $"%O{runtimes}"

    /// hostfxr's `pal::char_t`: UTF-16 `wchar_t` on Windows, UTF-8 `char` elsewhere. The oracle decodes
    /// strictly, so that a lossy encoding cannot round-trip by accident.
    let private hostFxrEncoding : Encoding * int =
        if OperatingSystem.IsWindows () then
            UnicodeEncoding (false, false, true) :> Encoding, 2
        else
            UTF8Encoding (false, true) :> Encoding, 1

    /// Pieces of well-formed UTF-16 with no NUL: ASCII, and characters that take two, three and four
    /// bytes of UTF-8 (the last as surrogate pairs). None begins with a low surrogate or ends with a
    /// high one.
    let private genPieces : Gen<string list> =
        [
            "a"
            "Z"
            "0"
            "/"
            "\\"
            "."
            "-"
            " "
            "é"
            "ß"
            "日"
            "€"
            "😀"
            "\U0001F415"
        ]
        |> Gen.elements
        |> Gen.listOf

    let private genPath : Gen<string> = genPieces |> Gen.map (String.concat "")

    /// Strings holding one lone surrogate among `genPieces`. These are built in code, not taken from
    /// a `TestCase`, because a custom attribute stores its strings as UTF-8, which replaces a lone
    /// surrogate with U+FFFD.
    let private genIllFormedPath : Gen<string> =
        gen {
            let! pieces = genPieces
            let! index = Gen.choose (0, List.length pieces)
            let! surrogate = Gen.elements [ '\uD800' ; '\uDBFF' ; '\uDC00' ; '\uDFFF' ]
            return pieces |> List.insertAt index (string<char> surrogate) |> String.concat ""
        }

    [<Test>]
    let ``a path reaches hostfxr as its char_t string, ended by exactly one NUL`` () =
        let encoding, unitSize = hostFxrEncoding

        let property (path : string) : bool =
            let encoded = DotnetEnvironmentInfo.EncodeForHostFxr path
            let body = encoded.Length - unitSize

            let isNulAt (offset : int) =
                Seq.forall (fun i -> encoded.[offset + i] = 0uy) (seq { 0 .. unitSize - 1 })

            body >= 0
            && body % unitSize = 0
            && isNulAt body
            && Seq.forall (fun unit -> not (isNulAt (unit * unitSize))) (seq { 0 .. body / unitSize - 1 })
            && encoding.GetString (encoded, 0, body) = path

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 1000, Prop.forAll (Arb.fromGen genPath) property)

    [<TestCase("/usr/share\000/dotnet")>]
    [<TestCase("\000")>]
    let ``a path containing NUL is refused, because hostfxr would read only up to it`` (path : string) =
        Assert.Throws<ArgumentException> (fun () -> DotnetEnvironmentInfo.EncodeForHostFxr path |> ignore)
        |> ignore

    [<Test>]
    let ``a path no encoding can represent is refused`` () =
        let property (path : string) : bool =
            try
                DotnetEnvironmentInfo.EncodeForHostFxr path |> ignore<byte[]>
                false
            with :? EncoderFallbackException ->
                true

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 1000, Prop.forAll (Arb.fromGen genIllFormedPath) property)

    [<Test>]
    let ``GetSpecific finds the frameworks of an installation whose path is not ASCII`` () =
        let parent =
            Path.Combine (Path.GetTempPath (), "locator-test-" + Guid.NewGuid().ToString "N")

        let root = Path.Combine (parent, "dötnet-日本")
        let frameworkDir = Path.Combine (root, "shared", "Microsoft.NETCore.App")
        let versionDir = Path.Combine (frameworkDir, "99.0.0")
        Directory.CreateDirectory versionDir |> ignore<DirectoryInfo>

        try
            // hostfxr counts a framework version only if its .deps.json is present.
            File.WriteAllText (Path.Combine (versionDir, "Microsoft.NETCore.App.deps.json"), "{}")

            let dotnetName =
                if OperatingSystem.IsWindows () then
                    "dotnet.exe"
                else
                    "dotnet"

            let dotnet = Path.Combine (root, dotnetName)
            File.WriteAllText (dotnet, "")

            let info = DotnetEnvironmentInfo.GetSpecific (FileInfo dotnet)

            info.Frameworks
            |> List.ofSeq
            |> shouldEqual
                [
                    DotnetEnvironmentFrameworkInfo ("Microsoft.NETCore.App", frameworkDir, "99.0.0")
                ]
        finally
            Directory.Delete (parent, true)
