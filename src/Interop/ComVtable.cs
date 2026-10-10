using System;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // 直接呼叫 COM 物件函式表（vtable）裡的函式：slot 是第幾個（照 Windows SDK 標頭檔裡的順序，從 IUnknown 的 0 開始數），
    // T 是對應的委派型別。這樣不需要額外的套件就能用 Direct2D / Direct3D 的介面
    static class ComVtable
    {
        public static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
        }
    }
}
