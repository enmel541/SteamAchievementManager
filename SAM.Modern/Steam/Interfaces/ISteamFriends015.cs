/* Added by SAM Modern (not part of the original SAM.API).
 * Derived work of gibbed/SteamAchievementManager, see LICENSE-SAM.txt.
 * Only the beginning of the ISteamFriends vtable is declared; it is identical across versions 013-017.
 */

using System.Runtime.InteropServices;

namespace SAM.API.Interfaces
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ISteamFriends015
    {
        public IntPtr GetPersonaName;
        public IntPtr SetPersonaName;
        public IntPtr GetPersonaState;
        public IntPtr GetFriendCount;
        public IntPtr GetFriendByIndex;
        public IntPtr GetFriendRelationship;
        public IntPtr GetFriendPersonaState;
        public IntPtr GetFriendPersonaName;
    }
}
