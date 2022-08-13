
#if TAIKO_IL2CPP
using System;
using System.Runtime.InteropServices;
// using Il2CppSystem.Runtime.InteropServices;
using UnhollowerBaseLib;

namespace TakoTako;

public class ReferenceObject<T> : Il2CppObjectBase
{
    public T Value { get; private set; }
    public ReferenceObject(IntPtr pointer) : base(pointer)
    {
        Value = Marshal.PtrToStructure<T>(pointer);
    }
    
    public static  implicit operator T (ReferenceObject<T> value) => value.Value;
}
#endif
