namespace WoofWare.DotnetRuntimeLocator.Test

open System
open System.IO
open System.Reflection.Emit
open System.Runtime.InteropServices
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

/// `hostfxr_get_dotnet_environment_info`, which hostfxr.h declares `HOSTFXR_CALLTYPE` (`__cdecl`).
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type GetDotnetEnvironmentInfoFn = delegate of nativeint * nativeint * nativeint * nativeint -> int

/// The C library's `qsort` comparator.
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type QsortComparer = delegate of nativeint * nativeint -> int

/// The C library's `qsort`.
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Qsort = delegate of nativeint * unativeint * unativeint * nativeint -> unit

/// `DotnetEnvironmentInfo.CallDelegate` against a stand-in for hostfxr, so that the result it reports
/// can be one real hostfxr never would.
[<TestFixture>]
module TestHostFxrCallback =

    /// Calls the native `hostfxr_get_dotnet_environment_info_result_fn` (`__cdecl`, like hostfxr) at
    /// the first argument with the other two, as hostfxr would. F# has no syntax for calling a function
    /// pointer, and `Marshal.GetDelegateForFunctionPointer` on a pointer made from a managed delegate
    /// returns that delegate, so a call through it would never leave managed code.
    let private invokeResultFn : Action<nativeint, nativeint, nativeint> =
        let invoker =
            DynamicMethod ("invokeResultFn", null, [| typeof<nativeint> ; typeof<nativeint> ; typeof<nativeint> |])

        let il = invoker.GetILGenerator ()
        il.Emit OpCodes.Ldarg_1
        il.Emit OpCodes.Ldarg_2
        il.Emit OpCodes.Ldarg_0
        il.EmitCalli (OpCodes.Calli, CallingConvention.Cdecl, null, [| typeof<nativeint> ; typeof<nativeint> |])
        il.Emit OpCodes.Ret

        invoker.CreateDelegate<Action<nativeint, nativeint, nativeint>> ()

    let private qsort : Qsort =
        let libc =
            if OperatingSystem.IsWindows () then "msvcrt.dll"
            elif OperatingSystem.IsMacOS () then "libc"
            else "libc.so.6"

        let export = NativeLibrary.GetExport (NativeLibrary.Load libc, "qsort")
        Marshal.GetDelegateForFunctionPointer<Qsort> export

    /// Runs `action` from inside native C frames, as hostfxr runs its callback: from `qsort`'s
    /// comparator, which sorting two elements calls at least once. Where an exception cannot unwind
    /// through native frames (on Unix), one escaping `action` ends the process.
    let private fromNativeFrames (action : unit -> unit) : unit =
        let mutable ran = false

        let comparer =
            QsortComparer (fun _ _ ->
                if not ran then
                    ran <- true
                    action ()

                0
            )

        let elements = Marshal.AllocHGlobal (2 * IntPtr.Size)

        try
            qsort.Invoke (elements, 2un, unativeint IntPtr.Size, Marshal.GetFunctionPointerForDelegate comparer)
        finally
            Marshal.FreeHGlobal elements
            GC.KeepAlive comparer

        if not ran then
            failwith "qsort never called its comparator"

    /// Calls `CallDelegate` with a stand-in for `hostfxr_get_dotnet_environment_info`, which runs
    /// `fake` with a function that reports a native result through the caller's callback, and returns
    /// what `fake` returns. The callback runs from inside native frames, as it does under hostfxr.
    let private callWith (fake : (nativeint -> unit) -> int) : DotnetEnvironmentInfo =
        let stub =
            GetDotnetEnvironmentInfoFn (fun _dotnetRoot _reserved callback context ->
                fake (fun info -> fromNativeFrames (fun () -> invokeResultFn.Invoke (callback, info, context)))
            )

        try
            DotnetEnvironmentInfo.CallDelegate (null, Marshal.GetFunctionPointerForDelegate stub)
        finally
            GC.KeepAlive stub

    /// Runs `action` on a native `hostfxr_dotnet_environment_info` with no SDKs or frameworks, whose
    /// `size` field states `size`.
    let private withNativeInfo (size : int) (action : nativeint -> 'a) : 'a =
        // The struct's strings are CharSet.Auto, as StringToHGlobalAuto writes them.
        let version = Marshal.StringToHGlobalAuto "1.2.3"
        let commitHash = Marshal.StringToHGlobalAuto "abc"
        let info = Marshal.AllocHGlobal (7 * IntPtr.Size)

        try
            // size, hostfxr_version, hostfxr_commit_hash, sdk_count, sdks, framework_count, frameworks
            [ nativeint size ; version ; commitHash ; 0n ; 0n ; 0n ; 0n ]
            |> List.iteri (fun i field -> Marshal.WriteIntPtr (info, i * IntPtr.Size, field))

            action info
        finally
            Marshal.FreeHGlobal info
            Marshal.FreeHGlobal commitHash
            Marshal.FreeHGlobal version

    let private wellFormedSize : int =
        Marshal.SizeOf<InteropStructs.DotnetEnvironmentInfoNative> ()

    [<Test>]
    let ``the result hostfxr reports is returned`` () =
        let info =
            callWith (fun report ->
                withNativeInfo wellFormedSize report
                0
            )

        info.HostFxrVersion |> shouldEqual "1.2.3"
        info.HostFxrCommitHash |> shouldEqual "abc"
        info.Sdks |> shouldBeEmpty
        info.Frameworks |> shouldBeEmpty

    [<Test>]
    let ``a result that cannot be read is thrown to the caller, not across hostfxr's frames`` () =
        let thrown =
            Assert.Throws<InvalidDataException> (fun () ->
                callWith (fun report ->
                    withNativeInfo (wellFormedSize + 1) report
                    0
                )
                |> ignore
            )

        thrown.Message |> shouldContainText "did not match"

    [<Test>]
    let ``a failure code from hostfxr is thrown`` () =
        let thrown = Assert.Throws<Exception> (fun () -> callWith (fun _ -> 7) |> ignore)

        thrown.Message |> shouldContainText "exit code: 7"

    [<Test>]
    let ``success without a result is thrown`` () =
        Assert.Throws<NullReferenceException> (fun () -> callWith (fun _ -> 0) |> ignore)
        |> ignore
