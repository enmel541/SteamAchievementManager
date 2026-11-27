/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers
{
    public class SteamApps008 : NativeWrapper<ISteamApps008>
    {
        #region IsSubscribed
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeIsSubscribedApp(IntPtr self, uint gameId);

        public bool IsSubscribedApp(uint gameId)
        {
            return this.Call<bool, NativeIsSubscribedApp>(this.Functions.IsSubscribedApp, this.ObjectAddress, gameId);
        }
        #endregion

        // ADDED (SAM Modern): DLC list of the app this process runs as (SteamAppId context).
        #region GetDLCCount
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int NativeGetDLCCount(IntPtr self);

        public int GetDLCCount()
        {
            return this.Call<int, NativeGetDLCCount>(this.Functions.GetDLCCount, this.ObjectAddress);
        }
        #endregion

        #region GetDLCDataByIndex
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeGetDLCDataByIndex(
            IntPtr self,
            int index,
            out uint appId,
            [MarshalAs(UnmanagedType.I1)] out bool available,
            IntPtr name,
            int nameLength);

        public bool GetDLCDataByIndex(int index, out uint appId, out bool available, out string name)
        {
            const int length = 512;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.WriteByte(buffer, 0, 0);
                var call = this.GetFunction<NativeGetDLCDataByIndex>(this.Functions.GetDLCDataByIndex);
                bool ok = call(this.ObjectAddress, index, out appId, out available, buffer, length);
                name = ok ? NativeStrings.PointerToString(buffer, length) : null;
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        #endregion

        #region GetCurrentGameLanguage
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr NativeGetCurrentGameLanguage(IntPtr self);

        public string GetCurrentGameLanguage()
        {
            var languagePointer = this.Call<IntPtr, NativeGetCurrentGameLanguage>(
                this.Functions.GetCurrentGameLanguage,
                this.ObjectAddress);
            return NativeStrings.PointerToString(languagePointer);
        }
        #endregion
    }
}
