/* Added by SAM Modern (not part of the original SAM.API).
 * Derived work of gibbed/SteamAchievementManager, see LICENSE-SAM.txt.
 */

using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers
{
    public class SteamFriends015 : NativeWrapper<ISteamFriends015>
    {
        /// <summary>k_EFriendFlagImmediate: echte Freunde (keine Gruppen-/Clan-Mitglieder).</summary>
        public const int FlagImmediate = 0x04;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int NativeGetFriendCount(IntPtr self, int flags);

        public int GetFriendCount(int flags)
        {
            return this.Call<int, NativeGetFriendCount>(this.Functions.GetFriendCount, this.ObjectAddress, flags);
        }

        // As with SteamUser012.GetSteamId(), the CSteamID is returned through a pointer to a return value here.
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void NativeGetFriendByIndex(IntPtr self, out ulong steamId, int index, int flags);

        public ulong GetFriendByIndex(int index, int flags)
        {
            var call = this.GetFunction<NativeGetFriendByIndex>(this.Functions.GetFriendByIndex);
            call(this.ObjectAddress, out ulong steamId, index, flags);
            return steamId;
        }

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr NativeGetFriendPersonaName(IntPtr self, ulong steamId);

        public string GetFriendPersonaName(ulong steamId)
        {
            var pointer = this.Call<IntPtr, NativeGetFriendPersonaName>(
                this.Functions.GetFriendPersonaName,
                this.ObjectAddress,
                steamId);
            return NativeStrings.PointerToString(pointer);
        }
    }
}
