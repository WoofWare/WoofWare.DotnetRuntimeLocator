using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     Information known to `dotnet` about what frameworks and runtimes are available.
/// </summary>
/// <param name="HostFxrVersion">Version of the runtime, e.g. "8.0.5"</param>
/// <param name="HostFxrCommitHash">
///     A commit hash of the .NET runtime (as of this writing, this is probably a hash from
///     https://github.com/dotnet/runtime).
/// </param>
/// <param name="Sdks">Collection of .NET SDKs we were able to find.</param>
/// <param name="Frameworks">Collection of .NET runtimes we were able to find.</param>
public record DotnetEnvironmentInfo(
    string HostFxrVersion,
    string HostFxrCommitHash,
    IReadOnlyList<DotnetEnvironmentSdkInfo> Sdks,
    IReadOnlyList<DotnetEnvironmentFrameworkInfo> Frameworks)
{
    private static readonly Lazy<FileInfo> HostFxr = new(() =>
    {
        switch (Environment.GetEnvironmentVariable("WOOFWARE_DOTNET_LOCATOR_LIBHOSTFXR"))
        {
            case null:
                break;
            case var s:
            {
                return new FileInfo(s);
            }
        }

        // First, we might be self-contained: try and find it next to us.
        var selfContainedAttempt = Directory.GetParent(Assembly.GetExecutingAssembly().Location);
        if (selfContainedAttempt != null)
        {
            var attempt = selfContainedAttempt.EnumerateFiles("*hostfxr*").FirstOrDefault();
            if (attempt != null) return attempt;
        }

        var runtimeDir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var parent1 = runtimeDir.Parent ??
                      throw new Exception("Unable to locate the host/fxr directory in the .NET runtime");
        var parent2 = parent1.Parent ??
                      throw new Exception("Unable to locate the host/fxr directory in the .NET runtime");
        var parent3 = parent2.Parent ??
                      throw new Exception("Unable to locate the host/fxr directory in the .NET runtime");
        var fxrDir = new DirectoryInfo(Path.Combine(parent3.FullName, "host", "fxr"));
        // Until net6, libhostfxr did not contain the entrypoint we use, and I can't be bothered to reimplement
        // it on those runtimes. I'm just going to assume you have no runtimes earlier than 3 installed.
        Func<DirectoryInfo, bool> isAcceptableName =
            di => !di.Name.StartsWith("3.", StringComparison.Ordinal) &&
                  !di.Name.StartsWith("5.", StringComparison.Ordinal);
        return fxrDir.EnumerateDirectories().First(isAcceptableName).EnumerateFiles("*hostfxr*").First();
    });

    private static FileInfo ResolveAllSymlinks(FileInfo f)
    {
        while (!ReferenceEquals(null, f.LinkTarget))
        {
            var parent = f.Directory ?? new DirectoryInfo("/");
            f = new FileInfo(Path.Combine(parent.FullName, f.LinkTarget));
        }

        return f;
    }

    /// <summary>
    ///     hostfxr_get_dotnet_environment_info's callback. Takes a DotnetEnvironmentInfoNative
    ///     (https://github.com/dotnet/runtime/blob/2dba5a3587de19160fb09129dcd3d7a4089b67b5/src/native/corehost/hostfxr.h#L311)
    ///     and a GCHandle, and stores in the handle's target the DotnetEnvironmentInfo it renders, or else
    ///     the exception which rendering it threw.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void StoreResult(IntPtr envInfo, IntPtr retLoc)
    {
        var handle = GCHandle.FromIntPtr(retLoc);
        // An exception cannot leave this method: hostfxr's frames lie between it and the caller, and on
        // Unix the runtime cannot unwind through them, so the process would abort. CallDelegate rethrows it.
        try
        {
            handle.Target = FromNativeConstructor.FromNative(
                Marshal.PtrToStructure<InteropStructs.DotnetEnvironmentInfoNative>(envInfo));
        }
        catch (Exception e)
        {
            handle.Target = ExceptionDispatchInfo.Capture(e);
        }
    }

    /// <summary>
    ///     <paramref name="path" /> as the string hostfxr takes it: a NUL-terminated <c>pal::char_t</c> string,
    ///     which is UTF-16 on Windows and UTF-8 elsewhere.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path" /> contains NUL.</exception>
    /// <exception cref="EncoderFallbackException">
    ///     <paramref name="path" /> contains a lone surrogate, which has no encoding.
    /// </exception>
    internal static byte[] EncodeForHostFxr(string path)
    {
        // hostfxr would read only up to the first NUL, and so look somewhere other than the caller named.
        if (path.Contains('\0'))
            throw new ArgumentException($"A path passed to hostfxr cannot contain NUL; provided: {path}");

        // Strict encoders: a lossy one would also send hostfxr to some other path.
        Encoding encoding = OperatingSystem.IsWindows()
            ? new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // hostfxr reads the string up to its terminator, so without one it reads past the array into
        // whatever the GC heap holds next.
        return encoding.GetBytes(path + "\0");
    }

    /// <summary>
    ///     Calls <paramref name="getDotnetEnvironmentInfo" />, a pointer to hostfxr's
    ///     <c>hostfxr_get_dotnet_environment_info</c> or a function of its signature, and returns what it reported.
    /// </summary>
    /// <remarks>
    ///     The function's implementation is
    ///     https://github.com/dotnet/runtime/blob/2dba5a3587de19160fb09129dcd3d7a4089b67b5/src/native/corehost/fxr/hostfxr.cpp#L357
    ///     It takes:
    ///     * The path to the directory which contains the `dotnet` executable, as `EncodeForHostFxr` encodes it
    ///     * A structure which is reserved for future use and which must currently be null
    ///     * A pointer to a callback which takes two arguments: a DotnetEnvironmentInfoNative
    ///     (https://github.com/dotnet/runtime/blob/2dba5a3587de19160fb09129dcd3d7a4089b67b5/src/native/corehost/hostfxr.h#L311)
    ///     and a context object you supplied.
    ///     `StoreResult` is that callback.
    ///     * A pointer to the context object you want to consume in the callback.
    ///     It returns zero on success. hostfxr.h declares it, and the callback, <c>HOSTFXR_CALLTYPE</c>, which is
    ///     <c>__cdecl</c>.
    /// </remarks>
    internal static unsafe DotnetEnvironmentInfo CallDelegate(string? dotnetExePath, IntPtr getDotnetEnvironmentInfo)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, int>)getDotnetEnvironmentInfo;
        byte[]? dotnet = null;
        if (dotnetExePath != null)
        {
            dotnet = EncodeForHostFxr(dotnetExePath);
        }
        fixed (byte* dotnetPath = dotnet)
        {
            var handle = GCHandle.Alloc(null);
            try
            {
                // A pointer to a static method, not to a delegate, so there is no object whose lifetime the
                // native call could outlast.
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback = &StoreResult;

                var rc = f((IntPtr)dotnetPath, IntPtr.Zero, (IntPtr)callback, GCHandle.ToIntPtr(handle));
                if (rc != 0) throw new Exception($"Could not obtain .NET environment information (exit code: {rc})");

                switch (handle.Target)
                {
                    case DotnetEnvironmentInfo info:
                        return info;
                    case ExceptionDispatchInfo thrown:
                        thrown.Throw();
                        throw new InvalidOperationException("unreachable: ExceptionDispatchInfo.Throw returned");
                    case null:
                        throw new NullReferenceException(
                            "Unexpectedly failed to populate DotnetEnvironmentInfo, despite the native call succeeding.");
                    case var other:
                        throw new InvalidOperationException(
                            $"hostfxr's callback stored an unexpected {other.GetType()}");
                }
            }
            finally
            {
                handle.Free();
            }
        }
    }

    /// <summary>
    ///     Get the environment information that is available to the specified `dotnet` executable.
    /// </summary>
    /// <param name="dotnetExe">
    ///     A `dotnet` (or `dotnet.exe`) executable, e.g. one from /usr/bin/dotnet. Set this to null if you
    ///     want us to just do our best.
    /// </param>
    /// <returns>Information about the environment available to the given executable.</returns>
    /// <exception cref="Exception">Throws on any failure; handles nothing gracefully.</exception>
    public static DotnetEnvironmentInfo GetSpecific(FileInfo? dotnetExe)
    {
        var hostFxr = HostFxr.Value;
        var lib = NativeLibrary.Load(hostFxr.FullName);
        try
        {
            var ptr = NativeLibrary.GetExport(lib, "hostfxr_get_dotnet_environment_info");
            if (ptr == IntPtr.Zero) throw new Exception("Unable to load function from native library");

            string? dotnetParent = null;
            if (dotnetExe != null)
            {
                var dotnetNoSymlinks = ResolveAllSymlinks(dotnetExe);
                var parent = dotnetNoSymlinks.Directory;
                if (parent != null)
                {
                    dotnetParent = parent.FullName;
                }
            }

            return CallDelegate(dotnetParent, ptr);
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

    private static FileInfo? FindDotnetAbove(DirectoryInfo path)
    {
        while (true)
        {
            var candidate = Path.Combine(path.FullName, "dotnet");
            if (File.Exists(candidate)) return new FileInfo(candidate);

            if (ReferenceEquals(path.Parent, null)) return null;

            path = path.Parent;
        }
    }

    private static FileInfo? LocateDotnetExe()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
        {
            foreach (var component in path.Split(':'))
            {
                var dotnet = Path.Combine(component, "dotnet");
                if (File.Exists(dotnet)) return new FileInfo(dotnet);
            }
        }

        var renv = RuntimeEnvironment.GetRuntimeDirectory();
        return FindDotnetAbove(new DirectoryInfo(renv));
    }

    /// <summary>
    ///     Get the environment information that is available to some arbitrary `dotnet` executable we were able to find.
    /// </summary>
    /// <returns>Information about the environment available to `dotnet`.</returns>
    /// <exception cref="Exception">Throws on any failure; handles nothing gracefully.</exception>
    public static DotnetEnvironmentInfo Get()
    {
        var dotnetExe = Environment.GetEnvironmentVariable("WOOFWARE_DOTNET_LOCATOR_DOTNET_EXE") switch
        {
            null => LocateDotnetExe(),
            var s => new FileInfo(s)
        };

        // `null` can happen! Maybe we're self-contained.
        return GetSpecific(dotnetExe);
    }
}

