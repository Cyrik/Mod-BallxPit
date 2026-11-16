using System;

// The mod's sources are written against Il2CppInterop, which wraps every game object in a
// proxy: casts go through TryCast/Cast, identity through the native Pointer, and proxy-only
// methods are marked [HideFromIl2Cpp]. The macOS build of the game runs on Mono, where game
// objects are ordinary managed objects, so these shims give the same names their plain
// meaning and the shared sources compile unchanged.

namespace Il2CppInterop.Runtime.Attributes
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor)]
    internal sealed class HideFromIl2CppAttribute : Attribute { }
}

namespace EvoHelper
{
    internal static class Il2CppCompat
    {
        public static T TryCast<T>(this object o) where T : class => o as T;

        public static T Cast<T>(this UnityEngine.Object o) where T : UnityEngine.Object => (T)o;

        public static Type GetIl2CppType(this object o) => o.GetType();

        extension(UnityEngine.Object o)
        {
            // Identity key for a Unity object. The instance id stays readable after the object
            // is destroyed, which callers rely on to drop stale dictionary entries.
            public IntPtr Pointer => (IntPtr)o.GetInstanceID();
        }
    }
}
