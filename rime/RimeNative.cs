// Standalone Rime wrapper. The layout mirrors the official librime 1.17.0
// rime_api.h; this project keeps the third-party DLL as an unmodified sidecar.
#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudeZhInput.Rime;

[StructLayout(LayoutKind.Sequential)]
internal struct RimeTraits
{
    public int DataSize;
    public IntPtr SharedDataDir;
    public IntPtr UserDataDir;
    public IntPtr DistributionName;
    public IntPtr DistributionCodeName;
    public IntPtr DistributionVersion;
    public IntPtr AppName;
    public IntPtr Modules;
    public int MinLogLevel;
    public IntPtr LogDir;
    public IntPtr PrebuiltDataDir;
    public IntPtr StagingDir;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RimeComposition
{
    public int Length;
    public int CursorPos;
    public int SelStart;
    public int SelEnd;
    public IntPtr Preedit;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RimeCandidateNative
{
    public IntPtr Text;
    public IntPtr Comment;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RimeMenu
{
    public int PageSize;
    public int PageNo;
    public int IsLastPage;
    public int HighlightedCandidateIndex;
    public int NumCandidates;
    public IntPtr Candidates;
    public IntPtr SelectKeys;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RimeCommit
{
    public int DataSize;
    public IntPtr Text;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RimeContext
{
    public int DataSize;
    public RimeComposition Composition;
    public RimeMenu Menu;
    public IntPtr CommitTextPreview;
    public IntPtr SelectLabels;
}

// Every member after DataSize is a native function pointer. Keeping all
// intervening fields is required: the target pointer is selected by offset.
[StructLayout(LayoutKind.Sequential)]
internal struct RimeApiLayout
{
    public int DataSize;
    public IntPtr Setup;
    public IntPtr SetNotificationHandler;
    public IntPtr Initialize;
    public IntPtr Finalize;
    public IntPtr StartMaintenance;
    public IntPtr IsMaintenanceMode;
    public IntPtr JoinMaintenanceThread;
    public IntPtr DeployerInitialize;
    public IntPtr Prebuild;
    public IntPtr Deploy;
    public IntPtr DeploySchema;
    public IntPtr DeployConfigFile;
    public IntPtr SyncUserData;
    public IntPtr CreateSession;
    public IntPtr FindSession;
    public IntPtr DestroySession;
    public IntPtr CleanupStaleSessions;
    public IntPtr CleanupAllSessions;
    public IntPtr ProcessKey;
    public IntPtr CommitComposition;
    public IntPtr ClearComposition;
    public IntPtr GetCommit;
    public IntPtr FreeCommit;
    public IntPtr GetContext;
    public IntPtr FreeContext;
    public IntPtr GetStatus;
    public IntPtr FreeStatus;
    public IntPtr SetOption;
    public IntPtr GetOption;
    public IntPtr SetProperty;
    public IntPtr GetProperty;
    public IntPtr GetSchemaList;
    public IntPtr FreeSchemaList;
    public IntPtr GetCurrentSchema;
    public IntPtr SelectSchema;
    public IntPtr SchemaOpen;
    public IntPtr ConfigOpen;
    public IntPtr ConfigClose;
    public IntPtr ConfigGetBool;
    public IntPtr ConfigGetInt;
    public IntPtr ConfigGetDouble;
    public IntPtr ConfigGetString;
    public IntPtr ConfigGetCString;
    public IntPtr ConfigUpdateSignature;
    public IntPtr ConfigBeginMap;
    public IntPtr ConfigNext;
    public IntPtr ConfigEnd;
    public IntPtr SimulateKeySequence;
    public IntPtr RegisterModule;
    public IntPtr FindModule;
    public IntPtr RunTask;
    public IntPtr GetSharedDataDir;
    public IntPtr GetUserDataDir;
    public IntPtr GetSyncDir;
    public IntPtr GetUserId;
    public IntPtr GetUserDataSyncDir;
    public IntPtr ConfigInit;
    public IntPtr ConfigLoadString;
    public IntPtr ConfigSetBool;
    public IntPtr ConfigSetInt;
    public IntPtr ConfigSetDouble;
    public IntPtr ConfigSetString;
    public IntPtr ConfigGetItem;
    public IntPtr ConfigSetItem;
    public IntPtr ConfigClear;
    public IntPtr ConfigCreateList;
    public IntPtr ConfigCreateMap;
    public IntPtr ConfigListSize;
    public IntPtr ConfigBeginList;
    public IntPtr GetInput;
    public IntPtr GetCaretPos;
    public IntPtr SelectCandidate;
    public IntPtr GetVersion;
    public IntPtr SetCaretPos;
    public IntPtr SelectCandidateOnCurrentPage;
    public IntPtr CandidateListBegin;
    public IntPtr CandidateListNext;
    public IntPtr CandidateListEnd;
    public IntPtr UserConfigOpen;
    public IntPtr CandidateListFromIndex;
    public IntPtr GetPrebuiltDataDir;
    public IntPtr GetStagingDir;
    public IntPtr CommitProto;
    public IntPtr ContextProto;
    public IntPtr StatusProto;
    public IntPtr GetStateLabel;
    public IntPtr DeleteCandidate;
    public IntPtr DeleteCandidateOnCurrentPage;
    public IntPtr GetStateLabelAbbreviated;
    public IntPtr SetInput;
    public IntPtr GetSharedDataDirS;
    public IntPtr GetUserDataDirS;
    public IntPtr GetPrebuiltDataDirS;
    public IntPtr GetStagingDirS;
    public IntPtr GetSyncDirS;
    public IntPtr HighlightCandidate;
    public IntPtr HighlightCandidateOnCurrentPage;
    public IntPtr ChangePage;
}

internal static class RimeNative
{
    private static readonly object ResolverGate = new();
    private static string? _dllPath;
    private static IntPtr _nativeHandle;
    private static bool _resolverInstalled;

    internal static void Configure(string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("librime runtime was not found.", fullPath);

        lock (ResolverGate)
        {
            if (_dllPath is not null && !string.Equals(_dllPath, fullPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only one librime DLL may be loaded per process.");

            _dllPath = fullPath;
            if (_resolverInstalled)
                return;

            NativeLibrary.SetDllImportResolver(typeof(RimeNative).Assembly, ResolveLibrary);
            _resolverInstalled = true;
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "rime.dll", StringComparison.OrdinalIgnoreCase)
            || _dllPath is null)
            return IntPtr.Zero;
        lock (ResolverGate)
        {
            if (_nativeHandle == IntPtr.Zero)
                _nativeHandle = NativeLibrary.Load(_dllPath);
            return _nativeHandle;
        }
    }

    [DllImport("rime.dll", CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true, EntryPoint = "rime_get_api")]
    internal static extern IntPtr GetApi();

    // The official Windows DLL does not export the optional key_table helper
    // symbols. Use the X11 keysym values from the bundled key_table.h instead.
    internal static int GetKeycodeByName(string name)
    {
        if (name.Length == 1 && name[0] <= 0x7f)
            return name[0];
        return name switch
        {
            "space" => 0x0020,
            "BackSpace" => 0xff08,
            "Tab" => 0xff09,
            "Return" or "Enter" => 0xff0d,
            "Escape" => 0xff1b,
            "Delete" => 0xffff,
            "Home" => 0xff50,
            "Left" => 0xff51,
            "Up" => 0xff52,
            "Right" => 0xff53,
            "Down" => 0xff54,
            "Page_Up" => 0xff55,
            "Page_Down" => 0xff56,
            "End" => 0xff57,
            "Insert" => 0xff63,
            _ => throw new ArgumentException($"Unknown Rime keysym '{name}'.", nameof(name)),
        };
    }

    internal static int GetModifierByName(string name)
        => name switch
        {
            "Shift" => 1 << 0,
            "Lock" or "Caps_Lock" => 1 << 1,
            "Control" or "Ctrl" => 1 << 2,
            "Alt" or "Mod1" => 1 << 3,
            "Release" => 1 << 30,
            _ => throw new ArgumentException($"Unknown Rime modifier '{name}'.", nameof(name)),
        };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetupDelegate(ref RimeTraits traits);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InitializeDelegate(ref RimeTraits traits);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FinalizeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate UIntPtr CreateSessionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr GetVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SessionDelegate(UIntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProcessKeyDelegate(UIntPtr session, int keycode, int mask);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClearCompositionDelegate(UIntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CommitDelegate(UIntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetCommitDelegate(UIntPtr session, ref RimeCommit commit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FreeCommitDelegate(ref RimeCommit commit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetContextDelegate(UIntPtr session, ref RimeContext context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FreeContextDelegate(ref RimeContext context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SelectCandidateDelegate(UIntPtr session, UIntPtr index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SelectSchemaDelegate(UIntPtr session, IntPtr schemaId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ChangePageDelegate(UIntPtr session, int backward);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetOptionDelegate(
        UIntPtr session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string option,
        int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetOptionDelegate(UIntPtr session, IntPtr option);

    internal static T Function<T>(IntPtr address) where T : Delegate
    {
        if (address == IntPtr.Zero)
            throw new InvalidOperationException("librime API function is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    internal static RimeApiLayout LoadApi()
    {
        IntPtr address = GetApi();
        if (address == IntPtr.Zero)
            throw new InvalidOperationException("rime_get_api returned NULL.");

        int dataSize = Marshal.ReadInt32(address);
        int requiredSize = Marshal.SizeOf<RimeApiLayout>();
        if (dataSize < requiredSize - sizeof(int))
            throw new NotSupportedException("librime API is older than the required fields.");

        return Marshal.PtrToStructure<RimeApiLayout>(address);
    }

    internal static int StructDataSize<T>() where T : struct
        => Marshal.SizeOf<T>() - sizeof(int);

    internal static string ReadUtf8(IntPtr address)
        => address == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(address) ?? string.Empty;
}