internal class FromNativeConstructor
{
    internal static DotnetEnvironmentInfo FromNative(InteropStructs.DotnetEnvironmentInfoNative native)
    {
        if (native.size < 0 || native.size > int.MaxValue)
            throw new InvalidDataException("size field did not fit in an int");
        var size = (int)native.size;
        if (native.framework_count < 0 || native.framework_count > int.MaxValue)
            throw new InvalidDataException("framework_count field did not fit in an int");
        var frameworkCount = (int)native.framework_count;
        if (native.sdk_count < 0 || native.sdk_count > int.MaxValue)
            throw new InvalidDataException("sdk_count field did not fit in an int");
        var sdkCount = (int)native.sdk_count;

        if (size != Marshal.SizeOf(native))
            throw new InvalidDataException($"size field {size} did not match expected size {Marshal.SizeOf(native)}");

        var frameworks = new List<DotnetEnvironmentFrameworkInfo>((int)native.framework_count);
        for (var i = 0; i < frameworkCount; i++)
        {
            var frameworkInfo = new IntPtr(native.frameworks.ToInt64() +
                                           i * Marshal.SizeOf<InteropStructs.DotnetEnvironmentFrameworkInfoNative>());
            frameworks.Add(DotnetEnvironmentFrameworkInfo.FromNative(
                Marshal.PtrToStructure<InteropStructs.DotnetEnvironmentFrameworkInfoNative>(frameworkInfo)));
        }

        var sdks = new List<DotnetEnvironmentSdkInfo>((int)native.sdk_count);
        for (var i = 0; i < sdkCount; i++)
        {
            var sdkInfo = new IntPtr(native.sdks.ToInt64() +
                                     i * Marshal.SizeOf<InteropStructs.DotnetEnvironmentSdkInfoNative>());
            sdks.Add(DotnetEnvironmentSdkInfo.FromNative(
                Marshal.PtrToStructure<InteropStructs.DotnetEnvironmentSdkInfoNative>(sdkInfo)));
        }

        return new DotnetEnvironmentInfo(native.hostfxr_version, native.hostfxr_commit_hash, sdks, frameworks);
    }
}
