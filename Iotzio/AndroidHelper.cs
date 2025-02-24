#if ANDROID
using System.Runtime.InteropServices;
using System.Threading;
using System;

namespace Com.Iotzio.Api;

public static partial class AndroidHelper
{

    private static object? libraryLoaded = null;

    /// <summary>
    ///
    /// Call this always on your MainActivity OnCreate function as stated:
    ///
    /// AndroidHelper.OnActivityCreate(this);
    ///
    /// </summary>
    public static void OnActivityCreate(Android.Content.Context context)
    {
        LazyInitializer.EnsureInitialized(ref libraryLoaded, () =>
        {
            var result = OnActivityCreateNative(Java.Interop.JniEnvironment.EnvironmentPointer, IntPtr.Zero, context.Handle);

            if (result != 1)
            {
                throw new Exception("Failed to initialize Android Context.");
            }

            return new();
        });
    }

    [LibraryImport("iotzio_core", EntryPoint = "Java_com_iotzio_api_AndroidHelper_onActivityCreateNative")]
    internal static partial byte OnActivityCreateNative(IntPtr env, IntPtr thiz, IntPtr context);
}
#endif